using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>Report creation: number and date are automatic; the first number is asked once per project.</summary>
public static class DprFlow
{
    public static async Task NewAsync()
    {
        if (Desk.Project == null) { MainWindow.Current.Open(new ReportsView()); return; }
        var db = Desk.Db;
        var pid = Desk.ProjectId;
        if (await db.NeedsFirstReportNoAsync(pid))
        {
            var text = Dialogs.Prompt("First report number", "Number of this project's first daily report.\nEvery next report is numbered automatically.", "1");
            if (text == null) return;
            if (!int.TryParse(text, out var first) || first < 1) first = 1;
            if (await db.GetAsync<Project>(pid) is { } p) { p.FirstReportNo = first; await db.SaveAsync(p); }
        }
        var date = await db.NextReportDateAsync(pid);
        var existing = (await db.ListForProjectAsync<DailyReport>(pid)).Where(r => r.Date.Date == date).OrderBy(r => r.ReportNo).ToList();
        if (existing.Count > 0)
        {
            var i = Dialogs.Choose("Report exists", $"There is already a report for {date:ddd dd-MMM-yyyy}.", [$"Open {existing[^1].Title}", "Create another report (another shift)"]);
            if (i == 0) { MainWindow.Current.Open(new DprEditorView(existing[^1].Id)); return; }
            if (i != 1) return;
        }
        MainWindow.Current.Open(new DprEditorView(null, date));
    }

    public static async Task TodayAsync()
    {
        if (Desk.Project == null) { await NewAsync(); return; }
        var today = (await Desk.Db.ListForProjectAsync<DailyReport>(Desk.ProjectId)).FirstOrDefault(r => r.Date.Date == DateTime.Today);
        if (today != null) MainWindow.Current.Open(new DprEditorView(today.Id));
        else await NewAsync();
    }
}

/// <summary>Search, period, status and agency filters shared by the reports list and the report manager.</summary>
public class ReportFilterPanel : StackPanel
{
    public ReportFilter Filter { get; } = new();
    public event Action? Changed;
    private readonly ComboBox _agency = new() { Width = 170 };
    private readonly DatePicker _from = new() { Width = 120, IsEnabled = false };
    private readonly DatePicker _to = new() { Width = 120, IsEnabled = false };
    private static readonly string[] Periods = ["All dates", "Today", "Yesterday", "This week", "Last 7 days", "This month", "Last month", "Custom"];

    public ReportFilterPanel()
    {
        Orientation = Orientation.Vertical;
        Margin = new Thickness(10, 8, 10, 4);
        var search = new TextBox { Width = 280, ToolTip = "Search remarks, activities, report no." };
        search.TextChanged += (_, _) => { Filter.Search = search.Text; Changed?.Invoke(); };
        var period = W.Choice(Periods.Select(p => (p, p)), "All dates", ApplyPeriod);
        _from.SelectedDateChanged += (_, _) => { if (_from.IsEnabled) { Filter.From = _from.SelectedDate; Changed?.Invoke(); } };
        _to.SelectedDateChanged += (_, _) => { if (_to.IsEnabled) { Filter.To = _to.SelectedDate; Changed?.Invoke(); } };
        var statuses = W.Multi(Enum.GetValues<ReportStatus>().Select(s => (s, s.ToString())), Filter.Statuses, () => Changed?.Invoke());
        _agency.SelectionChanged += (_, _) => { Filter.SubcontractorId = (_agency.SelectedItem as Pick)?.Id ?? ""; Changed?.Invoke(); };

        var row1 = new WrapPanel();
        row1.Children.Add(W.Text("🔍 "));
        row1.Children.Add(search);
        row1.Children.Add(new Border { Width = 12 });
        row1.Children.Add(period);
        var row2 = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        row2.Children.Add(W.Muted("From "));
        row2.Children.Add(_from);
        row2.Children.Add(W.Muted("  to "));
        row2.Children.Add(_to);
        row2.Children.Add(W.Muted("     Status "));
        row2.Children.Add(statuses);
        row2.Children.Add(W.Muted("   Agency "));
        row2.Children.Add(_agency);
        Children.Add(row1);
        Children.Add(row2);
    }

    private void ApplyPeriod(string p)
    {
        var t = DateTime.Today;
        var weekStart = t.AddDays(-(((int)t.DayOfWeek + 6) % 7));
        var m = new DateTime(t.Year, t.Month, 1);
        _from.IsEnabled = _to.IsEnabled = p == "Custom";
        (Filter.From, Filter.To) = p switch
        {
            "Today" => (t, t),
            "Yesterday" => (t.AddDays(-1), t.AddDays(-1)),
            "This week" => (weekStart, t),
            "Last 7 days" => (t.AddDays(-6), t),
            "This month" => (m, t),
            "Last month" => (m.AddMonths(-1), m.AddDays(-1)),
            "Custom" => (_from.SelectedDate ?? t.AddDays(-30), _to.SelectedDate ?? t),
            _ => ((DateTime?)null, (DateTime?)null),
        };
        if (p == "Custom") { _from.SelectedDate = Filter.From; _to.SelectedDate = Filter.To; }
        else { _from.SelectedDate = Filter.From; _to.SelectedDate = Filter.To; }
        Changed?.Invoke();
    }

