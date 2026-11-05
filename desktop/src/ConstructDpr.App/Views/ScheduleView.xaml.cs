using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Core.Scheduling;
using ConstructDpr.Reports;

namespace ConstructDpr.App.Views;

public partial class ScheduleView : UserControl, IRefreshable
{
    public ScheduleView() => InitializeComponent();

    public void Refresh()
    {
        var acts = AppState.Activities();
        var model = GanttLayout.Build(acts, AppState.Dependencies(), AppState.Today, Zoom.Value, 28);
        Chart.Model = model;
        var finish = acts.Max(a => a.EarlyFinish);
        Summary.Text = $"{acts.Count} activities · {acts.Count(a => a.Critical)} critical · forecast finish {finish:dd MMM yyyy} · " +
                       "relationships FS/SS/FF/SF with lag; hover a row for details";
        if (model.TodayX is { } x) Dispatcher.BeginInvoke(() => Scroll.ScrollToHorizontalOffset(Math.Max(0, x - 300)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnZoom(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded) Refresh();
    }

    private void OnRun(object sender, RoutedEventArgs e)
    {
        try
        {
            AppState.Schedule.Run(AppState.Project.Id, AppState.Today);
            AppState.NotifyChanged();
            Refresh();
        }
        catch (ScheduleException ex) { Dialogs.Error(ex.Message); }
    }

    private void OnPdf(object sender, RoutedEventArgs e) =>
        Dialogs.SaveAndOpen(SchedulePdfReport.Generate(AppState.Project, AppState.Activities(), AppState.Dependencies(), AppState.Today),
            $"{AppState.Project.Code}-schedule-{AppState.Today:yyyyMMdd}.pdf", "PDF (*.pdf)|*.pdf");

    private void OnExcel(object sender, RoutedEventArgs e) =>
        Dialogs.SaveAndOpen(ExcelService.ExportSchedule(AppState.Project, AppState.Activities()),
            $"{AppState.Project.Code}-schedule-{AppState.Today:yyyyMMdd}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
}
