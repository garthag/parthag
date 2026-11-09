using System.IO;
using System.Text.Json;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Model;
using ConstructDpr.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.App;

/// <summary>Settings kept next to the database (user name and the last project opened).</summary>
public sealed class AppSettings
{
    public string UserName { get; set; } = "planning.engineer";
    public int? ProjectId { get; set; }
}

/// <summary>Single-user desktop state: one SQLite file, one open project.</summary>
public static class AppState
{
    public static string DataFolder { get; private set; } = "";
    public static string DbPath => Path.Combine(DataFolder, "construct-dpr.db");
    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public static AppDbContext Db { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = new();
    public static Project Project { get; private set; } = null!;
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public static DprService Dprs => new(Db, Settings.UserName);
    public static ScheduleService Schedule => new(Db);

    /// <summary>Raised after data changes that other screens show (approvals, schedule runs, imports).</summary>
    public static event Action? Changed;
    public static void NotifyChanged() => Changed?.Invoke();

    public static void Initialise(string dataFolder)
    {
        DataFolder = dataFolder;
        Directory.CreateDirectory(dataFolder);
        if (File.Exists(SettingsPath))
        {
            try { Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
            catch (JsonException) { Settings = new(); }
        }
        Db = AppDbContext.Open(DbPath);
        if (!Db.Projects.Any()) DemoData.Create(Db, Today);
        Project = Db.Projects.FirstOrDefault(p => p.Id == Settings.ProjectId) ?? Db.Projects.OrderBy(p => p.Id).First();
        Settings.ProjectId = Project.Id;
        SaveSettings();
    }

    public static void SaveSettings() => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));

    public static List<Activity> Activities() =>
        Db.Activities.Where(a => a.ProjectId == Project.Id).OrderBy(a => a.SortOrder).ThenBy(a => a.Code).ToList();

    public static List<Dependency> Dependencies() => Db.Dependencies.Where(d => d.ProjectId == Project.Id).ToList();

    /// <summary>Deletes everything and loads the demo project again.</summary>
    public static void ResetToDemo()
    {
        Db.Dispose();
        SqliteCleanup();
        File.Delete(DbPath);
        Db = AppDbContext.Open(DbPath);
        Project = DemoData.Create(Db, Today);
        Settings.ProjectId = Project.Id;
        SaveSettings();
        NotifyChanged();
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    public static void Reload() => Db.ChangeTracker.Clear();
}
