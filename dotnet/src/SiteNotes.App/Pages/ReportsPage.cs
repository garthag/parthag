using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>Creating reports: number and date are automatic; the first number is asked only once per project.</summary>
public static class DprFlow
{
    public static async Task NewAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var db = AppServices.Db;
        var pid = AppServices.ProjectId;
        var page = Shell.Current.CurrentPage;

        if (await db.NeedsFirstReportNoAsync(pid))
        {
            var text = await page.DisplayPromptAsync("First report number",
                "Number of this project's first daily report. Every next report is numbered automatically.",
                "Start", "Cancel", "1", maxLength: 6, keyboard: Keyboard.Numeric, initialValue: "1");
            if (text == null) return;
            if (!int.TryParse(text, out var first) || first < 1) first = 1;
            var project = await db.GetAsync<Project>(pid);
            if (project != null)
            {
                project.FirstReportNo = first;
                await db.SaveAsync(project);
            }
        }

        var date = await db.NextReportDateAsync(pid);
        var existing = (await db.ListForProjectAsync<DailyReport>(pid)).Where(r => r.Date.Date == date).OrderBy(r => r.ReportNo).ToList();
        if (existing.Count > 0)
        {
            var open = $"Open {existing[^1].Title}";
            var c = await page.DisplayActionSheetAsync($"There is already a report for {date:ddd dd-MMM}", "Cancel", null, open, "New report (another shift)");
            if (c == open) { await AppShell.Push(new DprEditorPage(existing[^1].Id)); return; }
            if (c != "New report (another shift)") return;
        }
        await AppShell.Push(new DprEditorPage(null, date));
    }

    /// <summary>Today's report if it exists, otherwise a new one.</summary>
    public static async Task TodayAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var today = (await AppServices.Db.ListForProjectAsync<DailyReport>(AppServices.ProjectId)).FirstOrDefault(r => r.Date.Date == DateTime.Today);
        if (today != null) await AppShell.Push(new DprEditorPage(today.Id));
        else await NewAsync();
    }
}

