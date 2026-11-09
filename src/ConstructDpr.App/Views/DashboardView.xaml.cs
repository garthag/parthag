using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ConstructDpr.Shared.Model;

namespace ConstructDpr.App.Views;

public sealed record ListItem(string Title, string Detail, string Badge, Brush BadgeBrush, double Pct = 0);

public partial class DashboardView : UserControl, IRefreshable
{
    public DashboardView() => InitializeComponent();

    public async Task RefreshAsync()
    {
        if (AppState.Pid == 0) { Kpis.Children.Clear(); Attention.ItemsSource = null; Bars.ItemsSource = null; Curve.Points = null; return; }
        var api = AppState.Api;
        var t1 = api.DashboardAsync(AppState.Pid);
        var t2 = api.ProjectAsync(AppState.Pid);
        var t3 = api.ActivitiesAsync(AppState.Pid);
        var (s, p, all) = (await t1, await t2, await t3);
        var inr = CultureInfo.GetCultureInfo("en-IN");
        var variance = s.ActualPct - s.PlannedPct;
        var good = (Brush)FindResource("Good"); var warn = (Brush)FindResource("Warn"); var bad = (Brush)FindResource("Bad"); var brand = (Brush)FindResource("BrandMid");

        Kpis.Children.Clear();
        Kpis.Children.Add(Kpi("Physical progress", $"{s.ActualPct:0.0}%", $"Planned {s.PlannedPct:0.0}% · variance {variance:+0.0;-0.0;0.0}%", variance < -2 ? bad : good));
        Kpis.Children.Add(Kpi("Forecast finish", s.ForecastFinish?.ToString("dd MMM yyyy") ?? "—",
            p.CompletionDate is { } cd && s.ForecastFinish is { } ff ? (ff > cd ? $"{ff.DayNumber - cd.DayNumber} days late vs {cd:dd MMM}" : $"On time (contract {cd:dd MMM yyyy})") : "Run the schedule",
            p.CompletionDate is { } c2 && s.ForecastFinish > c2 ? bad : good));
        Kpis.Children.Add(Kpi("Executed value", "₹ " + s.ExecutedValue.ToString("#,##0", inr), $"of ₹ {s.ContractValue.ToString("#,##0", inr)} BOQ ({(s.ContractValue == 0 ? 0 : s.ExecutedValue * 100 / s.ContractValue):0.0}%)", brand));
        Kpis.Children.Add(Kpi("Approvals pending", s.PendingApprovals.ToString(), $"{s.ApprovedReports} reports approved · {s.HeadcountToday} workers last report", s.PendingApprovals > 0 ? warn : good));
        Kpis.Children.Add(Kpi("Activities", s.Activities.ToString(), $"{s.Critical} on the critical path", brand));
        Kpis.Children.Add(Kpi("Delayed activities", s.Delayed.ToString(), "Past baseline finish, not complete", s.Delayed > 0 ? bad : good));
        Kpis.Children.Add(Kpi("Data date", AppState.Today.ToString("dd MMM yyyy"), "CPM recalculated from progress", brand));
        Kpis.Children.Add(Kpi("Contract completion", p.CompletionDate?.ToString("dd MMM yyyy") ?? "—", $"Start {p.StartDate:dd MMM yyyy}", brand));
        Curve.Points = s.SCurve;

        var acts = all.Where(a => a.Status != ActivityStatus.Cancelled).ToList();
        Attention.ItemsSource = acts
            .Where(a => a.Status != ActivityStatus.Completed && (a.Critical || (a.BaselineFinish ?? a.PlannedFinish) < AppState.Today))
            .OrderBy(a => a.TotalFloat ?? 0).Take(7)
            .Select(a => (a.BaselineFinish ?? a.PlannedFinish) < AppState.Today
                ? new ListItem($"{a.Code} {a.Name}", $"Baseline finish {a.BaselineFinish:dd MMM} · {a.ProgressPct:0}% done", "DELAYED", bad)
                : new ListItem($"{a.Code} {a.Name}", $"Finish {a.EarlyFinish:dd MMM} · float {a.TotalFloat} d", "CRITICAL", warn))
            .ToList();
        Bars.ItemsSource = acts.Where(a => a.TotalQty > 0)
            .Select(a => new ListItem($"{a.Code}  {a.Name}", $"{a.AcceptedQty:#,##0.##} / {a.TotalQty:#,##0.##} {a.Unit} ({a.ProgressPct:0.#}%)", "",
                a.Critical ? bad : brand, (double)a.ProgressPct)).ToList();
    }

    private Border Kpi(string label, string value, string note, Brush accent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = accent, Margin = new Thickness(0, 2, 0, 2) });
        panel.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Style = (Style)FindResource("CardBorder"), Margin = new Thickness(6), Child = panel,
            BorderThickness = new Thickness(4, 1, 1, 1), BorderBrush = accent,
        };
    }
}
