using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Services;

/// <summary>Loads and saves the report manager's choices per project (synced like any other data).</summary>
public static class ReportProfiles
{
    public static async Task<(ReportProfile Profile, ReportOptions Options)> LoadAsync()
    {
        var pid = AppServices.ProjectId;
        var p = (await AppServices.Db.ListForProjectAsync<ReportProfile>(pid)).OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault()
                ?? new ReportProfile { ProjectId = pid };
        var o = ReportOptions.FromJson(p.OptionsJson);
        if (p.OptionsJson == "" && AppServices.CurrentProject is { } proj)
        {
            o.Layout.CompanyName = proj.Contractor;
            o.Layout.HeaderLines = string.Join("\n", new[]
            {
                proj.Code != "" ? $"Project ref: {proj.Code}" : "",
                proj.Client != "" ? $"Client: {proj.Client}" : "",
            }.Where(x => x != ""));
        }
        return (p, o);
    }

    /// <summary>The saved options with logos resolved for this device: what every export uses.</summary>
    public static async Task<ReportOptions> CurrentAsync() => (await LoadAsync()).Options.Resolve(AppServices.Db.MediaPath);

    public static async Task SaveAsync(ReportProfile p, ReportOptions o)
    {
        p.ProjectId = AppServices.ProjectId;
        p.OptionsJson = o.ToJson();
        await AppServices.Db.SaveAsync(p);
    }

    /// <summary>Picks an image and stores it in the media folder (so it syncs); returns the file name.</summary>
    public static async Task<string?> PickLogoAsync()
    {
        var files = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1, MaximumWidth = 800, MaximumHeight = 800 });
        var f = files?.FirstOrDefault();
        if (f == null) return null;
        var name = "logo_" + Guid.NewGuid().ToString("N") + (Path.GetExtension(f.FileName) is { Length: > 0 } e ? e.ToLowerInvariant() : ".png");
        await using (var src = await f.OpenReadAsync())
        await using (var dst = File.Create(AppServices.Db.MediaPath(name)))
            await src.CopyToAsync(dst);
        // register as media so the file is uploaded to the sync server
        await AppServices.Db.SaveAsync(new MediaItem { ProjectId = AppServices.ProjectId, OwnerType = MediaOwner.Note, OwnerId = "logo", Kind = MediaKind.Document, FileName = name, Caption = "Report logo" });
        return name;
    }
}
