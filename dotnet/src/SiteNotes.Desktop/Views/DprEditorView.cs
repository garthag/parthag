using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

/// <summary>Grid row that can tell the grid its calculated cells changed.</summary>
public abstract class LiveRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public class ActRow(ReportActivity r, double doneBefore) : LiveRow
{
    public ReportActivity R { get; } = r;
    public double DoneBefore { get; set; } = doneBefore;
    public bool Linked => R.ProgressKey != "";
    public string Source => R.DrawingItemId != "" ? "Drawing" : R.ActivityId != "" ? "Plan" : "Unplanned";
    public double ToDate => Math.Round((Linked ? DoneBefore : 0) + R.TodayQty, 4);
    public double Balance => R.PlannedQty > 0 ? Math.Max(0, R.PlannedQty - ToDate) : 0;
    public double Percent => R.PlannedQty > 0 ? Math.Min(100, ToDate / R.PlannedQty * 100) : 0;
    public string Check => R.PlannedQty > 0 && ToDate > R.PlannedQty ? $"⚠ exceeds plan by {PdfReports.N(ToDate - R.PlannedQty)}" : Percent >= 100 ? "✔ completed" : "";
}

public class ManRow(ReportManpower m, Func<WorkCalendar> cal, Func<string, string> activityName, Func<string, (ManpowerSource, string)> party) : LiveRow
{
    public ReportManpower M { get; } = m;
    /// <summary>"S:id" for a subcontractor, "D:id" for a department.</summary>
    public string PartyKey
    {
        get => (M.Source == ManpowerSource.Subcontractor ? "S:" : "D:") + M.PartyId;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            M.Source = value.StartsWith("S:") ? ManpowerSource.Subcontractor : ManpowerSource.Department;
            M.PartyId = value[2..];
            M.PartyName = party(value).Item2;
        }
    }
    public string ActivityId { get => M.ActivityId; set { M.ActivityId = value ?? ""; M.ActivityName = activityName(M.ActivityId); } }
    public double ManHours => M.Count * M.Hours;
    public decimal Cost => CostCalculator.Cost(M, cal());
}

public class MachineRow(ReportMachine m, Func<WorkCalendar> cal, Func<string, string> activityName) : LiveRow
{
    public ReportMachine M { get; } = m;
    public string ActivityId { get => M.ActivityId; set { M.ActivityId = value ?? ""; M.ActivityName = activityName(M.ActivityId); } }
    public decimal Cost => CostCalculator.Cost(M, cal());
}

public class MaterialRow(ReportMaterial m, Func<string, string> activityName) : LiveRow
{
    public ReportMaterial M { get; } = m;
    public string ActivityId { get => M.ActivityId; set { M.ActivityId = value ?? ""; M.ActivityName = activityName(M.ActivityId); } }
    public decimal Cost => CostCalculator.Cost(M);
}

/// <summary>
/// Daily progress report editor. Header + live summary, then one tab per section with a
/// spreadsheet-style grid (type, Tab, Enter). Cumulative quantity, %, man-hours and cost calculate as you type.
/// </summary>
public class DprEditorView : ViewBase, ISaveable, IPrintable
{
    private readonly string? _reportId;
    private readonly DateTime _newDate;
    private readonly string _key = "dpr:" + Guid.NewGuid().ToString("N");
    private DprBundle _b = new();
    private WorkCalendar _cal = new();
    private List<Activity> _activities = [];
    private List<Subcontractor> _subs = [];
    private List<Department> _depts = [];
    private Dictionary<string, double> _doneBefore = [];
    private bool _dirty;

    private readonly ObservableCollection<ActRow> _acts = [];
    private readonly ObservableCollection<ManRow> _men = [];
    private readonly ObservableCollection<MachineRow> _machines = [];
    private readonly ObservableCollection<MaterialRow> _materials = [];
    private readonly ObservableCollection<Hindrance> _hindrances = [];
    private readonly ObservableCollection<Requirement> _requirements = [];
    private readonly WrapPanel _summary = new();
    private readonly TabControl _tabs = new() { Margin = new Thickness(10, 0, 10, 10) };
    private readonly TextBlock _statusBadge = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

    public DprEditorView(string? reportId, DateTime? newDate = null)
    {
        _reportId = reportId;
        _newDate = newDate ?? DateTime.Today;
        if (reportId != null) _key = "dpr:" + reportId;
    }

    public override string Title => (_dirty ? "* " : "") + (_b.Report.ReportNo == 0 ? "New DPR" : $"{PdfReports.ReportLabel(_b)} · {_b.Report.Date:dd-MMM}");
    public override string Key => _key;
    public bool IsDirty => _dirty;

    private void Dirty()
    {
        var was = _dirty;
        _dirty = true;
        Recalc();
        if (!was) MainWindow.Current.Retitle(this);
    }

