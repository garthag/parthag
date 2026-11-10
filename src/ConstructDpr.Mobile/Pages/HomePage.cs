using ConstructDpr.Mobile.Services;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Mobile.Pages;

/// <summary>Dashboard: progress against plan, forecast, value, approvals waiting, activities needing attention.</summary>
public sealed class HomePage : ProjectPage
{
    public HomePage() : base("Dashboard") { }

    protected override async Task LoadAsync()
    {
        var pid = AppState.Pid;
        var t1 = AppState.Api.DashboardAsync(pid);
        var t2 = AppState.Api.ProjectAsync(pid);
        var t3 = AppState.Api.ActivitiesAsync(pid);
        var (s, p, acts) = (await t1, await t2, await t3);
        var variance = s.ActualPct - s.PlannedPct;
        var late = p.CompletionDate is { } cd && s.ForecastFinish > cd;

        var kpis = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) }, ColumnSpacing = 10, RowSpacing = 0 };
        void Kpi(int i, string label, string value, string note, Color color)
        {
            if (kpis.RowDefinitions.Count <= i / 2) kpis.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var card = Ui.Card(new VerticalStackLayout
            {
                Children =
                {
                    Ui.Small(label),
                    new Label { Text = value, FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = color },
                    new Label { Text = note, FontSize = 12, TextColor = Ui.Muted },
                },
            }, color);
            kpis.Add(card, i % 2, i / 2);
        }
        Kpi(0, "Physical progress", $"{Ui.N(s.ActualPct)}%", $"Plan {Ui.N(s.PlannedPct)}% · {(variance >= 0 ? "+" : "")}{Ui.N(variance)}%", variance < -2 ? Ui.Bad : Ui.Good);
        Kpi(1, "Forecast finish", Ui.D(s.ForecastFinish), p.CompletionDate is { } c ? (late ? $"{s.ForecastFinish!.Value.DayNumber - c.DayNumber} days late" : "On time") : "", late ? Ui.Bad : Ui.Good);
        Kpi(2, "Approvals pending", s.PendingApprovals.ToString(), $"{s.ApprovedReports} approved", s.PendingApprovals > 0 ? Ui.Warn : Ui.Good);
        Kpi(3, "Executed value", Ui.Money(s.ExecutedValue), $"of {Ui.Money(s.ContractValue)}", Ui.BrandMid);
        Kpi(4, "Delayed", s.Delayed.ToString(), "past baseline finish", s.Delayed > 0 ? Ui.Bad : Ui.Good);
        Kpi(5, "Workers", s.HeadcountToday.ToString(), "in the last approved report", Ui.BrandMid);

        var attention = Ui.Stack(4);
        foreach (var a in acts.Where(a => a.Status is not (ActivityStatus.Completed or ActivityStatus.Cancelled) && (a.Critical || (a.BaselineFinish ?? a.PlannedFinish) < AppState.Today))
                     .OrderBy(a => a.TotalFloat ?? 0).Take(6))
        {
            var isLate = (a.BaselineFinish ?? a.PlannedFinish) < AppState.Today;
            attention.Children.Add(Ui.Row(
                new VerticalStackLayout { Children = { new Label { Text = $"{a.Code} {a.Name}", FontAttributes = FontAttributes.Bold, FontSize = 14, TextColor = Ui.Text },
                    Ui.Small(isLate ? $"Baseline finish {Ui.D(a.BaselineFinish)} · {Ui.N(a.ProgressPct, 0)}% done" : $"Finish {Ui.D(a.EarlyFinish)} · float {a.TotalFloat} d") } },
                Ui.Badge(isLate ? "DELAYED" : "CRITICAL", isLate ? Ui.Bad : Ui.Warn)));
        }

        var header = new VerticalStackLayout
        {
            Children =
            {
                Ui.H1(p.Code), Ui.Small(p.Name),
                Ui.Small($"{Ui.Role(AppState.Role)} · {AppState.Me?.DisplayName} · live: {AppState.Live.Status}", AppState.Live.Status == "live" ? Ui.Good : Ui.Muted),
            },
        };
        var page = Ui.Scroll(header);
        var stack = (VerticalStackLayout)page.Content;
        if (AppState.Can(Module.Dpr, Act.Create))
            stack.Children.Add(Ui.Primary("＋  Today's daily report", async (_, _) => await Navigation.PushAsync(new NewReportPage())));
        stack.Children.Add(kpis);
        stack.Children.Add(Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { Ui.H2("Needs attention"), attention } }));
        Content = page;
    }
}
