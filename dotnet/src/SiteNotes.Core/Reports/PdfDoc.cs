using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SkiaSharp;

namespace SiteNotes.Core.Reports;

public record PdfColumn(string Title, float Weight, bool AlignRight = false);

/// <summary>
/// A small flowing-layout PDF writer on top of SkiaSharp (works the same on Android and Windows).
/// Content is added top to bottom; pages break automatically and table headers repeat.
/// Units are PDF points (A4 = 595 × 842).
/// </summary>
public sealed class PdfDoc : IDisposable
{
    public float PageWidth { get; }
    public float PageHeight { get; }
    public float Margin { get; set; } = 32;
    public string HeaderTitle { get; set; }
    public string HeaderSubtitle { get; set; }
    public bool ShowHeader { get; set; } = true;
    public int PageNumber { get; private set; }

    public static readonly SKColor Accent = SKColor.Parse("#1F4E79");
    public static readonly SKColor AccentLight = SKColor.Parse("#DCE8F5");
    public static readonly SKColor Ink = SKColor.Parse("#1A1A1A");
    public static readonly SKColor Muted = SKColor.Parse("#666666");
    public static readonly SKColor Grid = SKColor.Parse("#B8C4D0");
    public static readonly SKColor Zebra = SKColor.Parse("#F4F7FA");
    public static readonly SKColor Warn = SKColor.Parse("#B91C1C");

    private readonly SKDocument _doc;
    private SKCanvas? _c;
    private float _y;
    private readonly Dictionary<(string, bool, bool), SKTypeface> _faces = new();
    private readonly string _generated = DateTime.Now.ToString("dd-MMM-yyyy HH:mm");

    public PdfDoc(Stream output, string title, string subtitle = "", bool landscape = false)
    {
        PageWidth = landscape ? 842 : 595;
        PageHeight = landscape ? 595 : 842;
        HeaderTitle = title;
        HeaderSubtitle = subtitle;
        _doc = SKDocument.CreatePdf(output, new SKDocumentPdfMetadata
        {
            Title = title,
            Subject = subtitle,
            Creator = "SiteNotes DPR",
            Producer = "SiteNotes DPR",
            Creation = DateTime.Now,
            Modified = DateTime.Now,
            RasterDpi = 150,
        });
    }

    public float ContentWidth => PageWidth - 2 * Margin;
    private float Bottom => PageHeight - Margin - 14;
    public float Y => _y;
    private SKCanvas C => _c ?? NewPage();

    // ---------- fonts ----------

    public SKTypeface Face(string logical = "Default", bool bold = false, bool italic = false)
    {
        var key = (logical, bold, italic);
        if (_faces.TryGetValue(key, out var tf)) return tf;
        var style = new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        string[] families = logical switch
        {
            "Serif" => ["Times New Roman", "Noto Serif", "serif"],
            "Mono" => ["Consolas", "Courier New", "Noto Sans Mono", "monospace"],
            "Casual" => ["Comic Sans MS", "casual"],
            "Cursive" => ["Segoe Script", "cursive"],
            "Condensed" => ["Arial Narrow", "Bahnschrift", "sans-serif-condensed"],
            _ => ["Segoe UI", "Arial", "Roboto", "Noto Sans", "sans-serif"],
        };
        foreach (var f in families)
        {
            var t = SKTypeface.FromFamilyName(f, style);
            if (t != null && t.FamilyName.Equals(f, StringComparison.OrdinalIgnoreCase)) { tf = t; break; }
        }
        tf ??= SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
        _faces[key] = tf;
        return tf;
    }

    public SKFont Font(float size, bool bold = false, bool italic = false, string family = "Default") =>
        new(Face(family, bold, italic), size) { Subpixel = true, Edging = SKFontEdging.Antialias };

