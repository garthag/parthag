using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>A simple edit page: fields in a scroll view, Save and Delete in the toolbar.</summary>
public class FormPage<T> : ContentPage where T : SyncEntity
{
    private readonly T _item;
    private readonly Func<T, string?> _validate;
    private readonly Func<T, Task>? _afterSave;

    public FormPage(string title, T item, Func<T, string?> validate, Func<T, Task>? afterSave, params View[] fields)
    {
        Title = title;
        _item = item;
        _validate = validate;
        _afterSave = afterSave;
        this.Page();
        ToolbarItems.Add(new ToolbarItem("💾 Save", null, () => _ = Ui.Guard(SaveAsync)));
        ToolbarItems.Add(new ToolbarItem("🗑 Delete", null, () => _ = Ui.Guard(DeleteAsync)) { Order = ToolbarItemOrder.Secondary });
        var stack = new VerticalStackLayout { Padding = 14, Spacing = 10, MaximumWidthRequest = 900 };
        foreach (var f in fields) stack.Children.Add(f);
        stack.Children.Add(Ui.Button("💾 Save", SaveAsync));
        Content = new ScrollView { Content = stack };
    }

    private async Task SaveAsync()
    {
        var error = _validate(_item);
        if (error != null) { await DisplayAlertAsync("Check the form", error, "OK"); return; }
        await AppServices.Db.SaveAsync(_item);
        if (_afterSave != null) await _afterSave(_item);
        await Navigation.PopAsync();
    }

    private async Task DeleteAsync()
    {
        if (!await DisplayAlertAsync("Delete?", "This removes it on every synced device.", "Delete", "Cancel")) return;
        await AppServices.Db.DeleteAsync(_item);
        await Navigation.PopAsync();
    }
}

public static class EntityForms
{
    private static View F(string label, View input) => Ui.Field(label, input);

    private static Switch Switch(bool value, Action<bool> changed)
    {
        var sw = new Switch { IsToggled = value, OnColor = Ui.Primary, HorizontalOptions = LayoutOptions.Start };
        sw.Toggled += (_, a) => changed(a.Value);
        return sw;
    }

