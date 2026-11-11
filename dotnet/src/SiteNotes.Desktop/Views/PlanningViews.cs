using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

public class ProjectsView : GridView<Project>
{
    public ProjectsView() : base("Projects", () => new Project { Name = "New project" },
        g => g.Text("Project name", nameof(Project.Name), 240)
              .Text("Code / ref", nameof(Project.Code), 100)
              .Text("Client", nameof(Project.Client), 160)
              .Text("Contractor", nameof(Project.Contractor), 160)
              .Text("Consultant / PMC", nameof(Project.Consultant), 150)
              .Text("Location", nameof(Project.Location), 120)
              .Date("Start", nameof(Project.StartDate))
              .Date("Completion", nameof(Project.EndDate))
              .Text("Report prefix", nameof(Project.ReportPrefix), 90)
              .Number("First report no.", nameof(Project.FirstReportNo), 100, "0")
              .Check("Copy yesterday into new DPR", nameof(Project.CarryForward), 170),
        p => $"{p.Name} {p.Code} {p.Client} {p.Contractor} {p.Location}", projectScoped: false,
        hint: "Type directly in the grid. Double-click a project to make it the current one. Report numbers and dates are automatic (first number is asked once).")
    {
        Order = l => l.OrderBy(p => p.Name);
        AfterSave = async p =>
        {
            if ((await Desk.Db.ListForProjectAsync<Department>(p.Id)).Count == 0)
                await Desk.Db.SaveAllAsync(Lists.DefaultDepartments.Select(n => new Department { ProjectId = p.Id, Name = n }));
            if (Desk.Project == null || Desk.Project.Id == p.Id) await Desk.SetProjectAsync(p);
            await MainWindow.Current.FillProjectsAsync();
        };
        OpenDetail = async p => { await Desk.SetProjectAsync(p); Desk.Say($"Current project: {p.Name}"); };
        BulkActions = [("Make current project", l => _ = Desk.SetProjectAsync(l[0]))];
    }

    public void AddNew() => Dispatcher.BeginInvoke(async () => await Dialogs.Guard(AddAsync), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
}

/// <summary>Working hours, weekdays, holidays, overtime factor and currency.</summary>
public class CalendarView : ViewBase
{
    public override string Title => "Work calendar";

    public override async Task RefreshAsync()
    {
        var cal = await Desk.Db.CalendarAsync(Desk.ProjectId);
        var preview = W.Muted("");
        void Preview()
        {
            var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            var end = start.AddMonths(1).AddDays(-1);
            var days = cal.WorkingDaysBetween(start, end);
            preview.Text = $"{start:MMMM yyyy}: {days} working days · {days * cal.HoursPerDay:0.#} working hours per person";
        }
        var weekdays = new WrapPanel();
        string[] names = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        for (var i = 0; i < 7; i++)
        {
            var d = i.ToString()[0];
            weekdays.Children.Add(W.Check(names[i], cal.WorkingDays.Contains(d), on =>
            {
                var set = cal.WorkingDays.ToHashSet();
                if (on) set.Add(d); else set.Remove(d);
                cal.WorkingDays = new string(set.OrderBy(c => c).ToArray());
                Preview();
            }));
        }
        Preview();
        Content = W.Scroll(W.Stack(
            W.Heading("Work calendar"),
            W.Card(W.Stack(
                W.Form(4,
                    W.Field("Normal hours per day", W.Number(cal.HoursPerDay, v => { cal.HoursPerDay = v; Preview(); })),
                    W.Field("Shift start (hh:mm)", W.Box(cal.ShiftStart.ToString(@"hh\:mm"), v => { if (TimeSpan.TryParse(v, out var t)) cal.ShiftStart = t; })),
                    W.Field("Overtime pay factor", W.Money(cal.OvertimeFactor, v => cal.OvertimeFactor = v <= 0 ? 1 : v)),
                    W.Field("Currency symbol", W.Box(cal.Currency, v => cal.Currency = v))),
                W.Field("Working days", weekdays),
                W.Field("Holidays (yyyy-mm-dd, separated by commas)", W.Box(cal.Holidays, v => { cal.Holidays = v; Preview(); }, multiline: true)),
                preview,
                W.Muted("Used for man-days (hours ÷ hours per day), per-day rates, overtime cost, planned durations and forecast finish dates."),
                W.Wrap(W.Button("Save calendar", async () =>
                {
                    cal.ProjectId = Desk.ProjectId;
                    await Desk.Db.SaveAsync(cal);
                    Desk.Say("Calendar saved");
                }, Glyph.Save, primary: true))))));
    }
}

/// <summary>WBS tree: Project › Plant › Unit › Area › System › Work package.</summary>
public class WbsView : ViewBase
{
    private readonly TreeView _tree = new() { MinWidth = 320, Margin = new Thickness(10) };
    private readonly ContentControl _editor = new() { Margin = new Thickness(10) };
    private List<WbsNode> _all = [];

