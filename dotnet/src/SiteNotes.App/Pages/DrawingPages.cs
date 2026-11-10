using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.App.Pages;

/// <summary>Drawing register: each drawing with quantity, allocated %, completed % and work status.</summary>
public class DrawingListPage : ListPage<Drawing>
{
    private DrawingRegister _reg = new();

    public DrawingListPage() : base("Drawing register") { }

    protected override string[] SortOptions => ["Drawing no.", "Least complete", "Most complete"];
    protected override string[] FilterOptions => ["Not fully allocated", "In progress", "Completed", "Needs attention"];

    protected override bool Filter(Drawing d, string f)
    {
        var p = _reg.Drawings.FirstOrDefault(x => x.Drawing.Id == d.Id);
        if (p == null) return false;
        return f switch
        {
            "Not fully allocated" => p.Items.Any(i => i.Unallocated > 1e-9),
            "In progress" => p.State == DrawingState.InProgress,
            "Completed" => p.State == DrawingState.Completed,
            _ => p.Items.Any(i => i.OverAllocated > 0 || i.CompletedWithoutAllocation > 0 || i.Excess > 0),
        };
    }

    protected override IEnumerable<Drawing> Sort(IEnumerable<Drawing> items, string sort)
    {
        double Pct(Drawing d) => _reg.Drawings.FirstOrDefault(x => x.Drawing.Id == d.Id)?.Percent ?? 0;
        return sort switch
        {
            "Least complete" => items.OrderBy(Pct),
            "Most complete" => items.OrderByDescending(Pct),
            _ => items.OrderBy(d => d.Number),
        };
    }

    protected override Drawing Create() => new() { ProjectId = AppServices.ProjectId, IssueDate = DateTime.Today };
    protected override Page EditPage(Drawing d) => new DrawingFormPage(d);

    protected override ListRow ToRow(Drawing d)
    {
        var p = _reg.Drawings.FirstOrDefault(x => x.Drawing.Id == d.Id);
        return new ListRow(d, $"{d.Number} {d.Revision} · {d.Title}",
            p == null ? "" : $"{p.TotalsText} · allocated {p.AllocatedPercent:0.#} % · completed {p.Percent:0.#} % · {p.Agencies} agencies",
            p == null ? "" : DrawingReports.StateText(p.State).ToUpperInvariant(),
            p?.State == DrawingState.Completed ? Ui.Success : p?.State == DrawingState.InProgress ? Ui.Primary : Ui.Accent,
            p?.WbsPath ?? "");
    }

    protected override bool CanExport => true;

    protected override async void OnAppearing()
    {
        _reg = await Task.Run(() => new DrawingService(AppServices.Db).ComputeAsync(AppServices.ProjectId));
        base.OnAppearing();
    }

    protected override async Task ExportPdfAsync(List<Drawing> items, string filterText)
    {
        var opt = await ReportProfiles.CurrentAsync();
        var path = await FileExport.CreateAsync($"Drawing_register_{DateTime.Now:yyyyMMdd_HHmm}.pdf", s => DrawingReports.Pdf(s, AppServices.CurrentProject, _reg, filterText, opt));
        await FileExport.OfferAsync(this, path);
    }

    protected override async Task ExportExcelAsync(List<Drawing> items, string filterText)
    {
        var path = await FileExport.CreateAsync($"Drawing_register_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", s => DrawingReports.Excel(s, AppServices.CurrentProject, _reg, filterText));
        await FileExport.OfferAsync(this, path);
    }
}

/// <summary>One drawing: its details, its items (BOQ / equipment / parts) and each item's allocations to agencies.</summary>
public class DrawingFormPage : ContentPage
{
    private readonly Drawing _d;
    private List<DrawingItem> _items = [];
    private List<DrawingAllocation> _allocs = [];
    private List<DrawingItem> _origItems = [];
    private List<DrawingAllocation> _origAllocs = [];
    private List<Subcontractor> _subs = [];
    private DrawingRegister _reg = new();
    private readonly VerticalStackLayout _body = new() { Spacing = 10, Padding = 12 };

    public DrawingFormPage(Drawing d)
    {
        _d = d;
        Title = string.IsNullOrEmpty(d.Number) ? "New drawing" : d.Number;
        this.Page();
        ToolbarItems.Add(new ToolbarItem("💾 Save", null, () => _ = Ui.Guard(SaveAsync)));
        ToolbarItems.Add(new ToolbarItem("🗑 Delete", null, () => _ = Ui.Guard(DeleteAsync)) { Order = ToolbarItemOrder.Secondary });
        Content = new ScrollView { Content = _body };
    }

