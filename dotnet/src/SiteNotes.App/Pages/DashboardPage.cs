using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>Home: project switcher, quick actions, today's figures, 14-day manpower chart, attention list.</summary>
public class DashboardPage : ContentPage
{
    private readonly VerticalStackLayout _body = new() { Spacing = 8, Padding = 12 };

    public DashboardPage()
    {
        Title = "Dashboard";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("⟳ Sync", null, () => _ = Ui.Guard(async () =>
        {
            if (!AppServices.SyncSettings.Enabled) { await DisplayAlertAsync("Sync is off", "Add your server in Settings & sync to share data in real time.", "OK"); return; }
            await AppServices.Sync.SyncNowAsync();
            await DisplayAlertAsync("Sync", AppServices.Sync.LastMessage, "OK");
        })));
        Content = new ScrollView { Content = _body };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AppServices.DataChanged += Refresh;
        AppServices.ProjectChanged += Refresh;
        await Ui.Guard(BuildAsync);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AppServices.DataChanged -= Refresh;
        AppServices.ProjectChanged -= Refresh;
    }

    private async void Refresh() => await Ui.Guard(BuildAsync);

    private async Task BuildAsync()
    {
        var db = AppServices.Db;
        var projects = (await db.ListAsync<Project>()).OrderBy(p => p.Name).ToList();
        _body.Children.Clear();

        var picker = new Picker { Title = "Select project", ItemsSource = projects, SelectedItem = projects.FirstOrDefault(p => p.Id == AppServices.ProjectId) };
        picker.SelectedIndexChanged += async (_, _) => { if (picker.SelectedItem is Project p && p.Id != AppServices.ProjectId) await AppServices.SetProjectAsync(p); };
        var head = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], ColumnSpacing = 8 };
        head.Add(picker, 0);
        head.Add(Ui.Button("＋ Project", () => AppShell.Push(EntityForms.ProjectForm(new Project())), false), 1);
        _body.Children.Add(head);

        if (AppServices.CurrentProject == null)
        {
            _body.Children.Add(Ui.Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Ui.Title("Welcome to SiteNotes DPR"),
                    Ui.Label("1. Create your project.  2. Add subcontractors, resources & rates, WBS and activities.  3. Fill the daily report each day – progress, man-days and cost add up by themselves."),
                    Ui.Button("Create project", () => AppShell.Push(EntityForms.ProjectForm(new Project()))),
                    Ui.Button("Load a sample project to explore", async () => { var p = await SampleData.CreateAsync(AppServices.Db); await AppServices.SetProjectAsync(p); }, false),
                }
            }));
            return;
        }

        var pid = AppServices.ProjectId;
        _body.Children.Add(Ui.SectionHeader("Quick actions"));
        _body.Children.Add(Ui.Columns(7,
            Quick("📅", "Today's DPR", DprFlow.TodayAsync),
            Quick("📝", "New note", async () => await AppShell.Push(new NoteEditorPage(null, ""))),
            Quick("📄", "Scan", async () => await AppShell.Push(new ScannerPage(null, ""))),
            Quick("📈", "Progress", async () => await AppShell.Push(new ProgressPage())),
            Quick("🖨", "Reports", async () => await AppShell.Push(new ReportCenterPage())),
            Quick("⚠️", "Hindrance", async () => await AppShell.Push(EntityForms.HindranceForm(new Hindrance { ProjectId = pid }))),
            Quick("📦", "Requirement", async () => await AppShell.Push(EntityForms.RequirementForm(new Requirement { ProjectId = pid, RaisedBy = AppServices.Db.UserName })))));

        var reports = (await db.ListForProjectAsync<DailyReport>(pid)).OrderByDescending(r => r.Date).ToList();
        var latest = reports.FirstOrDefault();
        var hind = await db.ListForProjectAsync<Hindrance>(pid);
        var reqs = await db.ListForProjectAsync<Requirement>(pid);
        var pp = await Task.Run(() => new ProgressService(db).ComputeAsync(pid));
        var cur = pp.Calendar.Currency;
        var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var month = reports.Where(r => r.Date >= monthStart).ToList();

        _body.Children.Add(Ui.SectionHeader(latest == null ? "No reports yet" : $"Latest report · {latest.Date:ddd dd-MMM}"));
        _body.Children.Add(Ui.Columns(4,
            Ui.Stat("Manpower (latest DPR)", latest?.TotalManpower.ToString() ?? "0"),
            Ui.Stat("Overall progress", $"{pp.OverallPercent:0.#} %", Ui.Success),
            Ui.Stat("Open hindrances", hind.Count(h => h.Status == HindranceStatus.Open).ToString(), Ui.Danger),
            Ui.Stat("Pending requirements", reqs.Count(r => r.Status is RequirementStatus.Pending or RequirementStatus.Approved or RequirementStatus.Ordered) + (reqs.Any(r => r.IsOverdue) ? $" ({reqs.Count(r => r.IsOverdue)} late)" : ""), Ui.Accent),
            Ui.Stat("Man-days this month", PdfReports.N(month.Sum(r => r.TotalManpower))),
            Ui.Stat($"Cost this month ({cur})", PdfReports.M(month.Sum(r => r.TotalCost)), Ui.Accent),
            Ui.Stat("Activities completed", $"{pp.Activities.Count(a => a.State == WorkState.Completed)} / {pp.Activities.Count}", Ui.Success),
            Ui.Stat("Activities delayed", pp.Activities.Count(a => a.State == WorkState.Delayed).ToString(), Ui.Danger)));

        _body.Children.Add(Ui.SectionHeader("Manpower – last 14 days"));
        _body.Children.Add(Ui.Card(Chart(reports)));

        var attention = new VerticalStackLayout { Spacing = 4 };
        foreach (var a in pp.Activities.Where(a => a.State == WorkState.Delayed).Take(5))
            attention.Children.Add(Ui.Label($"⏱ {a.Activity} – {a.Percent:0.#} % (plan {a.PlannedPercentToday:0.#} %), forecast {PdfReports.D(a.ForecastFinish)}", 13, false, Ui.Danger));
        foreach (var r in reqs.Where(r => r.IsOverdue).Take(5))
            attention.Children.Add(Ui.Label($"📦 R-{r.SerialNo:000} {r.Item} – needed by {r.RequiredBy:dd-MMM}", 13, false, Ui.Accent));
        foreach (var h in hind.Where(h => h.Status == HindranceStatus.Open).OrderBy(h => h.FromDate).Take(5))
            attention.Children.Add(Ui.Label($"⚠ H-{h.SerialNo:000} {h.Description} – open {h.DaysOpen} day(s)", 13));
        if (attention.Children.Count > 0)
        {
            _body.Children.Add(Ui.SectionHeader("Needs attention"));
            _body.Children.Add(Ui.Card(attention));
        }

        _body.Children.Add(Ui.SectionHeader("Recent reports"));
        foreach (var r in reports.Take(5))
        {
            var row = Ui.Card(new VerticalStackLayout
            {
                Children =
                {
                    Ui.Label(r.Title, 15, true),
                    Ui.Caption($"{r.Status} · {r.TotalManpower} manpower · {r.TotalMachines} machines · {cur} {PdfReports.M(r.TotalCost)}"),
                }
            });
            var tap = new TapGestureRecognizer();
            var id = r.Id;
            tap.Tapped += async (_, _) => await AppShell.Push(new DprEditorPage(id));
            row.GestureRecognizers.Add(tap);
            _body.Children.Add(row);
        }
    }

    private static View Quick(string icon, string text, Func<Task> action)
    {
        var card = Ui.Card(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = icon, FontSize = 26, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = text, FontSize = 12, HorizontalOptions = LayoutOptions.Center, TextColor = Ui.Primary, FontAttributes = FontAttributes.Bold },
            }
        }, 8);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Ui.Guard(async () => { if (await AppShell.RequireProjectAsync()) await action(); });
        card.GestureRecognizers.Add(tap);
        return card;
    }

    /// <summary>Simple bar chart drawn with boxes (no chart library needed).</summary>
    private static View Chart(List<DailyReport> reports)
    {
        var days = Enumerable.Range(0, 14).Select(i => DateTime.Today.AddDays(i - 13)).ToList();
        var values = days.Select(d => reports.Where(r => r.Date.Date == d).Sum(r => r.TotalManpower)).ToList();
        var max = Math.Max(1, values.Max());
        var g = new Grid { HeightRequest = 150, ColumnSpacing = 3 };
        g.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var i = 0; i < days.Count; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var h = 110.0 * values[i] / max;
            var col = new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.End, Spacing = 1,
                Children =
                {
                    new Label { Text = values[i] == 0 ? "" : values[i].ToString(), FontSize = 9, HorizontalOptions = LayoutOptions.Center, TextColor = Ui.Muted },
                    new BoxView { HeightRequest = Math.Max(values[i] == 0 ? 1 : 3, h), Color = days[i] == DateTime.Today ? Ui.Accent : Ui.Primary, CornerRadius = 2 },
                }
            };
            g.Add(col, i, 0);
            g.Add(new Label { Text = days[i].ToString("dd"), FontSize = 9, HorizontalOptions = LayoutOptions.Center, TextColor = Ui.Muted }, i, 1);
        }
        return g;
    }
}
