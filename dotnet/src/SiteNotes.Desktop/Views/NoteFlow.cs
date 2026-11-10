using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SiteNotes.Core.Models;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>
/// Converts the note model (blocks) to a WPF FlowDocument for the rich text editor, and back.
/// The note type of each paragraph is read from what you see (heading size, ☐ / ➜ prefix, quote bar, list),
/// so editing with Enter / Backspace / Tab in the RichTextBox always round-trips correctly.
/// </summary>
public static class NoteFlow
{
    public const string Unchecked = "☐ ", Checked = "☑ ", Arrow = "➜ ";
    public static readonly Brush QuoteBar = Theme.Primary;

    private static readonly (string Logical, string Windows)[] Fonts =
    [
        ("Serif", "Times New Roman"), ("Mono", "Consolas"), ("Casual", "Comic Sans MS"), ("Cursive", "Segoe Script"), ("Condensed", "Bahnschrift Condensed"),
    ];

    public static FontFamily Font(string logical) => new(Fonts.FirstOrDefault(f => f.Logical == logical).Windows ?? "Segoe UI");
    public static string Logical(FontFamily f) => Fonts.FirstOrDefault(x => f.Source.Contains(x.Windows, StringComparison.OrdinalIgnoreCase)).Logical ?? "Default";

    public static double HeadingSize(BlockType t) => NoteBlock.DefaultSize(t);

    // ------------------------------------------------------------------ model → document

