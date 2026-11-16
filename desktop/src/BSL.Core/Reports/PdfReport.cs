using System.Data;
using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace BSL.Core.Reports;

public enum ColKind { Text, Int, Number, Money, Date }

public sealed record ReportColumn(string Name, string Caption, ColKind Kind = ColKind.Text, double Weight = 1, bool Total = false);

/// <summary>PDF output using MigraDoc (MIT licence). Tables, grouping with sub-totals, record cards.</summary>
public static class PdfReport
{
    public static string CompanyName { get; set; } = "BSL BF#3 Revamp – Project Control";
    public static string PreparedBy { get; set; } = Environment.UserName;
    static readonly CultureInfo India = new("en-IN");

    static bool _fontsReady;
    public static void EnsureFonts()
    {
        if (_fontsReady) return;
        // always our resolver: PDFsharp's own Windows lookup returns no font (and crashes) when Arial is missing
        GlobalFontSettings.FontResolver = new FileFontResolver();
        _fontsReady = true;
    }

    /// <summary>Guesses column kinds from a DataTable when no explicit layout is given.</summary>
    public static List<ReportColumn> AutoColumns(DataTable t, IEnumerable<string>? skip = null)
    {
        var sk = new HashSet<string>(skip ?? [], StringComparer.OrdinalIgnoreCase);
        return t.Columns.Cast<DataColumn>().Where(c => !sk.Contains(c.ColumnName)).Select(c =>
        {
            var n = c.ColumnName;
            var kind = c.DataType == typeof(DateTime) ? ColKind.Date
                : c.DataType == typeof(int) || c.DataType == typeof(long) || c.DataType == typeof(short) ? ColKind.Int
                : c.DataType == typeof(decimal) || c.DataType == typeof(double) || c.DataType == typeof(float)
                    ? (n.Contains("Amount") || n.Contains("Rate") || n.Contains("Value") || n.Contains("Margin") || n.Contains("Paid") || n.Contains("Done") ? ColKind.Money : ColKind.Number)
                : ColKind.Text;
            var total = (kind is ColKind.Number or ColKind.Money) && !n.Contains("Rate") && !n.Contains("Pct") && !n.EndsWith('%') && !n.Contains("Sets");
            return new ReportColumn(n, string.IsNullOrEmpty(c.Caption) ? n : c.Caption, kind, kind == ColKind.Text ? 2 : 1, total);
        }).ToList();
    }

    public static void Table(string path, string title, string? subtitle, DataTable data, IList<ReportColumn> cols,
                             string? groupBy = null, bool? landscape = null, IEnumerable<DataRow>? rows = null)
    {
        EnsureFonts();
        var doc = NewDocument(title, landscape ?? cols.Count > 7);
        var sec = doc.LastSection;
        AddTitle(sec, title, subtitle);

        var list = (rows ?? data.Rows.Cast<DataRow>()).Where(r => r.RowState != DataRowState.Deleted).ToList();
        var usable = (sec.PageSetup.PageWidth - sec.PageSetup.LeftMargin - sec.PageSetup.RightMargin).Point;
        var shown = cols.Where(c => c.Name != groupBy && data.Columns.Contains(c.Name)).ToList();
        var totalWeight = shown.Sum(c => c.Weight);
        var fontSize = shown.Count > 14 ? 6 : shown.Count > 10 ? 7 : 8;

        var table = sec.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = new Color(180, 180, 180);
        table.Format.Font.Size = fontSize;
        foreach (var c in shown) table.AddColumn(Unit.FromPoint(usable * c.Weight / totalWeight));

        var hdr = table.AddRow();
        hdr.HeadingFormat = true;
        hdr.Shading.Color = new Color(31, 78, 120);
        hdr.Format.Font.Color = Colors.White;
        hdr.Format.Font.Bold = true;
        for (int i = 0; i < shown.Count; i++)
        {
            hdr.Cells[i].AddParagraph(shown[i].Caption);
            hdr.Cells[i].Format.Alignment = shown[i].Kind == ColKind.Text ? ParagraphAlignment.Left : ParagraphAlignment.Right;
        }

        var groups = groupBy != null && data.Columns.Contains(groupBy)
            ? list.GroupBy(r => Format(r[groupBy], ColKind.Text)).ToList()
            : [list.GroupBy(_ => "").First()];
        if (list.Count == 0) groups = [];

        foreach (var g in groups)
        {
            if (groupBy != null)
            {
                var gr = table.AddRow();
                gr.Shading.Color = new Color(221, 235, 247);
                gr.Cells[0].MergeRight = shown.Count - 1;
                var cap = cols.FirstOrDefault(c => c.Name == groupBy)?.Caption ?? groupBy;
                gr.Cells[0].AddParagraph($"{cap}: {(g.Key.Length == 0 ? "(blank)" : g.Key)}   ({g.Count()} rows)").Format.Font.Bold = true;
            }
            bool alt = false;
            foreach (var r in g)
            {
                var row = table.AddRow();
                if (alt) row.Shading.Color = new Color(247, 247, 247);
                alt = !alt;
                for (int i = 0; i < shown.Count; i++)
                {
                    row.Cells[i].AddParagraph(Format(r[shown[i].Name], shown[i].Kind));
                    row.Cells[i].Format.Alignment = shown[i].Kind == ColKind.Text ? ParagraphAlignment.Left : ParagraphAlignment.Right;
                }
            }
            if (groupBy != null && shown.Any(c => c.Total)) AddTotalRow(table, shown, g, "Sub-total", new Color(242, 242, 242));
        }
        if (shown.Any(c => c.Total) && list.Count > 0) AddTotalRow(table, shown, list, "GRAND TOTAL", new Color(255, 242, 204));

        sec.AddParagraph($"{list.Count} rows").Format.SpaceBefore = Unit.FromPoint(6);
        Render(doc, path);
    }

