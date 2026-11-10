using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>Shared filter bar: date preset, from/to, status, subcontractor, search and sort.</summary>
public class FilterBar : VerticalStackLayout
{
    public ReportFilter Filter { get; } = new();
    public event Action? Changed;
    private readonly DatePicker _from = new() { Format = "dd-MMM-yyyy" };
    private readonly DatePicker _to = new() { Format = "dd-MMM-yyyy" };
    private readonly Picker _sub = new() { Title = "Subcontractor" };

    public FilterBar()
    {
        Spacing = 6;
        Padding = new Thickness(12, 8, 12, 0);
        var preset = new Picker { Title = "Period", ItemsSource = new[] { "All dates", "Today", "Yesterday", "This week", "Last 7 days", "This month", "Last month", "Custom" }, SelectedIndex = 0 };
        var status = new Picker { Title = "Status", ItemsSource = new[] { "Any status", "Draft", "Submitted", "Approved" }, SelectedIndex = 0 };
        var sort = new Picker
        {
            Title = "Sort",
            ItemsSource = new[] { "Newest first", "Oldest first", "Report no.", "Most manpower", "Least manpower", "Status" },
            SelectedIndex = 0,
        };
        var search = new Entry { Placeholder = "🔍 Search remarks, activities, report no.", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
        _from.IsEnabled = _to.IsEnabled = false;

        preset.SelectedIndexChanged += (_, _) =>
        {
            var t = DateTime.Today;
            var weekStart = t.AddDays(-(((int)t.DayOfWeek + 6) % 7));
            var m = new DateTime(t.Year, t.Month, 1);
            (DateTime? f, DateTime? to) = (preset.SelectedItem as string) switch
            {
                "Today" => (t, t),
                "Yesterday" => (t.AddDays(-1), t.AddDays(-1)),
                "This week" => (weekStart, t),
                "Last 7 days" => (t.AddDays(-6), t),
                "This month" => (m, t),
                "Last month" => (m.AddMonths(-1), m.AddDays(-1)),
                "Custom" => (_from.Date ?? t.AddDays(-30), _to.Date ?? t),
                _ => ((DateTime?)null, (DateTime?)null),
            };
            var custom = preset.SelectedItem as string == "Custom";
            _from.IsEnabled = _to.IsEnabled = custom;
            if (f != null) _from.Date = f;
            if (to != null) _to.Date = to;
            Filter.From = f;
            Filter.To = to;
            Changed?.Invoke();
        };
        _from.DateSelected += (_, a) => { if (_from.IsEnabled) { Filter.From = a.NewDate; Changed?.Invoke(); } };
        _to.DateSelected += (_, a) => { if (_to.IsEnabled) { Filter.To = a.NewDate; Changed?.Invoke(); } };
        status.SelectedIndexChanged += (_, _) =>
        {
            Filter.Status = Enum.TryParse<ReportStatus>(status.SelectedItem as string, out var s) ? s : null;
            Changed?.Invoke();
        };
        sort.SelectedIndexChanged += (_, _) => { Filter.Sort = (ReportSort)Math.Max(0, sort.SelectedIndex); Changed?.Invoke(); };
        search.TextChanged += (_, a) => { Filter.Search = a.NewTextValue ?? ""; Changed?.Invoke(); };
        _sub.SelectedIndexChanged += (_, _) => { Filter.SubcontractorId = (_sub.SelectedItem as Subcontractor)?.Id ?? ""; Changed?.Invoke(); };

        Children.Add(search);
        Children.Add(Ui.Columns(6, preset, _from, _to, status, _sub, sort));
    }

    public async Task LoadAsync()
    {
        Filter.ProjectId = AppServices.ProjectId;
        var subs = (await AppServices.Db.ListForProjectAsync<Subcontractor>(AppServices.ProjectId)).OrderBy(s => s.Name).ToList();
        subs.Insert(0, new Subcontractor { Id = "", Name = "All agencies" });
        var keep = Filter.SubcontractorId;
        _sub.ItemsSource = subs;
        _sub.SelectedItem = subs.FirstOrDefault(s => s.Id == keep) ?? subs[0];
    }

    public string Describe() => Filter.Describe();
}

/// <summary>List of daily reports with filter, sort and register exports.</summary>
public class ReportsPage : ContentPage
{
    private readonly FilterBar _filter = new();
    private readonly CollectionView _list = new() { SelectionMode = SelectionMode.Single, EmptyView = Ui.Caption("No reports match. Tap ＋ New DPR.") };
    private readonly Label _summary = Ui.Caption("");
    private List<DailyReport> _items = [];

