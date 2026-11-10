using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>
/// Daily progress report editor. One scrolling page of collapsible sections with a live summary bar
/// (manpower, man-hours, machines, cost). Cumulative quantities are calculated, never typed.
/// </summary>
public class DprEditorPage : ContentPage
{
    private DprBundle _b = new();
    private readonly string? _reportId;
    private readonly DateTime _newDate;
    private WorkCalendar _cal = new();
    private List<Activity> _activities = [];
    private List<Subcontractor> _subs = [];
    private List<Department> _depts = [];
    private Dictionary<string, double> _doneBefore = [];
    private bool _dirty;

    private readonly Label _sumManpower = SummaryValue();
    private readonly Label _sumSplit = Ui.Caption("");
    private readonly Label _sumHours = SummaryValue();
    private readonly Label _sumMachines = SummaryValue();
    private readonly Label _sumCost = SummaryValue();
    private readonly Label _locked = new() { Text = "🔒 Approved – tap Unlock in the toolbar to edit", BackgroundColor = Ui.Success, TextColor = Colors.White, Padding = 8, IsVisible = false };
    private readonly VerticalStackLayout _form = new() { Spacing = 8, Padding = 12 };
    private readonly Section _secActivities = new("1. Work progress (activities)");
    private readonly Section _secManpower = new("2. Manpower");
    private readonly Section _secMachines = new("3. Machinery & equipment", false);
    private readonly Section _secMaterials = new("4. Material & non-material resources", false);