    public override async Task RefreshAsync()
    {
        if (_dirty) return; // never throw away typing
        var pid = Desk.ProjectId;
        _cal = await Desk.Db.CalendarAsync(pid);
        _activities = (await Desk.Db.ListForProjectAsync<Activity>(pid)).OrderBy(a => a.Code).ThenBy(a => a.Name).ToList();
        _subs = (await Desk.Db.ListForProjectAsync<Subcontractor>(pid)).OrderBy(s => s.Name).ToList();
        _depts = (await Desk.Db.ListForProjectAsync<Department>(pid)).OrderBy(d => d.Name).ToList();
        _b = _reportId != null && (await Desk.Db.GetAsync<DailyReport>(_reportId)) != null
            ? await Desk.Db.LoadReportAsync(_reportId)
            : await Desk.Db.NewReportAsync(pid, _newDate);
        await LoadDoneBeforeAsync();
        Fill();
        Build();
        Recalc();
        MainWindow.Current.Retitle(this);
    }

    private async Task LoadDoneBeforeAsync()
    {
        var r = _b.Report;
        var earlier = (await Desk.Db.ListForProjectAsync<DailyReport>(r.ProjectId))
            .Where(x => x.Id != r.Id && (x.Date.Date < r.Date.Date || (x.Date.Date == r.Date.Date && x.ReportNo < r.ReportNo)))
            .Select(x => x.Id).ToHashSet();
        _doneBefore = (await Desk.Db.ListForProjectAsync<ReportActivity>(r.ProjectId))
            .Where(x => x.ProgressKey != "" && earlier.Contains(x.ReportId))
            .GroupBy(x => x.ProgressKey).ToDictionary(g => g.Key, g => g.Sum(x => x.TodayQty));
        foreach (var a in _acts) { a.DoneBefore = _doneBefore.GetValueOrDefault(a.R.ProgressKey); a.Notify(); }
    }

    private string ActName(string id) => _activities.FirstOrDefault(a => a.Id == id)?.ToString() ?? "";
    private (ManpowerSource, string) Party(string key) => key.StartsWith("S:")
        ? (ManpowerSource.Subcontractor, _subs.FirstOrDefault(s => s.Id == key[2..])?.Name ?? "")
        : (ManpowerSource.Department, _depts.FirstOrDefault(d => d.Id == key[2..])?.Name ?? "");

    private void Fill()
    {
        _acts.Clear(); foreach (var a in _b.Activities) _acts.Add(new ActRow(a, _doneBefore.GetValueOrDefault(a.ProgressKey)));
        _men.Clear(); foreach (var m in _b.Manpower) _men.Add(new ManRow(m, () => _cal, ActName, Party));
        _machines.Clear(); foreach (var m in _b.Machines) _machines.Add(new MachineRow(m, () => _cal, ActName));
        _materials.Clear(); foreach (var m in _b.Materials) _materials.Add(new MaterialRow(m, ActName));
        _hindrances.Clear(); foreach (var h in _b.Hindrances) _hindrances.Add(h);
        _requirements.Clear(); foreach (var q in _b.Requirements) _requirements.Add(q);
    }

    /// <summary>Copies the grid rows back into the bundle (order = grid order).</summary>
    private void Collect()
    {
        _b.Activities = _acts.Select(a => { a.R.CumulativeQty = a.ToDate; return a.R; }).ToList();
        _b.Manpower = _men.Select(m => m.M).ToList();
        _b.Machines = _machines.Select(m => m.M).ToList();
        _b.Materials = _materials.Select(m => m.M).ToList();
        _b.Hindrances = [.. _hindrances];
        _b.Requirements = [.. _requirements];
    }

    // ------------------------------------------------------------------ layout