    public async Task LoadAsync()
    {
        Filter.ProjectId = Desk.ProjectId;
        var keep = Filter.SubcontractorId;
        var subs = (await Desk.Db.ListForProjectAsync<Subcontractor>(Desk.ProjectId)).OrderBy(s => s.Name).Select(s => new Pick(s.Id, s.Name)).Prepend(new Pick("", "All agencies")).ToList();
        _agency.ItemsSource = subs;
        _agency.SelectedItem = subs.FirstOrDefault(s => s.Id == keep) ?? subs[0];
    }
}

/// <summary>Row in the reports list (read-only view of a report's cached totals).</summary>
public class ReportRow(DailyReport r, string prefix)
{
    public DailyReport R { get; } = r;
    public string No => $"{prefix}{R.ReportNo:000}";
    public DateTime Date => R.Date;
    public string Day => R.Date.ToString("ddd");
    public string Status => R.Status.ToString();
    public int Manpower => R.TotalManpower;
    public double ManHours => R.TotalManHours;
    public int Machines => R.TotalMachines;
    public int Activities => R.ActivityCount;
    public int Hindrances => R.HindranceCount;
    public decimal Cost => R.TotalCost;
    public string Weather => R.WeatherMorning == R.WeatherEvening ? R.WeatherMorning : $"{R.WeatherMorning} / {R.WeatherEvening}";
    public string PreparedBy => R.PreparedBy;
    public string Remarks => R.Remarks;
    public int ReportNo => R.ReportNo;
}

/// <summary>All daily reports: filter, click column headers to sort, Ctrl/Shift to multi-select, bulk actions.</summary>
public class ReportsView : ViewBase, IPrintable
{
    private readonly ReportFilterPanel _filter = new();
    private readonly ObservableCollection<ReportRow> _rows = [];
    private readonly DataGrid _grid;
    private readonly TextBlock _summary = W.Muted("");
    private List<DailyReport> _found = [];

    public override string Title => "Daily reports";

    public ReportsView()
    {
        _grid = new GridBuilder(_rows, editable: false)
            .Text("No.", nameof(ReportRow.No), 80)
            .Date("Date", nameof(ReportRow.Date))
            .Text("Day", nameof(ReportRow.Day), 50)
            .Status("Status", nameof(ReportRow.Status), 90)
            .Number("Manpower", nameof(ReportRow.Manpower), 85, "0")
            .Number("Man-hours", nameof(ReportRow.ManHours), 85)
            .Number("Machines", nameof(ReportRow.Machines), 75, "0")
            .Number("Activities", nameof(ReportRow.Activities), 75, "0")
            .Number("Hindrances", nameof(ReportRow.Hindrances), 80, "0")
            .Number("Cost", nameof(ReportRow.Cost), 110, "#,0.00")
            .Text("Weather", nameof(ReportRow.Weather), 120)
            .Text("Prepared by", nameof(ReportRow.PreparedBy), 120)
            .Text("Remarks", nameof(ReportRow.Remarks)).Grid;
        _grid.Margin = new Thickness(10, 0, 10, 10);
        _grid.MouseDoubleClick += (_, _) => OpenSelected();
        _grid.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { OpenSelected(); e.Handled = true; } };
        _filter.Changed += async () => await Dialogs.Guard(ReloadAsync);