/// <summary>Daily reports: search, period, status, agency and sort; tap to open; Select for bulk actions.</summary>
public class ReportsPage : ContentPage
{
    private readonly FilterBar _filter = new();
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single, EmptyView = Ui.Caption("No reports match. Tap ＋ New DPR.") };
    private readonly Label _summary = Ui.Caption("");
    private readonly Grid _bulkBar = new() { IsVisible = false, Padding = new Thickness(12, 6), ColumnSpacing = 6, BackgroundColor = Ui.PrimaryDark };
    private readonly Label _bulkCount = new() { TextColor = Colors.White, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
    private List<DailyReport> _items = [];
    private bool _selecting;

    public ReportsPage()
    {
        Title = "Daily reports";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("＋ New DPR", null, () => _ = Ui.Guard(DprFlow.NewAsync)));
        ToolbarItems.Add(new ToolbarItem("☑ Select", null, () => SetSelecting(!_selecting)));
        ToolbarItems.Add(new ToolbarItem("PDF", null, () => _ = Ui.Guard(() => ExportAsync(_items, "pdf"))));
        ToolbarItems.Add(new ToolbarItem("Excel", null, () => _ = Ui.Guard(() => ExportAsync(_items, "xlsx"))));
        ToolbarItems.Add(new ToolbarItem("Report manager…", null, () => _ = AppShell.Push(new ReportCenterPage())) { Order = ToolbarItemOrder.Secondary });

        _list.ItemTemplate = new DataTemplate(() =>
        {
            var t = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold };
            t.SetBinding(Label.TextProperty, nameof(ListRow.Title));
            t.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#E5E7EB"));
            var s = new Label { FontSize = 12, TextColor = Ui.Muted };
            s.SetBinding(Label.TextProperty, nameof(ListRow.Subtitle));
            var d = new Label { FontSize = 12, TextColor = Ui.Primary, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation };
            d.SetBinding(Label.TextProperty, nameof(ListRow.Detail));
            var c = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold };
            c.SetBinding(Label.TextProperty, nameof(ListRow.Chip));
            c.SetBinding(Label.TextColorProperty, nameof(ListRow.ChipColor));
            var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
            g.Add(new VerticalStackLayout { Children = { t, s, d } }, 0);
            g.Add(c, 1);
            return new ContentView { Padding = new Thickness(12, 3), Content = Ui.Card(g, 10) };
        });
        _list.SelectionChanged += async (_, e) =>
        {
            if (_selecting) { _bulkCount.Text = $"{e.CurrentSelection.Count} selected"; return; }
            if (e.CurrentSelection.FirstOrDefault() is not ListRow row) return;
            _list.SelectedItem = null;
            await AppShell.Push(new DprEditorPage(((DailyReport)row.Item).Id));
        };
        _filter.Changed += async () => await Ui.Guard(ReloadAsync);
        _summary.Margin = new Thickness(16, 4);

        _bulkBar.ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto)];
        _bulkBar.Add(_bulkCount, 0);
        _bulkBar.Add(BulkButton("Actions…", BulkActionsAsync), 1);
        _bulkBar.Add(BulkButton("All", () => { _list.SelectedItems = [.. (IEnumerable<ListRow>)_list.ItemsSource]; return Task.CompletedTask; }), 2);
        _bulkBar.Add(BulkButton("Done", () => { SetSelecting(false); return Task.CompletedTask; }), 3);

        var grid = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        grid.Add(_filter, 0, 0);
        grid.Add(_summary, 0, 1);
        grid.Add(_bulkBar, 0, 2);
        grid.Add(_list, 0, 3);
        Content = grid;
    }

    private static Button BulkButton(string text, Func<Task> action)
    {
        var b = new Button { Text = text, BackgroundColor = Colors.White, TextColor = Ui.Primary, Padding = new Thickness(10, 4), CornerRadius = 6, FontSize = 13 };
        b.Clicked += async (_, _) => await Ui.Guard(action);
        return b;
    }

    private void SetSelecting(bool on)
    {
        _selecting = on;
        _list.SelectionMode = on ? SelectionMode.Multiple : SelectionMode.Single;
        _list.SelectedItems?.Clear();
        _list.SelectedItem = null;
        _bulkBar.IsVisible = on;
        _bulkCount.Text = "Tap reports to select";
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _filter.LoadAsync();
        await Ui.Guard(ReloadAsync);
    }

    private async Task ReloadAsync()
    {
        _filter.Filter.ProjectId = AppServices.ProjectId;
        _items = await AppServices.Reports.FindAsync(_filter.Filter);
        var cal = await AppServices.Db.CalendarAsync(AppServices.ProjectId);
        _list.ItemsSource = _items.Select(r => new ListRow(r, r.Title,
            $"👷 {r.TotalManpower}  ·  ⏱ {PdfReports.N(r.TotalManHours)} man-hrs  ·  🚜 {r.TotalMachines}  ·  🏗 {r.ActivityCount}" + (r.HindranceCount > 0 ? $"  ·  ⚠ {r.HindranceCount}" : ""),
            r.Status.ToString().ToUpperInvariant(), Ui.StatusColor(r.Status.ToString()),
            (r.TotalCost > 0 ? $"{cal.Currency} {PdfReports.M(r.TotalCost)} · " : "") + r.WeatherMorning + (string.IsNullOrWhiteSpace(r.Remarks) ? "" : $" · {r.Remarks}"))).ToList();
        _summary.Text = $"{_items.Count} report(s) · {_items.Sum(r => r.TotalManpower)} man-days" + (_items.Any(r => r.TotalCost > 0) ? $" · {cal.Currency} {PdfReports.M(_items.Sum(r => r.TotalCost))}" : "");
    }

    private List<DailyReport> Selected() => _list.SelectedItems?.OfType<ListRow>().Select(r => (DailyReport)r.Item).ToList() ?? [];

    private async Task BulkActionsAsync()
    {
        var sel = Selected();
        if (sel.Count == 0) { await DisplayAlertAsync("Nothing selected", "Tap reports to select them first.", "OK"); return; }
        var c = await DisplayActionSheetAsync($"{sel.Count} report(s)", "Cancel", "Delete",
            "PDF – all in one", "PDF – register summary", "Excel workbook", "Mark Submitted", "Mark Approved", "Back to Draft");
        switch (c)
        {
            case "PDF – all in one": await ExportAsync(sel, "full"); break;
            case "PDF – register summary": await ExportAsync(sel, "pdf"); break;
            case "Excel workbook": await ExportAsync(sel, "xlsx"); break;
            case "Mark Submitted": await SetStatusAsync(sel, ReportStatus.Submitted); break;
            case "Mark Approved": await SetStatusAsync(sel, ReportStatus.Approved); break;
            case "Back to Draft": await SetStatusAsync(sel, ReportStatus.Draft); break;
            case "Delete":
                if (!await DisplayAlertAsync($"Delete {sel.Count} report(s)?", "They are removed on every synced device.", "Delete", "Cancel")) return;
                foreach (var b in await AppServices.Db.LoadReportsAsync(AppServices.ProjectId, sel))
                {
                    b.Activities.Clear(); b.Manpower.Clear(); b.Machines.Clear(); b.Materials.Clear(); b.Hindrances.Clear(); b.Requirements.Clear();
                    await AppServices.Db.SaveReportAsync(b);
                    await AppServices.Db.DeleteAsync(b.Report);
                }
                break;
            default: return;
        }
        SetSelecting(false);
        await ReloadAsync();
    }

    private static async Task SetStatusAsync(List<DailyReport> reports, ReportStatus status)
    {
        foreach (var r in reports)
        {
            r.Status = status;
            if (status == ReportStatus.Approved && string.IsNullOrWhiteSpace(r.ApprovedBy)) r.ApprovedBy = AppServices.Db.UserName;
        }
        await AppServices.Db.SaveAllAsync(reports);
    }

    private async Task ExportAsync(List<DailyReport> reports, string kind)
    {
        if (reports.Count == 0) { await DisplayAlertAsync("Nothing to export", "No reports match the filter.", "OK"); return; }
        var bundles = await AppServices.Db.LoadReportsAsync(AppServices.ProjectId, _filter.Filter.ApplySort(reports).ToList());
        var cal = await AppServices.Db.CalendarAsync(AppServices.ProjectId);
        var opt = await ReportProfiles.CurrentAsync();
        var desc = reports.Count == _items.Count ? _filter.Describe() : $"{reports.Count} selected report(s)";
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var path = kind switch
        {
            "xlsx" => await FileExport.CreateAsync($"DPR_register_{stamp}.xlsx", s => ExcelReports.ReportWorkbook(s, AppServices.CurrentProject, bundles, desc, cal)),
            "full" => await FileExport.CreateAsync($"DPR_all_{stamp}.pdf", s => PdfReports.ReportRegister(s, AppServices.CurrentProject, bundles, desc, true, AppServices.Db.MediaPath, cal, opt)),
            _ => await FileExport.CreateAsync($"DPR_register_{stamp}.pdf", s => PdfReports.ReportRegister(s, AppServices.CurrentProject, bundles, desc, false, AppServices.Db.MediaPath, cal, opt)),
        };
        await FileExport.OfferAsync(this, path);
    }
}