    static void AddTotalRow(Table table, List<ReportColumn> shown, IEnumerable<DataRow> rows, string label, Color shade)
    {
        var tr = table.AddRow();
        tr.Shading.Color = shade;
        tr.Format.Font.Bold = true;
        tr.Cells[0].AddParagraph(label);
        for (int i = 0; i < shown.Count; i++)
        {
            if (!shown[i].Total) continue;
            var sum = rows.Sum(r => r[shown[i].Name] is DBNull or null ? 0m : Convert.ToDecimal(r[shown[i].Name], CultureInfo.InvariantCulture));
            tr.Cells[i].AddParagraph(Format(sum, shown[i].Kind));
            tr.Cells[i].Format.Alignment = ParagraphAlignment.Right;
        }
    }

    /// <summary>Form-view print: one record as labelled sections (like a contractor detail sheet).</summary>
    public static void RecordCard(string path, string title, string? subtitle, IEnumerable<(string Section, IList<(string Label, string Value)> Fields)> sections)
    {
        EnsureFonts();
        var doc = NewDocument(title, false);
        var sec = doc.LastSection;
        AddTitle(sec, title, subtitle);
        var usable = (sec.PageSetup.PageWidth - sec.PageSetup.LeftMargin - sec.PageSetup.RightMargin).Point;
        foreach (var (name, fields) in sections)
        {
            if (fields.Count == 0) continue;
            var h = sec.AddParagraph(name);
            h.Format.Font.Bold = true; h.Format.Font.Size = 11; h.Format.Font.Color = new Color(31, 78, 120);
            h.Format.SpaceBefore = Unit.FromPoint(10); h.Format.SpaceAfter = Unit.FromPoint(3);
            var t = sec.AddTable();
            t.Borders.Width = 0.25; t.Borders.Color = new Color(200, 200, 200);
            t.Format.Font.Size = 9;
            t.AddColumn(Unit.FromPoint(usable * 0.18)); t.AddColumn(Unit.FromPoint(usable * 0.32));
            t.AddColumn(Unit.FromPoint(usable * 0.18)); t.AddColumn(Unit.FromPoint(usable * 0.32));
            for (int i = 0; i < fields.Count; i += 2)
            {
                var r = t.AddRow();
                for (int k = 0; k < 2 && i + k < fields.Count; k++)
                {
                    r.Cells[k * 2].Shading.Color = new Color(242, 242, 242);
                    r.Cells[k * 2].AddParagraph(fields[i + k].Label).Format.Font.Bold = true;
                    r.Cells[k * 2 + 1].AddParagraph(fields[i + k].Value);
                }
            }
        }
        sec.AddParagraph().Format.SpaceBefore = Unit.FromPoint(50);
        var sign = sec.AddTable();
        foreach (var _ in Enumerable.Range(0, 3)) sign.AddColumn(Unit.FromPoint(usable / 3));
        var sr = sign.AddRow();
        string[] who = ["Prepared by", "Checked by", "Approved by"];
        for (int i = 0; i < 3; i++) { sr.Cells[i].AddParagraph("____________________"); sr.Cells[i].AddParagraph(who[i]); }
        Render(doc, path);
    }