        var tb = W.ToolBar(
            (Glyph.Add, "New DPR", DprFlow.NewAsync),
            (Glyph.Open, "Open", () => { OpenSelected(); return Task.CompletedTask; }),
            null,
            (Glyph.Print, "PDF register", () => ExportAsync("pdf")),
            (Glyph.Document, "PDF – full reports", () => ExportAsync("full")),
            (Glyph.Excel, "Excel", () => ExportAsync("xlsx")),
            null,
            (Glyph.Accept, "Submit", () => SetStatusAsync(ReportStatus.Submitted)),
            (Glyph.Accept, "Approve", () => SetStatusAsync(ReportStatus.Approved)),
            (Glyph.Edit, "Back to draft", () => SetStatusAsync(ReportStatus.Draft)),
            (Glyph.Delete, "Delete", DeleteAsync),
            null,
            (Glyph.Print, "Report manager…", () => { MainWindow.Current.Open(new ReportManagerView()); return Task.CompletedTask; }));
        var dock = new DockPanel();
        DockPanel.SetDock(tb, Dock.Top);
        dock.Children.Add(tb);
        DockPanel.SetDock(_filter, Dock.Top);
        dock.Children.Add(_filter);
        _summary.Margin = new Thickness(12, 2, 12, 6);
        DockPanel.SetDock(_summary, Dock.Top);
        dock.Children.Add(_summary);
        dock.Children.Add(_grid);
        Content = dock;
    }

    public override async Task RefreshAsync()
    {
        await _filter.LoadAsync();
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _filter.Filter.ProjectId = Desk.ProjectId;
        _found = await Desk.Reports.FindAsync(_filter.Filter);
        var prefix = Desk.Project?.ReportPrefix ?? "DPR-";
        _rows.Clear();
        foreach (var r in _found) _rows.Add(new ReportRow(r, prefix));
        var cur = (await Desk.Db.CalendarAsync(Desk.ProjectId)).Currency;
        _summary.Text = $"{_found.Count} report(s) · {_found.Sum(r => r.TotalManpower)} man-days · {PdfReports.N(_found.Sum(r => r.TotalManHours))} man-hours" +
                        (_found.Any(r => r.TotalCost > 0) ? $" · {cur} {PdfReports.M(_found.Sum(r => r.TotalCost))}" : "") +
                        "   ·   tip: click a column header to sort, Ctrl/Shift+click to select several";
    }

    private List<DailyReport> Selected() => _grid.SelectedItems.OfType<ReportRow>().Select(r => r.R).ToList();

    private void OpenSelected()
    {
        foreach (var r in Selected().Take(8)) MainWindow.Current.Open(new DprEditorView(r.Id));
    }

    public Task PdfAsync() => ExportAsync(Selected().Count == 1 ? "one" : "pdf");

    private async Task ExportAsync(string kind)
    {
        var sel = Selected();
        var reports = sel.Count > 1 || kind == "one" ? sel : _found;
        if (reports.Count == 0) { Dialogs.Info("No reports to export. Change the filter or create a report."); return; }
        var bundles = await Desk.Db.LoadReportsAsync(Desk.ProjectId, _filter.Filter.ApplySort(reports).ToList());
        var cal = await Desk.Db.CalendarAsync(Desk.ProjectId);
        var opt = await Desk.ReportOptionsAsync();
        var desc = reports == _found ? _filter.Filter.Describe() : $"{reports.Count} selected report(s)";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        switch (kind)
        {
            case "one":
                await Dialogs.CreateAndOpenAsync($"{PdfReports.ReportLabel(bundles[0])}_{bundles[0].Report.Date:yyyy-MM-dd}.pdf", s => PdfReports.DailyReport(s, bundles[0], Desk.Db.MediaPath, cal, opt));
                break;
            case "xlsx":
                await Dialogs.CreateAndOpenAsync($"DPR_workbook_{stamp}.xlsx", s => ExcelReports.ReportWorkbook(s, Desk.Project, bundles, desc, cal));
                break;
            case "full":
                await Dialogs.CreateAndOpenAsync($"DPR_all_{stamp}.pdf", s => PdfReports.ReportRegister(s, Desk.Project, bundles, desc, true, Desk.Db.MediaPath, cal, opt));
                break;
            default:
                await Dialogs.CreateAndOpenAsync($"DPR_register_{stamp}.pdf", s => PdfReports.ReportRegister(s, Desk.Project, bundles, desc, false, Desk.Db.MediaPath, cal, opt));
                break;
        }
    }

    private async Task SetStatusAsync(ReportStatus status)
    {
        var sel = Selected();
        if (sel.Count == 0) { Dialogs.Info("Select one or more reports first."); return; }
        foreach (var r in sel)
        {
            r.Status = status;
            if (status == ReportStatus.Approved && string.IsNullOrWhiteSpace(r.ApprovedBy)) r.ApprovedBy = Desk.Db.UserName;
        }
        await Desk.Db.SaveAllAsync(sel);
        await ReloadAsync();
        Desk.Say($"{sel.Count} report(s) marked {status}");
    }

    private async Task DeleteAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        if (!Dialogs.Confirm($"Delete {sel.Count} report(s) with all their rows? They are removed on every synced device.")) return;
        foreach (var b in await Desk.Db.LoadReportsAsync(Desk.ProjectId, sel))
        {
            b.Activities.Clear(); b.Manpower.Clear(); b.Machines.Clear(); b.Materials.Clear(); b.Hindrances.Clear(); b.Requirements.Clear();
            await Desk.Db.SaveReportAsync(b);
            await Desk.Db.DeleteAsync(b.Report);
        }
        await ReloadAsync();
    }
}
