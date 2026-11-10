using SiteNotes.Core;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;
using SiteNotes.Core.Sync;

namespace SiteNotes.App.Services;

/// <summary>App-wide singletons. Kept deliberately simple (no DI container needed by pages).</summary>
public static class AppServices
{
    public static LocalDatabase Db { get; private set; } = null!;
    public static SyncClient Sync { get; private set; } = null!;
    public static SyncSettings SyncSettings { get; private set; } = new();
    public static ReportQueryService Reports { get; private set; } = null!;

    public static Project? CurrentProject { get; private set; }
    public static event Action? ProjectChanged;

    private static bool _started;

    public static async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        Db = new LocalDatabase(FileSystem.AppDataDirectory);
        await Db.InitAsync();
        Reports = new ReportQueryService(Db);

        SyncSettings = new SyncSettings
        {
            ServerUrl = await Db.GetSettingAsync("Sync.Url") ?? "",
            ApiKey = await Db.GetSettingAsync("Sync.Key") ?? "",
            DeviceId = await Db.GetSettingAsync("Sync.DeviceId") ?? Guid.NewGuid().ToString("N"),
        };
        await Db.SetSettingAsync("Sync.DeviceId", SyncSettings.DeviceId);
        Sync = new SyncClient(Db, SyncSettings);

        var lastProject = await Db.GetSettingAsync("CurrentProject");
        var projects = await Db.ListAsync<Project>();
        CurrentProject = projects.FirstOrDefault(p => p.Id == lastProject) ?? projects.OrderBy(p => p.Name).FirstOrDefault();

        // Projects arriving from another device: pick one if we had none.
        Db.RemoteMerged += () => MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (CurrentProject == null)
            {
                var first = (await Db.ListAsync<Project>()).FirstOrDefault();
                if (first != null) await SetProjectAsync(first);
            }
            RaiseDataChanged();
        });
        Db.Changed += _ => RaiseDataChanged();

        _ = Task.Run(Sync.StartAsync);
    }

    /// <summary>Raised on the UI thread whenever local or synced data changed (bursts are merged: at most ~3 per second).</summary>
    public static event Action? DataChanged;
    private static CancellationTokenSource? _dataDebounce;

    private static void RaiseDataChanged()
    {
        _dataDebounce?.Cancel();
        var cts = _dataDebounce = new CancellationTokenSource();
        Task.Delay(300, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) MainThread.BeginInvokeOnMainThread(() => DataChanged?.Invoke());
        }, TaskScheduler.Default);
    }

    public static async Task SetProjectAsync(Project? p)
    {
        CurrentProject = p;
        await Db.SetSettingAsync("CurrentProject", p?.Id ?? "");
        ProjectChanged?.Invoke();
    }

    public static string ProjectId => CurrentProject?.Id ?? "";

    /// <summary>Saves new sync settings and restarts the connection.</summary>
    public static async Task ReconfigureSyncAsync(string url, string key, string user)
    {
        await Db.SetSettingAsync("Sync.Url", url.Trim());
        await Db.SetSettingAsync("Sync.Key", key.Trim());
        await Db.SetSettingAsync("UserName", user.Trim());
        Db.UserName = user.Trim();
        await Sync.DisposeAsync();
        SyncSettings = new SyncSettings { ServerUrl = url.Trim(), ApiKey = key.Trim(), DeviceId = SyncSettings.DeviceId };
        Sync = new SyncClient(Db, SyncSettings);
        SyncReplaced?.Invoke();
        _ = Task.Run(Sync.StartAsync);
    }

    public static event Action? SyncReplaced;
}