    static Document NewDocument(string title, bool landscape)
    {
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Author = PreparedBy;
        var st = doc.Styles[StyleNames.Normal]!;
        st.Font.Name = "Arial";
        st.Font.Size = 8;
        var sec = doc.AddSection();
        sec.PageSetup = doc.DefaultPageSetup.Clone();
        sec.PageSetup.PageFormat = PageFormat.A4;
        // MigraDoc 6: set the real page size; Orientation alone does not swap width/height
        sec.PageSetup.PageWidth = landscape ? Unit.FromMillimeter(297) : Unit.FromMillimeter(210);
        sec.PageSetup.PageHeight = landscape ? Unit.FromMillimeter(210) : Unit.FromMillimeter(297);
        sec.PageSetup.LeftMargin = sec.PageSetup.RightMargin = Unit.FromCentimeter(1.2);
        sec.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        sec.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);

        var hp = sec.Headers.Primary.AddParagraph();
        hp.AddFormattedText(CompanyName, TextFormat.Bold);
        hp.AddTab(); hp.AddText(title);
        hp.Format.Font.Size = 8;
        hp.Format.Borders.Bottom.Width = 0.5;
        var width = (sec.PageSetup.PageWidth - sec.PageSetup.LeftMargin - sec.PageSetup.RightMargin);
        hp.Format.TabStops.AddTabStop(width, TabAlignment.Right);

        var fp = sec.Footers.Primary.AddParagraph();
        fp.Format.Font.Size = 7;
        fp.AddText($"Printed {DateTime.Now:dd-MM-yyyy HH:mm} by {PreparedBy}");
        fp.AddTab(); fp.AddText("Page "); fp.AddPageField(); fp.AddText(" of "); fp.AddNumPagesField();
        fp.Format.TabStops.AddTabStop(width, TabAlignment.Right);
        return doc;
    }

    static void AddTitle(Section sec, string title, string? subtitle)
    {
        var p = sec.AddParagraph(title);
        p.Format.Font.Size = 14; p.Format.Font.Bold = true; p.Format.SpaceAfter = Unit.FromPoint(2);
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var s = sec.AddParagraph(subtitle);
            s.Format.Font.Size = 9; s.Format.Font.Italic = true;
        }
        sec.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
    }

    static void Render(Document doc, string path)
    {
        var r = new PdfDocumentRenderer { Document = doc };
        r.RenderDocument();
        r.PdfDocument.Save(path);
    }

    public static string Format(object? v, ColKind kind) => v switch
    {
        null or DBNull => "",
        DateTime d => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        decimal or double or float or int or long or short when kind == ColKind.Money => Convert.ToDecimal(v).ToString("#,##,##0.00", India),
        decimal or double or float when kind is ColKind.Number or ColKind.Text => Convert.ToDecimal(v).ToString("#,##0.###", CultureInfo.InvariantCulture),
        int or long or short => Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? ""
    };
}

/// <summary>Loads TrueType files from the Windows fonts folder (or common Linux folders).</summary>
public sealed class FileFontResolver : IFontResolver
{
    /// <summary>Font folders in search order: app-local "fonts" folder, Windows fonts, per-user fonts, Linux fonts.</summary>
    static readonly string[] Dirs = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "fonts"),
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Fonts"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"),
        "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/truetype/dejavu", "/usr/share/fonts/truetype", "/usr/share/fonts"
    }.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct().ToArray();

    static readonly (string Regular, string Bold, string Italic, string BoldItalic)[] Candidates =
    [
        ("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
        ("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
        ("calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"),
        ("LiberationSans-Regular.ttf", "LiberationSans-Bold.ttf", "LiberationSans-Italic.ttf", "LiberationSans-BoldItalic.ttf"),
        ("DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf")
    ];

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new((bold, italic) switch { (true, true) => "BI", (true, false) => "B", (false, true) => "I", _ => "R" });

    public byte[]? GetFont(string faceName)
    {
        foreach (var c in Candidates)
        {
            var file = faceName switch { "B" => c.Bold, "I" => c.Italic, "BI" => c.BoldItalic, _ => c.Regular };
            foreach (var d in Dirs)
            {
                var p = FindFile(d, file);
                if (p != null) return File.ReadAllBytes(p);
            }
        }
        throw new FileNotFoundException("No TrueType font found for PDF output. Install Arial, or copy arial.ttf / arialbd.ttf / ariali.ttf / arialbi.ttf " +
                                        "into a 'fonts' folder next to the program. Searched: " + string.Join("; ", Dirs));
    }

    static string? FindFile(string dir, string file)
    {
        if (!Directory.Exists(dir)) return null;
        var direct = Path.Combine(dir, file);
        if (File.Exists(direct)) return direct;
        try { return Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }
}