    public ReportsPage()
    {
        Title = "Daily reports";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("＋ New DPR", null, () => _ = Ui.Guard(NewAsync)));
        ToolbarItems.Add(new ToolbarItem("PDF", null, () => _ = Ui.Guard(() => ExportAsync("pdf"))));
        ToolbarItems.Add(new ToolbarItem("Excel", null, () => _ = Ui.Guard(() => ExportAsync("xlsx"))));
        ToolbarItems.Add(new ToolbarItem("PDF with full reports", null, () => _ = Ui.Guard(() => ExportAsync("full"))) { Order = ToolbarItemOrder.Secondary });

        _list.ItemTemplate = new DataTemplate(() =>
        {
            var t = new Label { FontSize = 15, FontAttributes = FontAttributes.Bold };
            t.SetBinding(Label.TextProperty, nameof(ListRow.Title));
            t.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#111827"), Color.FromArgb("#E5E7EB"));
            var s = new Label { FontSize = 12, TextColor = Ui.Muted };
            s.SetBinding(Label.TextProperty, nameof(ListRow.Subtitle));
            var d = new Label { FontSize = 12, TextColor = Ui.Primary };
            d.SetBinding(Label.TextProperty, nameof(ListRow.Detail));
            var c = new Label { FontSize = 11, FontAttributes = FontAttributes.Bold };
            c.SetBinding(Label.TextProperty, nameof(ListRow.Chip));
            c.SetBinding(Label.TextColorProperty, nameof(ListRow.ChipColor));
            var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
            g.Add(new VerticalStackLayout { Children = { t, s, d } }, 0);
            g.Add(c, 1);
            return new ContentView { Padding = new Thickness(12, 4), Content = Ui.Card(g) };
        });
        _list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is not ListRow row) return;
            _list.SelectedItem = null;
            await AppShell.Push(new DprEditorPage(((DailyReport)row.Item).Id));
        };
        _filter.Changed += async () => await Ui.Guard(ReloadAsync);
        _summary.Margin = new Thickness(16, 4);

        var g = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        g.Add(_filter, 0, 0);
        g.Add(_summary, 0, 1);
        g.Add(_list, 0, 2);
        Content = g;
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
        _list.ItemsSource = _items.Select(r => new ListRow(r, $"DPR #{r.ReportNo} · {r.Date:ddd, dd-MMM-yyyy}",
            $"{r.TotalManpower} manpower · {PdfReports.N(r.TotalManHours)} man-hrs · {r.TotalMachines} machines · {r.ActivityCount} activities · {r.HindranceCount} hindrances",
            r.Status.ToString().ToUpperInvariant(), Ui.StatusColor(r.Status.ToString()),
            $"{r.WeatherMorning} · {cal.Currency} {PdfReports.M(r.TotalCost)}" + (string.IsNullOrWhiteSpace(r.Remarks) ? "" : $" · {r.Remarks}"))).ToList();
        _summary.Text = $"{_items.Count} report(s) · {_items.Sum(r => r.TotalManpower)} man-days · {cal.Currency} {PdfReports.M(_items.Sum(r => r.TotalCost))} · {_filter.Describe()}";
    }

    private async Task NewAsync()
    {
        if (!await AppShell.RequireProjectAsync()) return;
        var choice = await DisplayActionSheetAsync("New report for", "Cancel", null, "Today", "Yesterday", "Pick a date…");
        DateTime? d = choice switch { "Today" => DateTime.Today, "Yesterday" => DateTime.Today.AddDays(-1), "Pick a date…" => DateTime.Today, _ => null };
        if (d == null) return;
        await AppShell.Push(new DprEditorPage(null, d));
    }

    private async Task ExportAsync(string kind)
    {
        if (_items.Count == 0) { await DisplayAlertAsync("Nothing to export", "No reports match the filter.", "OK"); return; }
        var bundles = await AppServices.Reports.LoadBundlesAsync(_filter.Filter);
        var cal = await AppServices.Db.CalendarAsync(AppServices.ProjectId);
        var desc = _filter.Describe();
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        var path = kind switch
        {
            "xlsx" => await FileExport.CreateAsync($"DPR_register_{stamp}.xlsx", s => ExcelReports.ReportWorkbook(s, AppServices.CurrentProject, bundles, desc, cal)),
            "full" => await FileExport.CreateAsync($"DPR_full_{stamp}.pdf", s => PdfReports.ReportRegister(s, AppServices.CurrentProject, bundles, desc, true, AppServices.Db.MediaPath, cal)),
            _ => await FileExport.CreateAsync($"DPR_register_{stamp}.pdf", s => PdfReports.ReportRegister(s, AppServices.CurrentProject, bundles, desc, false, AppServices.Db.MediaPath, cal)),
        };
        await FileExport.OfferAsync(this, path);
    }
}
