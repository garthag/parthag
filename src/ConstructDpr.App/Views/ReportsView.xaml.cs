using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.App.Views;

public sealed record DprChoice(int Id, string Title);

/// <summary>PDF (QuestPDF) and Excel (ClosedXML) downloads, Excel import and the audit trail — all through the API.</summary>
public partial class ReportsView : UserControl, IRefreshable
{
    public ReportsView()
    {
        InitializeComponent();
        SetPeriod(Monday(DateTime.Today), DateTime.Today);
    }

    private static DateTime Monday(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
    private void SetPeriod(DateTime from, DateTime to) => (PeriodFrom.SelectedDate, PeriodTo.SelectedDate) = (from, to);
    private void OnThisWeek(object sender, RoutedEventArgs e) => SetPeriod(Monday(DateTime.Today), DateTime.Today);
    private void OnLastWeek(object sender, RoutedEventArgs e) => SetPeriod(Monday(DateTime.Today).AddDays(-7), Monday(DateTime.Today).AddDays(-1));
    private void OnThisMonth(object sender, RoutedEventArgs e) => SetPeriod(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today);
    private async void OnPeriodPdf(object sender, RoutedEventArgs e) => await Period(false);
    private async void OnPeriodExcel(object sender, RoutedEventArgs e) => await Period(true);

    private async Task Period(bool excel)
    {
        if (PeriodFrom.SelectedDate is not { } f || PeriodTo.SelectedDate is not { } t) { Dialogs.Error("Choose the start and end dates."); return; }
        await Dialogs.RunAsync(async () => Dialogs.SaveAndOpen(await AppState.Api.PeriodReportAsync(AppState.Pid, DateOnly.FromDateTime(f), DateOnly.FromDateTime(t), excel)));
    }

    public async Task RefreshAsync()
    {
        if (AppState.Pid == 0) { DprPick.ItemsSource = null; Audit.ItemsSource = null; return; }
        var keep = DprPick.SelectedValue;
        DprPick.ItemsSource = (await AppState.Api.DprsAsync(AppState.Pid))
            .Select(d => new DprChoice(d.Id, $"{d.Number}{(d.Revision > 0 ? " R" + d.Revision : "")} · {d.ReportDate:dd MMM yyyy} · {d.Status}")).ToList();
        DprPick.SelectedValue = keep;
        if (DprPick.SelectedIndex < 0) DprPick.SelectedIndex = 0;
        Audit.ItemsSource = AppState.Can(Module.Reports, Act.View) ? await AppState.Api.AuditAsync(AppState.Pid, 40) : null;
    }

    private static async Task Download(ReportKind kind, int? dprId = null) =>
        await Dialogs.RunAsync(async () => Dialogs.SaveAndOpen(await AppState.Api.ReportAsync(AppState.Pid, kind, dprId)));

    private async void OnDprPdf(object sender, RoutedEventArgs e) { if (DprPick.SelectedValue is int id) await Download(ReportKind.DprPdf, id); }
    private async void OnDprExcel(object sender, RoutedEventArgs e) { if (DprPick.SelectedValue is int id) await Download(ReportKind.DprExcel, id); }
    private async void OnSchedulePdf(object sender, RoutedEventArgs e) => await Download(ReportKind.SchedulePdf);
    private async void OnScheduleExcel(object sender, RoutedEventArgs e) => await Download(ReportKind.ScheduleExcel);
    private async void OnTemplate(object sender, RoutedEventArgs e) => await Download(ReportKind.ActivityTemplate);

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var path = Dialogs.OpenFile("Excel workbook (*.xlsx)|*.xlsx");
        if (path is null) return;
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(path); }
        catch (IOException ex) { Dialogs.Error("The file could not be read: " + ex.Message); return; }
        ImportResult? result = null;
        if (!await Dialogs.RunAsync(async () => result = await AppState.Api.ImportActivitiesAsync(AppState.Pid, bytes))) return;
        if (result!.Errors.Count > 0)
        {
            Dialogs.Error("Nothing was imported. Please fix these rows and try again:\n\n" + string.Join("\n", result.Errors.Take(25)) +
                          (result.Errors.Count > 25 ? $"\n… and {result.Errors.Count - 25} more" : ""));
            return;
        }
        await RefreshAsync();
        Dialogs.Info($"Imported: {result.Created} new and {result.Updated} updated activities. The schedule was recalculated; add relationships on the Activities screen.");
    }
}
