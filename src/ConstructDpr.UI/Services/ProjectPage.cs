using ConstructDpr.Shared.Api;
using Microsoft.AspNetCore.Components;

namespace ConstructDpr.UI.Services;

/// <summary>
/// Base for screens that show the open project: loads on open, reloads when another project is chosen and when a
/// change arrives from another device (live sync).
/// </summary>
public abstract class ProjectPage : ComponentBase, IDisposable
{
    [Inject] protected AppState State { get; set; } = null!;
    [Inject] protected IConstructDprApi Api { get; set; } = null!;
    [Inject] protected Notifier Notes { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;

    private int _loadedFor;
    protected bool Loading { get; private set; } = true;
    protected int Pid => State.ProjectId;
    protected static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnStateChanged;
        State.LiveChanged += OnLive;
        await ReloadAsync();
    }

    /// <summary>Loads the screen's data for <see cref="Pid"/>.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Which live changes make this screen reload (default: all).</summary>
    protected virtual bool Wants(LiveChange c) => true;

    protected async Task ReloadAsync()
    {
        if (Pid == 0) return;
        _loadedFor = Pid;
        await Notes.TryAsync(LoadAsync);
        Loading = false;
    }

    private void OnStateChanged()
    {
        if (Pid != 0 && Pid != _loadedFor)
            InvokeAsync(async () => { Loading = true; StateHasChanged(); await ReloadAsync(); StateHasChanged(); });
        else InvokeAsync(StateHasChanged);
    }

    private void OnLive(LiveChange c)
    {
        if (Wants(c)) InvokeAsync(async () => { await ReloadAsync(); StateHasChanged(); });
    }

    public virtual void Dispose()
    {
        State.Changed -= OnStateChanged;
        State.LiveChanged -= OnLive;
    }
}
