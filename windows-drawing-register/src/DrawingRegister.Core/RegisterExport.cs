using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace DrawingRegister.Core;

/// <summary>Printable drawing register (A4 landscape PDF) and a CSV for Excel.</summary>
public static class RegisterExport
{
    // ------------------------------------------------------------------ CSV

    /// <summary>Every file in register order: current revision first, then the ones it superseded.</summary>
    public static string Csv(IEnumerable<Drawing> drawings)
    {
        static string Q(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.Append("S.No,Plant,Unit/Area,Type code,Type,Drawing No.,Title,Sheet kind,Revision,Current,Status,Received,File name,Stored at,ZIP folder,Remarks\r\n");
        var sn = 0;
        foreach (var (p, u, t, e) in Register.Flatten(Register.Organise(drawings)))
        {
            foreach (var d in e.Revisions)
            {
                sb.AppendJoin(',',
                    ++sn, Q(Register.PlantLabel(p.Plant)), Q(Register.UnitLabel(u.Unit)), t.Type.Code, Q(t.Type.Name),
                    Q(d.DrawingNo), Q(d.DisplayTitle), Q(d.Kind), Q(d.Revision), d == e.Current ? "Yes" : "Superseded",
                    Q(d.Status.Label()), d.ImportedOn.ToString("dd-MM-yyyy"), Q(d.FileName), Q(d.StoredPath), Q(d.SourcePath), Q(d.Remarks));
                sb.Append("\r\n");
            }
        }
        return sb.ToString();
    }

    public static void WriteCsv(IEnumerable<Drawing> drawings, string path) =>
        File.WriteAllText(path, Csv(drawings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    // ------------------------------------------------------------------ PDF

    static readonly XColor Accent = XColor.FromArgb(0xE6, 0x51, 0x00);
    static readonly XColor PlantBg = XColor.FromArgb(0xFF, 0xCC, 0x80);
    static readonly XColor UnitBg = XColor.FromArgb(0xFF, 0xE0, 0xB2);
    static readonly XColor TypeBg = XColor.FromArgb(0xFF, 0xF3, 0xE0);
    static readonly XColor Grey = XColor.FromArgb(0x75, 0x75, 0x75);

    public static byte[] Pdf(IEnumerable<Drawing> drawings, string projectName, bool includeSuperseded = true)
    {
        FontSetup.Ensure();
        var all = drawings.ToList();
        var groups = Register.Organise(all);
        var current = groups.Sum(g => g.Count);
        using var doc = new PdfDocument();
        doc.Info.Title = $"Drawing Register - {projectName}";
        doc.Info.Creator = "Drawing Register";
        var w = new PageWriter(doc, projectName, $"{current} drawings | {all.Count} files");

        // Summary: current drawings per plant/unit x type.
        w.Heading("Summary by unit and drawing type", 9);
        var summaryCols = new List<(string, double)> { ("Plant / Unit", 0.0) };
        summaryCols.AddRange(DrawingType.All.Select(t => (t.Code, 30.0)));
        summaryCols.Add(("Total", 36));
        w.TableHeader(summaryCols, XColors.LightGray);
        foreach (var p in groups)
            foreach (var u in p.Units)
            {
                var cells = new List<string> { $"{Register.PlantLabel(p.Plant)} > {Register.UnitLabel(u.Unit)}" };
                cells.AddRange(DrawingType.All.Select(t =>
                {
                    var n = u.Types.FirstOrDefault(x => x.Type == t)?.Drawings.Count ?? 0;
                    return n == 0 ? "-" : n.ToString();
                }));
                cells.Add(u.Count.ToString());
                w.Row(summaryCols, cells, bold: false);
            }
        w.Note(string.Join("   ", DrawingType.All.Select(t => $"{t.Code} = {t.Name}")));
        w.Gap(8);

        var cols = new List<(string, double)>
        {
            ("S.No", 30), ("Drawing No.", 150), ("Title", 0), ("Sheet kind", 95), ("Rev", 34), ("Status", 38),
            ("Earlier revs", 70), ("Received", 58), ("File", 150),
        };
        var sn = 0;
        foreach (var p in groups)
        {
            w.Band($"PLANT: {Register.PlantLabel(p.Plant)}   ({p.Count} drawings)", PlantBg, 10, keepWithNext: 5);
            foreach (var u in p.Units)
            {
                w.Band($"UNIT / AREA: {Register.UnitLabel(u.Unit)}   ({u.Count})", UnitBg, 9, keepWithNext: 4);
                foreach (var t in u.Types)
                {
                    w.Band($"{t.Type.Code} - {t.Type.Name}   ({t.Drawings.Count})", TypeBg, 8.5, keepWithNext: 2);
                    w.TableHeader(cols, XColor.FromArgb(0xF5, 0xF5, 0xF5));
                    foreach (var e in t.Drawings)
                    {
                        var d = e.Current;
                        w.Row(cols, [
                            (++sn).ToString(), d.DrawingNo, d.DisplayTitle, d.Kind, Revisions.Label(d.Revision), d.Status.Code(),
                            string.Join(", ", e.Superseded.Select(x => x.Revision)), d.ImportedOn.ToString("dd-MM-yyyy"), d.FileName,
                        ], bold: false, boldColumns: [1, 4]);
                        if (!includeSuperseded) continue;
                        foreach (var s in e.Superseded)
                            w.Row(cols, ["", "   superseded", s.DisplayTitle, "", Revisions.Label(s.Revision), s.Status.Code(), "",
                                s.ImportedOn.ToString("dd-MM-yyyy"), s.FileName], bold: false, colour: Grey, small: true);
                    }
                }
            }
        }
        w.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Minimal paginating table writer for A4 landscape.</summary>
    sealed class PageWriter
    {
        const double Margin = 28, RowH = 13, BandH = 16;
        readonly PdfDocument _doc;
        readonly string _project, _counts;
        readonly XFont _f = new("Arial", 7.5), _fb = new("Arial", 7.5, XFontStyleEx.Bold), _fs = new("Arial", 6.5);
        PdfPage _page = null!;
        XGraphics _g = null!;
        double _y;
        int _pageNo;

        public PageWriter(PdfDocument doc, string project, string counts)
        {
            _doc = doc; _project = project; _counts = counts;
            NewPage();
        }

        double Width => _page.Width.Point - 2 * Margin;
        double Bottom => _page.Height.Point - Margin - 14;

        void NewPage()
        {
            _g?.Dispose();
            _page = _doc.AddPage();
            _page.Size = PdfSharp.PageSize.A4;
            _page.Orientation = PdfSharp.PageOrientation.Landscape;
            _g = XGraphics.FromPdfPage(_page);
            _pageNo++;
            _g.DrawString("DRAWING REGISTER", new XFont("Arial", 14, XFontStyleEx.Bold), new XSolidBrush(Accent), Margin, Margin + 12);
            _g.DrawString(_project, new XFont("Arial", 10, XFontStyleEx.Bold), XBrushes.Black, Margin, Margin + 26);
            _g.DrawString($"{_counts}  |  Order: Plant > Unit > Type > Drawing No.", _f, XBrushes.Black,
                new XRect(Margin, Margin + 18, Width, 10), XStringFormats.TopRight);
            _g.DrawLine(new XPen(Accent, 1.5), Margin, Margin + 32, Margin + Width, Margin + 32);
            _g.DrawString($"Generated {DateTime.Now:dd-MM-yyyy HH:mm} | IFR review, IFA approval, IFC construction, AB as-built, FI information",
                _fs, new XSolidBrush(Grey), Margin, _page.Height.Point - Margin);
            _g.DrawString($"Page {_pageNo}", _fs, new XSolidBrush(Grey),
                new XRect(Margin, _page.Height.Point - Margin - 7, Width, 10), XStringFormats.TopRight);
            _y = Margin + 40;
        }

        void Need(double h) { if (_y + h > Bottom) NewPage(); }

        public void Gap(double h) => _y += h;

        public void Heading(string text, double size)
        {
            Need(RowH + 4);
            _g.DrawString(text, new XFont("Arial", size, XFontStyleEx.Bold), XBrushes.Black, Margin, _y + size);
            _y += size + 6;
        }

        public void Note(string text)
        {
            Need(RowH);
            _g.DrawString(Fit(text, _fs, Width), _fs, new XSolidBrush(Grey), Margin, _y + 9);
            _y += RowH;
        }

        public void Band(string text, XColor bg, double size, int keepWithNext = 0)
        {
            Need(BandH + 2 + keepWithNext * RowH);
            _g.DrawRectangle(new XSolidBrush(bg), Margin, _y + 2, Width, BandH);
            _g.DrawString(text, new XFont("Arial", size, XFontStyleEx.Bold), XBrushes.Black, Margin + 5, _y + 2 + BandH - 4.5);
            _y += BandH + 3;
        }

        double[] Widths(List<(string Name, double W)> cols)
        {
            var fixedW = cols.Sum(c => c.W);
            var flex = cols.Count(c => c.W == 0);
            return cols.Select(c => c.W == 0 ? Math.Max(40, (Width - fixedW) / Math.Max(1, flex)) : c.W).ToArray();
        }

        public void TableHeader(List<(string Name, double W)> cols, XColor bg)
        {
            Need(RowH * 2);
            _g.DrawRectangle(new XSolidBrush(bg), Margin, _y, Width, RowH);
            Cells(cols, cols.Select(c => c.Name).ToList(), _fb, XBrushes.Black);
        }

        public void Row(List<(string Name, double W)> cols, List<string> cells, bool bold, XColor? colour = null,
            bool small = false, int[]? boldColumns = null)
        {
            Need(RowH);
            var brush = colour is { } c ? new XSolidBrush(c) : XBrushes.Black;
            Cells(cols, cells, small ? _fs : bold ? _fb : _f, brush, small ? null : boldColumns);
            _g.DrawLine(new XPen(XColors.LightGray, 0.4), Margin, _y, Margin + Width, _y);
        }

        void Cells(List<(string Name, double W)> cols, List<string> cells, XFont font, XBrush brush, int[]? boldColumns = null)
        {
            var widths = Widths(cols);
            var x = Margin;
            for (var i = 0; i < cols.Count && i < cells.Count; i++)
            {
                var f = boldColumns?.Contains(i) == true ? _fb : font;
                _g.DrawString(Fit(cells[i], f, widths[i] - 6), f, brush, x + 3, _y + RowH - 3.5);
                x += widths[i];
            }
            _y += RowH;
        }

        string Fit(string s, XFont f, double w)
        {
            if (_g.MeasureString(s, f).Width <= w) return s;
            while (s.Length > 1 && _g.MeasureString(s + "…", f).Width > w) s = s[..^1];
            return s + "…";
        }

        public void Finish() => _g.Dispose();
    }
}

/// <summary>
/// PDFsharp needs to be told where fonts are. Uses Arial from Windows, or a
/// metric-compatible font (Liberation Sans / DejaVu Sans) on other systems.
/// </summary>
public sealed class FontSetup : IFontResolver
{
    static readonly object Lock = new();
    static bool _done;

    public static void Ensure()
    {
        lock (Lock)
        {
            if (_done) return;
            if (GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new FontSetup();
            _done = true;
        }
    }

    static readonly string[] Dirs =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        "/usr/share/fonts/truetype/liberation", "/usr/share/fonts/truetype/msttcorefonts",
        "/usr/share/fonts/truetype/dejavu", "/Library/Fonts", "/System/Library/Fonts/Supplemental",
    ];

    static readonly Dictionary<string, string[]> Files = new()
    {
        ["regular"] = ["arial.ttf", "Arial.ttf", "LiberationSans-Regular.ttf", "DejaVuSans.ttf"],
        ["bold"] = ["arialbd.ttf", "Arial Bold.ttf", "LiberationSans-Bold.ttf", "DejaVuSans-Bold.ttf"],
        ["italic"] = ["ariali.ttf", "Arial Italic.ttf", "LiberationSans-Italic.ttf", "DejaVuSans-Oblique.ttf"],
        ["bolditalic"] = ["arialbi.ttf", "Arial Bold Italic.ttf", "LiberationSans-BoldItalic.ttf", "DejaVuSans-BoldOblique.ttf"],
    };

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new((isBold, isItalic) switch
        {
            (true, true) => "bolditalic",
            (true, false) => "bold",
            (false, true) => "italic",
            _ => "regular",
        });

    public byte[]? GetFont(string faceName)
    {
        foreach (var file in Files[faceName].Concat(Files["regular"]))
            foreach (var dir in Dirs.Where(d => !string.IsNullOrEmpty(d)))
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
        throw new FileNotFoundException("No Arial, Liberation Sans or DejaVu Sans font found for the PDF register.");
    }
}
