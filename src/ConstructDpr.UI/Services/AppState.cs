using ConstructDpr.Client;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.AspNetCore.Components;

namespace ConstructDpr.UI.Services;

/// <summary>
/// The signed-in person, their projects and the open project, shared by every screen.
/// Live changes from the server are passed on through <see cref="LiveChanged"/> so screens reload.
/// </summary>
public sealed class AppState(AuthSession session, IConstructDprApi api, ILiveUpdates live, HostOptions host, NavigationManager nav)
{
    private bool _started;

    public UserInfo? Me { get; private set; }
    public AppFeatures? Features { get; private set; }
    public List<ProjectSummary> Projects { get; private set; } = new();
    public ProjectSummary? Project { get; private set; }
    public string LiveStatus => live.Status;
    public string ServerUrl => session.ServerUrl;
    public bool ServerFixed => host.FixedServerUrl is not null;
    public string Platform => host.Platform;

    /// <summary>Header, menus and the open screen re-render.</summary>
    public event Action? Changed;
    /// <summary>Someone (maybe on another device) committed a change in the open project.</summary>
    public event Action<LiveChange>? LiveChanged;

    public int ProjectId => Project?.Id ?? 0;
    public ProjectRole Role => Project?.MyRole ?? ProjectRole.Auditor;
    public bool Can(Module m, Act a) => Project is not null && Permissions.Can(Role, m, a);

    /// <summary>Loads the session; returns false when the person must sign in first.</summary>
    public async Task<bool> StartAsync()
    {
        if (!_started)
        {
            _started = true;
            await session.LoadAsync();
            if (host.FixedServerUrl is { } url && session.ServerUrl != AuthSession.Normalise(url)) await session.SetServerAsync(url);
            live.Changed += c =>
            {
                if (c.ProjectId != ProjectId) return;
                if (c.Entity == "members" || c.Entity == "project") _ = RefreshProjectsAsync();
                LiveChanged?.Invoke(c);
            };
            live.StatusChanged += _ => Changed?.Invoke();
        }
        if (!session.IsSignedIn) return false;
        if (Me is not null) return true;
        try
        {
            Me = await api.MeAsync();
            Features = await api.FeaturesAsync();
            await RefreshProjectsAsync();
            var saved = Projects.FirstOrDefault(p => p.Id == session.ProjectId) ?? Projects.FirstOrDefault();
            if (saved is not null) await OpenProjectAsync(saved.Id);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            await session.SignOutAsync();
            return false;
        }
    }

    public async Task RefreshProjectsAsync()
    {
        Projects = await api.ProjectsAsync();
        if (Project is not null) Project = Projects.FirstOrDefault(p => p.Id == Project.Id);
        Changed?.Invoke();
    }

    public async Task OpenProjectAsync(int projectId)
    {
        Project = Projects.FirstOrDefault(p => p.Id == projectId);
        await session.SetProjectAsync(Project?.Id);
        Changed?.Invoke();
        if (Project is not null) _ = live.WatchProjectAsync(Project.Id);
    }

    public async Task SignedInAsync()
    {
        Me = null;
        await StartAsync();
        Changed?.Invoke();
    }

    public async Task SignOutAsync()
    {
        await session.SignOutAsync();
        (Me, Project, Projects) = (null, null, new());
        if (live is LiveClient lc) await lc.StopAsync();
        Changed?.Invoke();
        nav.NavigateTo("login");
    }

    public void NotifyChanged() => Changed?.Invoke();
}
