namespace ConstructDpr.Mobile.Pages;

/// <summary>Bottom tabs: Home · Reports · Approve · Schedule · More.</summary>
public sealed class AppShell : Shell
{
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
                Tab("Home", "⌂", () => new HomePage()),
                Tab("Reports", "✎", () => new ReportsPage(approvals: false)),
                Tab("Approve", "✔", () => new ReportsPage(approvals: true)),
                Tab("Schedule", "▤", () => new SchedulePage()),
                Tab("More", "☰", () => new MorePage()),
            },
        });
    }

    private static ShellContent Tab(string title, string glyph, Func<Page> page) => new()
    {
        Title = title,
        Icon = new FontImageSource { Glyph = glyph, Size = 20, Color = Services.Ui.BrandMid },
        ContentTemplate = new DataTemplate(page),
    };
}