    private bool _loaded;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;
        var pid = AppServices.ProjectId;
        _subs = (await AppServices.Db.ListForProjectAsync<Subcontractor>(pid)).OrderBy(s => s.Name).ToList();
        _origItems = await AppServices.Db.ListForProjectAsync<DrawingItem>(pid, i => i.DrawingId == _d.Id);
        _origAllocs = await AppServices.Db.ListForProjectAsync<DrawingAllocation>(pid, a => a.DrawingId == _d.Id);
        _items = [.. _origItems.OrderBy(i => i.SortOrder)];
        _allocs = [.. _origAllocs];
        _reg = await Task.Run(() => new DrawingService(AppServices.Db).ComputeAsync(pid));
        var wbs = new Picker { Title = "WBS" };
        await EntityForms.FillWbsAsync(wbs, _d.WbsId, id => _d.WbsId = id);
        var p = _reg.Drawings.FirstOrDefault(x => x.Drawing.Id == _d.Id);

        _body.Children.Add(Ui.Columns(2,
            Ui.Field("Drawing no. *", Ui.Entry(_d.Number, "e.g. WTP-ST-1001", changed: v => _d.Number = v)),
            Ui.Field("Revision", Ui.Entry(_d.Revision, "R0", changed: v => _d.Revision = v)),
            Ui.Field("Title", Ui.Entry(_d.Title, "", changed: v => _d.Title = v)),
            Ui.Field("Discipline", new Combo(_d.Discipline, ["Civil", "Structural", "Mechanical", "Electrical", "Piping", "Instrumentation", "Architectural"], v => _d.Discipline = v)),
            Ui.Field("Status", Ui.EnumPicker(_d.Status, v => _d.Status = v)),
            Ui.Field("WBS (plant / unit / area)", wbs),
            Ui.Field("Issued on", Ui.OptionalDate(_d.IssueDate, v => _d.IssueDate = v))));
        if (p != null)
            _body.Children.Add(Ui.Columns(4,
                Ui.Stat("Quantity", p.TotalsText),
                Ui.Stat("Allocated", $"{p.AllocatedPercent:0.#} %", Ui.Accent),
                Ui.Stat("Completed", $"{p.Percent:0.#} %", Ui.Success),
                Ui.Stat("Status", DrawingReports.StateText(p.State), Ui.Primary)));
        _body.Children.Add(Ui.SectionHeader("Items – BOQ items, equipment and parts on this drawing"));
        _body.Children.Add(new RowList<DrawingItem>(_items, ItemRow, () => { }, "No items yet.",
            ("＋ BOQ item", RowList<DrawingItem>.Now(() => new DrawingItem { ProjectId = AppServices.ProjectId, DrawingId = _d.Id, Unit = "MT", SortOrder = _items.Count })),
            ("＋ Equipment", RowList<DrawingItem>.Now(() => new DrawingItem { ProjectId = AppServices.ProjectId, DrawingId = _d.Id, Kind = DrawingItemKind.Equipment, Unit = "Nos", SortOrder = _items.Count }))));
        _body.Children.Add(Ui.Caption("Completed quantities come from the daily reports (＋ Drawing work). One item can be split between several agencies."));
        _body.Children.Add(Ui.Button("💾 Save drawing", SaveAsync));
    }

    private View ItemRow(DrawingItem it)
    {
        var p = _reg.AllItems.FirstOrDefault(x => x.Item.Id == it.Id);
        var mine = _allocs.Where(a => a.DrawingItemId == it.Id).ToList();
        var status = Ui.Label(p == null ? "Not saved yet" :
            $"Allocated {PdfReports.N(p.Allocated)} · unallocated {PdfReports.N(p.Unallocated)} · completed {PdfReports.N(p.Completed)} · balance {PdfReports.N(p.Balance)} {it.Unit}" +
            (p.OverAllocated > 0 ? $" · ⚠ over-allocated {PdfReports.N(p.OverAllocated)}" : "") +
            (p.CompletedWithoutAllocation > 0 ? $" · ⚠ {PdfReports.N(p.CompletedWithoutAllocation)} done without allocation" : "") +
            (p.Excess > 0 ? $" · ⚠ {PdfReports.N(p.Excess)} beyond drawing qty" : ""), 12, true, p is { Excess: > 0 } or { OverAllocated: > 0 } ? Ui.Danger : Ui.Primary);
        var allocList = new VerticalStackLayout { Spacing = 4 };
        void RenderAllocs()
        {
            allocList.Children.Clear();
            foreach (var a in _allocs.Where(a => a.DrawingItemId == it.Id).ToList())
            {
                var ap = p?.Agencies.FirstOrDefault(x => x.SubcontractorId == a.SubcontractorId);
                var picker = new Picker { ItemsSource = _subs, SelectedItem = _subs.FirstOrDefault(s => s.Id == a.SubcontractorId), Title = "Agency" };
                picker.SelectedIndexChanged += (_, _) => { if (picker.SelectedItem is Subcontractor s) { a.SubcontractorId = s.Id; a.SubcontractorName = s.Name; } };
                var del = Ui.Tool("✕", () => { _allocs.Remove(a); RenderAllocs(); }, "Remove allocation", 34);
                var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(new GridLength(110)), new(GridLength.Auto)], ColumnSpacing = 6 };
                g.Add(picker, 0);
                g.Add(Ui.Number(a.AllocatedQty, v => a.AllocatedQty = v, "qty"), 1);
                g.Add(del, 2);
                allocList.Children.Add(g);
                if (ap != null) allocList.Children.Add(Ui.Caption($"   done {PdfReports.N(ap.Done)} of {PdfReports.N(ap.Allocated)} ({ap.Percent:0.#} %)" + (ap.Excess > 0 ? $" · ⚠ {PdfReports.N(ap.Excess)} beyond allocation" : "")));
            }
            var unalloc = it.Qty - _allocs.Where(a => a.DrawingItemId == it.Id).Sum(a => a.AllocatedQty);
            allocList.Children.Add(Ui.Button(unalloc > 0 ? $"＋ Allocate {PdfReports.N(unalloc)} {it.Unit} to an agency" : "＋ Allocate to an agency", async () =>
            {
                if (_subs.Count == 0) { await DisplayAlertAsync("No agencies", "Add subcontractors first.", "OK"); return; }
                var choice = await DisplayActionSheetAsync("Allocate to", "Cancel", null, _subs.Select(s => s.Name).ToArray());
                var sub = _subs.FirstOrDefault(s => s.Name == choice);
                if (sub == null) return;
                _allocs.Add(new DrawingAllocation
                {
                    ProjectId = AppServices.ProjectId, DrawingId = _d.Id, DrawingItemId = it.Id, SubcontractorId = sub.Id, SubcontractorName = sub.Name,
                    AllocatedQty = Math.Max(0, it.Qty - _allocs.Where(a => a.DrawingItemId == it.Id).Sum(a => a.AllocatedQty)),
                });
                RenderAllocs();
            }, false));
        }
        RenderAllocs();
        return new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                Ui.Columns(4,
                    Ui.Field("Item / BOQ no.", Ui.Entry(it.ItemNo, "e.g. 1457", changed: v => it.ItemNo = v)),
                    Ui.Field("Description", Ui.Entry(it.Description, "", changed: v => it.Description = v)),
                    Ui.Field("Type", Ui.EnumPicker(it.Kind, v => it.Kind = v)),
                    Ui.Field("Unit", new Combo(it.Unit, Lists.Units, v => it.Unit = v)),
                    Ui.Field("Drawing qty", Ui.Number(it.Qty, v => it.Qty = v))),
                status,
                Ui.Caption("Allocated to agencies:"),
                allocList,
            }
        };
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_d.Number)) { await DisplayAlertAsync("Drawing", "Enter the drawing number.", "OK"); return; }
        _d.ProjectId = AppServices.ProjectId;
        await AppServices.Db.SaveAsync(_d);
        for (var i = 0; i < _items.Count; i++) { _items[i].DrawingId = _d.Id; _items[i].SortOrder = i; }
        await AppServices.Db.SaveAllAsync(_items);
        foreach (var a in _allocs) a.DrawingId = _d.Id;
        await AppServices.Db.SaveAllAsync(_allocs.Where(a => _items.Any(i => i.Id == a.DrawingItemId)));
        foreach (var gone in _origItems.Where(o => !_items.Contains(o))) await AppServices.Db.DeleteAsync(gone);
        foreach (var gone in _origAllocs.Where(o => !_allocs.Contains(o) || _items.All(i => i.Id != o.DrawingItemId))) await AppServices.Db.DeleteAsync(gone);
        await Navigation.PopAsync();
    }

    private async Task DeleteAsync()
    {
        if (!await DisplayAlertAsync("Delete drawing?", "Its items and allocations are removed on all devices. Reported work stays in the daily reports.", "Delete", "Cancel")) return;
        foreach (var a in _origAllocs) await AppServices.Db.DeleteAsync(a);
        foreach (var i in _origItems) await AppServices.Db.DeleteAsync(i);
        await AppServices.Db.DeleteAsync(_d);
        await Navigation.PopAsync();
    }
}
