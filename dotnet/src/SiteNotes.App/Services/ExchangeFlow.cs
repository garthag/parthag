using SiteNotes.Core;
using SiteNotes.App.UI;
using SiteNotes.Core.Models;

namespace SiteNotes.App.Services;

/// <summary>Excel round trip on the phone/tablet: export the rows shown, edit in any spreadsheet app, import the file back.</summary>
public static class ExchangeFlow
{
    public static ExchangeSheets Sheets => new(AppServices.Db);

    public static async Task ExportAsync<T>(Page page, ExchangeSheet<T> sheet, IEnumerable<T> rows) where T : SyncEntity, new()
    {
        var list = rows.ToList();
        var path = await FileExport.CreateAsync($"{sheet.Name}_edit_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
            s => DataExchange.Export(s, sheet, list, $"Exported {DateTime.Now:dd-MMM-yyyy HH:mm} · {AppServices.CurrentProject?.Name}"));
        await FileExport.OfferAsync(page, path);
    }

    /// <summary>Picks a file (.xlsx / .csv); null when cancelled.</summary>
    public static async Task<FileResult?> PickSheetAsync(string title)
    {
        var types = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.Android] = ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv", "text/comma-separated-values", "application/vnd.ms-excel", "application/octet-stream"],
            [DevicePlatform.WinUI] = [".xlsx", ".csv"],
        });
        return await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title, FileTypes = types });
    }

    /// <summary>Picks the edited file, shows what would change and saves after confirmation. True when something was saved.</summary>
    public static async Task<bool> ImportAsync<T>(Page page, ExchangeSheet<T> sheet, bool projectScoped = true) where T : SyncEntity, new()
    {
        var file = await PickSheetAsync($"Import {sheet.Name}");
        if (file == null) return false;
        var existing = projectScoped ? await AppServices.Db.ListForProjectAsync<T>(AppServices.ProjectId) : await AppServices.Db.ListAsync<T>();
        var ms = new MemoryStream();
        await using (var src = await file.OpenReadAsync()) await src.CopyToAsync(ms);
        ms.Position = 0;
        var result = await Task.Run(() => DataExchange.Import(ms, file.FileName, sheet, existing));
        var warnings = result.Warnings.Count == 0 ? "" : "\n\n• " + string.Join("\n• ", result.Warnings.Take(8)) + (result.Warnings.Count > 8 ? $"\n… and {result.Warnings.Count - 8} more" : "");
        if (result.Added.Count + result.Updated.Count == 0)
        {
            await page.DisplayAlertAsync("Nothing to import", result.Summary + warnings, "OK");
            return false;
        }
        if (!await page.DisplayAlertAsync($"Import {sheet.Name}?", $"{result.Added.Count} new and {result.Updated.Count} changed row(s) will be saved ({result.Unchanged} unchanged). Nothing is deleted." + warnings, "Import", "Cancel"))
            return false;
        await Sheets.SaveAsync(sheet, result, projectScoped ? AppServices.ProjectId : "");
        await Ui.Toast($"{sheet.Name}: {result.Summary}");
        return true;
    }
}
