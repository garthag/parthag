using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using ConstructDpr.Client;
using ConstructDpr.Core;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Security;
using Microsoft.Data.SqlClient;

namespace ConstructDpr.App;

/// <summary>Where the data lives.</summary>
public enum DataMode
{
    /// <summary>One SQLite file on this PC: offline, no accounts.</summary>
    Local,
    /// <summary>A shared Microsoft SQL Server database on the office network: accounts, roles, live refresh.</summary>
    Office,
    /// <summary>Through the CONSTRUCT DPR sync server (for PCs away from the office); sync with the Android phones.</summary>
    Server,
}

/// <summary>Settings kept in the data folder (no secrets; those are in DPAPI-protected files).</summary>
public sealed class AppSettings
{
    public DataMode Mode { get; set; } = DataMode.Local;
    /// <summary>Name on reports in the offline (this PC only) mode.</summary>
    public string UserName { get; set; } = "planning.engineer";
    public int? LocalProjectId { get; set; }
    public string OfficeServer { get; set; } = @".\SQLEXPRESS";
    public string OfficeDatabase { get; set; } = "ConstructDpr";
    public bool OfficeWindowsAuth { get; set; } = true;
    public string OfficeSqlUser { get; set; } = "";
    public int? OfficeProjectId { get; set; }
    /// <summary>Run the sync server inside this app so Android phones can connect (This PC or Office SQL Server mode).</summary>
    public bool ShareWithPhones { get; set; }
    public int PhonePort { get; set; } = 5080;
}

/// <summary>Secrets for office mode (SQL login password, remembered account), encrypted for this Windows user.</summary>
public sealed class OfficeSecrets
{
    public string? SqlPassword { get; set; }
    public string? UserId { get; set; }
}

/// <summary>Small text file encrypted for the current Windows user (DPAPI).</summary>
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
/// Office mode: every few seconds asks the shared SQL Server for the project's latest change number and refreshes the
/// screens when a colleague changed something. Own changes are announced immediately and do not trigger a second refresh.
/// </summary>
public sealed class OfficeLive : ILiveUpdates
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Func<int, Task<int>> _stamp;
    private int _projectId, _known;
    private bool _busy;
    private string _status = "connecting";
    private readonly string _ok;

    public OfficeLive(Func<int, Task<int>> stamp, string okStatus = "office")
    {
        _stamp = stamp;
        _ok = okStatus;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
    }

    public event Action<LiveChange>? Changed;
    public event Action<string>? StatusChanged;
    public string Status => _status;

    public async Task WatchProjectAsync(int projectId)
    {
        _projectId = projectId;
        try { _known = await _stamp(projectId); SetStatus(_ok); }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException) { SetStatus("offline"); }
    }

    /// <summary>A change made by this app: show it now, and remember the new change number.</summary>
    public void Raise(LiveChange c)
    {
        Application.Current?.Dispatcher.BeginInvoke(async () =>
        {
            Changed?.Invoke(c);
            try { _known = await _stamp(c.ProjectId); } catch (Exception ex) when (ex is SqlException or InvalidOperationException) { }
        });
    }

    private async Task PollAsync()
    {
        if (_busy || _projectId == 0) return;
        _busy = true;
        try
        {
            var now = await _stamp(_projectId);
            SetStatus(_ok);
            if (now != _known)
            {
                _known = now;
                Changed?.Invoke(new LiveChange(_projectId, "all", null, "resync", "", DateTime.UtcNow));
            }
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException or NotFoundException)
        {
            SetStatus("offline");
        }
        finally { _busy = false; }
    }

    private void SetStatus(string s)
    {
        if (s == _status) return;
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void Stop() => _timer.Stop();
}

