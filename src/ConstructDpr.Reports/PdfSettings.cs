using QuestPDF.Infrastructure;

namespace ConstructDpr.Reports;

internal static class PdfSettings
{
    /// <summary>
    /// QuestPDF is free under its Community licence for organisations below its revenue threshold
    /// (see desktop/README.md). Missing glyphs (e.g. a script typed by a user that the bundled font lacks)
    /// are drawn as placeholders instead of failing the whole report.
    /// </summary>
    public static void Apply()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }
}
