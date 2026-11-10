using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>Planned resources for an activity (inside the activity form).</summary>
public class ActivityResourceEditor : VerticalStackLayout
{
    private readonly Activity _activity;
    private List<ActivityResource> _rows = [];
    private List<ActivityResource> _original = [];
    private readonly Label _total = Ui.Label("", 13, true, Ui.Primary);
    private WorkCalendar _cal = new();

    public ActivityResourceEditor(Activity a)
    {
        _activity = a;
        Spacing = 6;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _cal = await AppServices.Db.CalendarAsync(AppServices.ProjectId);
        _original = await AppServices.Db.ListAsync<ActivityResource>(r => r.ActivityId == _activity.Id);
        _rows = [.. _original];
        Children.Clear();
        Children.Add(new RowList<ActivityResource>(_rows, Row, UpdateTotal, "No resources assigned. Add manpower, machines, materials or other costs.",
            ("＋ From resource list", async () =>
            {
                var res = await PickResourceAsync();
                return res == null ? null : new ActivityResource
                {
                    ResourceId = res.Id, ResourceName = res.ToString(), Type = res.Type, Rate = res.Rate, Basis = res.Basis,
                    PlannedQty = res.Type is ResourceType.Manpower or ResourceType.Machinery ? 1 : _activity.PlannedQty,
                };
            }),
            ("＋ Blank row", RowList<ActivityResource>.Now(() => new ActivityResource { Type = ResourceType.Manpower }))));
        Children.Add(_total);
        UpdateTotal();
    }

    public static async Task<SiteResource?> PickResourceAsync(ResourceType? type = null)
    {
        var all = (await AppServices.Db.ListForProjectAsync<SiteResource>(AppServices.ProjectId))
            .Where(r => type == null || r.Type == type).OrderBy(r => r.Type).ThenBy(r => r.Name).ToList();
        if (all.Count == 0)
        {
            await Shell.Current.DisplayAlertAsync("No resources", "Add resources and rates first (menu › Resources & rates).", "OK");
            return null;
        }
        var labels = all.Select(r => $"{r} · {ResourceListPage.RateText(r.Rate, r.Basis, r.Unit)}").ToArray();
        var choice = await Shell.Current.DisplayActionSheetAsync("Choose resource", "Cancel", null, labels);
        var i = Array.IndexOf(labels, choice);
        return i < 0 ? null : all[i];
    }

    private int Duration() =>
        _activity.PlannedStart is { } s && _activity.PlannedFinish is { } f ? _cal.WorkingDaysBetween(s, f)
        : _activity.PlannedRatePerDay > 0 ? (int)Math.Ceiling(_activity.PlannedQty / _activity.PlannedRatePerDay) : 1;

    private View Row(ActivityResource r)
    {
        var cost = Ui.Label("", 12, true, Ui.Primary);
        void Refresh() { cost.Text = $"Planned cost: {_cal.Currency} {PdfReports.M(CostCalculator.PlannedCost(r, Duration(), _cal))}"; UpdateTotal(); }
        var timeBased = r.Type is ResourceType.Manpower or ResourceType.Machinery;
        var view = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Columns(3,
                    Ui.Field("SiteResource", Ui.Entry(r.ResourceName, "Name", changed: v => r.ResourceName = v)),
                    Ui.Field("Type", Ui.EnumPicker(r.Type, v => { r.Type = v; Refresh(); })),
                    Ui.Field(timeBased ? "Nos (people / machines)" : "Quantity", Ui.Number(r.PlannedQty, v => { r.PlannedQty = v; Refresh(); })),
                    Ui.Field("Hours each (blank = full duration)", Ui.Number(r.PlannedHours, v => { r.PlannedHours = v; Refresh(); }, "auto")),
                    Ui.Field("Rate", EntityForms.DecimalEntry(r.Rate, v => { r.Rate = v; Refresh(); })),
                    Ui.Field("Basis", Ui.EnumPicker(r.Basis, v => { r.Basis = v; Refresh(); }))),
                cost,
            }
        };
        Refresh();
        return view;
    }

    private void UpdateTotal()
    {
        var d = Duration();
        _total.Text = $"Duration {d} working day(s) · planned budget {_cal.Currency} {PdfReports.M(_rows.Sum(r => CostCalculator.PlannedCost(r, d, _cal)))}";
    }

    public async Task SaveAsync()
    {
        foreach (var r in _rows) { r.ActivityId = _activity.Id; r.ProjectId = _activity.ProjectId; }
        await AppServices.Db.SaveAllAsync(_rows);
        foreach (var gone in _original.Where(o => !_rows.Contains(o))) await AppServices.Db.DeleteAsync(gone);
    }
}

