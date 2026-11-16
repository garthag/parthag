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
    /// <summary>Second report header line: address / project / contract reference.</summary>
    public string CompanyAddress { get; set; } = "";
    /// <summary>Logo copied into the settings folder (PNG / JPG); printed on reports, Excel and the dashboard.</summary>
    public string? LogoPath { get; set; }
    public static string Folder => Dir;
    /// <summary>SystemAware (default), PerMonitorV2 or DpiUnaware (useful on remote desktop / Citrix).</summary>
    public string HighDpiMode { get; set; } = "SystemAware";

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BSLProjectControl");
    static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>
    /// Per-user settings; on first start falls back to "defaults.json" next to the program, which the installer
    /// writes with the SQL Server it installed (LocalDB or .\SQLEXPRESS).
    /// </summary>
    public static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { /* corrupt file → defaults */ }
        try
        {
            var machine = Path.Combine(AppContext.BaseDirectory, "defaults.json");
            if (File.Exists(machine)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(machine)) ?? new();
        }
        catch { /* ignore */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
