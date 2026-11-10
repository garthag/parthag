using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiteNotes.Core.Models;

public enum BlockType
{
    Paragraph, Heading1, Heading2, Heading3, Bullet, Numbered, Arrow, Checklist, Quote, Divider,
    Image, Video, Scan
}

public enum TextAlign { Left, Center, Right }

/// <summary>Character formatting for one block.</summary>
public class TextStyle
{
    /// <summary>Logical font: Default, Serif, Mono, Casual, Cursive, Condensed.</summary>
    public string FontFamily { get; set; } = "Default";
    /// <summary>Font size in device-independent units; 0 = automatic size for the block type.</summary>
    public double FontSize { get; set; }
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public bool Strikethrough { get; set; }
    public string Color { get; set; } = "";
    public string Highlight { get; set; } = "";
    public TextAlign Align { get; set; } = TextAlign.Left;

    public TextStyle Clone() => (TextStyle)MemberwiseClone();
}

/// <summary>
/// One block of a note: a paragraph, heading, list item, checklist item, divider or media.
/// A note is an ordered list of blocks, which keeps editing simple on phones and makes
/// the PDF export exact.
/// </summary>
public class NoteBlock
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public BlockType Type { get; set; } = BlockType.Paragraph;
    public string Text { get; set; } = "";
    public bool Checked { get; set; }
    public int Indent { get; set; }
    public TextStyle Style { get; set; } = new();
    /// <summary>MediaItem id (image / video) or ScanDocument id.</summary>
    public string MediaId { get; set; } = "";

    [JsonIgnore] public bool IsMedia => Type is BlockType.Image or BlockType.Video or BlockType.Scan;
    [JsonIgnore] public bool IsListItem => Type is BlockType.Bullet or BlockType.Numbered or BlockType.Arrow or BlockType.Checklist;

    [JsonIgnore] public double EffectiveSize => Style.FontSize > 0 ? Style.FontSize : DefaultSize(Type);

    /// <summary>Default font size for a block type.</summary>
    public static double DefaultSize(BlockType t) => t switch
    {
        BlockType.Heading1 => 24,
        BlockType.Heading2 => 20,
        BlockType.Heading3 => 17,
        _ => 15
    };
}

public static class NoteDocument
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<NoteBlock> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<NoteBlock>>(json, Json) ?? []; }
        catch (JsonException) { return [new NoteBlock { Text = json }]; }
    }

    public static string Serialize(IEnumerable<NoteBlock> blocks) => JsonSerializer.Serialize(blocks, Json);

    /// <summary>The marker drawn before a block (bullet, number, arrow, checkbox).</summary>
    public static string Marker(IReadOnlyList<NoteBlock> blocks, int index)
    {
        var b = blocks[index];
        return b.Type switch
        {
            BlockType.Bullet => b.Indent % 2 == 0 ? "•" : "◦",
            BlockType.Arrow => "➜",
            BlockType.Checklist => b.Checked ? "☑" : "☐",
            BlockType.Numbered => NumberFor(blocks, index) + ".",
            _ => ""
        };
    }

    /// <summary>Numbering restarts after any non-numbered block at the same or lower indent.</summary>
    public static int NumberFor(IReadOnlyList<NoteBlock> blocks, int index)
    {
        var indent = blocks[index].Indent;
        var n = 1;
        for (var i = index - 1; i >= 0; i--)
        {
            var b = blocks[i];
            if (b.Indent > indent) continue;
            if (b.Indent < indent || b.Type != BlockType.Numbered) break;
            n++;
        }
        return n;
    }

    public static string ToPlainText(IReadOnlyList<NoteBlock> blocks)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.Type == BlockType.Divider) { sb.AppendLine("———"); continue; }
            if (b.IsMedia) { if (!string.IsNullOrWhiteSpace(b.Text)) sb.AppendLine($"[{b.Type}] {b.Text}"); continue; }
            var marker = Marker(blocks, i);
            sb.Append(new string(' ', b.Indent * 2));
            if (marker.Length > 0) sb.Append(marker).Append(' ');
            sb.AppendLine(b.Text);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Checklist progress, e.g. (3, 5) = 3 of 5 done.</summary>
    public static (int Done, int Total) ChecklistProgress(IEnumerable<NoteBlock> blocks)
    {
        var items = blocks.Where(b => b.Type == BlockType.Checklist).ToList();
        return (items.Count(b => b.Checked), items.Count);
    }
}