    public override string Title => "WBS – plant / unit / area";

    public WbsView()
    {
        var tb = W.ToolBar(
            (Glyph.Add, "Add top level", () => AddAsync("")),
            (Glyph.Add, "Add under selected", () => AddAsync((_tree.SelectedItem as TreeViewItem)?.Tag is WbsNode n ? n.Id : "")),
            (Glyph.Delete, "Delete", DeleteAsync),
            null,
            (Glyph.Edit, "Export to edit", () => ExchangeUi.ExportAsync(() => ExchangeUi.Sheets.WbsAsync(Desk.ProjectId), _all.OrderBy(n => n.Level).ThenBy(n => n.SortOrder).ThenBy(n => n.Code))),
            (Glyph.Table, "Import Excel…", async () => { if (await ExchangeUi.ImportAsync(() => ExchangeUi.Sheets.WbsAsync(Desk.ProjectId))) await RefreshAsync(); }),
            null,
            (Glyph.Refresh, "Refresh", RefreshAsync));
        _tree.SelectedItemChanged += (_, _) => ShowEditor();
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(380) });
        split.ColumnDefinitions.Add(new ColumnDefinition());
        split.Children.Add(W.Card(_tree, 4));
        Grid.SetColumn(_editor, 1);
        split.Children.Add(_editor);
        var dock = new DockPanel();
        DockPanel.SetDock(tb, Dock.Top);
        dock.Children.Add(tb);
        dock.Children.Add(split);
        Content = dock;
    }

    public override async Task RefreshAsync()
    {
        _all = await Desk.Db.ListForProjectAsync<WbsNode>(Desk.ProjectId);
        var selected = (_tree.SelectedItem as TreeViewItem)?.Tag as WbsNode;
        _tree.Items.Clear();
        void AddChildren(ItemsControl parent, string parentId, int depth)
        {
            foreach (var n in _all.Where(n => n.ParentId == parentId).OrderBy(n => n.SortOrder).ThenBy(n => n.Code).ThenBy(n => n.Name))
            {
                var item = new TreeViewItem { Header = $"{n}   ·  {n.Level}", Tag = n, IsExpanded = true };
                parent.Items.Add(item);
                if (depth < 12) AddChildren(item, n.Id, depth + 1);
                if (selected?.Id == n.Id) item.IsSelected = true;
            }
        }
        AddChildren(_tree, "", 0);
        // orphans (parent deleted) at the top level
        foreach (var o in _all.Where(n => n.ParentId != "" && _all.All(p => p.Id != n.ParentId)))
            _tree.Items.Add(new TreeViewItem { Header = $"{o}  ·  {o.Level} (parent missing)", Tag = o });
        if (_all.Count == 0) _editor.Content = W.Muted("No WBS yet. Click “Add top level” (e.g. a plant), then add units, areas and systems under it.");
    }

    private void ShowEditor()
    {
        if ((_tree.SelectedItem as TreeViewItem)?.Tag is not WbsNode n) return;
        var map = _all.ToDictionary(x => x.Id);
        _editor.Content = W.Card(W.Stack(
            W.Text(ProgressService.WbsPath(n.Id, map), 13, true, Theme.Primary),
            W.Form(2,
                W.Field("Level", W.Combo(Enum.GetValues<WbsLevel>(), n.Level, v => n.Level = v)),
                W.Field("Code", W.Box(n.Code, v => n.Code = v)),
                W.Field("Name", W.Box(n.Name, v => n.Name = v)),
                W.Field("Order", W.Number(n.SortOrder, v => n.SortOrder = (int)v))),
            W.Wrap(W.Button("Save", async () => { await Desk.Db.SaveAsync(n); await RefreshAsync(); Desk.Say("WBS saved"); }, Glyph.Save, true)),
            W.Muted("Link activities to any level in Activities & progress. Reports group progress by this path.")));
    }

    private async Task AddAsync(string parentId)
    {
        var parent = _all.FirstOrDefault(p => p.Id == parentId);
        var level = parent == null ? WbsLevel.Plant : (WbsLevel)Math.Min((int)WbsLevel.WorkPackage, (int)parent.Level + 1);
        var name = Dialogs.Prompt("New WBS element", $"Name of the new {level.ToString().ToLowerInvariant()}" + (parent == null ? "" : $" under {parent}"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Desk.Db.SaveAsync(new WbsNode { ProjectId = Desk.ProjectId, ParentId = parentId, Name = name.Trim(), Level = level });
        await RefreshAsync();
    }

    private async Task DeleteAsync()
    {
        if ((_tree.SelectedItem as TreeViewItem)?.Tag is not WbsNode n) return;
        if (_all.Any(x => x.ParentId == n.Id)) { Dialogs.Info("Delete or move the elements under it first."); return; }
        if (!Dialogs.Confirm($"Delete {n}? Activities linked to it keep their data but lose the WBS link.")) return;
        await Desk.Db.DeleteAsync(n);
        _editor.Content = null;
        await RefreshAsync();
    }
}

/// <summary>Row of the activities grid: the editable plan plus calculated progress and cost.</summary>
public class ActivityRow(Activity a, ActivityProgress? p, string currency) : INotifyPropertyChanged
{
    public Activity A { get; } = a;
    public ActivityProgress? P { get; private set; } = p;
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>New calculation results: every calculated cell repaints, no grid refresh needed.</summary>
    public void Update(ActivityProgress? p)
    {
        P = p;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    public string Wbs => P?.WbsPath ?? "";
    public double Done => P?.DoneQty ?? 0;
    public double Percent => P?.Percent ?? 0;
    public double Balance => P?.RemainingQty ?? A.PlannedQty;
    public double PlannedPercent => P?.PlannedPercentToday ?? 0;
    public DateTime? ActualStart => P?.ActualStart;
    public DateTime? Forecast => P?.ForecastFinish;
    public double AvgPerDay => P?.AvgPerDay ?? 0;
    public string State => P == null ? "Not started" : ProgressReports.StateText(P.State);
    public decimal Cost => P?.Actual.Total ?? 0;
    public decimal CostPerUnit => P?.CostPerUnit ?? 0;
    public decimal CostPerDay => P?.CostPerDay ?? 0;
    public decimal Budget => P?.PlannedCost ?? 0;
    public double ManHours => P?.Actual.ManHours ?? 0;
    public string Agency => P?.Agency ?? "";
    public string Department => P?.Department ?? "";
    public string Currency => currency;
}

/// <summary>
/// Activities & progress: plan activities in the grid (code, name, WBS, method, agency, department,
/// unit, planned qty, dates, output/day, weightage); progress, forecast and cost are calculated from the
/// daily reports and shown in the same rows. Group by WBS / agency / department; resources per activity below.
/// </summary>
public class ActivitiesView : ViewBase
{
    private readonly ObservableCollection<ActivityRow> _rows = [];
    private readonly ICollectionView _view;
    private readonly DataGrid _grid;
    private readonly TextBox _search = new() { Width = 240 };
    private readonly ComboBox _state = new() { Width = 130, ItemsSource = new[] { "All", "Not started", "In progress", "Delayed", "Completed" }, SelectedIndex = 0, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ComboBox _group = new() { Width = 150, ItemsSource = new[] { "Group: WBS", "Group: agency", "Group: department", "No grouping" }, SelectedIndex = 0, Margin = new Thickness(8, 0, 0, 0) };
    private readonly WrapPanel _kpis = new() { Margin = new Thickness(10, 6, 0, 0) };
    private readonly ContentControl _detail = new();
    private ProjectProgress? _pp;
    private List<Pick> _wbs = [];

    public override string Title => "Activities & progress";

    public ActivitiesView()
    {
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = o => Match((ActivityRow)o);
        var subs = MasterViews.Subs();
        var depts = MasterViews.Depts();
        _wbs = LoadWbs();
        var gb = new GridBuilder(_view)
            .Text("Code", "A.Code", 70)
            .Text("Activity", "A.Name", 220)
            .Lookup("WBS", "A.WbsId", _wbs, 170)
            .Suggest("Method", "A.Method", ["Conventional", "Pump placement", "Crane & bucket", "Slip form", "Jump form", "Precast", "Mechanised", "Manual"], 120)
            .Lookup("Agency", "A.SubcontractorId", subs, 140)
            .Lookup("Department", "A.DepartmentId", depts, 120)
            .Suggest("Unit", "A.Unit", Lists.Units, 60)
            .Number("Planned qty", "A.PlannedQty", 90)
            .Number("Done", nameof(ActivityRow.Done), 80, readOnly: true)
            .Progress("Progress", nameof(ActivityRow.Percent), 120)
            .Number("Balance", nameof(ActivityRow.Balance), 80, readOnly: true)
            .Number("Plan % today", nameof(ActivityRow.PlannedPercent), 85, "0.#", readOnly: true)
            .Date("Plan start", "A.PlannedStart")
            .Date("Plan finish", "A.PlannedFinish")
            .Number("Output / day (plan)", "A.PlannedRatePerDay", 100)
            .Number("Avg / day (actual)", nameof(ActivityRow.AvgPerDay), 100, readOnly: true)
            .Date("Forecast finish", nameof(ActivityRow.Forecast), readOnly: true)
            .Status("Status", nameof(ActivityRow.State), 95)
            .Number("Cost", nameof(ActivityRow.Cost), 100, "#,0.00", readOnly: true)
            .Number("Cost / unit", nameof(ActivityRow.CostPerUnit), 90, "#,0.00", readOnly: true)
            .Number("Cost / day", nameof(ActivityRow.CostPerDay), 90, "#,0.00", readOnly: true)
            .Number("Budget", nameof(ActivityRow.Budget), 100, "#,0.00", readOnly: true)
            .Number("Weightage", "A.Weightage", 80)
            .Text("Location", "A.Location", 120);
        _grid = gb.OnEdited(async o => await Dialogs.Guard(() => SaveAsync((ActivityRow)o))).Grid;
        _grid.FrozenColumnCount = 2;
        _grid.SelectionChanged += (_, _) => ShowDetail();
        _grid.GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeader() });

        _search.TextChanged += (_, _) => _view.Refresh();
        _state.SelectionChanged += (_, _) => _view.Refresh();
        _group.SelectionChanged += (_, _) => ApplyGrouping();

        var tb = W.ToolBar(
            (Glyph.Add, "Add activity", AddAsync),
            (Glyph.Delete, "Delete selected", DeleteAsync),
            null,
            (Glyph.Print, "Progress & cost PDF", () => ExportAsync(true)),
            (Glyph.Excel, "Excel", () => ExportAsync(false)),
            null,
            (Glyph.Edit, "Export to edit", ExportEditAsync),
            (Glyph.Table, "Import Excel…", () => ImportEditAsync(null)),
            null,
            (Glyph.Refresh, "Recalculate", RefreshAsync));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 4) };
        bar.Children.Add(W.Text("🔍 "));
        bar.Children.Add(_search);
        bar.Children.Add(_state);
        bar.Children.Add(_group);
        bar.Children.Add(W.Muted("   Progress, forecast and cost are calculated from the daily reports (today's quantities add up, max 100 %)."));

        var split = new Grid();
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        split.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        _grid.Margin = new Thickness(10, 0, 10, 0);
        split.Children.Add(_grid);
        var splitter = new GridSplitter { Height = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Line };
        Grid.SetRow(splitter, 1);
        split.Children.Add(splitter);
        Grid.SetRow(_detail, 2);
        split.Children.Add(_detail);

        var dock = new DockPanel();
        foreach (var top in new UIElement[] { tb, _kpis, bar }) { DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top); }
        dock.Children.Add(split);
        Content = dock;
        ApplyGrouping();
    }

    private static List<Pick> LoadWbs() => Task.Run(async () =>
    {
        var all = await Desk.Db.ListForProjectAsync<WbsNode>(Desk.ProjectId);
        var map = all.ToDictionary(w => w.Id);
        return all.Select(w => new Pick(w.Id, ProgressService.WbsPath(w.Id, map))).OrderBy(p => p.Name).Prepend(new Pick("", "(no WBS)")).ToList();
    }).GetAwaiter().GetResult();

    private static DataTemplate GroupHeader()
    {
        var t = new FrameworkElementFactory(typeof(TextBlock));
        t.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        t.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        t.SetValue(TextBlock.ForegroundProperty, Theme.Primary);
        t.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 8, 0, 2));
        return new DataTemplate { VisualTree = t };
    }

    private void ApplyGrouping()
    {
        _view.GroupDescriptions.Clear();
        var path = _group.SelectedIndex switch { 0 => nameof(ActivityRow.Wbs), 1 => nameof(ActivityRow.Agency), 2 => nameof(ActivityRow.Department), _ => null };
        if (path != null) _view.GroupDescriptions.Add(new PropertyGroupDescription(path));
    }

    private bool Match(ActivityRow r)
    {
        var s = _search.Text.Trim();
        if (s != "" && !$"{r.A.Code} {r.A.Name} {r.Wbs} {r.Agency} {r.A.Location} {r.A.Method}".Contains(s, StringComparison.OrdinalIgnoreCase)) return false;
        return _state.SelectedItem is not string st || st == "All" || r.State == st;
    }

    public override async Task RefreshAsync()
    {
        var selectedId = (_grid.SelectedItem as ActivityRow)?.A.Id;
        _pp = await Task.Run(() => new ProgressService(Desk.Db).ComputeAsync(Desk.ProjectId));
        var acts = await Desk.Db.ListForProjectAsync<Activity>(Desk.ProjectId);
        var byId = _pp.Activities.ToDictionary(p => p.Activity.Id);
        _rows.Clear();
        foreach (var a in acts.OrderBy(a => byId.TryGetValue(a.Id, out var p) ? p.WbsPath : "").ThenBy(a => a.Code).ThenBy(a => a.Name))
            _rows.Add(new ActivityRow(a, byId.GetValueOrDefault(a.Id), _pp.Calendar.Currency));
        Kpis();
        if (selectedId != null) _grid.SelectedItem = _rows.FirstOrDefault(r => r.A.Id == selectedId);
    }

    private void Kpis()
    {
        if (_pp == null) return;
        var cur = _pp.Calendar.Currency;
        _kpis.Children.Clear();
        _kpis.Children.Add(W.Stat("Overall progress", $"{_pp.OverallPercent:0.#} %", Theme.Success));
        _kpis.Children.Add(W.Stat("Planned to date", $"{_pp.PlannedPercent:0.#} %", Theme.Muted));
        _kpis.Children.Add(W.Stat("Completed", $"{_pp.Activities.Count(a => a.State == WorkState.Completed)} / {_pp.Activities.Count}"));
        _kpis.Children.Add(W.Stat("Delayed", _pp.Activities.Count(a => a.State == WorkState.Delayed).ToString(), Theme.Danger));
        _kpis.Children.Add(W.Stat($"Actual cost ({cur})", PdfReports.M(_pp.Actual.Total), Theme.Accent));
        _kpis.Children.Add(W.Stat($"Budget ({cur})", PdfReports.M(_pp.PlannedCost), Theme.Muted));
        _kpis.Children.Add(W.Stat("Man-hours", PdfReports.N(_pp.Actual.ManHours)));
    }

    private async Task SaveAsync(ActivityRow r)
    {
        if (r.A.PlannedStart is { } s && r.A.PlannedFinish is { } f && f < s) { Dialogs.Info("Planned finish is before planned start."); return; }
        await Desk.Db.SaveAsync(r.A);
        // recalculate this row (cheap) so % / forecast / budget follow the new plan immediately
        var pp = await Task.Run(() => new ProgressService(Desk.Db).ComputeAsync(Desk.ProjectId));
        _pp = pp;
        r.Update(pp.Activities.FirstOrDefault(p => p.Activity.Id == r.A.Id));
        Kpis();
        Desk.Say($"Saved {r.A.Name}");
    }

    private async Task AddAsync()
    {
        var a = new Activity { ProjectId = Desk.ProjectId, Name = "New activity", Unit = "m³" };
        await Desk.Db.SaveAsync(a);
        var row = new ActivityRow(a, null, _pp?.Calendar.Currency ?? "₹");
        _rows.Add(row);
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row);
        _grid.CurrentCell = new DataGridCellInfo(row, _grid.Columns[1]);
        _grid.Focus();
        _grid.BeginEdit();
    }

    /// <summary>Activities as shown (search, status filter and column sort applied) to edit in Excel.</summary>
    public Task ExportEditAsync()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        return ExchangeUi.ExportAsync(() => ExchangeUi.Sheets.ActivitiesAsync(Desk.ProjectId), _view.Cast<ActivityRow>().Select(r => r.A),
            "Progress, forecast and cost are calculated from the daily reports and are not part of this sheet.");
    }

    public async Task ImportEditAsync(string? file)
    {
        if (await ExchangeUi.ImportAsync(() => ExchangeUi.Sheets.ActivitiesAsync(Desk.ProjectId), file)) await RefreshAsync();
    }

    private async Task DeleteAsync()
    {
        var sel = _grid.SelectedItems.OfType<ActivityRow>().ToList();
        if (sel.Count == 0) return;
        if (sel.Any(r => r.Done > 0) && !Dialogs.Confirm("Some selected activities already have progress in daily reports. Delete anyway? (Report rows stay, but lose the link.)")) return;
        if (!Dialogs.Confirm($"Delete {sel.Count} activit{(sel.Count == 1 ? "y" : "ies")}?")) return;
        foreach (var r in sel) { r.A.IsDeleted = true; _rows.Remove(r); }
        await Desk.Db.SaveAllAsync(sel.Select(r => r.A));
    }

    private async Task ExportAsync(bool pdf)
    {
        if (_pp == null) return;
        var period = $"As of {DateTime.Today:dd-MMM-yyyy}";
        var opt = await Desk.ReportOptionsAsync();
        if (pdf) await Dialogs.CreateAndOpenAsync($"Progress_cost_{DateTime.Now:yyyyMMdd_HHmm}.pdf", s => ProgressReports.Pdf(s, Desk.Project, _pp, period, true, opt));
        else await Dialogs.CreateAndOpenAsync($"Progress_cost_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", s => ProgressReports.Excel(s, Desk.Project, _pp, period));
    }

    // ------------------------------------------------------------------ bottom panel

    private void ShowDetail()
    {
        if (_pp == null) return;
        var tabs = new TabControl { Margin = new Thickness(10, 4, 10, 10) };
        if (_grid.SelectedItem is ActivityRow r)
        {
            tabs.Items.Add(new TabItem { Header = $"Photos & videos: {r.A.Name}", Content = new MediaPanel(MediaOwner.Activity, () => r.A.Id, defaultCaption: r.A.ToString()) });
            tabs.Items.Add(new TabItem { Header = $"Resources for: {r.A.Name}", Content = ResourcesPanel(r) });
        }
        tabs.Items.Add(new TabItem { Header = "Subcontractor-wise", Content = PartyGrid(_pp.BySubcontractor, "Subcontractor") });
        tabs.Items.Add(new TabItem { Header = "Department-wise", Content = PartyGrid(_pp.ByDepartment, "Department") });
        tabs.SelectedIndex = 0;
        _detail.Content = tabs;
    }

    private UIElement PartyGrid(List<PartySummary> list, string what) => new GridBuilder(list, editable: false)
        .Text(what, nameof(PartySummary.Name), 220)
        .Number("Man-days", nameof(PartySummary.ManDays), 90)
        .Number("Man-hours", nameof(PartySummary.ManHours), 90)
        .Number($"Cost ({_pp!.Calendar.Currency})", nameof(PartySummary.Cost), 120, "#,0.00")
        .Number("Activities", nameof(PartySummary.Activities), 80, "0")
        .Text("Work done (quantity)", nameof(PartySummary.ActivitiesDone), 0, wrap: true).Grid;

    /// <summary>Planned resources of the selected activity, with the planned cost (budget).</summary>
    private UIElement ResourcesPanel(ActivityRow r)
    {
        var list = new ObservableCollection<ActivityResource>(Task.Run(() => Desk.Db.ListAsync<ActivityResource>(x => x.ActivityId == r.A.Id)).GetAwaiter().GetResult());
        var total = W.Text("", 13, true, Theme.Primary);
        var cal = _pp!.Calendar;
        int Duration() => r.A.PlannedStart is { } s && r.A.PlannedFinish is { } f ? cal.WorkingDaysBetween(s, f)
            : r.A.PlannedRatePerDay > 0 ? (int)Math.Ceiling(r.A.PlannedQty / r.A.PlannedRatePerDay) : 1;
        void Total() => total.Text = $"Duration {Duration()} working day(s) · planned budget {cal.Currency} {PdfReports.M(list.Sum(x => CostCalculator.PlannedCost(x, Duration(), cal)))}";
        var grid = new GridBuilder(list)
            .Text("Resource", nameof(ActivityResource.ResourceName), 200)
            .Enum<ResourceType>("Type", nameof(ActivityResource.Type))
            .Number("Nos / quantity", nameof(ActivityResource.PlannedQty), 100)
            .Number("Hours each (blank = full duration)", nameof(ActivityResource.PlannedHours), 190)
            .Number("Rate", nameof(ActivityResource.Rate), 90, "#,0.00")
            .Enum<RateBasis>("Basis", nameof(ActivityResource.Basis))
            .OnEdited(async o => { await Desk.Db.SaveAsync((ActivityResource)o); Total(); })
            .Grid;
        Total();
        var add = W.Button("Add from resource list", async () =>
        {
            var res = await Desk.Db.ListForProjectAsync<SiteResource>(Desk.ProjectId);
            if (res.Count == 0) { Dialogs.Info("Add resources and rates first (Planning › Resources & rates)."); return; }
            res = res.OrderBy(x => x.Type).ThenBy(x => x.Name).ToList();
            var i = Dialogs.Choose("Assign resource", $"Resource for {r.A.Name}", res.Select(x => $"{x.Type}: {x} · {PdfReports.M(x.Rate)} {x.Basis}").ToList());
            if (i < 0) return;
            var x = res[i];
            var ar = new ActivityResource
            {
                ProjectId = Desk.ProjectId, ActivityId = r.A.Id, ResourceId = x.Id, ResourceName = x.ToString(), Type = x.Type, Rate = x.Rate, Basis = x.Basis,
                PlannedQty = x.Type is ResourceType.Manpower or ResourceType.Machinery ? 1 : r.A.PlannedQty,
            };
            await Desk.Db.SaveAsync(ar);
            list.Add(ar);
            Total();
        }, Glyph.Add);
        var remove = W.Button("Remove selected", async () =>
        {
            foreach (var x in grid.SelectedItems.OfType<ActivityResource>().ToList()) { await Desk.Db.DeleteAsync(x); list.Remove(x); }
            Total();
        }, Glyph.Delete);
        var dock = new DockPanel();
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        top.Children.Add(add);
        top.Children.Add(remove);
        top.Children.Add(total);
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(grid);
        return dock;
    }
}
