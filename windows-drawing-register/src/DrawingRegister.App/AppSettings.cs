using System.Text.Json;
using DrawingRegister.Core;

namespace DrawingRegister.App;

/// <summary>Per-user settings in %APPDATA%\DrawingRegister\settings.json.</summary>
public sealed class AppSettings
{
    public string ConnectionString { get; set; } = SqlDrawingStore.DefaultConnection;

    /// <summary>Folder (local or \\server\share) where drawing files are kept, Plant\Unit\Type.</summary>
    public string LibraryFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Drawing Register Library");

    public string ProjectName { get; set; } = "My Project";
    public bool ShowSuperseded { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DrawingRegister");
    static string FilePath => Path.Combine(Folder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
