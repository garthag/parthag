using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Shared.Scheduling;

namespace ConstructDpr.App.Views;

public partial class ScheduleView : UserControl, IRefreshable
{
    public ScheduleView() => InitializeComponent();

    private List<ConstructDpr.Shared.Model.Activity> _acts = new();
    private List<ConstructDpr.Shared.Model.Dependency> _deps = new();

    public async Task RefreshAsync()
    {
        if (AppState.Pid == 0) { Chart.Model = null; return; }
        var t1 = AppState.Api.ActivitiesAsync(AppState.Pid);
        var t2 = AppState.Api.DependenciesAsync(AppState.Pid);
        (_acts, _deps) = (await t1, await t2);
        Draw();
    }

    private void Draw()
    {
        var acts = _acts;
        if (acts.Count == 0) { Chart.Model = null; Summary.Text = "No activities yet."; return; }
        var model = GanttLayout.Build(acts, _deps, AppState.Today, Zoom.Value, 28);
        Chart.Model = model;
        var finish = acts.Max(a => a.EarlyFinish);
        Summary.Text = $"{acts.Count} activities · {acts.Count(a => a.Critical)} critical · forecast finish {finish:dd MMM yyyy} · " +
                       "relationships FS/SS/FF/SF with lag; hover a row for details";
        if (model.TodayX is { } x) Dispatcher.BeginInvoke(() => Scroll.ScrollToHorizontalOffset(Math.Max(0, x - 250)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnScroll(object sender, ScrollChangedEventArgs e) => Chart.TableOffset = e.HorizontalOffset;

    private void OnZoom(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) Draw();
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        ConstructDpr.Shared.Api.ScheduleSummary? r = null;
        if (await Dialogs.RunAsync(async () => r = await AppState.Api.RunScheduleAsync(AppState.Pid)))
            await Dialogs.RunAsync(RefreshAsync);
    }

    private async void OnPdf(object sender, RoutedEventArgs e) =>
        await Dialogs.RunAsync(async () => Dialogs.SaveAndOpen(await AppState.Api.ReportAsync(AppState.Pid, ConstructDpr.Shared.Api.ReportKind.SchedulePdf)));

    private async void OnExcel(object sender, RoutedEventArgs e) =>
        await Dialogs.RunAsync(async () => Dialogs.SaveAndOpen(await AppState.Api.ReportAsync(AppState.Pid, ConstructDpr.Shared.Api.ReportKind.ScheduleExcel)));
}
