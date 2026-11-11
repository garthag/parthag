using System.IO;
using SiteNotes.Core;
using SiteNotes.Core.Models;

namespace SiteNotes.Desktop.Services;

/// <summary>Excel round trip for a module: export the rows shown, edit in Excel, import the file back.</summary>
public static class ExchangeUi
{
    public static ExchangeSheets Sheets => new(Desk.Db);

    public static async Task ExportAsync<T>(Func<Task<ExchangeSheet<T>>> sheet, IEnumerable<T> rows, string note = "") where T : SyncEntity, new()
    {
        var s = await sheet();
        var list = rows.ToList();
        await Dialogs.CreateAndOpenAsync($"{s.Name}_edit_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", o => DataExchange.Export(o, s, list, note));
        Desk.Say($"{list.Count} row(s) exported – edit in Excel, save, then use “Import Excel…”");
    }

    /// <summary>Asks for the edited file, shows what would change and saves after confirmation. Returns true when something was saved.</summary>
    public static async Task<bool> ImportAsync<T>(Func<Task<ExchangeSheet<T>>> sheet, string? file = null, bool projectScoped = true) where T : SyncEntity, new()
    {
        var s = await sheet();
        if (file == null)
        {
            if (Dialogs.Headless) return false;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = $"Import {s.Name}", InitialDirectory = Desk.ReportsFolder, Filter = "Excel or CSV|*.xlsx;*.xlsm;*.csv|All files|*.*" };
            if (dlg.ShowDialog() != true) return false;
            file = dlg.FileName;
        }
        var existing = projectScoped ? await Desk.Db.ListForProjectAsync<T>(Desk.ProjectId) : await Desk.Db.ListAsync<T>();
        ExchangeResult<T> result;
        try
        {
            var path = file;
            result = await Task.Run(() => { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return DataExchange.Import(fs, path, s, existing); });
        }
        catch (IOException ex) { Dialogs.Info($"The file could not be read ({ex.Message}). Close it in Excel and try again."); return false; }
        var warnings = result.Warnings.Count == 0 ? "" : "\n\nWarnings:\n• " + string.Join("\n• ", result.Warnings.Take(12)) + (result.Warnings.Count > 12 ? $"\n… and {result.Warnings.Count - 12} more" : "");
        if (result.Added.Count + result.Updated.Count == 0)
        {
            Dialogs.Info($"Nothing to import: {result.Summary}." + warnings);
            return false;
        }
        if (!Dialogs.Confirm($"{s.Name}: {result.Added.Count} new row(s) and {result.Updated.Count} changed row(s) will be saved ({result.Unchanged} unchanged). Nothing is deleted.{warnings}\n\nImport now?")) return false;
        await Sheets.SaveAsync(s, result, projectScoped ? Desk.ProjectId : "");
        Desk.Say($"{s.Name} imported: {result.Summary}");
        return true;
    }
}
