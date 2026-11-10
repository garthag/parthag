using SiteNotes.App.Pages;
using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core.Models;

namespace SiteNotes.App;

/// <summary>Navigation: a side menu (always open on Windows, slide-out on phones) with a coloured title bar.</summary>
public class AppShell : Shell
{
    private readonly Label _project = new() { TextColor = Colors.White, FontSize = 13, LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _sync = new() { TextColor = Color.FromArgb("#C7D7EA"), FontSize = 12 };

    public AppShell()
    {
        FlyoutBehavior = Ui.IsDesktop ? FlyoutBehavior.Locked : FlyoutBehavior.Flyout;
        FlyoutWidth = 250;
        SetBackgroundColor(this, Ui.Primary);
        SetForegroundColor(this, Colors.White);
        SetTitleColor(this, Colors.White);
        SetUnselectedColor(this, Color.FromArgb("#C7D7EA"));
        SetNavBarHasShadow(this, true);

        FlyoutHeader = new VerticalStackLayout
        {
            Padding = new Thickness(16, 24, 16, 14), BackgroundColor = Ui.Primary, Spacing = 2,
            Children =
            {
                new Label { Text = "SiteNotes DPR", TextColor = Colors.White, FontSize = 20, FontAttributes = FontAttributes.Bold },
                _project, _sync,
            }
        };

        Add("📊  Dashboard", "dashboard", typeof(DashboardPage));
        Add("📅  Daily reports", "reports", typeof(ReportsPage));
        Add("📝  Notes & scans", "notes", typeof(NotesPage));
        Add("⚠️  Hindrances", "hindrances", typeof(HindranceListPage));
        Add("📦  Requirements", "requirements", typeof(RequirementListPage));
        Add("🖨️  Report centre", "reportcentre", typeof(ReportCenterPage));
        Add("🏗️  Activities", "activities", typeof(ActivityListPage));
        Add("👷  Subcontractors", "subcontractors", typeof(SubcontractorListPage));
        Add("🏢  Departments", "departments", typeof(DepartmentListPage));
        Add("📁  Projects", "projects", typeof(ProjectListPage));
        Add("⚙️  Settings & sync", "settings", typeof(SettingsPage));

        AppServices.ProjectChanged += Refresh;
        SyncBadge.Changed += Refresh;
        Refresh();
    }

    private void Add(string title, string route, Type page) =>
        Items.Add(new FlyoutItem
        {
            Title = title,
            Route = route,
            Items = { new ShellContent { Title = title[3..].Trim(), Route = route + "page", ContentTemplate = new DataTemplate(page) } }
        });

    private void Refresh()
    {
        _project.Text = AppServices.CurrentProject?.ToString() ?? "No project selected";
        _sync.Text = SyncBadge.Text(AppServices.Sync.State);
    }

    /// <summary>Pushes a page on the current navigation stack.</summary>
    public static Task Push(Page page) => Current.Navigation.PushAsync(page.Page());

    /// <summary>Most pages need a project. Returns false (after offering to create one) when none is selected.</summary>
    public static async Task<bool> RequireProjectAsync()
    {
        if (AppServices.CurrentProject != null) return true;
        if (await Current.DisplayAlertAsync("No project yet", "Create the project first. Every report, note and register belongs to a project.", "Create project", "Cancel"))
            await Push(EntityForms.ProjectForm(new Project()));
        return false;
    }
}
