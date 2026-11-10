namespace SiteNotes.App.Services;

/// <summary>Maps the note editor's logical font names to fonts that exist on each platform.</summary>
public static class FontCatalog
{
    public static readonly string[] Names = ["Default", "Serif", "Mono", "Casual", "Cursive", "Condensed"];

    public static readonly double[] Sizes = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 36];

    public static readonly (string Name, string Hex)[] Colors =
    [
        ("Auto", ""), ("Red", "#B91C1C"), ("Orange", "#C2410C"), ("Green", "#15803D"), ("Blue", "#1D4ED8"),
        ("Purple", "#7E22CE"), ("Grey", "#6B7280"), ("Brown", "#92400E"),
    ];

    public static readonly (string Name, string Hex)[] Highlights =
    [
        ("None", ""), ("Yellow", "#FEF08A"), ("Green", "#BBF7D0"), ("Blue", "#BFDBFE"), ("Pink", "#FBCFE8"), ("Orange", "#FED7AA"),
    ];

    public static string? Resolve(string logical)
    {
        var windows = DeviceInfo.Platform == DevicePlatform.WinUI;
        return logical switch
        {
            "Serif" => windows ? "Times New Roman" : "serif",
            "Mono" => windows ? "Consolas" : "monospace",
            "Casual" => windows ? "Comic Sans MS" : "casual",
            "Cursive" => windows ? "Segoe Script" : "cursive",
            "Condensed" => windows ? "Bahnschrift Condensed" : "sans-serif-condensed",
            _ => null, // platform default
        };
    }
}
