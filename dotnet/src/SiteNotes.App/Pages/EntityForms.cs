using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core.Models;

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
        return new FormPage<Activity>(string.IsNullOrEmpty(a.Name) ? "New activity" : a.Name, a,
            x => string.IsNullOrWhiteSpace(x.Name) ? "Enter the activity name." : null, null,
            Ui.Columns(2,
                F("Code / BOQ item", Ui.Entry(a.Code, "e.g. 4.2.1", changed: v => a.Code = v)),
                F("Activity *", Ui.Entry(a.Name, "e.g. RCC slab L5", changed: v => a.Name = v)),
                F("Location", Ui.Entry(a.Location, "Block / floor / grid", changed: v => a.Location = v)),
                F("Unit", new Combo(a.Unit, Lists.Units, v => a.Unit = v)),
                F("Planned quantity", Ui.Number(a.PlannedQty, v => a.PlannedQty = v)),
                F("Agency", subPicker),
                F("Planned start", Ui.OptionalDate(a.PlannedStart, v => a.PlannedStart = v)),
                F("Planned finish", Ui.OptionalDate(a.PlannedFinish, v => a.PlannedFinish = v))));
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