    /// <summary>Read-only panel showing what the app calculated for an activity from the daily reports.</summary>
    private static View ProgressPanel(Activity a)
    {
        var body = new VerticalStackLayout { Spacing = 4, Children = { Ui.Caption("Calculating…") } };
        if (string.IsNullOrEmpty(a.ProjectId)) return body;
        _ = Task.Run(async () =>
        {
            var pp = await new ProgressService(AppServices.Db).ComputeAsync(a.ProjectId);
            var p = pp.Activities.FirstOrDefault(x => x.Activity.Id == a.Id);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                body.Children.Clear();
                if (p == null) { body.Children.Add(Ui.Caption("Not reported yet. Progress appears after the first daily report.")); return; }
                var cur = pp.Calendar.Currency;
                body.Children.Add(new ProgressBar { Progress = p.Percent / 100, ProgressColor = p.Percent >= 100 ? Ui.Success : Ui.Primary, HeightRequest = 10 });
                body.Children.Add(Ui.Columns(4,
                    Ui.Stat("Done", $"{PdfReports.N(p.DoneQty)} {a.Unit}"),
                    Ui.Stat("Progress", $"{p.Percent:0.##} %", p.Percent >= 100 ? Ui.Success : Ui.Primary),
                    Ui.Stat("Balance", $"{PdfReports.N(p.RemainingQty)} {a.Unit}", Ui.Muted),
                    Ui.Stat("Status", ProgressReports.StateText(p.State), p.State == WorkState.Delayed ? Ui.Danger : Ui.Primary),
                    Ui.Stat("Days worked", p.DaysWorked.ToString(), Ui.Muted),
                    Ui.Stat("Average / day", $"{PdfReports.N(p.AvgPerDay)} {a.Unit}", Ui.Muted),
                    Ui.Stat("Forecast finish", PdfReports.D(p.ForecastFinish), p.State == WorkState.Delayed ? Ui.Danger : Ui.Muted),
                    Ui.Stat($"Cost ({cur})", PdfReports.M(p.Actual.Total), Ui.Accent)));
                body.Children.Add(Ui.Caption($"Done = sum of 'today' quantities in all daily reports. % = done ÷ planned × 100 (max 100). " +
                                             $"Forecast = balance ÷ average per day, on the work calendar. Cost/unit {PdfReports.M(p.CostPerUnit)} · cost/day {PdfReports.M(p.CostPerDay)}."));
            });
        });
        return body;
    }

    public static Page ProjectForm(Project p) => new FormPage<Project>(string.IsNullOrEmpty(p.Name) ? "New project" : p.Name, p,
        x => string.IsNullOrWhiteSpace(x.Name) ? "Enter the project name." : null,
        async x =>
        {
            if (AppServices.CurrentProject == null || AppServices.CurrentProject.Id == x.Id) await AppServices.SetProjectAsync(x);
            // New projects get the standard departments so manpower entry is quick.
            var depts = await AppServices.Db.ListForProjectAsync<Department>(x.Id);
            if (depts.Count == 0)
                await AppServices.Db.SaveAllAsync(Lists.DefaultDepartments.Select(n => new Department { ProjectId = x.Id, Name = n }));
        },
        Ui.Columns(2,
            F("Project name *", Ui.Entry(p.Name, "e.g. G+12 Residential Tower", changed: v => p.Name = v)),
            F("Project code", Ui.Entry(p.Code, "e.g. RT-01", changed: v => p.Code = v)),
            F("Client", Ui.Entry(p.Client, "", changed: v => p.Client = v)),
            F("Contractor", Ui.Entry(p.Contractor, "", changed: v => p.Contractor = v)),
            F("Consultant / PMC", Ui.Entry(p.Consultant, "", changed: v => p.Consultant = v)),
            F("Location", Ui.Entry(p.Location, "", changed: v => p.Location = v)),
            F("Start date", Ui.Date(p.StartDate, v => p.StartDate = v)),
            F("Completion date", Ui.OptionalDate(p.EndDate, v => p.EndDate = v))),
        Ui.SectionHeader("Daily reports"),
        Ui.Columns(3,
            F("Report number prefix", Ui.Entry(p.ReportPrefix, "DPR-", changed: v => p.ReportPrefix = v)),
            F("First report number", Ui.Number(p.FirstReportNo, v => p.FirstReportNo = (int)Math.Max(0, v), "asked once")),
            F("New report copies yesterday's crew & activities", Switch(p.CarryForward, v => p.CarryForward = v))),
        Ui.Caption("Numbers and dates are automatic: each new report takes the next number and the next working day."),
        F("Description / scope", Ui.Memo(p.Description, "Scope of work, contract value, notes…", v => p.Description = v)),
        Ui.Button("✔ Make this the current project", async () => { await AppServices.SetProjectAsync(p); await Ui.Toast($"{p.Name} is now the current project."); }, false));

    public static Page SubcontractorForm(Subcontractor s)
    {
        var active = new Switch { IsToggled = s.IsActive };
        active.Toggled += (_, a) => s.IsActive = a.Value;
        return new FormPage<Subcontractor>(string.IsNullOrEmpty(s.Name) ? "New subcontractor" : s.Name, s,
            x => string.IsNullOrWhiteSpace(x.Name) ? "Enter the subcontractor name." : null, null,
            Ui.Columns(2,
                F("Name *", Ui.Entry(s.Name, "Agency / firm name", changed: v => s.Name = v)),
                F("Trade / scope", new Combo(s.Trade, ["Civil", "Formwork", "Reinforcement", "Masonry", "Plaster", "Waterproofing", "Electrical", "Plumbing", "HVAC", "Fire fighting", "Painting", "Flooring", "Fabrication", "Labour supply"], v => s.Trade = v)),
                F("Contact person", Ui.Entry(s.ContactPerson, "", changed: v => s.ContactPerson = v)),
                F("Phone", Ui.Entry(s.Phone, "", Keyboard.Telephone, v => s.Phone = v)),
                F("Active on site", active)));
    }

    public static Page DepartmentForm(Department d) => new FormPage<Department>(string.IsNullOrEmpty(d.Name) ? "New department" : d.Name, d,
        x => string.IsNullOrWhiteSpace(x.Name) ? "Enter the department name." : null, null,
        F("Department *", new Combo(d.Name, Lists.DefaultDepartments, v => d.Name = v)),
        F("Head / in-charge", Ui.Entry(d.Head, "", changed: v => d.Head = v)));

    public static Page ActivityForm(Activity a)
    {
        var subPicker = new Picker { Title = "Agency" };
        _ = FillSubcontractorsAsync(subPicker, a.SubcontractorId, id => a.SubcontractorId = id);
        var deptPicker = new Picker { Title = "Department" };
        _ = FillAsync<Department>(deptPicker, a.DepartmentId, id => a.DepartmentId = id, "(none)");
        var wbsPicker = new Picker { Title = "WBS" };
        _ = FillWbsAsync(wbsPicker, a.WbsId, id => a.WbsId = id);
        var resources = new ActivityResourceEditor(a);
        return new FormPage<Activity>(string.IsNullOrEmpty(a.Name) ? "New activity" : a.Name, a,
            x => string.IsNullOrWhiteSpace(x.Name) ? "Enter the activity name."
                : x.PlannedStart != null && x.PlannedFinish != null && x.PlannedFinish < x.PlannedStart ? "Planned finish is before planned start." : null,
            _ => resources.SaveAsync(),
            Ui.SectionHeader("Activity"),
            Ui.Columns(2,
                F("Code / BOQ item", Ui.Entry(a.Code, "e.g. C-101", changed: v => a.Code = v)),
                F("Activity *", Ui.Entry(a.Name, "e.g. Raft concrete", changed: v => a.Name = v)),
                F("WBS (plant › unit › area)", wbsPicker),
                F("Location", Ui.Entry(a.Location, "Block / floor / grid", changed: v => a.Location = v)),
                F("Method", new Combo(a.Method, ["Conventional", "Pump placement", "Crane & bucket", "Slip form", "Jump form", "Precast", "Pre-engineered", "Mechanised", "Manual"], v => a.Method = v, "Construction method")),
                F("Agency (subcontractor)", subPicker),
                F("Department", deptPicker),
                F("Weightage (for overall %)", Ui.Number(a.Weightage, v => a.Weightage = v, "auto"))),
            Ui.SectionHeader("Quantity & schedule"),
            Ui.Columns(2,
                F("Unit", new Combo(a.Unit, Lists.Units, v => a.Unit = v)),
                F("Planned quantity (100 %)", Ui.Number(a.PlannedQty, v => a.PlannedQty = v)),
                F("Planned output per day", Ui.Number(a.PlannedRatePerDay, v => a.PlannedRatePerDay = v, "optional")),
                F("Planned start", Ui.OptionalDate(a.PlannedStart, v => a.PlannedStart = v)),
                F("Planned finish", Ui.OptionalDate(a.PlannedFinish, v => a.PlannedFinish = v))),
            Ui.SectionHeader("Progress (calculated automatically)"),
            ProgressPanel(a),
            Ui.SectionHeader("Assigned resources (planned)"), resources,
            F("Remarks", Ui.Memo(a.Remarks, "", v => a.Remarks = v, 50)));
    }

    public static async Task FillAsync<T>(Picker picker, string selectedId, Action<string> changed, string noneText) where T : SyncEntity, new()
    {
        var items = (await AppServices.Db.ListForProjectAsync<T>(AppServices.ProjectId)).OrderBy(x => x.ToString()).Cast<object>().ToList();
        var none = new PickItem("", noneText);
        var list = new List<PickItem> { none };
        list.AddRange(items.Select(i => new PickItem(((SyncEntity)i).Id, i.ToString() ?? "")));
        picker.ItemsSource = list;
        picker.SelectedItem = list.FirstOrDefault(x => x.Id == selectedId) ?? none;
        picker.SelectedIndexChanged += (_, _) => { if (picker.SelectedItem is PickItem p) changed(p.Id); };
    }

    public static async Task FillWbsAsync(Picker picker, string selectedId, Action<string> changed)
    {
        var all = await AppServices.Db.ListForProjectAsync<WbsNode>(AppServices.ProjectId);
        var map = all.ToDictionary(w => w.Id);
        var list = new List<PickItem> { new("", "(no WBS)") };
        list.AddRange(all.Select(w => new PickItem(w.Id, ProgressService.WbsPath(w.Id, map))).OrderBy(p => p.Name));
        picker.ItemsSource = list;
        picker.SelectedItem = list.FirstOrDefault(x => x.Id == selectedId) ?? list[0];
        picker.SelectedIndexChanged += (_, _) => { if (picker.SelectedItem is PickItem p) changed(p.Id); };
    }

    public static Page ResourceForm(SiteResource r)
    {
        var subPicker = new Picker { Title = "Agency" };
        _ = FillSubcontractorsAsync(subPicker, r.SubcontractorId, id => r.SubcontractorId = id);
        var deptPicker = new Picker { Title = "Department" };
        _ = FillAsync<Department>(deptPicker, r.DepartmentId, id => r.DepartmentId = id, "(none)");
        return new FormPage<SiteResource>(string.IsNullOrEmpty(r.Name) ? "New resource" : r.Name, r,
            x => string.IsNullOrWhiteSpace(x.Name) ? "Enter the resource name." : x.Rate < 0 ? "Rate cannot be negative." : null, null,
            Ui.Columns(2,
                F("Type", Ui.EnumPicker(r.Type, v => r.Type = v)),
                F("Code", Ui.Entry(r.Code, "e.g. L-MAS", changed: v => r.Code = v)),
                F("Name *", new Combo(r.Name, Lists.Trades.Concat(["JCB", "Tower crane", "Concrete pump", "Transit mixer", "RMC M25", "TMT steel", "Cement", "Scaffolding hire", "Testing charges"]), v => r.Name = v)),
                F("Unit", new Combo(r.Unit, Lists.Units, v => r.Unit = v)),
                F("Rate", DecimalEntry(r.Rate, v => r.Rate = v)),
                F("Rate basis", Ui.EnumPicker(r.Basis, v => r.Basis = v)),
                F("Supplied by (agency)", subPicker),
                F("Department (own staff)", deptPicker)),
            Ui.Caption("Manpower & machines: usually per day or per hour. Materials: per unit. Non-material (hire, testing, transport): per unit or lump sum."),
            F("Remarks", Ui.Memo(r.Remarks, "Rate source, validity…", v => r.Remarks = v, 50)));
    }

    public static Entry DecimalEntry(decimal value, Action<decimal> changed)
    {
        var e = new Entry { Text = value == 0 ? "" : value.ToString("0.##"), Placeholder = "0.00", Keyboard = Keyboard.Numeric, HorizontalTextAlignment = TextAlignment.End };
        e.TextChanged += (_, a) => changed(decimal.TryParse(a.NewTextValue, out var v) ? v : 0);
        return e;
    }

    public static Page WbsForm(WbsNode w, List<WbsNode> all)
    {
        var parentPicker = new Picker { Title = "Parent" };
        var map = all.ToDictionary(x => x.Id);
        var opts = new List<PickItem> { new("", "(top level)") };
        opts.AddRange(all.Where(x => x.Id != w.Id && !ProgressService.WbsPath(x.Id, map).Contains(w.ToString() + " ›") )
            .Select(x => new PickItem(x.Id, ProgressService.WbsPath(x.Id, map))).OrderBy(p => p.Name));
        parentPicker.ItemsSource = opts;
        parentPicker.SelectedItem = opts.FirstOrDefault(o => o.Id == w.ParentId) ?? opts[0];
        parentPicker.SelectedIndexChanged += (_, _) => { if (parentPicker.SelectedItem is PickItem p) w.ParentId = p.Id; };
        return new FormPage<WbsNode>(string.IsNullOrEmpty(w.Name) ? "New WBS element" : w.Name, w,
            x => string.IsNullOrWhiteSpace(x.Name) ? "Enter a name." : x.ParentId == x.Id ? "An element cannot be its own parent." : null, null,
            Ui.Columns(2,
                F("Level", Ui.EnumPicker(w.Level, v => w.Level = v)),
                F("Parent", parentPicker),
                F("Code", Ui.Entry(w.Code, "e.g. P1-U2", changed: v => w.Code = v)),
                F("Name *", Ui.Entry(w.Name, "e.g. Boiler plant / Unit 2 / Pump house", changed: v => w.Name = v))),
            Ui.Caption("Typical structure: Project › Plant › Unit › Area › System › Work package. Activities are linked to any level."));
    }

    public static async Task FillSubcontractorsAsync(Picker picker, string selectedId, Action<string> changed)
    {
        var subs = (await AppServices.Db.ListForProjectAsync<Subcontractor>(AppServices.ProjectId)).OrderBy(s => s.Name).ToList();
        subs.Insert(0, new Subcontractor { Id = "", Name = "(none / own)" });
        picker.ItemsSource = subs;
        picker.SelectedItem = subs.FirstOrDefault(s => s.Id == selectedId) ?? subs[0];
        picker.SelectedIndexChanged += (_, _) => { if (picker.SelectedItem is Subcontractor s) changed(s.Id); };
    }

    public static Page HindranceForm(Hindrance h)
    {
        var photos = new MediaStrip(MediaOwner.Hindrance, () => h.Id);
        return new FormPage<Hindrance>(h.SerialNo == 0 ? "New hindrance" : $"Hindrance H-{h.SerialNo:000}", h,
            x => string.IsNullOrWhiteSpace(x.Description) ? "Describe the hindrance." : null,
            async x => { if (x.SerialNo == 0) { x.SerialNo = await AppServices.Db.NextSerialAsync<Hindrance>(x.ProjectId); await AppServices.Db.SaveAsync(x); } },
            Ui.Columns(2,
                F("From date", Ui.Date(h.FromDate, v => h.FromDate = v)),
                F("Resolved on", Ui.OptionalDate(h.ToDate, v => h.ToDate = v)),
                F("Category", new Combo(h.Category, Lists.HindranceCategories, v => h.Category = v)),
                F("Responsible party", new Combo(h.ResponsibleParty, Lists.ResponsibleParties, v => h.ResponsibleParty = v)),
                F("Affected activity", Ui.Entry(h.AffectedActivity, "", changed: v => h.AffectedActivity = v)),
                F("Hours lost", Ui.Number(h.HoursLost, v => h.HoursLost = v)),
                F("Status", Ui.EnumPicker(h.Status, v => h.Status = v))),
            F("Description *", Ui.Memo(h.Description, "What is stopping the work?", v => h.Description = v)),
            F("Action taken / required", Ui.Memo(h.ActionTaken, "Letters, follow-up, decisions…", v => h.ActionTaken = v, 60)),
            Ui.SectionHeader("Photos & videos (evidence)"), photos);
    }

    public static Page RequirementForm(Requirement r) => new FormPage<Requirement>(r.SerialNo == 0 ? "New requirement" : $"Requirement R-{r.SerialNo:000}", r,
        x => string.IsNullOrWhiteSpace(x.Item) ? "Enter what is required." : null,
        async x => { if (x.SerialNo == 0) { x.SerialNo = await AppServices.Db.NextSerialAsync<Requirement>(x.ProjectId); await AppServices.Db.SaveAsync(x); } },
        F("Item / description *", Ui.Memo(r.Item, "e.g. TMT 12 mm Fe500D, 8 MT", v => r.Item = v, 50)),
        Ui.Columns(2,
            F("Category", new Combo(r.Category, Lists.RequirementCategories, v => r.Category = v)),
            F("Raised on", Ui.Date(r.RaisedDate, v => r.RaisedDate = v)),
            F("Quantity", Ui.Number(r.Quantity, v => r.Quantity = v)),
            F("Unit", new Combo(r.Unit, Lists.Units, v => r.Unit = v)),
            F("Required by", Ui.OptionalDate(r.RequiredBy, v => r.RequiredBy = v)),
            F("Priority", Ui.EnumPicker(r.Priority, v => r.Priority = v)),
            F("Raised by", Ui.Entry(r.RaisedBy, "", changed: v => r.RaisedBy = v)),
            F("Status", Ui.EnumPicker(r.Status, v => r.Status = v))),
        F("Remarks", Ui.Memo(r.Remarks, "PO no., supplier, follow-up…", v => r.Remarks = v, 50)));
}

/// <summary>Picker item with an id and a display name.</summary>
public record PickItem(string Id, string Name)
{
    public override string ToString() => Name;
}
