using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;

namespace SiteNotes.App.Pages;

/// <summary>
/// Shared filter: search, period chips (with custom dates), status chips (multi), agency, and sort chips.
/// Every tap applies immediately.
/// </summary>
public class FilterBar : VerticalStackLayout
{
    public ReportFilter Filter { get; } = new();
    public event Action? Changed;

    private readonly DatePicker _from = new() { Format = "dd-MMM-yyyy" };
    private readonly DatePicker _to = new() { Format = "dd-MMM-yyyy" };
    private readonly HorizontalStackLayout _custom;
    private readonly Picker _sub = new() { Title = "All agencies" };
    private readonly VerticalStackLayout _more = new() { Spacing = 6, IsVisible = false };

    private static readonly string[] Periods = ["All", "Today", "Yesterday", "This week", "Last 7 days", "This month", "Last month", "Custom"];

    public FilterBar(bool compact = true)
    {
        Spacing = 6;
        Padding = new Thickness(12, 8, 12, 0);

        var search = new Entry { Placeholder = "🔍 Search remarks, activities, report no.", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
        search.TextChanged += (_, a) => { Filter.Search = a.NewTextValue ?? ""; Changed?.Invoke(); };

        var period = new ChipGroup<string>(Periods.Select(p => (p, p)), ["All"]);
        _custom = new HorizontalStackLayout { Spacing = 8, IsVisible = false, Children = { Ui.Caption("From"), _from, Ui.Caption("to"), _to } };
        period.Changed += () => ApplyPeriod(period.Value ?? "All");
        _from.DateSelected += (_, a) => { Filter.From = a.NewDate; Changed?.Invoke(); };
        _to.DateSelected += (_, a) => { Filter.To = a.NewDate; Changed?.Invoke(); };

        var status = new ChipGroup<ReportStatus>(Enum.GetValues<ReportStatus>().Select(s => (s, s.ToString())), [], multi: true);
        status.Changed += () => { Filter.Statuses = [.. status.Values]; Changed?.Invoke(); };

        var sort = new ChipGroup<ReportSort>([
            (ReportSort.DateNewest, "Newest"), (ReportSort.DateOldest, "Oldest"), (ReportSort.ReportNo, "Report no."),
            (ReportSort.ManpowerHigh, "Most manpower"), (ReportSort.ManpowerLow, "Least manpower"), (ReportSort.Status, "Status"),
        ], [ReportSort.DateNewest]);
        sort.Changed += () => { Filter.Sort = sort.Value; Changed?.Invoke(); };

        _sub.SelectedIndexChanged += (_, _) => { Filter.SubcontractorId = (_sub.SelectedItem as Subcontractor)?.Id ?? ""; Changed?.Invoke(); };

        _more.Children.Add(Row("Status", status));
        _more.Children.Add(Row("Agency", _sub));
        _more.Children.Add(Row("Sort", sort));

        var toggle = new Button { Text = "Filter & sort ▾", FontSize = 13, Padding = new Thickness(10, 4), BackgroundColor = Colors.Transparent, TextColor = Ui.Primary, HorizontalOptions = LayoutOptions.Start };
        toggle.Clicked += (_, _) => { _more.IsVisible = !_more.IsVisible; toggle.Text = _more.IsVisible ? "Filter & sort ▴" : "Filter & sort ▾"; };
        if (!compact) { _more.IsVisible = true; toggle.IsVisible = false; }

        Children.Add(search);
        Children.Add(period);
        Children.Add(_custom);
        Children.Add(toggle);
        Children.Add(_more);
    }

    private static View Row(string label, View content) =>
        new VerticalStackLayout { Spacing = 2, Children = { Ui.Caption(label), content } };

    private void ApplyPeriod(string p)
    {
        var t = DateTime.Today;
        var weekStart = t.AddDays(-(((int)t.DayOfWeek + 6) % 7));
        var m = new DateTime(t.Year, t.Month, 1);
        _custom.IsVisible = p == "Custom";
        (Filter.From, Filter.To) = p switch
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
        if (p == "Custom") { _from.Date = Filter.From; _to.Date = Filter.To; }
        Changed?.Invoke();
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
