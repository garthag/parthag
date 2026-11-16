using System.IO;
using System.Text.Json;
using Eddms.Core;

namespace Eddms.Wpf;

/// <summary>Remembered between runs (the password is never saved).</summary>
public sealed class AppSettings
{
    public string LoginName { get; set; } = "";
    public string ConnectionString { get; set; } = EddmsDatabase.DefaultConnectionString;
    public string DownloadRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EDMS Drawings");
    public bool DownloadFiles { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EddmsDrawingRegister", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical */ }
    }
}