/// <summary>
/// The Windows app works in one of three modes, chosen in Settings (see <see cref="DataMode"/>).
/// Every screen talks to <see cref="Api"/> only, so all modes behave identically and follow the same rules.
/// </summary>
public static class AppState
{
    public static string DataFolder { get; private set; } = "";
    public static string DbPath => Path.Combine(DataFolder, "construct-dpr.db");
    private static string SettingsPath => Path.Combine(DataFolder, "settings.json");
    private static ProtectedFileStore OfficeSecretStore => new(Path.Combine(DataFolder, "office.bin"));

    public static AppSettings Settings { get; private set; } = new();
    public static IConstructDprApi Api { get; private set; } = null!;
    public static ILiveUpdates Live { get; private set; } = new LocalLive();
    public static AuthSession Session { get; private set; } = null!;
    public static DirectAccounts? OfficeAccounts { get; private set; }
    public static UserInfo Me { get; private set; } = new("local", "", "");
    public static AppFeatures Features { get; private set; } = new(true, false, "local");
    public static List<ProjectSummary> Projects { get; private set; } = new();
    public static ProjectSummary? Project { get; private set; }
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    public static DataMode Mode => Settings.Mode;
    public static bool IsServer => Settings.Mode == DataMode.Server;
    public static bool HasAccounts => Settings.Mode != DataMode.Local;
    /// <summary>The name stored on reports for the current person (e-mail with accounts, the chosen name offline).</summary>
    public static string MyReportName => HasAccounts ? Me.Email : Me.DisplayName;
    public static int Pid => Project?.Id ?? 0;
    public static ProjectRole Role => Project?.MyRole ?? ProjectRole.Auditor;
    public static bool Can(Module m, Act a) => Project is not null && Permissions.Can(Role, m, a);

    public static string ModeLabel => Settings.Mode switch
    {
        DataMode.Office => $"Office SQL Server · {Settings.OfficeServer} / {Settings.OfficeDatabase}",
        DataMode.Server => $"Sync server · {Session.ServerUrl}",
        _ => "This PC only · SQLite",
    };

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

    public static async Task<OfficeSecrets> LoadOfficeSecretsAsync()
    {
        var raw = await OfficeSecretStore.LoadAsync();
        try { return raw is null ? new() : JsonSerializer.Deserialize<OfficeSecrets>(raw, Json.Options) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static Task SaveOfficeSecretsAsync(OfficeSecrets s) => OfficeSecretStore.SaveAsync(JsonSerializer.Serialize(s, Json.Options));

    /// <summary>Connection string for the office SQL Server (Windows sign-in or a SQL login).</summary>
    public static string OfficeConnectionString(AppSettings s, string? sqlPassword)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = s.OfficeServer.Trim(),
            InitialCatalog = string.IsNullOrWhiteSpace(s.OfficeDatabase) ? "ConstructDpr" : s.OfficeDatabase.Trim(),
            IntegratedSecurity = s.OfficeWindowsAuth,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true, // office SQL Express usually has a self-signed certificate; traffic is still encrypted
            ConnectTimeout = 10,
            ApplicationName = "CONSTRUCT DPR",
        };
        if (!s.OfficeWindowsAuth) (b.UserID, b.Password) = (s.OfficeSqlUser, sqlPassword ?? "");
        return b.ConnectionString;
    }

    private static Func<AppDbContext> OfficeFactory(string conn)
    {
        var options = AppDbContext.Options(DbProvider.SqlServer, conn);
        return () => new AppDbContext(options);
    }

    /// <summary>Checks the office database (creating its tables on first use). Throws with a readable message.</summary>
    public static async Task<DirectAccounts> ConnectOfficeAsync(AppSettings s, string? sqlPassword)
    {
        var factory = OfficeFactory(OfficeConnectionString(s, sqlPassword));
        try
        {
            await Task.Run(() => { using var db = factory(); db.EnsureSchema(); });
        }
        catch (SqlException ex)
        {
            throw new WorkflowException($"Cannot open the SQL Server database \"{s.OfficeDatabase}\" on {s.OfficeServer}:\n{ex.Message}\n\n" +
                "Check the server name (for example .\\SQLEXPRESS or OFFICE-PC\\SQLEXPRESS), that SQL Server is running, " +
                "that TCP/IP is enabled for remote PCs, and that your Windows account or SQL login may create/use the database.");
        }
        return new DirectAccounts(factory);
    }

