using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using ConstructDpr.Client;
using ConstructDpr.Core;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.App;

public enum DataMode { Local, Server }

/// <summary>Settings kept in the data folder.</summary>
public sealed class AppSettings
{
    public DataMode Mode { get; set; } = DataMode.Local;
    /// <summary>Name on reports in the offline (this PC only) mode.</summary>
    public string UserName { get; set; } = "planning.engineer";
    public int? LocalProjectId { get; set; }
}

/// <summary>The sign-in session for server mode, encrypted for the current Windows user (DPAPI).</summary>
public sealed class ProtectedFileStore(string path) : ISessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConstructDpr.Session");

    public Task<string?> LoadAsync()
    {
        try
        {
            if (!File.Exists(path)) return Task.FromResult<string?>(null);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)));
        }
        catch (CryptographicException) { return Task.FromResult<string?>(null); }
    }

    public Task SaveAsync(string? value)
    {
        if (value is null) File.Delete(path);
        else File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        return Task.CompletedTask;
    }
}

/// <summary>Offline mode: changes are announced in-process so other open screens refresh.</summary>
public sealed class LocalLive : ILiveUpdates
{
    public event Action<LiveChange>? Changed;
    public event Action<string>? StatusChanged;
    public string Status => "local";
    public Task WatchProjectAsync(int projectId) => Task.CompletedTask;
    public void Raise(LiveChange c) => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke(c));
    internal void Touch() => StatusChanged?.Invoke(Status);
}

/// <summary>
/// The Windows app works in one of two modes, chosen in Settings:
/// <list type="bullet">
/// <item>Local: everything in one SQLite file on this PC, no account, works without internet.</item>
/// <item>Server: signs in to the CONSTRUCT DPR server and stays in live sync with the web and Android apps.</item>
/// </list>
/// Every screen talks to <see cref="Api"/> only, so both modes behave identically.
/// </summary>
public static class AppState
{
    public static string DataFolder { get; private set; } = "";
    public static string DbPath => Path.Combine(DataFolder, "construct-dpr.db");
    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public static AppSettings Settings { get; private set; } = new();
    public static IConstructDprApi Api { get; private set; } = null!;
    public static ILiveUpdates Live { get; private set; } = new LocalLive();
    public static AuthSession Session { get; private set; } = null!;
    public static UserInfo Me { get; private set; } = new("local", "", "");
    public static AppFeatures Features { get; private set; } = new(true, false, "local");
    public static List<ProjectSummary> Projects { get; private set; } = new();
    public static ProjectSummary? Project { get; private set; }
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public static bool IsServer => Settings.Mode == DataMode.Server;
    public static int Pid => Project?.Id ?? 0;
    public static ProjectRole Role => Project?.MyRole ?? ProjectRole.Auditor;
    public static bool Can(Module m, Act a) => Project is not null && Permissions.Can(Role, m, a);

    /// <summary>Project switched, signed in/out, settings changed: header and screens reload.</summary>
    public static event Action? Changed;
    /// <summary>A change was committed in the open project (here or on another device).</summary>
    public static event Action<LiveChange>? LiveChanged;

    public static void NotifyChanged() => Changed?.Invoke();

    public static void LoadSettings(string dataFolder)
    {
        DataFolder = dataFolder;
        Directory.CreateDirectory(dataFolder);
        if (File.Exists(SettingsPath))
        {
            try { Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json.Options) ?? new(); }
            catch (JsonException) { Settings = new(); }
        }
        Session = new AuthSession(new ProtectedFileStore(Path.Combine(dataFolder, "session.bin")));
    }

    public static void SaveSettings() => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Settings, Json.Options));

    /// <summary>Connects in the chosen mode. Returns false when server mode needs a sign-in first.</summary>
    public static async Task<bool> StartAsync()
    {
        if (Live is LiveClient old) await old.StopAsync();
        if (Settings.Mode == DataMode.Local)
        {
            var options = AppDbContext.Options(DbProvider.Sqlite, $"Data Source={DbPath}");
            using (var db = new AppDbContext(options)) db.EnsureSchema();
            var live = new LocalLive();
            Live = live;
            Me = new UserInfo("local", "", Settings.UserName);
            Api = new LocalApi(() => new AppDbContext(options), new Caller("local", "", Settings.UserName, IsLocalOwner: true),
                new AppFeatures(true, false, "this PC"), live.Raise);
            Features = await Api.FeaturesAsync();
            Projects = await Api.ProjectsAsync();
            if (Projects.Count == 0)
            {
                await Api.CreateDemoProjectAsync();
                Projects = await Api.ProjectsAsync();
            }
            Project = Projects.FirstOrDefault(p => p.Id == Settings.LocalProjectId) ?? Projects.FirstOrDefault();
        }
        else
        {
            await Session.LoadAsync();
            if (!Session.IsSignedIn) return false;
            var live = new LiveClient(Session);
            Live = live;
            Api = new RemoteApi(Session);
            Me = await Api.MeAsync();
            Features = await Api.FeaturesAsync();
            Projects = await Api.ProjectsAsync();
            Project = Projects.FirstOrDefault(p => p.Id == Session.ProjectId) ?? Projects.FirstOrDefault();
        }
        Live.Changed += c =>
        {
            if (c.ProjectId != Pid) return;
            Application.Current?.Dispatcher.BeginInvoke(() => LiveChanged?.Invoke(c));
        };
        Live.StatusChanged += _ => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        if (Project is not null) await Live.WatchProjectAsync(Project.Id);
        Changed?.Invoke();
        return true;
    }

    public static async Task OpenProjectAsync(int projectId)
    {
        Project = Projects.FirstOrDefault(p => p.Id == projectId);
        if (IsServer) await Session.SetProjectAsync(Project?.Id);
        else { Settings.LocalProjectId = Project?.Id; SaveSettings(); }
        if (Project is not null) await Live.WatchProjectAsync(Project.Id);
        Changed?.Invoke();
    }

    public static async Task RefreshProjectsAsync()
    {
        Projects = await Api.ProjectsAsync();
        Project = Projects.FirstOrDefault(p => p.Id == Pid) ?? Projects.FirstOrDefault();
        Changed?.Invoke();
    }

    public static async Task SwitchModeAsync(DataMode mode)
    {
        Settings.Mode = mode;
        SaveSettings();
        Project = null;
        Projects = new();
        await StartAsync();
    }

    public static async Task SignOutAsync()
    {
        if (Live is LiveClient lc) await lc.StopAsync();
        await Session.SignOutAsync();
        (Project, Projects) = (null, new());
        Changed?.Invoke();
    }

    /// <summary>Offline mode only: deletes this PC's database and loads the demo project again.</summary>
    public static async Task ResetLocalToDemoAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        Settings.LocalProjectId = null;
        SaveSettings();
        await StartAsync();
    }
}