    public static FlowDocument ToFlow(IReadOnlyList<NoteBlock> blocks, Func<string, MediaItem?> media)
    {
        var doc = new FlowDocument { FontFamily = new FontFamily("Segoe UI"), FontSize = 15, PagePadding = new Thickness(18), LineHeight = 1 };
        List? list = null;
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Type is BlockType.Bullet or BlockType.Numbered)
            {
                var marker = b.Type == BlockType.Bullet ? (b.Indent % 2 == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle) : TextMarkerStyle.Decimal;
                if (list == null || list.MarkerStyle != marker || (int)Math.Round(list.Margin.Left / 24) != b.Indent)
                {
                    list = new List { MarkerStyle = marker, Margin = new Thickness(b.Indent * 24, 0, 0, 4), Padding = new Thickness(24, 0, 0, 0) };
                    doc.Blocks.Add(list);
                }
                var p = Para(b, "");
                p.Margin = new Thickness(0);
                list.ListItems.Add(new ListItem(p));
                continue;
            }
            list = null;
            doc.Blocks.Add(b.Type switch
            {
                BlockType.Divider => new BlockUIContainer(new Separator { Margin = new Thickness(0, 6, 0, 6) }) { Tag = b },
                BlockType.Image or BlockType.Scan or BlockType.Video => MediaBlock(b, media(b.MediaId)),
                BlockType.Checklist => Para(b, b.Checked ? Checked : Unchecked),
                BlockType.Arrow => Para(b, Arrow),
                _ => Para(b, ""),
            });
        }
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        return doc;
    }

    private static Paragraph Para(NoteBlock b, string prefix)
    {
        var p = new Paragraph(new Run(prefix + b.Text)) { Margin = new Thickness(b.Indent * 24, 2, 0, 2) };
        ApplyStyle(p, b);
        return p;
    }

    /// <summary>Applies a block's look to a paragraph (and clears per-word formatting so the block stays uniform).</summary>
    public static void ApplyStyle(Paragraph p, NoteBlock b)
    {
        foreach (var inline in p.Inlines)
        {
            inline.ClearValue(TextElement.FontSizeProperty);
            inline.ClearValue(TextElement.FontWeightProperty);
            inline.ClearValue(TextElement.FontStyleProperty);
            inline.ClearValue(TextElement.ForegroundProperty);
            inline.ClearValue(TextElement.BackgroundProperty);
            inline.ClearValue(TextElement.FontFamilyProperty);
            inline.ClearValue(Inline.TextDecorationsProperty);
        }
        var s = b.Style;
        var heading = b.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3;
        p.FontSize = b.EffectiveSize;
        p.FontWeight = s.Bold || heading ? FontWeights.SemiBold : FontWeights.Normal;
        p.FontStyle = s.Italic || b.Type == BlockType.Quote ? FontStyles.Italic : FontStyles.Normal;
        p.FontFamily = Font(s.FontFamily);
        p.TextAlignment = s.Align switch { TextAlign.Center => TextAlignment.Center, TextAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
        var done = b.Type == BlockType.Checklist && b.Checked;
        p.Foreground = done ? Theme.Muted : Brush(s.Color) ?? (b.Type == BlockType.Quote ? Theme.Muted : Brushes.Black);
        p.Background = Brush(s.Highlight) ?? Brushes.Transparent;
        var deco = new TextDecorationCollection();
        if (s.Underline) deco.Add(TextDecorations.Underline);
        if (s.Strikethrough || done) deco.Add(TextDecorations.Strikethrough);
        p.TextDecorations = deco;
        if (b.Type == BlockType.Quote) { p.BorderBrush = QuoteBar; p.BorderThickness = new Thickness(3, 0, 0, 0); p.Padding = new Thickness(10, 2, 0, 2); }
        else { p.BorderThickness = new Thickness(0); p.Padding = new Thickness(0); }
        if (heading) p.Margin = new Thickness(p.Margin.Left, 10, 0, 4);
    }

    private static Brush? Brush(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); } catch { return null; }
    }

    private static BlockUIContainer MediaBlock(NoteBlock b, MediaItem? m)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
        var path = m == null ? "" : Desk.Db.MediaPath(m.FileName);
        if (b.Type == BlockType.Video)
        {
            var btn = new Button { Content = $"▶  {(b.Text != "" ? b.Text : m?.Caption ?? "Video")}", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 6, 12, 6) };
            btn.Click += (_, _) => { if (System.IO.File.Exists(path)) Dialogs.Open(path); };
            panel.Children.Add(btn);
        }
        else
        {
            var img = new Image { Source = System.IO.File.Exists(path) ? DprEditorView.Thumb(path, 900) : null, MaxHeight = 320, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand };
            img.MouseLeftButtonDown += async (_, e) =>
            {
                if (e.ClickCount != 2) return;
                if (b.Type == BlockType.Scan && await Desk.Db.GetAsync<ScanDocument>(b.MediaId) is { } d) Dialogs.Open(Desk.Db.MediaPath(d.PdfFileName));
                else if (System.IO.File.Exists(path)) Dialogs.Open(path);
            };
            panel.Children.Add(img);
            if (img.Source == null) panel.Children.Add(W.Muted(m == null ? "(missing picture)" : "(picture is downloading from the server…)"));
        }
        var caption = new TextBox { Text = b.Text != "" ? b.Text : m?.Caption ?? "", FontSize = 12, FontStyle = FontStyles.Italic, BorderThickness = new Thickness(0, 0, 0, 1), MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 240 };
        caption.TextChanged += (_, _) => b.Text = caption.Text;
        panel.Children.Add(caption);
        if (b.Type == BlockType.Scan) panel.Children.Add(W.Muted("📄 Scanned document – double-click to open the PDF"));
        return new BlockUIContainer(panel) { Tag = b };
    }

    // ------------------------------------------------------------------ document → model

    public static List<NoteBlock> FromFlow(FlowDocument doc)
    {
        var result = new List<NoteBlock>();
        foreach (var block in doc.Blocks) Read(block, 0, result);
        while (result.Count > 1 && result[^1] is { Type: BlockType.Paragraph, Text: "" }) result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void Read(Block block, int indent, List<NoteBlock> into)
    {
        switch (block)
        {
            case BlockUIContainer { Tag: NoteBlock nb }:
                into.Add(nb);
                break;
            case List list:
                var type = list.MarkerStyle is TextMarkerStyle.Disc or TextMarkerStyle.Circle or TextMarkerStyle.Square or TextMarkerStyle.Box or TextMarkerStyle.None
                    ? BlockType.Bullet : BlockType.Numbered;
                var level = indent + (int)Math.Round(list.Margin.Left / 24);
                foreach (var item in list.ListItems)
                    foreach (var child in item.Blocks)
                    {
                        if (child is Paragraph p)
                        {
                            var b = FromParagraph(p, level);
                            b.Type = type;
                            into.Add(b);
                        }
                        else Read(child, level + 1, into);
                    }
                break;
            case Paragraph p:
                into.Add(FromParagraph(p, indent));
                break;
            case Section s:
                foreach (var child in s.Blocks) Read(child, indent, into);
                break;
            case Table t:
                foreach (var g in t.RowGroups) foreach (var r in g.Rows) foreach (var c in r.Cells) foreach (var cb in c.Blocks) Read(cb, indent, into);
                break;
        }
    }

    /// <summary>The paragraph's own text (a TextRange would also include the list bullet "•\t").</summary>
    public static string InlineText(InlineCollection inlines)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var i in inlines)
        {
            switch (i)
            {
                case Run r: sb.Append(r.Text); break;
                case LineBreak: sb.Append('\n'); break;
                case Span s: sb.Append(InlineText(s.Inlines)); break;
            }
        }
        return sb.ToString();
    }

    private static NoteBlock FromParagraph(Paragraph p, int indent)
    {
        var text = InlineText(p.Inlines).TrimEnd('\r', '\n');
        var first = p.Inlines.FirstInline as Run;
        double size = first?.ReadLocalValue(TextElement.FontSizeProperty) is double fs ? fs : p.FontSize;
        var weight = first?.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight fw ? fw : p.FontWeight;
        var style = first?.ReadLocalValue(TextElement.FontStyleProperty) is FontStyle fst ? fst : p.FontStyle;
        var bold = weight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();
        var b = new NoteBlock { Indent = indent + (int)Math.Round(p.Margin.Left / 24) };

        if (text.StartsWith(Unchecked) || text.StartsWith(Checked))
        {
            b.Type = BlockType.Checklist;
            b.Checked = text.StartsWith(Checked);
            text = text[2..];
        }
        else if (text.StartsWith(Arrow)) { b.Type = BlockType.Arrow; text = text[2..]; }
        else if (p.BorderThickness.Left >= 2) b.Type = BlockType.Quote;
        else if (bold && size >= 22) b.Type = BlockType.Heading1;
        else if (bold && size >= 19) b.Type = BlockType.Heading2;
        else if (bold && size >= 16.5) b.Type = BlockType.Heading3;

        b.Text = text;
        var s = b.Style;
        s.FontSize = Math.Abs(size - NoteBlock.DefaultSize(b.Type)) < 0.5 ? 0 : Math.Round(size, 1);
        var heading = b.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3;
        s.Bold = bold && !heading;
        s.Italic = style == FontStyles.Italic && b.Type != BlockType.Quote;
        var family = first?.ReadLocalValue(TextElement.FontFamilyProperty) as FontFamily ?? p.FontFamily;
        s.FontFamily = Logical(family);
        var decos = (first?.ReadLocalValue(Inline.TextDecorationsProperty) as TextDecorationCollection) ?? p.TextDecorations;
        s.Underline = decos?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
        var strike = decos?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        s.Strikethrough = strike && !(b.Type == BlockType.Checklist && b.Checked);
        var fg = (first?.ReadLocalValue(TextElement.ForegroundProperty) as SolidColorBrush) ?? p.Foreground as SolidColorBrush;
        s.Color = fg == null || fg.Color == Colors.Black || fg.Color == Theme.Muted.Color ? "" : $"#{fg.Color.R:X2}{fg.Color.G:X2}{fg.Color.B:X2}";
        var bg = (first?.ReadLocalValue(TextElement.BackgroundProperty) as SolidColorBrush) ?? p.Background as SolidColorBrush;
        s.Highlight = bg == null || bg.Color.A == 0 ? "" : $"#{bg.Color.R:X2}{bg.Color.G:X2}{bg.Color.B:X2}";
        s.Align = p.TextAlignment switch { TextAlignment.Center => TextAlign.Center, TextAlignment.Right => TextAlign.Right, _ => TextAlign.Left };
        return b;
    }
}
