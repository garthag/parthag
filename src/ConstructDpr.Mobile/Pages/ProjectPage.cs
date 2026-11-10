using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Api;

namespace ConstructDpr.Mobile.Pages;

/// <summary>
/// Base for screens showing the open project: loads when shown, reloads when another project is chosen and when a
/// change arrives from another phone or an office PC (live sync), but never while the user has unsaved edits.
/// </summary>
public abstract class ProjectPage : ContentPage
{
    private bool _visible, _loading;

    protected ProjectPage(string title)
    {
        Title = title;
        BackgroundColor = Ui.Page;
        AppState.Changed += () => { if (_visible) _ = ReloadAsync(); };
        AppState.LiveChanged += c => { if (_visible && Wants(c)) _ = ReloadAsync(); };
    }

    /// <summary>Fills the page for <see cref="AppState.Pid"/>.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Which live changes reload this screen (default: all).</summary>
    protected virtual bool Wants(LiveChange c) => true;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        _ = ReloadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
    }

    protected async Task ReloadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            if (AppState.Pid == 0)
            {
                Content = Ui.Scroll(Ui.H1("No project yet"),
                    Ui.Body("Ask your project administrator to add your e-mail (" + AppState.Me?.Email + ") to the project in the Windows app (Projects & people)."),
                    Ui.Primary("Load demo project", async (_, _) =>
                    {
                        await Ui.TryAsync(this, async () => { var p = await AppState.Api.CreateDemoProjectAsync(); await AppState.RefreshProjectsAsync(); await AppState.OpenProjectAsync(p.Id); });
                    }));
                return;
            }
            await Ui.TryAsync(this, LoadAsync);
        }
        finally { _loading = false; }
    }
}
