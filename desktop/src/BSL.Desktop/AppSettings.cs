using System.Text.Json;

namespace BSL.Desktop;

/// <summary>User settings stored in %AppData%\BSLProjectControl\settings.json.</summary>
public sealed class AppSettings
{
    public string ConnectionString { get; set; } =
        @"Server=(localdb)\MSSQLLocalDB;Database=BSL_ProjectControl;Trusted_Connection=True;TrustServerCertificate=True";
    public string CompanyName { get; set; } = "BSL BF#3 Revamp – Project Control";
    public string PreparedBy { get; set; } = Environment.UserName;
    public string? LastFolder { get; set; }

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSLProjectControl");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { /* corrupt file → defaults */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