/// <summary>Working hours, working weekdays, holidays, overtime factor and currency.</summary>
public class CalendarPage : ContentPage
{
    public CalendarPage()
    {
        Title = "Work calendar";
        this.Page();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!await AppShell.RequireProjectAsync()) return;
        var cal = await AppServices.Db.CalendarAsync(AppServices.ProjectId);
        var days = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap };
        string[] names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        for (var i = 0; i < 7; i++)
        {
            var d = i.ToString();
            var cb = new CheckBox { IsChecked = cal.WorkingDays.Contains(d), Color = Ui.Primary };
            cb.CheckedChanged += (_, a) =>
            {
                var set = cal.WorkingDays.ToHashSet();
                if (a.Value) set.Add(d[0]); else set.Remove(d[0]);
                cal.WorkingDays = new string(set.OrderBy(c => c).ToArray());
            };
            days.Children.Add(new HorizontalStackLayout { Margin = new Thickness(0, 0, 10, 0), Children = { cb, new Label { Text = names[i], VerticalOptions = LayoutOptions.Center } } });
        }
        var shift = new TimePicker { Time = cal.ShiftStart };
        shift.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TimePicker.Time) && shift.Time is { } t) cal.ShiftStart = t; };
        var preview = Ui.Caption("");
        void Preview()
        {
            var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            preview.Text = $"{start:MMMM yyyy}: {cal.WorkingDaysBetween(start, end)} working days · {cal.WorkingDaysBetween(start, end) * cal.HoursPerDay:0.#} working hours";
        }
        Preview();
        Content = Ui.Scroll(new VerticalStackLayout
        {
            Spacing = 10, MaximumWidthRequest = 800,
            Children =
            {
                Ui.Columns(2,
                    Ui.Field("Normal hours per day", Ui.Number(cal.HoursPerDay, v => { cal.HoursPerDay = v; Preview(); })),
                    Ui.Field("Shift start", shift),
                    Ui.Field("Overtime pay factor (e.g. 1.5 or 2)", EntityForms.DecimalEntry(cal.OvertimeFactor, v => cal.OvertimeFactor = v <= 0 ? 1 : v)),
                    Ui.Field("Currency symbol", Ui.Entry(cal.Currency, "₹", changed: v => cal.Currency = v))),
                Ui.Field("Working days", days),
                Ui.Field("Holidays (yyyy-mm-dd, comma separated)", Ui.Memo(cal.Holidays, "2026-11-14, 2026-12-25", v => { cal.Holidays = v; Preview(); }, 60)),
                preview,
                Ui.Caption("Used for: man-days (hours ÷ hours per day), per-day rates, overtime cost, planned durations and forecast finish dates."),
                Ui.Button("💾 Save calendar", async () =>
                {
                    cal.ProjectId = AppServices.ProjectId;
                    await AppServices.Db.SaveAsync(cal);
                    Preview();
                    await DisplayAlertAsync("Saved", "Calendar saved. Costs and forecasts use it from now on.", "OK");
                }),
            }
        });
    }
}

