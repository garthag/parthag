using ConstructDpr.Client;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile.Services;

/// <summary>Sign-in tokens in Android's secure storage (encrypted by the phone's keystore).</summary>
public sealed class SecureSessionStore : ISessionStore
{
    private const string Key = "cdpr.session";

    public async Task<string?> LoadAsync()
    {
        try { return await SecureStorage.Default.GetAsync(Key); }
        catch (Exception) { SecureStorage.Default.Remove(Key); return null; } // keystore reset (e.g. after restoring a backup)
    }

    public async Task SaveAsync(string? value)
    {
        if (value is null) SecureStorage.Default.Remove(Key);
        else await SecureStorage.Default.SetAsync(Key, value);
    }
}

/// <summary>The signed-in person, their projects and the open project, shared by all screens.</summary>
public static class AppState
{
    public static AuthSession Session { get; } = new(new SecureSessionStore());
    public static RemoteApi Api { get; } = new(Session);
    public static LiveClient Live { get; } = new(Session);

    public static UserInfo? Me { get; private set; }
    public static List<ProjectSummary> Projects { get; private set; } = new();
    public static ProjectSummary? Project { get; private set; }
    public static int Pid => Project?.Id ?? 0;
    public static ProjectRole Role => Project?.MyRole ?? ProjectRole.Auditor;
    public static bool Can(Module m, Act a) => Project is not null && ConstructDpr.Shared.Security.Permissions.Can(Role, m, a);
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>Project switched or signed in/out.</summary>
    public static event Action? Changed;
    /// <summary>Someone (maybe on a PC in the office) changed the open project.</summary>
    public static event Action<LiveChange>? LiveChanged;

    private static bool _wired;

    /// <summary>Returns false when the person must sign in first.</summary>
    public static async Task<bool> StartAsync()
    {
        if (!_wired)
        {
            _wired = true;
            Live.Changed += c => { if (c.ProjectId == Pid) MainThread.BeginInvokeOnMainThread(() => LiveChanged?.Invoke(c)); };
            Live.StatusChanged += _ => MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
        }
        await Session.LoadAsync();
        if (!Session.IsSignedIn) return false;
        try
        {
            Me = await Api.MeAsync();
            Projects = await Api.ProjectsAsync();
        }
        catch (UnauthorizedAccessException)
        {
            await Session.SignOutAsync();
            return false;
        }
        Project = Projects.FirstOrDefault(p => p.Id == Session.ProjectId) ?? Projects.FirstOrDefault();
        if (Project is not null) _ = Live.WatchProjectAsync(Project.Id);
        Changed?.Invoke();
        return true;
    }

    public static async Task RefreshProjectsAsync()
    {
        Projects = await Api.ProjectsAsync();
        Project = Projects.FirstOrDefault(p => p.Id == Pid) ?? Projects.FirstOrDefault();
        Changed?.Invoke();
    }

    public static async Task OpenProjectAsync(int projectId)
    {
        Project = Projects.FirstOrDefault(p => p.Id == projectId);
        await Session.SetProjectAsync(Project?.Id);
        if (Project is not null) _ = Live.WatchProjectAsync(Project.Id);
        Changed?.Invoke();
    }

    public static async Task SignOutAsync()
    {
        await Live.StopAsync();
        await Session.SignOutAsync();
        (Me, Project, Projects) = (null, null, new());
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Application.Current?.Windows.FirstOrDefault() is { } w) w.Page = new Pages.LoginPage();
        });
    }

    /// <summary>Opens a downloaded report in the share sheet (save to Files, WhatsApp, e-mail…).</summary>
    public static async Task ShareFileAsync(FileDownload f)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, f.FileName);
        await File.WriteAllBytesAsync(path, f.Content);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = f.FileName, File = new ShareFile(path, f.ContentType) });
    }
}
