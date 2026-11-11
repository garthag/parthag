using ConstructDpr.Mobile.Services;

namespace ConstructDpr.Mobile.Pages;

/// <summary>Project switch, account, server, sign out.</summary>
public sealed class MorePage : ContentPage
{
    public MorePage()
    {
        Title = "More";
        BackgroundColor = Ui.Page;
        AppState.Changed += () => MainThread.BeginInvokeOnMainThread(Build);
        Build();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Build();
    }

    private static DateOnly Monday() => AppState.Today.AddDays(-(((int)AppState.Today.DayOfWeek + 6) % 7));

    private async Task Period(DateOnly from, DateOnly to) =>
        await Ui.TryAsync(this, async () => await AppState.ShareFileAsync(await AppState.Api.PeriodReportAsync(AppState.Pid, from, to, excel: false)));

    private void Build()
    {
        var projects = new Picker { Title = "Project", ItemsSource = AppState.Projects.Select(p => $"{p.Code} — {p.Name}").ToList(), FontSize = 15 };
        projects.SelectedIndex = AppState.Projects.FindIndex(p => p.Id == AppState.Pid);
        projects.SelectedIndexChanged += async (_, _) =>
        {
            if (projects.SelectedIndex >= 0 && AppState.Projects[projects.SelectedIndex].Id != AppState.Pid)
                await AppState.OpenProjectAsync(AppState.Projects[projects.SelectedIndex].Id);
        };
        Content = Ui.Scroll(
            Ui.H1("More"),
            Ui.Card(new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    Ui.H2("You"),
                    Ui.Body(AppState.Me?.DisplayName ?? ""),
                    Ui.Small(AppState.Me?.Email ?? ""),
                    Ui.Small("Role on this project: " + (AppState.Project is null ? "—" : Ui.Role(AppState.Role))),
                },
            }),
            Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { Ui.H2("Project"), Ui.Boxed(projects),
                Ui.Plain("Load demo project", async (_, _) => await Ui.TryAsync(this, async () =>
                {
                    var p = await AppState.Api.CreateDemoProjectAsync();
                    await AppState.RefreshProjectsAsync();
                    await AppState.OpenProjectAsync(p.Id);
                }, "Demo project loaded.")) } }),
            Ui.Card(new VerticalStackLayout
            {
                Spacing = 6,
                IsVisible = AppState.Can(ConstructDpr.Shared.Security.Module.Reports, ConstructDpr.Shared.Security.Act.Export),
                Children =
                {
                    Ui.H2("Progress report (PDF)"),
                    Ui.Small("From approved daily reports: quantities, manpower, equipment, materials, delays."),
                    Ui.Plain("This week", async (_, _) => await Period(Monday(), AppState.Today)),
                    Ui.Plain("Last week", async (_, _) => await Period(Monday().AddDays(-7), Monday().AddDays(-1))),
                    Ui.Plain("This month", async (_, _) => await Period(new DateOnly(AppState.Today.Year, AppState.Today.Month, 1), AppState.Today)),
                },
            }),
            Ui.Card(new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    Ui.H2("Server"),
                    Ui.Small(AppState.Session.ServerUrl),
                    Ui.Small("Live updates: " + AppState.Live.Status, AppState.Live.Status == "live" ? Ui.Good : Ui.Muted),
                    Ui.Danger("Sign out", async (_, _) => await AppState.SignOutAsync()),
                },
            }),
            Ui.Small($"CONSTRUCT DPR for Android {AppInfo.Current.VersionString} · C# / .NET MAUI"));
    }
}