    private void Build()
    {
        var r = _b.Report;
        var locked = r.Status == ReportStatus.Approved;
        var tb = W.ToolBar(
            (Glyph.Save, "Save  (Ctrl+S)", SaveAsync),
            (Glyph.Print, "PDF  (Ctrl+P)", PdfAsync),
            (Glyph.Excel, "Excel", ExcelAsync),
            null,
            (Glyph.Accept, "Submit", () => SetStatusAsync(ReportStatus.Submitted)),
            (Glyph.Accept, "Approve & lock", () => SetStatusAsync(ReportStatus.Approved)),
            (Glyph.Edit, "Unlock (draft)", () => SetStatusAsync(ReportStatus.Draft)),
            null,
            (Glyph.Delete, "Delete report", DeleteAsync));
        _statusBadge.Text = locked ? "🔒 APPROVED – read only" : r.Status.ToString().ToUpperInvariant();
        _statusBadge.Foreground = Theme.StatusBrush(r.Status.ToString());
        tb.Items.Add(_statusBadge);

        var header = W.Card(W.Form(6,
            W.Field("Report no.", W.Text(PdfReports.ReportLabel(_b), 16, true, Theme.Primary)),
            W.Field("Date", W.Date(r.Date, v => { if (v is { } d) { r.Date = d; _ = LoadDoneBeforeAsync(); Dirty(); } })),
            W.Field("Shift", W.Combo(Enum.GetValues<Shift>(), r.Shift, v => { r.Shift = v; Dirty(); })),
            W.Field("Weather – morning", W.Suggest(r.WeatherMorning, Lists.Weather, v => { r.WeatherMorning = v; Dirty(); })),
            W.Field("Weather – evening", W.Suggest(r.WeatherEvening, Lists.Weather, v => { r.WeatherEvening = v; Dirty(); })),
            W.Field("Working hours", W.Number(r.WorkingHours, v => { r.WorkingHours = v; Dirty(); })),
            W.Field("Temperature °C", W.Number(r.TemperatureC ?? 0, v => { r.TemperatureC = v == 0 ? null : v; Dirty(); })),
            W.Field("Rainfall mm", W.Number(r.RainfallMm ?? 0, v => { r.RainfallMm = v == 0 ? null : v; Dirty(); })),
            W.Field("Prepared by", W.Box(r.PreparedBy, v => { r.PreparedBy = v; Dirty(); })),
            W.Field("Checked by", W.Box(r.CheckedBy, v => { r.CheckedBy = v; Dirty(); })),
            W.Field("Approved by", W.Box(r.ApprovedBy, v => { r.ApprovedBy = v; Dirty(); })),
            W.Field("Visitors", W.Box(r.Visitors, v => { r.Visitors = v; Dirty(); }))), 10);
        header.IsEnabled = !locked;
        header.Margin = new Thickness(10, 8, 10, 6);
        _summary.Margin = new Thickness(10, 0, 10, 4);

        _tabs.Items.Clear();
        _tabs.Items.Add(Tab("1  Work progress", ActivitiesTab(), locked));
        _tabs.Items.Add(Tab("2  Manpower", ManpowerTab(), locked));
        _tabs.Items.Add(Tab("3  Machinery", MachineryTab(), locked));
        _tabs.Items.Add(Tab("4  Materials & non-material", MaterialsTab(), locked));
        _tabs.Items.Add(Tab("5  Hindrances", HindranceTab(), locked));
        _tabs.Items.Add(Tab("6  Requirements", RequirementTab(), locked));
        _tabs.Items.Add(Tab("7  Safety & remarks", RemarksTab(), locked));
        _tabs.Items.Add(Tab("8  Photos & videos", PhotosTab(), false));
        _tabs.SelectedIndex = 0;

        var dock = new DockPanel();
        foreach (var top in new UIElement[] { tb, header, _summary }) { DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top); }
        dock.Children.Add(_tabs);
        Content = dock;
    }

    private static TabItem Tab(string header, UIElement content, bool locked) =>
        new() { Header = header, Content = new Border { IsEnabled = !locked, Child = content, Padding = new Thickness(0, 6, 0, 0) } };

    private static DockPanel WithButtons(DataGrid grid, params (string Glyph, string Text, Func<Task> Action)?[] buttons)
    {
        var tb = W.ToolBar(buttons);
        var d = new DockPanel();
        DockPanel.SetDock(tb, Dock.Top);
        d.Children.Add(tb);
        grid.Margin = new Thickness(0, 4, 0, 0);
        d.Children.Add(grid);
        return d;
    }

    private DataGrid Editable(GridBuilder gb)
    {
        var g = gb.OnEdited(o => { if (o is LiveRow lr) lr.Notify(); Dirty(); }).Grid;
        g.CanUserDeleteRows = true;
        g.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Delete && g.CurrentCell.IsValid && !g.IsReadOnly) Dispatcher.BeginInvoke(Dirty); };
        return g;
    }

    private List<Pick> ActivityPicks() => _activities.Select(a => new Pick(a.Id, a.ToString())).Prepend(new Pick("", "General / overheads")).ToList();
    private List<Pick> SubPicks() => _subs.Select(s => new Pick(s.Id, s.Name)).Prepend(new Pick("", "(own)")).ToList();

    private UIElement ActivitiesTab()
    {
        var grid = Editable(new GridBuilder(_acts)
            .Text("From", nameof(ActRow.Source), 70, readOnly: true)
            .Text("Activity / drawing item", "R.ActivityName", 260)
            .Text("Location", "R.Location", 130)
            .Lookup("Agency", "R.SubcontractorId", SubPicks(), 150)
            .Text("Unit", "R.Unit", 60)
            .Number("Planned (100 %)", "R.PlannedQty", 100, readOnly: true)
            .Number("TODAY", "R.TodayQty", 90)
            .Number("To date (auto)", nameof(ActRow.ToDate), 100, readOnly: true)
            .Number("Balance", nameof(ActRow.Balance), 85, readOnly: true)
            .Progress("Progress", nameof(ActRow.Percent), 120)
            .Text("Check", nameof(ActRow.Check), 160, readOnly: true)
            .Text("Remarks", "R.Remarks"));
        grid.CellEditEnding += (_, e) =>
        {
            if (e.Row.Item is ActRow a && e.Column.Header is "Agency") Dispatcher.BeginInvoke(() => a.R.SubcontractorName = _subs.FirstOrDefault(s => s.Id == a.R.SubcontractorId)?.Name ?? "");
        };
        return WithButtons(grid,
            (Glyph.Add, "Add activities from plan…", AddActivitiesAsync),
            (Glyph.Hierarchy, "Add drawing work…", AddDrawingWorkAsync),
            (Glyph.Add, "Add unplanned work", () => { _acts.Add(new ActRow(new ReportActivity { ActivityName = "Unplanned work" }, 0)); Dirty(); return Task.CompletedTask; }),
            (Glyph.Delete, "Remove selected", () => Remove(grid, _acts)),
            null,
            (Glyph.Chart, "Only today's quantity is typed: “To date”, balance and % add up from all earlier reports automatically.", () => Task.CompletedTask));
    }

    private Task AddActivitiesAsync()
    {
        var open = _activities.Where(a => _acts.All(x => x.R.ActivityId != a.Id || x.R.DrawingItemId != "") && (a.PlannedQty <= 0 || _doneBefore.GetValueOrDefault("A:" + a.Id) < a.PlannedQty)).ToList();
        if (open.Count == 0)
        {
            Dialogs.Info(_activities.Count == 0 ? "No activities planned yet. Add them in Planning › Activities & progress." : "All open activities are already in this report.");
            return Task.CompletedTask;
        }
        var chosen = PickMany("Add activities", "Tick the activities worked on (not yet 100 %):",
            open.Select(a => $"{a}   ({PdfReports.N(_doneBefore.GetValueOrDefault("A:" + a.Id))} of {PdfReports.N(a.PlannedQty)} {a.Unit})").ToList());
        foreach (var i in chosen)
        {
            var a = open[i];
            _acts.Add(new ActRow(new ReportActivity
            {
                ActivityId = a.Id, ActivityName = a.ToString(), Location = a.Location, Unit = a.Unit, PlannedQty = a.PlannedQty,
                SubcontractorId = a.SubcontractorId, SubcontractorName = _subs.FirstOrDefault(s => s.Id == a.SubcontractorId)?.Name ?? "",
            }, _doneBefore.GetValueOrDefault("A:" + a.Id)));
        }
        if (chosen.Count > 0) Dirty();
        return Task.CompletedTask;
    }

    /// <summary>Work on drawing items: one line per agency allocation (and any agency for unallocated items).</summary>
    private async Task AddDrawingWorkAsync()
    {
        var pid = Desk.ProjectId;
        var drawings = (await Desk.Db.ListForProjectAsync<Drawing>(pid)).ToDictionary(d => d.Id);
        var items = (await Desk.Db.ListForProjectAsync<DrawingItem>(pid)).Where(i => drawings.ContainsKey(i.DrawingId)).ToDictionary(i => i.Id);
        var allocs = await Desk.Db.ListForProjectAsync<DrawingAllocation>(pid);
        if (items.Count == 0) { Dialogs.Info("No drawings yet. Add them in Planning › Drawing register (items + allocations to agencies)."); return; }
        var options = allocs.Where(a => items.ContainsKey(a.DrawingItemId))
            .Select(a => (Item: items[a.DrawingItemId], Sub: a.SubcontractorId, Name: a.SubcontractorName, Qty: a.AllocatedQty))
            .GroupBy(x => (x.Item.Id, x.Sub)).Select(g => (g.First().Item, g.Key.Sub, g.First().Name, Qty: g.Sum(x => x.Qty)))
            .Where(x => _acts.All(a => a.R.DrawingItemId != x.Item.Id || a.R.SubcontractorId != x.Sub))
            .Where(x => _doneBefore.GetValueOrDefault($"D:{x.Item.Id}:{x.Sub}") < x.Qty)
            .OrderBy(x => drawings[x.Item.DrawingId].Number).ThenBy(x => x.Item.SortOrder).ThenBy(x => x.Name).ToList();
        if (options.Count == 0) { Dialogs.Info("Every allocated drawing item is either complete or already in this report."); return; }
        var chosen = PickMany("Add drawing work", "Tick the drawing items worked on today (one line per agency allocation):",
            options.Select(o => $"{drawings[o.Item.DrawingId].Number} · {o.Item}  –  {o.Name}: {PdfReports.N(_doneBefore.GetValueOrDefault($"D:{o.Item.Id}:{o.Sub}"))} of {PdfReports.N(o.Qty)} {o.Item.Unit}").ToList());
        foreach (var o in chosen.Select(i => options[i]))
        {
            var r = new ReportActivity
            {
                DrawingItemId = o.Item.Id, ActivityId = o.Item.ActivityId, ActivityName = $"{drawings[o.Item.DrawingId].Number} / {o.Item}",
                Unit = o.Item.Unit, PlannedQty = o.Qty, SubcontractorId = o.Sub, SubcontractorName = o.Name,
            };
            _acts.Add(new ActRow(r, _doneBefore.GetValueOrDefault(r.ProgressKey)));
        }
        if (chosen.Count > 0) Dirty();
    }

    /// <summary>Check-list dialog: tick several items at once.</summary>
    private static List<int> PickMany(string title, string label, List<string> items)
    {
        if (Dialogs.Headless) return items.Count > 0 ? [0] : [];
        var boxes = items.Select(i => new CheckBox { Content = i, Margin = new Thickness(2, 3, 2, 3) }).ToList();
        var list = new StackPanel();
        foreach (var b in boxes) list.Children.Add(b);
        var ok = new Button { Content = "Add", IsDefault = true, MinWidth = 80 };
        var all = new Button { Content = "Select all", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var w = new Window { Title = title, Width = 560, Height = 520, Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ok.Click += (_, _) => w.DialogResult = true;
        all.Click += (_, _) => boxes.ForEach(b => b.IsChecked = true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(all); buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var dock = new DockPanel { Margin = new Thickness(14) };
        var head = W.Text(label);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(head);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = list, Margin = new Thickness(0, 8, 0, 0) });
        w.Content = dock;
        return w.ShowDialog() == true ? boxes.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => x.i).ToList() : [];
    }

    private UIElement ManpowerTab()
    {
        var parties = _subs.Select(s => new Pick("S:" + s.Id, "Subcontractor · " + s.Name))
            .Concat(_depts.Select(d => new Pick("D:" + d.Id, "Department · " + d.Name))).ToList();
        var grid = Editable(new GridBuilder(_men)
            .Lookup("Subcontractor / department", nameof(ManRow.PartyKey), parties, 230)
            .Suggest("Trade / designation", "M.Trade", Lists.Trades, 150)
            .Lookup("Worked on activity", nameof(ManRow.ActivityId), ActivityPicks(), 210)
            .Number("Nos", "M.Count", 60, "0")
            .Number("Hours each", "M.Hours", 80)
            .Number("Man-hours", nameof(ManRow.ManHours), 85, readOnly: true)
            .Number("Rate", "M.Rate", 85, "#,0.00")
            .Enum<RateBasis>("Basis", "M.Basis", 90)
            .Number("Cost", nameof(ManRow.Cost), 100, "#,0.00", readOnly: true)
            .Text("Remarks", "M.Remarks"));
        return WithButtons(grid,
            (Glyph.People, "Add subcontractor crew", () => AddMan(ManpowerSource.Subcontractor)),
            (Glyph.Org, "Add department staff", () => AddMan(ManpowerSource.Department)),
            (Glyph.Calculator, "Add from resource list…", () => AddFromResourcesAsync(ResourceType.Manpower)),
            (Glyph.Delete, "Remove selected", () => Remove(grid, _men)));
    }

    private Task AddMan(ManpowerSource src)
    {
        _men.Add(new ManRow(new ReportManpower { Source = src, Count = 1, Hours = _cal.HoursPerDay }, () => _cal, ActName, Party));
        Dirty();
        return Task.CompletedTask;
    }

    private async Task AddFromResourcesAsync(ResourceType? type)
    {
        var res = (await Desk.Db.ListForProjectAsync<SiteResource>(Desk.ProjectId))
            .Where(x => type == null ? x.Type is ResourceType.Material or ResourceType.Other : x.Type == type).OrderBy(x => x.Name).ToList();
        if (res.Count == 0) { Dialogs.Info("No matching resources yet. Add them in Planning › Resources & rates (with rates, so costs calculate)."); return; }
        var chosen = PickMany("Add from resource list", "Tick the resources used today:", res.Select(x => $"{x}  ·  {PdfReports.M(x.Rate)} {x.Basis}").ToList());
        foreach (var x in chosen.Select(i => res[i]))
        {
            switch (x.Type)
            {
                case ResourceType.Manpower:
                    var sub = x.SubcontractorId != "";
                    _men.Add(new ManRow(new ReportManpower
                    {
                        Source = sub ? ManpowerSource.Subcontractor : ManpowerSource.Department, PartyId = sub ? x.SubcontractorId : x.DepartmentId,
                        PartyName = sub ? _subs.FirstOrDefault(s => s.Id == x.SubcontractorId)?.Name ?? "" : _depts.FirstOrDefault(d => d.Id == x.DepartmentId)?.Name ?? "",
                        Trade = x.Name, ResourceId = x.Id, Rate = x.Rate, Basis = x.Basis, Count = 1, Hours = _cal.HoursPerDay,
                    }, () => _cal, ActName, Party));
                    break;
                case ResourceType.Machinery:
                    _machines.Add(new MachineRow(new ReportMachine { Name = x.Name, ResourceId = x.Id, Rate = x.Rate, Basis = x.Basis, WorkingHours = _cal.HoursPerDay, Ownership = x.SubcontractorId == "" ? MachineOwnership.Own : MachineOwnership.Hired }, () => _cal, ActName));
                    break;
                default:
                    _materials.Add(new MaterialRow(new ReportMaterial { Material = x.Name, Kind = x.Type, Unit = x.Unit, ResourceId = x.Id, Rate = x.Rate, Basis = x.Basis }, ActName));
                    break;
            }
        }
        if (chosen.Count > 0) Dirty();
    }

    private UIElement MachineryTab()
    {
        var grid = Editable(new GridBuilder(_machines)
            .Suggest("Machine / equipment", "M.Name", ["JCB", "Excavator", "Tower crane", "Mobile crane", "Concrete pump", "Transit mixer", "Batching plant", "Dumper", "Roller", "DG set", "Welding machine", "Hoist"], 180)
            .Enum<MachineOwnership>("Own / hired", "M.Ownership", 90)
            .Lookup("Worked on activity", nameof(MachineRow.ActivityId), ActivityPicks(), 210)
            .Number("Nos", "M.Count", 55, "0")
            .Number("Working hrs", "M.WorkingHours", 85)
            .Number("Idle hrs", "M.IdleHours", 70)
            .Number("Fuel (L)", "M.FuelLitres", 70)
            .Number("Rate", "M.Rate", 85, "#,0.00")
            .Enum<RateBasis>("Basis", "M.Basis", 90)
            .Number("Cost", nameof(MachineRow.Cost), 100, "#,0.00", readOnly: true)
            .Text("Remarks", "M.Remarks"));
        return WithButtons(grid,
            (Glyph.Add, "Add machine", () => { _machines.Add(new MachineRow(new ReportMachine { WorkingHours = _cal.HoursPerDay }, () => _cal, ActName)); Dirty(); return Task.CompletedTask; }),
            (Glyph.Calculator, "Add from resource list…", () => AddFromResourcesAsync(ResourceType.Machinery)),
            (Glyph.Delete, "Remove selected", () => Remove(grid, _machines)),
            null,
            (Glyph.Warning, "Idle hours are charged for hired machines only.", () => Task.CompletedTask));
    }

    private UIElement MaterialsTab()
    {
        var grid = Editable(new GridBuilder(_materials)
            .Enum<ResourceType>("Type", "M.Kind", 95)
            .Suggest("Material / resource", "M.Material", ["Cement", "RMC", "TMT steel", "Sand", "Aggregate 20 mm", "Bricks", "AAC blocks", "Scaffolding hire", "Shuttering hire", "Testing charges", "Transport", "Water tanker"], 190)
            .Lookup("Used on activity", nameof(MaterialRow.ActivityId), ActivityPicks(), 210)
            .Suggest("Unit", "M.Unit", Lists.Units, 60)
            .Number("Received", "M.Received", 80)
            .Number("Consumed / used", "M.Consumed", 100)
            .Number("Rate", "M.Rate", 85, "#,0.00")
            .Enum<RateBasis>("Basis", "M.Basis", 90)
            .Number("Cost", nameof(MaterialRow.Cost), 100, "#,0.00", readOnly: true)
            .Text("Remarks / challan", "M.Remarks"));
        return WithButtons(grid,
            (Glyph.Add, "Add material", () => { _materials.Add(new MaterialRow(new ReportMaterial(), ActName)); Dirty(); return Task.CompletedTask; }),
            (Glyph.Add, "Add non-material (hire, testing…)", () => { _materials.Add(new MaterialRow(new ReportMaterial { Kind = ResourceType.Other, Basis = RateBasis.LumpSum, Unit = "LS" }, ActName)); Dirty(); return Task.CompletedTask; }),
            (Glyph.Calculator, "Add from resource list…", () => AddFromResourcesAsync(null)),
            (Glyph.Delete, "Remove selected", () => Remove(grid, _materials)));
    }

    private UIElement HindranceTab()
    {
        var grid = Editable(new GridBuilder(_hindrances)
            .Suggest("Category", nameof(Hindrance.Category), Lists.HindranceCategories, 150)
            .Text("Description", nameof(Hindrance.Description), 0, wrap: true)
            .Text("Affected activity", nameof(Hindrance.AffectedActivity), 160)
            .Suggest("Responsible", nameof(Hindrance.ResponsibleParty), Lists.ResponsibleParties, 130)
            .Number("Hours lost", nameof(Hindrance.HoursLost), 80)
            .Enum<HindranceStatus>("Status", nameof(Hindrance.Status), 90)
            .Text("Action", nameof(Hindrance.ActionTaken), 180));
        return WithButtons(grid,
            (Glyph.Add, "Add hindrance", () => { _hindrances.Add(new Hindrance { ProjectId = Desk.ProjectId, FromDate = _b.Report.Date }); Dirty(); return Task.CompletedTask; }),
            (Glyph.Delete, "Remove selected", () => Remove(grid, _hindrances)));
    }

    private UIElement RequirementTab()
    {
        var grid = Editable(new GridBuilder(_requirements)
            .Text("Item / description", nameof(Requirement.Item), 0, wrap: true)
            .Suggest("Category", nameof(Requirement.Category), Lists.RequirementCategories, 130)
            .Number("Qty", nameof(Requirement.Quantity), 70)
            .Suggest("Unit", nameof(Requirement.Unit), Lists.Units, 60)
            .Date("Required by", nameof(Requirement.RequiredBy))
            .Enum<Priority>("Priority", nameof(Requirement.Priority), 85)
            .Enum<RequirementStatus>("Status", nameof(Requirement.Status), 100));
        return WithButtons(grid,
            (Glyph.Add, "Add requirement", () => { _requirements.Add(new Requirement { ProjectId = Desk.ProjectId, RaisedDate = _b.Report.Date, RaisedBy = _b.Report.PreparedBy }); Dirty(); return Task.CompletedTask; }),
            (Glyph.Delete, "Remove selected", () => Remove(grid, _requirements)));
    }

    private UIElement RemarksTab()
    {
        var r = _b.Report;
        return W.Scroll(W.Stack(
            W.Form(2,
                W.Field("Toolbox talk topic", W.Suggest(r.ToolboxTalkTopic, ["Working at height", "PPE", "Excavation safety", "Electrical safety", "Lifting & rigging", "Housekeeping", "Fire safety", "Hot work", "Scaffolding", "Heat stress"], v => { r.ToolboxTalkTopic = v; Dirty(); })),
                W.Field("Attendees", W.Number(r.ToolboxTalkAttendees, v => { r.ToolboxTalkAttendees = (int)v; Dirty(); }))),
            W.Field("Safety observations", W.Box(r.SafetyRemarks, v => { r.SafetyRemarks = v; Dirty(); }, true, 70)),
            W.Field("Plan for tomorrow", W.Box(r.PlanForTomorrow, v => { r.PlanForTomorrow = v; Dirty(); }, true, 70)),
            W.Field("Remarks", W.Box(r.Remarks, v => { r.Remarks = v; Dirty(); }, true, 90))), 4);
    }

    private UIElement PhotosTab()
    {
        var panel = new MediaPanel(MediaOwner.Report, () => _b.Report.Id);
        panel.CountChanged += n => { if (_tabs.Items.Count > 7) ((TabItem)_tabs.Items[7]).Header = $"8  Photos & videos ({n})"; };
        return panel;
    }

    /// <summary>Decodes a small thumbnail only (fast and light on memory).</summary>
    public static BitmapImage? Thumb(string path, int width = 320)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.DecodePixelWidth = width;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    private Task Remove<T>(DataGrid grid, ObservableCollection<T> list)
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        var sel = grid.SelectedItems.OfType<T>().ToList();
        if (sel.Count == 0) { Dialogs.Info("Select the row(s) to remove (click the row, Ctrl/Shift for several)."); return Task.CompletedTask; }
        foreach (var s in sel) list.Remove(s);
        Dirty();
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------ totals

    private void Recalc()
    {
        Collect();
        DprCalculator.UpdateTotals(_b);
        var sub = DprCalculator.Total(_b.Manpower, ManpowerSource.Subcontractor);
        var dept = DprCalculator.Total(_b.Manpower, ManpowerSource.Department);
        var cost = CostCalculator.ForReport(_b, _cal);
        _summary.Children.Clear();
        _summary.Children.Add(W.Stat($"Manpower  (sub {sub} · dept {dept})", (sub + dept).ToString()));
        _summary.Children.Add(W.Stat("Man-hours", PdfReports.N(_b.Manpower.Sum(m => m.ManHours))));
        _summary.Children.Add(W.Stat("Machines", _b.Machines.Sum(m => m.Count).ToString()));
        _summary.Children.Add(W.Stat("Activities worked", $"{_b.Activities.Count(a => a.TodayQty > 0)} / {_b.Activities.Count}"));
        _summary.Children.Add(W.Stat($"Cost today ({_cal.Currency})", PdfReports.M(cost.Total), Theme.Accent));
        if (_b.Hindrances.Count > 0) _summary.Children.Add(W.Stat("Hours lost", PdfReports.N(_b.Hindrances.Sum(h => h.HoursLost)), Theme.Danger));
        if (_tabs.Items.Count >= 6)
        {
            ((TabItem)_tabs.Items[0]).Header = $"1  Work progress ({_b.Activities.Count})";
            ((TabItem)_tabs.Items[1]).Header = $"2  Manpower ({sub + dept})";
            ((TabItem)_tabs.Items[2]).Header = $"3  Machinery ({_b.Machines.Sum(m => m.Count)})";
            ((TabItem)_tabs.Items[3]).Header = $"4  Materials & non-material ({_b.Materials.Count})";
            ((TabItem)_tabs.Items[4]).Header = $"5  Hindrances ({_b.Hindrances.Count})";
            ((TabItem)_tabs.Items[5]).Header = $"6  Requirements ({_b.Requirements.Count})";
        }
    }

    // ------------------------------------------------------------------ actions

    public async Task SaveAsync()
    {
        foreach (var g in FindGrids(_tabs)) g.CommitEdit(DataGridEditingUnit.Row, true);
        Collect();
        if (_b.Report.Status == ReportStatus.Approved && _dirty && !Dialogs.Confirm("This report is approved. Save the changes anyway?")) return;
        _b.Activities.RemoveAll(a => a.ActivityId == "" && string.IsNullOrWhiteSpace(a.ActivityName) && a.TodayQty == 0);
        _b.Hindrances.RemoveAll(h => string.IsNullOrWhiteSpace(h.Description) && h.HoursLost == 0);
        _b.Requirements.RemoveAll(q => string.IsNullOrWhiteSpace(q.Item));
        var dup = (await Desk.Db.ListForProjectAsync<DailyReport>(_b.Report.ProjectId))
            .FirstOrDefault(x => x.Id != _b.Report.Id && x.Date.Date == _b.Report.Date.Date && x.Shift == _b.Report.Shift);
        if (dup != null && !Dialogs.Confirm($"{dup.Title} already exists for this date and shift. Save anyway?")) return;
        await Desk.Db.SaveReportAsync(_b);
        _dirty = false;
        MainWindow.Current.Retitle(this);
        Desk.Say($"Saved {PdfReports.ReportLabel(_b)} · {DateTime.Now:HH:mm:ss}" + (Desk.SyncSettings.Enabled ? " · syncing" : ""));
    }

    private static IEnumerable<DataGrid> FindGrids(DependencyObject root)
    {
        if (root is DataGrid g) yield return g;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var x in FindGrids(child)) yield return x;
    }

    private async Task SetStatusAsync(ReportStatus status)
    {
        if (status == ReportStatus.Approved && string.IsNullOrWhiteSpace(_b.Report.ApprovedBy))
        {
            var who = Dialogs.Prompt("Approve", "Approved by", Desk.Db.UserName);
            if (who == null) return;
            _b.Report.ApprovedBy = who;
        }
        _b.Report.Status = status;
        _dirty = true;
        await SaveAsync();
        Build();
        Recalc();
    }

    public async Task PdfAsync()
    {
        if (_dirty) await SaveAsync();
        var b = await Desk.Db.LoadReportAsync(_b.Report.Id);
        var opt = await Desk.ReportOptionsAsync();
        await Dialogs.CreateAndOpenAsync($"{PdfReports.ReportLabel(b)}_{b.Report.Date:yyyy-MM-dd}.pdf", s => PdfReports.DailyReport(s, b, Desk.Db.MediaPath, _cal, opt));
    }

    private async Task ExcelAsync()
    {
        if (_dirty) await SaveAsync();
        var b = await Desk.Db.LoadReportAsync(_b.Report.Id);
        await Dialogs.CreateAndOpenAsync($"{PdfReports.ReportLabel(b)}_{b.Report.Date:yyyy-MM-dd}.xlsx", s => ExcelReports.ReportWorkbook(s, b.Project, [b], b.Report.Title, _cal));
    }

    private async Task DeleteAsync()
    {
        if (!Dialogs.Confirm("Delete this report and all its rows? It is removed on every synced device.")) return;
        _b.Activities.Clear(); _b.Manpower.Clear(); _b.Machines.Clear(); _b.Materials.Clear(); _b.Hindrances.Clear(); _b.Requirements.Clear();
        if (await Desk.Db.GetAsync<DailyReport>(_b.Report.Id) != null)
        {
            await Desk.Db.SaveReportAsync(_b);
            await Desk.Db.DeleteAsync(_b.Report);
        }
        _dirty = false;
        MainWindow.Current.CloseView(this);
    }
}
