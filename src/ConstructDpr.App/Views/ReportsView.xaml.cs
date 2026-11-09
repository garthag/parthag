using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Reports;

namespace ConstructDpr.App.Views;

public sealed record DprChoice(int Id, string Title);

public partial class ReportsView : UserControl, IRefreshable
{
    public ReportsView() => InitializeComponent();

    public void Refresh()
    {
        DprPick.ItemsSource = AppState.Dprs.List(AppState.Project.Id)
            .Select(d => new DprChoice(d.Id, $"{d.Number}{(d.Revision > 0 ? " R" + d.Revision : "")} · {d.ReportDate:dd MMM yyyy} · {d.Status}")).ToList();
        DprPick.SelectedIndex = 0;
        Audit.ItemsSource = AppState.Db.Audit.OrderByDescending(a => a.Id).Take(40).ToList();
    }

    private void OnDprPdf(object sender, RoutedEventArgs e)
    {
        if (DprPick.SelectedValue is not int id) return;
        var d = AppState.Dprs.Load(id);
        Dialogs.SaveAndOpen(DprPdfReport.Generate(d, AppState.Project), $"{d.Number}-R{d.Revision}.pdf", "PDF (*.pdf)|*.pdf");
    }

    private void OnDprExcel(object sender, RoutedEventArgs e)
    {
        if (DprPick.SelectedValue is not int id) return;
        var d = AppState.Dprs.Load(id);
        Dialogs.SaveAndOpen(ExcelService.ExportDpr(d, AppState.Project), $"{d.Number}-R{d.Revision}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");
    }

    private void OnSchedulePdf(object sender, RoutedEventArgs e) =>
        Dialogs.SaveAndOpen(SchedulePdfReport.Generate(AppState.Project, AppState.Activities(), AppState.Dependencies(), AppState.Today),
            $"{AppState.Project.Code}-schedule-{AppState.Today:yyyyMMdd}.pdf", "PDF (*.pdf)|*.pdf");

    private void OnScheduleExcel(object sender, RoutedEventArgs e) =>
        Dialogs.SaveAndOpen(ExcelService.ExportSchedule(AppState.Project, AppState.Activities()),
            $"{AppState.Project.Code}-schedule-{AppState.Today:yyyyMMdd}.xlsx", "Excel workbook (*.xlsx)|*.xlsx");

    private void OnTemplate(object sender, RoutedEventArgs e) =>
        Dialogs.SaveAndOpen(ExcelService.ActivityTemplate(), "activity-import-template.xlsx", "Excel workbook (*.xlsx)|*.xlsx");

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var path = Dialogs.OpenFile("Excel workbook (*.xlsx)|*.xlsx");
        if (path is null) return;
        ImportResult result;
        try
        {
            using var stream = File.OpenRead(path);
            result = ExcelService.ImportActivities(AppState.Db, AppState.Project.Id, stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            Dialogs.Error("The file could not be read as an Excel workbook: " + ex.Message);
            return;
        }
        if (result.Errors.Count > 0)
        {
            Dialogs.Error("Nothing was imported. Please fix these rows and try again:\n\n" + string.Join("\n", result.Errors.Take(25)) +
                          (result.Errors.Count > 25 ? $"\n… and {result.Errors.Count - 25} more" : ""));
            return;
        }
        AppState.Schedule.Run(AppState.Project.Id, AppState.Today);
        AppState.NotifyChanged();
        Refresh();
        Dialogs.Info($"Imported: {result.Created} new and {result.Updated} updated activities. The schedule was recalculated; add relationships on the Activities screen.");
    }
}
