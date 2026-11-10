namespace SiteNotes.App.Services;

/// <summary>Writes generated PDF / Excel files and opens or shares them.
/// Windows: saved in Documents\SiteNotes Reports. Android: app storage, then the share sheet
/// (WhatsApp, email, Drive…).</summary>
public static class FileExport
{
    public static string ReportsFolder
    {
        get
        {
            var root = DeviceInfo.Platform == DevicePlatform.WinUI
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SiteNotes Reports")
                : Path.Combine(FileSystem.AppDataDirectory, "reports");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name.Replace(' ', '_').Trim('_');
    }

    /// <summary>Runs <paramref name="write"/> on a background thread into a new file and returns its path.</summary>
    public static async Task<string> CreateAsync(string fileName, Action<Stream> write)
    {
        var path = Path.Combine(ReportsFolder, SafeName(fileName));
        await Task.Run(() =>
        {
            using var fs = File.Create(path);
            write(fs);
        });
        return path;
    }

    public static async Task OpenAsync(string path)
    {
        try { await Launcher.Default.OpenAsync(new OpenFileRequest(Path.GetFileName(path), new ReadOnlyFile(path))); }
        catch { await ShareAsync(path); }
    }

    public static Task ShareAsync(string path) =>
        Share.Default.RequestAsync(new ShareFileRequest { Title = Path.GetFileName(path), File = new ShareFile(path) });

    /// <summary>Asks Open / Share and does it.</summary>
    public static async Task OfferAsync(Page page, string path)
    {
        var choice = await page.DisplayActionSheetAsync($"Saved {Path.GetFileName(path)}", "Close", null, "Open", "Share");
        if (choice == "Open") await OpenAsync(path);
        else if (choice == "Share") await ShareAsync(path);
    }
}