/// <summary>Live activity progress: % bars, forecast, cost per unit/day, grouped by WBS; PDF and Excel export.</summary>
public class ProgressPage : ContentPage
{
    private readonly VerticalStackLayout _body = new() { Spacing = 6, Padding = 12 };
    private readonly Picker _view = new() { ItemsSource = new[] { "All", "In progress", "Delayed", "Not started", "Completed" }, SelectedIndex = 0 };
    private readonly Picker _group = new() { ItemsSource = new[] { "WBS", "Agency", "Department", "None" }, SelectedIndex = 0 };
    private readonly Entry _search = new() { Placeholder = "🔍 Search activity", ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private ProjectProgress? _pp;

    public ProgressPage()
    {
        Title = "Progress & cost";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("PDF", null, () => _ = Ui.Guard(() => ExportAsync(false))));
        ToolbarItems.Add(new ToolbarItem("Excel", null, () => _ = Ui.Guard(() => ExportAsync(true))));
        ToolbarItems.Add(new ToolbarItem("＋ Activity", null, async () => { if (await AppShell.RequireProjectAsync()) await AppShell.Push(EntityForms.ActivityForm(new Activity { ProjectId = AppServices.ProjectId })); }));
        _view.SelectedIndexChanged += (_, _) => Render();
        _group.SelectedIndexChanged += (_, _) => Render();
        _search.TextChanged += (_, _) => Render();
        var bar = new Grid { ColumnDefinitions = [new(GridLength.Star), new(new GridLength(130)), new(new GridLength(130))], ColumnSpacing = 8, Padding = new Thickness(12, 8, 12, 0) };
        bar.Add(_search, 0); bar.Add(_view, 1); bar.Add(_group, 2);
        var g = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)] };
        g.Add(bar, 0, 0);
        g.Add(new ScrollView { Content = _body }, 0, 1);
        Content = g;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (AppServices.CurrentProject == null) { _body.Children.Clear(); _body.Children.Add(Ui.Caption("Select or create a project first.")); return; }
        _body.Children.Clear();
        _body.Children.Add(new ActivityIndicator { IsRunning = true });
        _pp = await Task.Run(() => new ProgressService(AppServices.Db).ComputeAsync(AppServices.ProjectId));
        Render();
    }

    private void Render()
    {
        if (_pp == null) return;
        var cur = _pp.Calendar.Currency;
        _body.Children.Clear();
        _body.Children.Add(Ui.Columns(4,
            Ui.Stat("Overall progress", $"{_pp.OverallPercent:0.#} %"),
            Ui.Stat("Planned to date", $"{_pp.PlannedPercent:0.#} %", Ui.Muted),
            Ui.Stat($"Actual cost ({cur})", PdfReports.M(_pp.Actual.Total), Ui.Accent),
            Ui.Stat($"Budget ({cur})", PdfReports.M(_pp.PlannedCost), Ui.Muted)));

        var s = _search.Text?.Trim() ?? "";
        var filter = _view.SelectedItem as string ?? "All";
        var items = _pp.Activities.Where(a =>
            (s == "" || a.Activity.ToString().Contains(s, StringComparison.OrdinalIgnoreCase) || a.WbsPath.Contains(s, StringComparison.OrdinalIgnoreCase)) &&
            (filter == "All" || ProgressReports.StateText(a.State) == filter)).ToList();
        if (items.Count == 0) _body.Children.Add(Ui.Caption("No activities. Tap ＋ Activity to plan the work."));

        var groupBy = _group.SelectedItem as string ?? "WBS";
        foreach (var grp in items.GroupBy(a => groupBy switch
                 {
                     "WBS" => a.WbsPath == "" ? "No WBS" : a.WbsPath,
                     "Agency" => a.Agency == "" ? "Own / no agency" : a.Agency,
                     "Department" => a.Department == "" ? "No department" : a.Department,
                     _ => "",
                 }))
        {
            if (grp.Key != "") _body.Children.Add(Ui.SectionHeader($"{grp.Key} · {grp.Count()}"));
            foreach (var a in grp) _body.Children.Add(Card(a, cur));
        }

        if (_pp.BySubcontractor.Count > 0)
        {
            _body.Children.Add(Ui.SectionHeader("Subcontractor-wise"));
            foreach (var p in _pp.BySubcontractor) _body.Children.Add(PartyCard(p, cur));
        }
        if (_pp.ByDepartment.Count > 0)
        {
            _body.Children.Add(Ui.SectionHeader("Department-wise"));
            foreach (var p in _pp.ByDepartment) _body.Children.Add(PartyCard(p, cur));
        }
    }

    private View Card(ActivityProgress a, string cur)
    {
        var color = a.State switch
        {
            WorkState.Completed => Ui.Success,
            WorkState.Delayed => Ui.Danger,
            WorkState.InProgress => Ui.Primary,
            _ => Ui.Muted,
        };
        var head = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        head.Add(Ui.Label(a.Activity.ToString(), 15, true), 0);
        head.Add(Ui.Chip(ProgressReports.StateText(a.State), color), 1);
        var card = Ui.Card(new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                head,
                new ProgressBar { Progress = a.Percent / 100, ProgressColor = color, HeightRequest = 8 },
                Ui.Label($"{a.Percent:0.##} % · {PdfReports.N(a.DoneQty)} of {PdfReports.N(a.Activity.PlannedQty)} {a.Activity.Unit} · balance {PdfReports.N(a.RemainingQty)}" +
                         (a.PlannedPercentToday > 0 ? $" · planned {a.PlannedPercentToday:0.#} %" : ""), 13),
                Ui.Caption($"Start {PdfReports.D(a.ActualStart)} · {a.DaysWorked} day(s) worked · {PdfReports.N(a.AvgPerDay)}/day · forecast finish {PdfReports.D(a.ForecastFinish)}" +
                           (a.Activity.PlannedFinish is { } pf ? $" (plan {pf:dd-MMM})" : "")),
                Ui.Label($"Cost {cur} {PdfReports.M(a.Actual.Total)}" +
                         (a.DoneQty > 0 ? $" · {PdfReports.M(a.CostPerUnit)}/{a.Activity.Unit}" : "") +
                         (a.DaysWorked > 0 ? $" · {PdfReports.M(a.CostPerDay)}/day" : "") +
                         (a.Actual.ManHours > 0 ? $" · {PdfReports.N(a.Actual.ManHours)} man-hrs" : "") +
                         (a.PlannedCost > 0 ? $" · budget {PdfReports.M(a.PlannedCost)}" : ""), 12, false, Ui.Primary),
            }
        });
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await AppShell.Push(EntityForms.ActivityForm(a.Activity));
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private static View PartyCard(PartySummary p, string cur) => Ui.Card(new VerticalStackLayout
    {
        Spacing = 2,
        Children =
        {
            Ui.Label(p.Name, 15, true),
            Ui.Label($"{PdfReports.N(p.ManDays)} man-days · {PdfReports.N(p.ManHours)} man-hrs · {cur} {PdfReports.M(p.Cost)} · {p.Activities} activities", 13),
            Ui.Caption(p.ActivitiesDone),
        }
    });

    private async Task ExportAsync(bool excel)
    {
        if (_pp == null) return;
        var period = $"As of {DateTime.Today:dd-MMM-yyyy}";
        var opt = await ReportProfiles.CurrentAsync();
        var path = await FileExport.CreateAsync($"Progress_cost_{DateTime.Now:yyyyMMdd_HHmm}.{(excel ? "xlsx" : "pdf")}", s =>
        {
            if (excel) ProgressReports.Excel(s, AppServices.CurrentProject, _pp, period);
            else ProgressReports.Pdf(s, AppServices.CurrentProject, _pp, period, true, opt);
        });
        await FileExport.OfferAsync(this, path);
    }
}
