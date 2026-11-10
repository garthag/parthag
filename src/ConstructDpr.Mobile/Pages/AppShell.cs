using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile.Pages;

/// <summary>Bottom tabs: Home · Reports · Approve · Schedule · More.</summary>
public sealed class AppShell : Shell
{
    private readonly ShellContent _approve;

    public AppShell()
    {
        Shell.SetBackgroundColor(this, Services.Ui.BrandDark);
        Shell.SetForegroundColor(this, Colors.White);
        Shell.SetTitleColor(this, Colors.White);
        Shell.SetTabBarBackgroundColor(this, Colors.White);
        Shell.SetTabBarForegroundColor(this, Services.Ui.BrandMid);
        Shell.SetTabBarTitleColor(this, Services.Ui.BrandMid);
        Shell.SetTabBarUnselectedColor(this, Services.Ui.Muted);
        Items.Add(new TabBar
        {
            Items =
            {
                Tab("Home", "home", "⌂", () => new HomePage()),
                Tab("Reports", "reports", "✎", () => new ReportsPage(approvals: false)),
                (_approve = Tab("Approve", "approve", "✔", () => new ReportsPage(approvals: true))),
                Tab("Schedule", "schedule", "▤", () => new SchedulePage()),
                Tab("More", "more", "☰", () => new MorePage()),
            },
        });
        AppState.Changed += () => _ = UpdateApprovalsAsync();
        AppState.LiveChanged += c => { _ = UpdateApprovalsAsync(); };
        _ = UpdateApprovalsAsync();
    }

    /// <summary>"Approve (2)" when reports wait for someone who may approve them.</summary>
    private async Task UpdateApprovalsAsync()
    {
        var n = 0;
        try
        {
            if (AppState.Pid != 0 && AppState.Can(Module.Dpr, Act.Approve))
                n = (await AppState.Api.DprsAsync(AppState.Pid)).Count(d => d.Status == DprStatus.Submitted);
        }
        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or NotFoundException) { }
        MainThread.BeginInvokeOnMainThread(() => _approve.Title = n > 0 ? $"Approve ({n})" : "Approve");
    }

    private static ShellContent Tab(string title, string route, string glyph, Func<Page> page) => new()
    {
        Title = title,
        Route = route,
        Icon = new FontImageSource { Glyph = glyph, Size = 20, Color = Services.Ui.BrandMid },
        ContentTemplate = new DataTemplate(page),
    };
}