    // ───────── "Let phones connect to this PC": the sync server inside this app ─────────

    private static Microsoft.AspNetCore.Builder.WebApplication? _phoneServer;
    public static bool PhoneServerRunning => _phoneServer is not null;
    public static string? PhoneServerError { get; private set; }

    /// <summary>Addresses phones on the same network can type (computer name and IPv4 addresses).</summary>
    public static List<string> PhoneAddresses()
    {
        var list = new List<string> { $"http://{System.Net.Dns.GetHostName()}:{Settings.PhonePort}" };
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                 && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback))
            foreach (var a in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                list.Add($"http://{a.Address}:{Settings.PhonePort}");
        return list.Distinct().ToList();
    }

    public static async Task StartPhoneServerAsync()
    {
        await StopPhoneServerAsync();
        PhoneServerError = null;
        if (!Settings.ShareWithPhones || Settings.Mode == DataMode.Server) return;
        var (provider, connection) = Settings.Mode == DataMode.Office
            ? ("SqlServer", OfficeConnectionString(Settings, (await LoadOfficeSecretsAsync()).SqlPassword))
            : ("Sqlite", $"Data Source={DbPath}");
        try
        {
            var app = await Task.Run(() => ConstructDpr.Server.SyncServerHost.Create([], new Dictionary<string, string?>
            {
                ["Urls"] = $"http://0.0.0.0:{Settings.PhonePort}",
                ["Database:Provider"] = provider,
                ["ConnectionStrings:Default"] = connection,
                ["Features:AdminPortal"] = "false",
                ["Features:Registration"] = "true",
                ["Logging:LogLevel:Default"] = "Warning",
            }));
            await app.StartAsync();
            _phoneServer = app;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            PhoneServerError = $"Phones cannot connect: port {Settings.PhonePort} is already in use on this PC " +
                               $"(is the separate CONSTRUCT DPR sync server running here?). {ex.GetBaseException().Message}";
        }
        Changed?.Invoke();
    }

    public static async Task StopPhoneServerAsync()
    {
        if (_phoneServer is null) return;
        var app = _phoneServer;
        _phoneServer = null;
        try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await app.StopAsync(cts.Token); await app.DisposeAsync(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Connects in the chosen mode. Returns false when a sign-in is needed first.</summary>
    public static async Task<bool> StartAsync()
    {
        StopLive();
        await StopPhoneServerAsync();
        switch (Settings.Mode)
        {
            case DataMode.Local:
            {
                var options = AppDbContext.Options(DbProvider.Sqlite, $"Data Source={DbPath}");
                using (var db = new AppDbContext(options)) db.EnsureSchema();
                // Polls the file for changes, so reports sent from phones (built-in sync server) appear without F5.
                LocalApi api = null!;
                var live = new OfficeLive(pid => api.ChangeStampAsync(pid), "local");
                Live = live;
                Me = new UserInfo("local", "", Settings.UserName);
                OfficeAccounts = null;
                api = new LocalApi(() => new AppDbContext(options), new Caller("local", "", Settings.UserName, IsLocalOwner: true),
                    new AppFeatures(true, false, "this PC"), live.Raise);
                Api = api;
                Features = await Api.FeaturesAsync();
                Projects = await Api.ProjectsAsync();
                if (Projects.Count == 0)
                {
                    await Api.CreateDemoProjectAsync();
                    Projects = await Api.ProjectsAsync();
                }
                Project = Projects.FirstOrDefault(p => p.Id == Settings.LocalProjectId) ?? Projects.FirstOrDefault();
                break;
            }
            case DataMode.Office:
            {
                var secrets = await LoadOfficeSecretsAsync();
                if (secrets.UserId is null) return false;
                var conn = OfficeConnectionString(Settings, secrets.SqlPassword);
                OfficeAccounts = await ConnectOfficeAsync(Settings, secrets.SqlPassword);
                var caller = await Task.Run(() => OfficeAccounts.Find(secrets.UserId));
                if (caller is null) return false;
                LocalApi api = null!;
                var live = new OfficeLive(pid => api.ChangeStampAsync(pid));
                api = new LocalApi(OfficeFactory(conn), caller, new AppFeatures(true, true, "SQL Server"), live.Raise);
                (Api, Live) = (api, live);
                Me = new UserInfo(caller.UserId, caller.Email, caller.DisplayName);
                Features = await Api.FeaturesAsync();
                Projects = await Api.ProjectsAsync();
                Project = Projects.FirstOrDefault(p => p.Id == Settings.OfficeProjectId) ?? Projects.FirstOrDefault();
                break;
            }
            default:
            {
                await Session.LoadAsync();
                if (!Session.IsSignedIn) return false;
                Live = new LiveClient(Session);
                Api = new RemoteApi(Session);
                OfficeAccounts = null;
                Me = await Api.MeAsync();
                Features = await Api.FeaturesAsync();
                Projects = await Api.ProjectsAsync();
                Project = Projects.FirstOrDefault(p => p.Id == Session.ProjectId) ?? Projects.FirstOrDefault();
                break;
            }
        }
        Live.Changed += c =>
        {
            if (c.ProjectId != Pid) return;
            Application.Current?.Dispatcher.BeginInvoke(() => LiveChanged?.Invoke(c));
        };
        Live.StatusChanged += _ => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
        if (Project is not null) await Live.WatchProjectAsync(Project.Id);
        if (Settings.ShareWithPhones && Settings.Mode != DataMode.Server) await StartPhoneServerAsync();
        Changed?.Invoke();
        return true;
    }

    private static void StopLive()
    {
        if (Live is LiveClient lc) _ = lc.StopAsync();
        if (Live is OfficeLive ol) ol.Stop();
    }

    /// <summary>The people screen works in every mode now (phones join "This PC" through the built-in sync server).</summary>
    public static bool CanManagePeople => Features.AdminPortal || Settings.Mode != DataMode.Server;

    public static async Task OpenProjectAsync(int projectId)
    {
        Project = Projects.FirstOrDefault(p => p.Id == projectId);
        switch (Settings.Mode)
        {
            case DataMode.Server: await Session.SetProjectAsync(Project?.Id); break;
            case DataMode.Office: Settings.OfficeProjectId = Project?.Id; SaveSettings(); break;
            default: Settings.LocalProjectId = Project?.Id; SaveSettings(); break;
        }
        if (Project is not null) await Live.WatchProjectAsync(Project.Id);
        Changed?.Invoke();
    }

    public static async Task RefreshProjectsAsync()
    {
        Projects = await Api.ProjectsAsync();
        Project = Projects.FirstOrDefault(p => p.Id == Pid) ?? Projects.FirstOrDefault();
        Changed?.Invoke();
    }

    public static async Task<bool> SwitchModeAsync(DataMode mode)
    {
        Settings.Mode = mode;
        SaveSettings();
        Project = null;
        Projects = new();
        return await StartAsync();
    }

    public static async Task SignOutAsync()
    {
        StopLive();
        if (Settings.Mode == DataMode.Office)
        {
            var s = await LoadOfficeSecretsAsync();
            s.UserId = null;
            await SaveOfficeSecretsAsync(s);
        }
        else await Session.SignOutAsync();
        (Project, Projects) = (null, new());
        Changed?.Invoke();
    }

    /// <summary>Offline mode only: deletes this PC's database and loads the demo project again.</summary>
    public static async Task ResetLocalToDemoAsync()
    {
        await StopPhoneServerAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(DbPath);
        Settings.LocalProjectId = null;
        SaveSettings();
        await StartAsync();
    }
}