    private static SKPaint Fill(SKColor c) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Fill };
    private static SKPaint Stroke(SKColor c, float w = 0.6f) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

    // ---------- pages ----------

    public SKCanvas NewPage()
    {
        if (_c != null) EndPage();
        _c = _doc.BeginPage(PageWidth, PageHeight);
        PageNumber++;
        _y = Margin;
        if (ShowHeader) DrawPageHeader();
        DrawFooter();
        return _c;
    }

    private void EndPage()
    {
        _doc.EndPage();
        _c = null;
    }

    private void DrawPageHeader()
    {
        var c = _c!;
        using var band = Fill(Accent);
        c.DrawRect(Margin, _y, ContentWidth, 26, band);
        using var f = Font(12, bold: true);
        using var white = Fill(SKColors.White);
        c.DrawText(Ellipsize(HeaderTitle, f, ContentWidth * 0.62f), Margin + 8, _y + 17.5f, SKTextAlign.Left, f, white);
        using var fs = Font(8.5f);
        c.DrawText(Ellipsize(HeaderSubtitle, fs, ContentWidth * 0.36f), Margin + ContentWidth - 8, _y + 16.5f, SKTextAlign.Right, fs, white);
        _y += 34;
    }

    private void DrawFooter()
    {
        var c = _c!;
        var y = PageHeight - Margin + 4;
        using var line = Stroke(Grid);
        c.DrawLine(Margin, y - 10, Margin + ContentWidth, y - 10, line);
        using var f = Font(7.5f);
        using var p = Fill(Muted);
        c.DrawText($"SiteNotes DPR · generated {_generated}", Margin, y, SKTextAlign.Left, f, p);
        c.DrawText($"Page {PageNumber}", Margin + ContentWidth, y, SKTextAlign.Right, f, p);
    }

    public void EnsureSpace(float height)
    {
        if (_c == null || _y + height > Bottom) NewPage();
    }

    public void Space(float h = 8) { if (_c != null) _y += h; }

    // ---------- text ----------

    public static string Ellipsize(string text, SKFont f, float width)
    {
        if (string.IsNullOrEmpty(text) || f.MeasureText(text) <= width) return text ?? "";
        var s = text;
        while (s.Length > 1 && f.MeasureText(s + "…") > width) s = s[..^1];
        return s + "…";
    }

    /// <summary>Word-wraps text (respecting line breaks); very long words are split by character.</summary>
    public static List<string> Wrap(string? text, SKFont f, float width)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text)) { lines.Add(""); return lines; }
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (f.MeasureText(candidate) <= width) { line = candidate; continue; }
                if (line.Length > 0) lines.Add(line);
                line = word;
                while (line.Length > 1 && f.MeasureText(line) > width)
                {
                    var n = line.Length - 1;
                    while (n > 1 && f.MeasureText(line[..n]) > width) n--;
                    lines.Add(line[..n]);
                    line = line[n..];
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Flowing text block with full styling (used for notes).</summary>
    public void Text(string text, float size = 10, bool bold = false, bool italic = false, SKColor? color = null,
        string family = "Default", bool underline = false, bool strike = false, TextAlign align = TextAlign.Left,
        float indent = 0, string marker = "", SKColor? highlight = null, float spacingAfter = 4)
    {
        using var f = Font(size, bold, italic, family);
        using var paint = Fill(color ?? Ink);
        var lineH = size * 1.32f;
        var markerW = marker.Length > 0 ? Math.Max(f.MeasureText(marker) + 5, size * 1.2f) : 0;
        var x0 = Margin + indent + markerW;
        var width = ContentWidth - indent - markerW;
        var lines = Wrap(text, f, width);
        for (var i = 0; i < lines.Count; i++)
        {
            EnsureSpace(lineH);
            var baseline = _y + size;
            var w = f.MeasureText(lines[i]);
            var x = align switch
            {
                TextAlign.Center => x0 + (width - w) / 2,
                TextAlign.Right => x0 + width - w,
                _ => x0
            };
            if (i == 0 && marker.Length > 0)
                C.DrawText(marker, Margin + indent, baseline, SKTextAlign.Left, f, paint);
            if (highlight is { } hl)
            {
                using var hp = Fill(hl);
                C.DrawRect(x - 1, _y + 1, w + 2, lineH - 1, hp);
            }
            C.DrawText(lines[i], x, baseline, SKTextAlign.Left, f, paint);
            if (underline || strike)
            {
                using var lp = Stroke(color ?? Ink, Math.Max(0.6f, size / 16));
                if (underline) C.DrawLine(x, baseline + 1.6f, x + w, baseline + 1.6f, lp);
                if (strike) C.DrawLine(x, baseline - size * 0.3f, x + w, baseline - size * 0.3f, lp);
            }
            _y += lineH;
        }
        _y += spacingAfter;
    }

    public void Paragraph(string? text, float size = 9.5f, SKColor? color = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Text(text, size, color: color);
    }

    /// <summary>Numbered section band, e.g. "3. MANPOWER".</summary>
    public void Section(string title, string? right = null)
    {
        EnsureSpace(60);
        Space(4);
        using var bg = Fill(AccentLight);
        C.DrawRect(Margin, _y, ContentWidth, 18, bg);
        using var bar = Fill(Accent);
        C.DrawRect(Margin, _y, 3, 18, bar);
        using var f = Font(10, bold: true);
        using var ink = Fill(Accent);
        C.DrawText(title.ToUpperInvariant(), Margin + 9, _y + 12.8f, SKTextAlign.Left, f, ink);
        if (right != null)
        {
            using var fr = Font(8.5f);
            C.DrawText(right, Margin + ContentWidth - 6, _y + 12.5f, SKTextAlign.Right, fr, ink);
        }
        _y += 24;
    }

    public void Divider()
    {
        EnsureSpace(10);
        using var p = Stroke(Grid, 0.8f);
        C.DrawLine(Margin, _y + 4, Margin + ContentWidth, _y + 4, p);
        _y += 10;
    }

    /// <summary>Label/value pairs laid out in a grid of boxes.</summary>
    public void KeyValues(IEnumerable<(string Label, string? Value)> pairs, int columns = 3)
    {
        var items = pairs.ToList();
        var cellW = ContentWidth / columns;
        using var lf = Font(7, bold: false);
        using var vf = Font(9.5f, bold: true);
        using var lp = Fill(Muted);
        using var vp = Fill(Ink);
        using var border = Stroke(Grid);
        for (var i = 0; i < items.Count; i += columns)
        {
            var row = items.Skip(i).Take(columns).ToList();
            var h = row.Max(p => Wrap(p.Value ?? "-", vf, cellW - 10).Count) * 12 + 16;
            EnsureSpace(h);
            for (var j = 0; j < row.Count; j++)
            {
                var x = Margin + j * cellW;
                C.DrawRect(x, _y, cellW, h, border);
                C.DrawText(row[j].Label.ToUpperInvariant(), x + 5, _y + 9, SKTextAlign.Left, lf, lp);
                var ly = _y + 21;
                foreach (var l in Wrap(string.IsNullOrWhiteSpace(row[j].Value) ? "-" : row[j].Value, vf, cellW - 10))
                {
                    C.DrawText(l, x + 5, ly, SKTextAlign.Left, vf, vp);
                    ly += 12;
                }
            }
            _y += h;
        }
        _y += 6;
    }

    /// <summary>Row of highlighted figures (total manpower, machines…).</summary>
    public void Tiles(IEnumerable<(string Label, string Value)> tiles)
    {
        var list = tiles.ToList();
        if (list.Count == 0) return;
        const float h = 42, gap = 6;
        EnsureSpace(h + 8);
        var w = (ContentWidth - gap * (list.Count - 1)) / list.Count;
        using var bg = Fill(Accent);
        using var white = Fill(SKColors.White);
        using var vf = Font(15, bold: true);
        using var lf = Font(7);
        for (var i = 0; i < list.Count; i++)
        {
            var x = Margin + i * (w + gap);
            C.DrawRoundRect(x, _y, w, h, 4, 4, bg);
            C.DrawText(list[i].Value, x + w / 2, _y + 21, SKTextAlign.Center, vf, white);
            C.DrawText(Ellipsize(list[i].Label.ToUpperInvariant(), lf, w - 6), x + w / 2, _y + 34, SKTextAlign.Center, lf, white);
        }
        _y += h + 10;
    }

    /// <summary>Bordered table with wrapping cells, zebra rows, a repeated header and an optional totals row.</summary>
    public void Table(IReadOnlyList<PdfColumn> cols, IEnumerable<string?[]> rows, string?[]? totals = null,
        float fontSize = 8, string? emptyText = "No entries")
    {
        var rowList = rows.ToList();
        var totalWeight = cols.Sum(c => c.Weight);
        var widths = cols.Select(c => ContentWidth * c.Weight / totalWeight).ToArray();
        using var hf = Font(fontSize, bold: true);
        using var cf = Font(fontSize);
        using var tf = Font(fontSize, bold: true);
        var lineH = fontSize * 1.3f;
        const float pad = 3.5f;

        float Header()
        {
            var lines = cols.Select((c, i) => Wrap(c.Title, hf, widths[i] - 2 * pad)).ToList();
            var h = lines.Max(l => l.Count) * lineH + 2 * pad;
            EnsureSpace(h + lineH + 2 * pad);
            using var bg = Fill(Accent);
            using var white = Fill(SKColors.White);
            C.DrawRect(Margin, _y, ContentWidth, h, bg);
            var x = Margin;
            for (var i = 0; i < cols.Count; i++)
            {
                var ly = _y + pad + fontSize;
                foreach (var l in lines[i])
                {
                    if (cols[i].AlignRight) C.DrawText(l, x + widths[i] - pad, ly, SKTextAlign.Right, hf, white);
                    else C.DrawText(l, x + pad, ly, SKTextAlign.Left, hf, white);
                    ly += lineH;
                }
                x += widths[i];
            }
            _y += h;
            return h;
        }

        void Row(string?[] cells, bool zebra, bool bold)
        {
            var font = bold ? tf : cf;
            var lines = cols.Select((c, i) => Wrap(i < cells.Length ? cells[i] ?? "" : "", font, widths[i] - 2 * pad)).ToList();
            var h = lines.Max(l => l.Count) * lineH + 2 * pad;
            if (_c == null || _y + h > Bottom) { NewPage(); Header(); }
            using var ink = Fill(Ink);
            using var border = Stroke(Grid, 0.5f);
            if (zebra || bold)
            {
                using var bg = Fill(bold ? AccentLight : Zebra);
                C.DrawRect(Margin, _y, ContentWidth, h, bg);
            }
            var x = Margin;
            for (var i = 0; i < cols.Count; i++)
            {
                C.DrawRect(x, _y, widths[i], h, border);
                var ly = _y + pad + fontSize;
                foreach (var l in lines[i])
                {
                    if (cols[i].AlignRight) C.DrawText(l, x + widths[i] - pad, ly, SKTextAlign.Right, font, ink);
                    else C.DrawText(l, x + pad, ly, SKTextAlign.Left, font, ink);
                    ly += lineH;
                }
                x += widths[i];
            }
            _y += h;
        }

        Header();
        if (rowList.Count == 0 && emptyText != null)
        {
            Row([emptyText], false, false);
        }
        for (var i = 0; i < rowList.Count; i++) Row(rowList[i], i % 2 == 1, false);
        if (totals != null && rowList.Count > 0) Row(totals, false, true);
        _y += 8;
    }

    // ---------- images ----------

    /// <summary>Photos two per row with captions underneath.</summary>
    public void ImageGrid(IEnumerable<(string Path, string Caption)> images, int perRow = 2, float imageHeight = 170)
    {
        var list = images.ToList();
        const float gap = 10;
        var w = (ContentWidth - gap * (perRow - 1)) / perRow;
        using var cf = Font(7.5f);
        using var ink = Fill(Muted);
        using var border = Stroke(Grid);
        for (var i = 0; i < list.Count; i += perRow)
        {
            var row = list.Skip(i).Take(perRow).ToList();
            var capLines = row.Max(r => Math.Min(3, Wrap(r.Caption, cf, w).Count));
            var h = imageHeight + capLines * 10 + 10;
            EnsureSpace(h);
            for (var j = 0; j < row.Count; j++)
            {
                var x = Margin + j * (w + gap);
                var rect = new SKRect(x, _y, x + w, _y + imageHeight);
                using var img = ImageTools.LoadForPdf(row[j].Path, 1200);
                if (img != null) C.DrawImage(img, Fit(img.Width, img.Height, rect), new SKSamplingOptions(SKFilterMode.Linear));
                else
                {
                    using var tf = Font(9);
                    C.DrawText("(image not available on this device)", rect.MidX, rect.MidY, SKTextAlign.Center, tf, ink);
                }
                C.DrawRect(rect, border);
                var ly = _y + imageHeight + 10;
                foreach (var l in Wrap(row[j].Caption, cf, w).Take(3))
                {
                    C.DrawText(l, x, ly, SKTextAlign.Left, cf, ink);
                    ly += 10;
                }
            }
            _y += h;
        }
    }

    /// <summary>A single image across the content width (note images).</summary>
    public void Image(string path, float maxHeight = 320, string? caption = null)
    {
        using var img = ImageTools.LoadForPdf(path, 1600);
        if (img == null) { Paragraph($"[image missing: {Path.GetFileName(path)}]", 8, Muted); return; }
        var w = ContentWidth;
        var h = Math.Min(maxHeight, w * img.Height / img.Width);
        EnsureSpace(h + 16);
        var rect = Fit(img.Width, img.Height, new SKRect(Margin, _y, Margin + w, _y + h));
        C.DrawImage(img, rect, new SKSamplingOptions(SKFilterMode.Linear));
        _y += h + 4;
        if (!string.IsNullOrWhiteSpace(caption)) Text(caption, 8, italic: true, color: Muted, align: TextAlign.Center);
        else _y += 4;
    }

    /// <summary>One image filling a whole page with no header (scanned documents).</summary>
    public void FullPageImage(string path, float margin = 18)
    {
        var keep = ShowHeader;
        ShowHeader = false;
        if (_c != null) EndPage();
        _c = _doc.BeginPage(PageWidth, PageHeight);
        PageNumber++;
        ShowHeader = keep;
        using var img = ImageTools.LoadForPdf(path, 2200, 82);
        if (img != null)
            _c.DrawImage(img, Fit(img.Width, img.Height, new SKRect(margin, margin, PageWidth - margin, PageHeight - margin)),
                new SKSamplingOptions(SKFilterMode.Linear));
        EndPage();
    }

    public void Placeholder(string text, float height = 40)
    {
        EnsureSpace(height + 6);
        using var border = new SKPaint { Color = Grid, Style = SKPaintStyle.Stroke, StrokeWidth = 0.8f, PathEffect = SKPathEffect.CreateDash([4, 3], 0) };
        C.DrawRoundRect(Margin, _y, ContentWidth, height, 4, 4, border);
        using var f = Font(9);
        using var p = Fill(Muted);
        C.DrawText(text, Margin + ContentWidth / 2, _y + height / 2 + 3, SKTextAlign.Center, f, p);
        _y += height + 6;
    }

    /// <summary>Signature boxes at the end of a report.</summary>
    public void Signatures(params (string Role, string Name)[] people)
    {
        const float h = 58, gap = 12;
        EnsureSpace(h + 10);
        Space(6);
        var w = (ContentWidth - gap * (people.Length - 1)) / people.Length;
        using var border = Stroke(Grid);
        using var lf = Font(8, bold: true);
        using var nf = Font(8.5f);
        using var ink = Fill(Ink);
        using var muted = Fill(Muted);
        for (var i = 0; i < people.Length; i++)
        {
            var x = Margin + i * (w + gap);
            C.DrawRect(x, _y, w, h, border);
            C.DrawText(people[i].Role.ToUpperInvariant(), x + 6, _y + 11, SKTextAlign.Left, lf, muted);
            C.DrawLine(x + 6, _y + h - 18, x + w - 6, _y + h - 18, border);
            C.DrawText(people[i].Name, x + 6, _y + h - 6, SKTextAlign.Left, nf, ink);
        }
        _y += h + 6;
    }

    private static SKRect Fit(float w, float h, SKRect box)
    {
        var s = Math.Min(box.Width / w, box.Height / h);
        var dw = w * s;
        var dh = h * s;
        var x = box.Left + (box.Width - dw) / 2;
        var y = box.Top + (box.Height - dh) / 2;
        return new SKRect(x, y, x + dw, y + dh);
    }

    public void Dispose()
    {
        if (_c == null && PageNumber == 0) NewPage();
        if (_c != null) EndPage();
        _doc.Close();
        _doc.Dispose();
    }
}