    public DprEditorPage(string? reportId, DateTime? newDate = null)
    {
        _reportId = reportId;
        _newDate = newDate ?? DateTime.Today;
        Title = "Daily progress report";
        this.Page();
        ToolbarItems.Add(new ToolbarItem("💾 Save", null, () => _ = Ui.Guard(() => SaveAsync(true))));
        ToolbarItems.Add(new ToolbarItem("PDF", null, () => _ = Ui.Guard(PdfAsync)));
        ToolbarItems.Add(new ToolbarItem("Submit", null, () => _ = Ui.Guard(() => SetStatusAsync(ReportStatus.Submitted))) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Approve", null, () => _ = Ui.Guard(() => SetStatusAsync(ReportStatus.Approved))) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Unlock (back to draft)", null, () => _ = Ui.Guard(() => SetStatusAsync(ReportStatus.Draft))) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Excel", null, () => _ = Ui.Guard(ExcelAsync)) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Delete report", null, () => _ = Ui.Guard(DeleteAsync)) { Order = ToolbarItemOrder.Secondary });

        var summary = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)],
            Padding = new Thickness(12, 8), BackgroundColor = Ui.PrimaryDark,
        };
        summary.Add(SummaryCell("Manpower", _sumManpower, _sumSplit), 0);
        summary.Add(SummaryCell("Man-hours", _sumHours), 1);
        summary.Add(SummaryCell("Machines", _sumMachines), 2);
        summary.Add(SummaryCell("Cost today", _sumCost), 3);
        _sumSplit.TextColor = Color.FromArgb("#C7D7EA");

        var g = new Grid { RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)] };
        g.Add(summary, 0, 0);
        g.Add(_locked, 0, 1);
        g.Add(new ScrollView { Content = _form }, 0, 2);
        Content = g;
    }

    private static Label SummaryValue() => new() { TextColor = Colors.White, FontSize = 18, FontAttributes = FontAttributes.Bold };

    private static View SummaryCell(string label, Label value, Label? extra = null)
    {
        var v = new VerticalStackLayout { Children = { value, new Label { Text = label, TextColor = Color.FromArgb("#C7D7EA"), FontSize = 11 } } };
        if (extra != null) v.Children.Add(extra);
        return v;
    }

    private bool _loaded;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        await Ui.Guard(LoadAsync);
    }

    protected override bool OnBackButtonPressed()
    {
        if (!_dirty) return base.OnBackButtonPressed();
        Dispatcher.Dispatch(async () =>
        {
            var choice = await DisplayActionSheetAsync("Save changes?", "Keep editing", null, "Save", "Discard");
            if (choice == "Save") { await SaveAsync(false); await Navigation.PopAsync(); }
            else if (choice == "Discard") { _dirty = false; await Navigation.PopAsync(); }
        });
        return true;
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
        // Auto-save so nothing typed on site is lost (e.g. desktop menu navigation).
        if (_dirty && _b.Report.Status != ReportStatus.Approved) await SaveAsync(false);
    }

    private async Task LoadAsync()
    {
        var pid = AppServices.ProjectId;
        _cal = await AppServices.Db.CalendarAsync(pid);
        _activities = (await AppServices.Db.ListForProjectAsync<Activity>(pid)).OrderBy(a => a.Code).ThenBy(a => a.Name).ToList();
        _subs = (await AppServices.Db.ListForProjectAsync<Subcontractor>(pid)).Where(s => s.IsActive).OrderBy(s => s.Name).ToList();
        _depts = (await AppServices.Db.ListForProjectAsync<Department>(pid)).OrderBy(d => d.Name).ToList();

        if (_reportId != null) _b = await AppServices.Db.LoadReportAsync(_reportId);
        else
        {
            _b = await AppServices.Db.NewReportAsync(pid, _newDate);
        }
        await LoadDoneBeforeAsync();
        Title = _b.Report.Title;
        Build();
        Recalc();
        _dirty = false; // an untouched new report is not saved
    }

    private async Task LoadDoneBeforeAsync()
    {
        var r = _b.Report;
        var earlier = (await AppServices.Db.ListForProjectAsync<DailyReport>(r.ProjectId))
            .Where(x => x.Id != r.Id && (x.Date.Date < r.Date.Date || (x.Date.Date == r.Date.Date && x.ReportNo < r.ReportNo)))
            .Select(x => x.Id).ToHashSet();
        _doneBefore = (await AppServices.Db.ListForProjectAsync<ReportActivity>(r.ProjectId))
            .Where(x => x.ActivityId != "" && earlier.Contains(x.ReportId))
            .GroupBy(x => x.ActivityId).ToDictionary(g => g.Key, g => g.Sum(x => x.TodayQty));
    }

    private void Changed()
    {
        _dirty = true;
        Recalc();
    }

    private void Recalc()
    {
        DprCalculator.UpdateTotals(_b);
        var sub = DprCalculator.Total(_b.Manpower, ManpowerSource.Subcontractor);
        var dept = DprCalculator.Total(_b.Manpower, ManpowerSource.Department);
        _sumManpower.Text = (sub + dept).ToString();
        _sumSplit.Text = $"Sub {sub} · Dept {dept}";
        _sumHours.Text = PdfReports.N(_b.Manpower.Sum(m => m.ManHours));
        _sumMachines.Text = _b.Machines.Sum(m => m.Count).ToString();
        _sumCost.Text = $"{_cal.Currency}{PdfReports.M(CostCalculator.ForReport(_b, _cal).Total)}";
        _secActivities.Badge = $"{_b.Activities.Count(a => a.TodayQty > 0)}/{_b.Activities.Count} worked";
        _secManpower.Badge = $"{sub + dept} nos";
        _secMachines.Badge = $"{_b.Machines.Sum(m => m.Count)} nos";
        _secMaterials.Badge = $"{_b.Materials.Count} items";
        _locked.IsVisible = _b.Report.Status == ReportStatus.Approved;
        _form.IsEnabled = _b.Report.Status != ReportStatus.Approved;
    }

    // ------------------------------------------------------------------ layout

    private void Build()
    {
        var r = _b.Report;
        _form.Children.Clear();

        _form.Children.Add(new Section("General").With(
            Ui.Columns(4,
                Ui.Field("Date", Ui.Date(r.Date, v => { r.Date = v; Title = r.Title; _ = LoadDoneBeforeAsync(); Changed(); })),
                Ui.Field("Report no.", Ui.Label($"#{r.ReportNo}", 16, true)),
                Ui.Field("Shift", Ui.EnumPicker(r.Shift, v => { r.Shift = v; Changed(); })),
                Ui.Field("Status", Ui.Label(r.Status.ToString(), 16, true, Ui.StatusColor(r.Status.ToString()))),
                Ui.Field("Weather – morning", new Combo(r.WeatherMorning, Lists.Weather, v => { r.WeatherMorning = v; Changed(); })),
                Ui.Field("Weather – evening", new Combo(r.WeatherEvening, Lists.Weather, v => { r.WeatherEvening = v; Changed(); })),
                Ui.Field("Temperature °C", Ui.Number(r.TemperatureC ?? 0, v => { r.TemperatureC = v == 0 ? null : v; Changed(); })),
                Ui.Field("Rainfall mm", Ui.Number(r.RainfallMm ?? 0, v => { r.RainfallMm = v == 0 ? null : v; Changed(); })),
                Ui.Field("Working hours", Ui.Number(r.WorkingHours, v => { r.WorkingHours = v; Changed(); })),
                Ui.Field("Prepared by", Ui.Entry(r.PreparedBy, "", changed: v => { r.PreparedBy = v; Changed(); })),
                Ui.Field("Checked by", Ui.Entry(r.CheckedBy, "", changed: v => { r.CheckedBy = v; Changed(); })),
                Ui.Field("Approved by", Ui.Entry(r.ApprovedBy, "", changed: v => { r.ApprovedBy = v; Changed(); })))));

        _secActivities.ClearBody();
        _secActivities.With(new RowList<ReportActivity>(_b.Activities, ActivityRow, ActivitiesChanged,
            "No activities yet. Add from the activity list (progress then sums up automatically).",
            ("＋ Activity from list", AddActivityAsync), ("＋ Unplanned work", RowList<ReportActivity>.Now(() => new ReportActivity()))));
        _form.Children.Add(_secActivities);

        _secManpower.ClearBody();
        _secManpower.With(new RowList<ReportManpower>(_b.Manpower, ManpowerRow, Changed, "No manpower entered.",
            ("＋ Subcontractor crew", RowList<ReportManpower>.Now(() => new ReportManpower { Source = ManpowerSource.Subcontractor, Hours = _cal.HoursPerDay, Count = 1 })),
            ("＋ Department staff", RowList<ReportManpower>.Now(() => new ReportManpower { Source = ManpowerSource.Department, Hours = _cal.HoursPerDay, Count = 1 })),
            ("＋ From resource list", ManpowerFromListAsync)));
        _form.Children.Add(_secManpower);

        _secMachines.ClearBody();
        _secMachines.With(new RowList<ReportMachine>(_b.Machines, MachineRow, Changed, "No machinery entered.",
            ("＋ Machine from list", MachineFromListAsync),
            ("＋ Other machine", RowList<ReportMachine>.Now(() => new ReportMachine { WorkingHours = _cal.HoursPerDay }))));
        _form.Children.Add(_secMachines);

        _secMaterials.ClearBody();
        _secMaterials.With(new RowList<ReportMaterial>(_b.Materials, MaterialRow, Changed, "No materials or other resources entered.",
            ("＋ From resource list", MaterialFromListAsync),
            ("＋ Material", RowList<ReportMaterial>.Now(() => new ReportMaterial())),
            ("＋ Non-material (hire, testing…)", RowList<ReportMaterial>.Now(() => new ReportMaterial { Kind = ResourceType.Other, Basis = RateBasis.LumpSum, Unit = "LS" }))));
        _form.Children.Add(_secMaterials);

        _form.Children.Add(new Section("5. Hindrances", false).With(new RowList<Hindrance>(_b.Hindrances, HindranceRow, Changed, "No hindrance today.",
            ("＋ Hindrance", RowList<Hindrance>.Now(() => new Hindrance { ProjectId = r.ProjectId, FromDate = r.Date })))));
        _form.Children.Add(new Section("6. Requirements", false).With(new RowList<Requirement>(_b.Requirements, RequirementRow, Changed, "No new requirement.",
            ("＋ Requirement", RowList<Requirement>.Now(() => new Requirement { ProjectId = r.ProjectId, RaisedDate = r.Date, RaisedBy = r.PreparedBy })))));

        _form.Children.Add(new Section("7. Safety & toolbox talk", false).With(
            Ui.Columns(2,
                Ui.Field("Toolbox talk topic", new Combo(r.ToolboxTalkTopic, ["Working at height", "PPE", "Excavation safety", "Electrical safety", "Lifting & rigging", "Housekeeping", "Fire safety", "Hot work", "Scaffolding", "Heat stress"], v => { r.ToolboxTalkTopic = v; Changed(); })),
                Ui.Field("Attendees", Ui.Number(r.ToolboxTalkAttendees, v => { r.ToolboxTalkAttendees = (int)v; Changed(); }))),
            Ui.Field("Safety observations", Ui.Memo(r.SafetyRemarks, "Unsafe acts, near misses, PPE compliance…", v => { r.SafetyRemarks = v; Changed(); }, 60))));

        var media = new MediaStrip(MediaOwner.Report, () => _b.Report.Id);
        var mediaSec = new Section("8. Photos & videos", false).With(media);
        media.CountChanged += n => mediaSec.Badge = $"{n}";
        _form.Children.Add(mediaSec);

        _form.Children.Add(new Section("9. Plan for tomorrow & remarks").With(
            Ui.Field("Plan for tomorrow", Ui.Memo(r.PlanForTomorrow, "Work planned, resources needed…", v => { r.PlanForTomorrow = v; Changed(); }, 60)),
            Ui.Field("Visitors", Ui.Entry(r.Visitors, "Client / consultant visits", changed: v => { r.Visitors = v; Changed(); })),
            Ui.Field("Remarks", Ui.Memo(r.Remarks, "General remarks", v => { r.Remarks = v; Changed(); }, 60))));

        _form.Children.Add(Ui.Button("💾 Save report", () => SaveAsync(true)));
    }

    // ------------------------------------------------------------------ "add from list" pickers

    private async Task<ReportActivity?> AddActivityAsync()
    {
        var a = await PickActivityAsync();
        if (a == null) return null;
        if (_b.Activities.Any(x => x.ActivityId == a.Id)) { await DisplayAlertAsync("Already added", a.ToString(), "OK"); return null; }
        return new ReportActivity
        {
            ActivityId = a.Id, ActivityName = a.ToString(), Location = a.Location, Unit = a.Unit, PlannedQty = a.PlannedQty,
            SubcontractorId = a.SubcontractorId, SubcontractorName = _subs.FirstOrDefault(s => s.Id == a.SubcontractorId)?.Name ?? "",
            CumulativeQty = _doneBefore.GetValueOrDefault(a.Id),
        };
    }

    /// <summary>Activities changed: the "worked on activity" pickers in other rows need the new list.</summary>
    private void ActivitiesChanged()
    {
        Changed();
        foreach (var list in new View[] { _secManpower, _secMachines, _secMaterials }) RebuildRows(list);
    }

    private static void RebuildRows(Element e)
    {
        switch (e)
        {
            case RowList<ReportManpower> a: a.Rebuild(); return;
            case RowList<ReportMachine> b: b.Rebuild(); return;
            case RowList<ReportMaterial> c: c.Rebuild(); return;
        }
        if (e is IVisualTreeElement v)
            foreach (var child in v.GetVisualChildren().OfType<Element>()) RebuildRows(child);
    }

    private async Task<ReportManpower?> ManpowerFromListAsync()
    {
        var res = await ActivityResourceEditor.PickResourceAsync(ResourceType.Manpower);
        if (res == null) return null;
        var fromSub = res.SubcontractorId != "";
        return new ReportManpower
        {
            Source = fromSub ? ManpowerSource.Subcontractor : ManpowerSource.Department,
            PartyId = fromSub ? res.SubcontractorId : res.DepartmentId,
            PartyName = fromSub ? _subs.FirstOrDefault(s => s.Id == res.SubcontractorId)?.Name ?? "" : _depts.FirstOrDefault(d => d.Id == res.DepartmentId)?.Name ?? "",
            Trade = res.Name, ResourceId = res.Id, Rate = res.Rate, Basis = res.Basis, Count = 1, Hours = _cal.HoursPerDay,
        };
    }

    private async Task<ReportMachine?> MachineFromListAsync()
    {
        var res = await ActivityResourceEditor.PickResourceAsync(ResourceType.Machinery);
        return res == null ? null : new ReportMachine
        {
            Name = res.Name, ResourceId = res.Id, Rate = res.Rate, Basis = res.Basis, WorkingHours = _cal.HoursPerDay,
            Ownership = res.SubcontractorId == "" ? MachineOwnership.Own : MachineOwnership.Hired,
        };
    }

    private async Task<ReportMaterial?> MaterialFromListAsync()
    {
        var res = await ActivityResourceEditor.PickResourceAsync();
        if (res is { Type: ResourceType.Material or ResourceType.Other })
            return new ReportMaterial { Material = res.Name, Kind = res.Type, Unit = res.Unit, ResourceId = res.Id, Rate = res.Rate, Basis = res.Basis };
        if (res != null) await DisplayAlertAsync("Not a material", "Pick a Material or Other resource here.", "OK");
        return null;
    }

    private async Task<Activity?> PickActivityAsync()
    {
        if (_activities.Count == 0)
        {
            if (await DisplayAlertAsync("No activities", "Create activities first (planned quantity = 100 %).", "Create now", "Cancel"))
                await AppShell.Push(EntityForms.ActivityForm(new Activity { ProjectId = AppServices.ProjectId }));
            return null;
        }
        var open = _activities.Where(a => a.PlannedQty <= 0 || _doneBefore.GetValueOrDefault(a.Id) < a.PlannedQty).ToList();
        var labels = open.Select(a => $"{a} ({PdfReports.N(_doneBefore.GetValueOrDefault(a.Id))}/{PdfReports.N(a.PlannedQty)} {a.Unit})").ToArray();
        var choice = await DisplayActionSheetAsync("Activity (not yet 100 %)", "Cancel", null, labels);
        var i = Array.IndexOf(labels, choice);
        return i < 0 ? null : open[i];
    }

    /// <summary>Picker of today's activities, used to book manpower, machines and materials against an activity.</summary>
    private Picker ActivityPicker(string selectedId, string selectedName, Action<string, string> changed)
    {
        var items = new List<PickItem> { new("", "General / overheads") };
        items.AddRange(_b.Activities.Where(a => a.ActivityId != "" || a.ActivityName != "")
            .Select(a => new PickItem(a.ActivityId != "" ? a.ActivityId : "name:" + a.ActivityName, a.ActivityName)));
        items.AddRange(_activities.Where(a => items.All(i => i.Id != a.Id)).Select(a => new PickItem(a.Id, a.ToString())));
        var key = selectedId != "" ? selectedId : selectedName != "" ? "name:" + selectedName : "";
        var p = new Picker { ItemsSource = items, SelectedItem = items.FirstOrDefault(i => i.Id == key) ?? items[0] };
        p.SelectedIndexChanged += (_, _) =>
        {
            if (p.SelectedItem is not PickItem it) return;
            if (it.Id.StartsWith("name:")) changed("", it.Name);
            else changed(it.Id, it.Id == "" ? "" : it.Name);
        };
        return p;
    }

    private View ActivityRow(ReportActivity a)
    {
        var progress = new ProgressBar { ProgressColor = Ui.Primary, HeightRequest = 8 };
        var info = Ui.Caption("");
        void Update()
        {
            a.CumulativeQty = Math.Round((a.ActivityId != "" ? _doneBefore.GetValueOrDefault(a.ActivityId) : 0) + a.TodayQty, 4);
            var pct = a.PlannedQty > 0 ? a.CumulativeQty / a.PlannedQty * 100 : 0;
            progress.Progress = Math.Min(1, pct / 100);
            progress.ProgressColor = pct >= 100 ? Ui.Success : Ui.Primary;
            info.Text = a.PlannedQty > 0
                ? $"Cumulative {PdfReports.N(a.CumulativeQty)} of {PdfReports.N(a.PlannedQty)} {a.Unit} = {Math.Min(100, pct):0.##} %" +
                  (pct > 100 ? $"  ⚠ exceeds plan by {PdfReports.N(a.CumulativeQty - a.PlannedQty)}" : pct >= 100 ? "  ✔ completed" : $" · balance {PdfReports.N(a.PlannedQty - a.CumulativeQty)}")
                : "Unplanned item (no planned quantity)";
            info.TextColor = pct > 100 ? Ui.Danger : Ui.Muted;
        }
        var agency = new Picker { Title = "Agency" };
        var subs = new List<Subcontractor> { new() { Id = "", Name = "(own)" } };
        subs.AddRange(_subs);
        agency.ItemsSource = subs;
        agency.SelectedItem = subs.FirstOrDefault(s => s.Id == a.SubcontractorId) ?? subs[0];
        agency.SelectedIndexChanged += (_, _) => { if (agency.SelectedItem is Subcontractor s) { a.SubcontractorId = s.Id; a.SubcontractorName = s.Id == "" ? "" : s.Name; Changed(); } };

        View name = a.ActivityId != ""
            ? Ui.Label(a.ActivityName, 15, true)
            : Ui.Entry(a.ActivityName, "Unplanned work description", changed: v => { a.ActivityName = v; Changed(); });
        Update();
        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                name,
                Ui.Columns(4,
                    Ui.Field($"Today's quantity ({a.Unit})", Ui.Number(a.TodayQty, v => { a.TodayQty = v; Update(); Changed(); })),
                    Ui.Field("Location", Ui.Entry(a.Location, "Block / floor / grid", changed: v => { a.Location = v; Changed(); })),
                    Ui.Field("Agency", agency),
                    a.ActivityId == ""
                        ? Ui.Field("Unit", new Combo(a.Unit, Lists.Units, v => { a.Unit = v; Changed(); }))
                        : Ui.Field("Remarks", Ui.Entry(a.Remarks, "", changed: v => { a.Remarks = v; Changed(); }))),
                progress, info,
            }
        };
    }

    private View CostLabel(Func<decimal> cost)
    {
        var l = Ui.Label("", 12, true, Ui.Primary);
        void U() => l.Text = $"Cost: {_cal.Currency} {PdfReports.M(cost())}";
        U();
        _costUpdaters.Add(U);
        return l;
    }

    private readonly List<Action> _costUpdaters = [];

    private void RowChanged()
    {
        foreach (var u in _costUpdaters) u();
        Changed();
    }

    private View ManpowerRow(ReportManpower m)
    {
        var party = new Picker { Title = m.Source == ManpowerSource.Subcontractor ? "Subcontractor" : "Department" };
        var options = m.Source == ManpowerSource.Subcontractor
            ? _subs.Select(s => new PickItem(s.Id, s.Name)).ToList()
            : _depts.Select(d => new PickItem(d.Id, d.Name)).ToList();
        if (m.PartyName != "" && options.All(o => o.Id != m.PartyId)) options.Insert(0, new PickItem(m.PartyId, m.PartyName));
        party.ItemsSource = options;
        party.SelectedItem = options.FirstOrDefault(o => o.Id == m.PartyId && o.Name == m.PartyName) ?? options.FirstOrDefault(o => o.Id == m.PartyId && m.PartyId != "");
        party.SelectedIndexChanged += (_, _) => { if (party.SelectedItem is PickItem p) { m.PartyId = p.Id; m.PartyName = p.Name; Changed(); } };

        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Chip(m.Source == ManpowerSource.Subcontractor ? "SUBCONTRACTOR" : "DEPARTMENT", m.Source == ManpowerSource.Subcontractor ? Ui.Accent : Ui.Primary),
                Ui.Columns(4,
                    Ui.Field(m.Source == ManpowerSource.Subcontractor ? "Subcontractor" : "Department", party),
                    Ui.Field("Trade / designation", new Combo(m.Trade, Lists.Trades, v => { m.Trade = v; Changed(); })),
                    Ui.Field("Nos", Ui.Number(m.Count, v => { m.Count = (int)Math.Round(v); RowChanged(); })),
                    Ui.Field("Hours each", Ui.Number(m.Hours, v => { m.Hours = v; RowChanged(); })),
                    Ui.Field("Worked on activity", ActivityPicker(m.ActivityId, m.ActivityName, (id, n) => { m.ActivityId = id; m.ActivityName = n; Changed(); })),
                    Ui.Field("Rate", EntityForms.DecimalEntry(m.Rate, v => { m.Rate = v; RowChanged(); })),
                    Ui.Field("Rate basis", Ui.EnumPicker(m.Basis, v => { m.Basis = v; RowChanged(); })),
                    Ui.Field("Remarks", Ui.Entry(m.Remarks, "", changed: v => { m.Remarks = v; Changed(); }))),
                CostLabel(() => CostCalculator.Cost(m, _cal)),
            }
        };
    }

    private View MachineRow(ReportMachine m) => new VerticalStackLayout
    {
        Spacing = 6,
        Children =
        {
            Ui.Columns(4,
                Ui.Field("Machine / equipment", new Combo(m.Name, ["JCB", "Excavator", "Tower crane", "Mobile crane", "Concrete pump", "Transit mixer", "Batching plant", "Dumper", "Roller", "DG set", "Welding machine", "Hoist", "Tractor"], v => { m.Name = v; Changed(); })),
                Ui.Field("Own / hired", Ui.EnumPicker(m.Ownership, v => { m.Ownership = v; RowChanged(); })),
                Ui.Field("Nos", Ui.Number(m.Count, v => { m.Count = (int)Math.Round(v); RowChanged(); })),
                Ui.Field("Working hrs", Ui.Number(m.WorkingHours, v => { m.WorkingHours = v; RowChanged(); })),
                Ui.Field("Idle / breakdown hrs", Ui.Number(m.IdleHours, v => { m.IdleHours = v; RowChanged(); })),
                Ui.Field("Fuel (L)", Ui.Number(m.FuelLitres, v => { m.FuelLitres = v; Changed(); })),
                Ui.Field("Worked on activity", ActivityPicker(m.ActivityId, m.ActivityName, (id, n) => { m.ActivityId = id; m.ActivityName = n; Changed(); })),
                Ui.Field("Rate", EntityForms.DecimalEntry(m.Rate, v => { m.Rate = v; RowChanged(); })),
                Ui.Field("Rate basis", Ui.EnumPicker(m.Basis, v => { m.Basis = v; RowChanged(); })),
                Ui.Field("Remarks", Ui.Entry(m.Remarks, "", changed: v => { m.Remarks = v; Changed(); }))),
            CostLabel(() => CostCalculator.Cost(m, _cal)),
        }
    };

    private View MaterialRow(ReportMaterial m) => new VerticalStackLayout
    {
        Spacing = 6,
        Children =
        {
            Ui.Chip(m.Kind == ResourceType.Other ? "NON-MATERIAL" : "MATERIAL", m.Kind == ResourceType.Other ? Ui.Muted : Ui.Success),
            Ui.Columns(4,
                Ui.Field(m.Kind == ResourceType.Other ? "Description" : "Material", new Combo(m.Material, m.Kind == ResourceType.Other
                    ? ["Scaffolding hire", "Shuttering hire", "Testing charges", "Transport", "Water tanker", "Power / DG", "Survey"]
                    : ["Cement", "RMC", "TMT steel", "Sand", "Aggregate 20 mm", "Aggregate 10 mm", "Bricks", "AAC blocks", "Binding wire", "Shuttering oil"], v => { m.Material = v; Changed(); })),
                Ui.Field("Unit", new Combo(m.Unit, Lists.Units, v => { m.Unit = v; Changed(); })),
                Ui.Field("Received", Ui.Number(m.Received, v => { m.Received = v; Changed(); })),
                Ui.Field("Consumed / used", Ui.Number(m.Consumed, v => { m.Consumed = v; RowChanged(); })),
                Ui.Field("Used on activity", ActivityPicker(m.ActivityId, m.ActivityName, (id, n) => { m.ActivityId = id; m.ActivityName = n; Changed(); })),
                Ui.Field("Rate", EntityForms.DecimalEntry(m.Rate, v => { m.Rate = v; RowChanged(); })),
                Ui.Field("Rate basis", Ui.EnumPicker(m.Basis, v => { m.Basis = v; RowChanged(); })),
                Ui.Field("Remarks / challan no.", Ui.Entry(m.Remarks, "", changed: v => { m.Remarks = v; Changed(); }))),
            CostLabel(() => CostCalculator.Cost(m)),
        }
    };

    private View HindranceRow(Hindrance h) => Ui.Columns(3,
        Ui.Field("Category", new Combo(h.Category, Lists.HindranceCategories, v => { h.Category = v; Changed(); })),
        Ui.Field("Responsible", new Combo(h.ResponsibleParty, Lists.ResponsibleParties, v => { h.ResponsibleParty = v; Changed(); })),
        Ui.Field("Hours lost", Ui.Number(h.HoursLost, v => { h.HoursLost = v; Changed(); })),
        Ui.Field("Description", Ui.Entry(h.Description, "What stopped the work?", changed: v => { h.Description = v; Changed(); })),
        Ui.Field("Affected activity", Ui.Entry(h.AffectedActivity, "", changed: v => { h.AffectedActivity = v; Changed(); })),
        Ui.Field("Status", Ui.EnumPicker(h.Status, v => { h.Status = v; Changed(); })));

    private View RequirementRow(Requirement q) => Ui.Columns(3,
        Ui.Field("Item", Ui.Entry(q.Item, "What is needed?", changed: v => { q.Item = v; Changed(); })),
        Ui.Field("Category", new Combo(q.Category, Lists.RequirementCategories, v => { q.Category = v; Changed(); })),
        Ui.Field("Quantity", Ui.Number(q.Quantity, v => { q.Quantity = v; Changed(); })),
        Ui.Field("Unit", new Combo(q.Unit, Lists.Units, v => { q.Unit = v; Changed(); })),
        Ui.Field("Required by", Ui.OptionalDate(q.RequiredBy, v => { q.RequiredBy = v; Changed(); })),
        Ui.Field("Priority", Ui.EnumPicker(q.Priority, v => { q.Priority = v; Changed(); })));

    // ------------------------------------------------------------------ actions

    private async Task SaveAsync(bool notify)
    {
        if (_b.Report.Status == ReportStatus.Approved && notify)
        {
            await DisplayAlertAsync("Approved", "This report is approved and locked. Use Unlock to edit it.", "OK");
            return;
        }
        _b.Activities.RemoveAll(a => a.ActivityId == "" && string.IsNullOrWhiteSpace(a.ActivityName) && a.TodayQty == 0);
        _b.Hindrances.RemoveAll(h => string.IsNullOrWhiteSpace(h.Description) && h.HoursLost == 0);
        _b.Requirements.RemoveAll(q => string.IsNullOrWhiteSpace(q.Item));
        var dup = (await AppServices.Db.ListForProjectAsync<DailyReport>(_b.Report.ProjectId))
            .FirstOrDefault(x => x.Id != _b.Report.Id && x.Date.Date == _b.Report.Date.Date && x.Shift == _b.Report.Shift);
        if (dup != null && notify && !await DisplayAlertAsync("Duplicate date", $"DPR #{dup.ReportNo} already exists for this date and shift. Save anyway?", "Save", "Cancel"))
            return;
        await AppServices.Db.SaveReportAsync(_b);
        _dirty = false;
        Title = _b.Report.Title;
        if (notify) await DisplayAlertAsync("Saved", $"{_b.Report.Title} saved" + (AppServices.SyncSettings.Enabled ? " and syncing." : "."), "OK");
    }

    private async Task SetStatusAsync(ReportStatus status)
    {
        if (status == ReportStatus.Approved && string.IsNullOrWhiteSpace(_b.Report.ApprovedBy))
        {
            var who = await DisplayPromptAsync("Approve", "Approved by", "Approve", "Cancel", initialValue: AppServices.Db.UserName);
            if (who == null) return;
            _b.Report.ApprovedBy = who;
        }
        _b.Report.Status = status;
        await AppServices.Db.SaveReportAsync(_b);
        _dirty = false;
        Build();
        Recalc();
    }

    private async Task PdfAsync()
    {
        if (_dirty && _b.Report.Status != ReportStatus.Approved) await SaveAsync(false);
        var b = await AppServices.Db.LoadReportAsync(_b.Report.Id);
        var opt = await ReportProfiles.CurrentAsync();
        var path = await FileExport.CreateAsync($"{PdfReports.ReportLabel(b)}_{b.Report.Date:yyyy-MM-dd}.pdf",
            s => PdfReports.DailyReport(s, b, AppServices.Db.MediaPath, _cal, opt));
        await FileExport.OfferAsync(this, path);
    }

    private async Task ExcelAsync()
    {
        if (_dirty && _b.Report.Status != ReportStatus.Approved) await SaveAsync(false);
        var b = await AppServices.Db.LoadReportAsync(_b.Report.Id);
        var path = await FileExport.CreateAsync($"DPR_{b.Report.ReportNo}_{b.Report.Date:yyyy-MM-dd}.xlsx",
            s => ExcelReports.ReportWorkbook(s, b.Project, [b], b.Report.Title, _cal));
        await FileExport.OfferAsync(this, path);
    }

    private async Task DeleteAsync()
    {
        if (!await DisplayAlertAsync("Delete report?", "The report and its rows are removed on all devices.", "Delete", "Cancel")) return;
        _b.Activities.Clear(); _b.Manpower.Clear(); _b.Machines.Clear(); _b.Materials.Clear(); _b.Hindrances.Clear(); _b.Requirements.Clear();
        await AppServices.Db.SaveReportAsync(_b); // soft-deletes the rows
        await AppServices.Db.DeleteAsync(_b.Report);
        _dirty = false;
        await Navigation.PopAsync();
    }
}
