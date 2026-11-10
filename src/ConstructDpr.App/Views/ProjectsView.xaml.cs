using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.App.Views;

public sealed record ProjectRow(int Id, string Code, string Name, string? Client, string RoleLabel, string Open);
public sealed record PersonRow(int Id, string DisplayName, string Email, ProjectRole Role, string RoleLabel);
public sealed record RoleChoice(ProjectRole Role, string Label);

/// <summary>Switch, create and load projects; manage who works on the open project and with which role.</summary>
public partial class ProjectsView : UserControl, IRefreshable
{
    public ProjectsView()
    {
        InitializeComponent();
        RolePick.ItemsSource = Enum.GetValues<ProjectRole>().Select(r => new RoleChoice(r, Permissions.Label(r))).ToList();
        RolePick.SelectedValue = ProjectRole.SiteEngineer;
    }

    private int SelectedProjectId => (ProjectGrid.SelectedItem as ProjectRow)?.Id ?? AppState.Pid;

    public async Task RefreshAsync()
    {
        var keep = SelectedProjectId;
        ProjectGrid.ItemsSource = AppState.Projects
            .Select(p => new ProjectRow(p.Id, p.Code, p.Name, p.Client, Permissions.Label(p.MyRole), p.Id == AppState.Pid ? "OPEN" : "")).ToList();
        ProjectGrid.SelectedItem = (ProjectGrid.ItemsSource as List<ProjectRow>)!.FirstOrDefault(r => r.Id == keep);
        NewBtn.IsEnabled = AppState.Features.AdminPortal;
        Summary.Text = AppState.Mode switch
        {
            DataMode.Local => "This PC only: you are the administrator of every project here. People and roles need the office SQL Server or the sync server (Settings).",
            DataMode.Office => "Office SQL Server: colleagues create their account the first time they connect; then add them here with their role.",
            _ => AppState.Features.AdminPortal ? "Sync server: colleagues create an account in the Android or Windows app; then add them here with their role."
                                               : "Sync server: creating projects and managing people is switched off on this server; do it in Office SQL Server mode or set Features:AdminPortal on the server.",
        };
        await LoadPeopleAsync();
    }

    private void OnProjectSelected(object sender, SelectionChangedEventArgs e) => _ = LoadPeopleAsync();

    private async Task LoadPeopleAsync()
    {
        var row = ProjectGrid.SelectedItem as ProjectRow;
        var summary = AppState.Projects.FirstOrDefault(p => p.Id == row?.Id);
        PeopleTitle.Text = row is null ? "People" : $"People on {row.Code}";
        var canView = AppState.HasAccounts && AppState.Features.AdminPortal && summary is not null && Permissions.Can(summary.MyRole, Module.Members, Act.View);
        AddPanel.Visibility = canView && Permissions.Can(summary!.MyRole, Module.Members, Act.Create) ? Visibility.Visible : Visibility.Collapsed;
        PeopleNote.Text = !AppState.HasAccounts ? "Not available in \"This PC only\" mode."
            : !canView ? "Your role does not show the member list of this project." : "";
        if (!canView) { People.ItemsSource = null; return; }
        await Dialogs.RunAsync(async () =>
            People.ItemsSource = (await AppState.Api.MembersAsync(summary!.Id))
                .Select(m => new PersonRow(m.Id, m.DisplayName, m.Email, m.Role, Permissions.Label(m.Role))).ToList());
    }

    private void OnPersonSelected(object sender, SelectionChangedEventArgs e)
    {
        if (People.SelectedItem is not PersonRow p) return;
        EmailBox.Text = p.Email;
        RolePick.SelectedValue = p.Role;
    }

    private async void OnOpen(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is not ProjectRow r) return;
        await AppState.OpenProjectAsync(r.Id);
        await ((MainWindow)Window.GetWindow(this)).NavigateAsync("dashboard");
    }

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        var code = Dialogs.Prompt("New project", "Project code (short, unique), e.g. BF3-SKIP:");
        if (string.IsNullOrWhiteSpace(code)) return;
        var name = Dialogs.Prompt("New project", "Project name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        var client = Dialogs.Prompt("New project", "Client (optional):") ?? "";
        ProjectSummary? p = null;
        if (!await Dialogs.RunAsync(async () => p = await AppState.Api.CreateProjectAsync(new Project
            {
                Code = code, Name = name, Client = client.Length == 0 ? null : client, StartDate = AppState.Today, WorkDays = "1,2,3,4,5,6",
            }))) return;
        await AppState.RefreshProjectsAsync();
        await AppState.OpenProjectAsync(p!.Id);
        Dialogs.Info($"Project {p.Code} created and opened. Next: add activities (Activities & BOQ, or import them from Excel) and the people who work on it.");
    }

    private async void OnDemo(object sender, RoutedEventArgs e)
    {
        ProjectSummary? p = null;
        if (!await Dialogs.RunAsync(async () => p = await AppState.Api.CreateDemoProjectAsync())) return;
        await AppState.RefreshProjectsAsync();
        await AppState.OpenProjectAsync(p!.Id);
    }

    private async void OnAddMember(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is not ProjectRow r || RolePick.SelectedValue is not ProjectRole role) return;
        if (await Dialogs.RunAsync(() => AppState.Api.AddMemberAsync(r.Id, EmailBox.Text.Trim(), role)))
        {
            EmailBox.Text = "";
            await LoadPeopleAsync();
        }
    }

    private async void OnRemoveMember(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is not ProjectRow r || People.SelectedItem is not PersonRow p) return;
        if (!Dialogs.Confirm($"Remove {p.DisplayName} ({p.Email}) from {r.Code}?")) return;
        if (await Dialogs.RunAsync(() => AppState.Api.RemoveMemberAsync(r.Id, p.Id))) await LoadPeopleAsync();
    }
}
