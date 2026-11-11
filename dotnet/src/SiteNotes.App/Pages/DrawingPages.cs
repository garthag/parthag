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

    public DrawingListPage() : base("Drawing register")
    {
        // items (BOQ) and allocations of the drawings shown, as their own edit-in-Excel sheets
        ToolbarItems.Add(new ToolbarItem("Export BOQ items to edit", null, () => _ = Ui.Guard(async () =>
        {
            var ids = ShownIds();
            await ExchangeFlow.ExportAsync(this, await ExchangeFlow.Sheets.DrawingItemsAsync(AppServices.ProjectId),
                (await AppServices.Db.ListForProjectAsync<DrawingItem>(AppServices.ProjectId)).Where(i => ids.ContainsKey(i.DrawingId)).OrderBy(i => ids[i.DrawingId]).ThenBy(i => i.SortOrder));
        })) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Import BOQ items…", null, () => _ = Ui.Guard(async () =>
        {
            if (await ExchangeFlow.ImportAsync(this, await ExchangeFlow.Sheets.DrawingItemsAsync(AppServices.ProjectId))) await RecalcAsync();
        })) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Export allocations to edit", null, () => _ = Ui.Guard(async () =>
        {
            var ids = ShownIds();
            await ExchangeFlow.ExportAsync(this, await ExchangeFlow.Sheets.AllocationsAsync(AppServices.ProjectId),
                (await AppServices.Db.ListForProjectAsync<DrawingAllocation>(AppServices.ProjectId)).Where(a => ids.ContainsKey(a.DrawingId)).OrderBy(a => ids[a.DrawingId]).ThenBy(a => a.SubcontractorName));
        })) { Order = ToolbarItemOrder.Secondary });
        ToolbarItems.Add(new ToolbarItem("Import allocations…", null, () => _ = Ui.Guard(async () =>
        {
            if (await ExchangeFlow.ImportAsync(this, await ExchangeFlow.Sheets.AllocationsAsync(AppServices.ProjectId))) await RecalcAsync();
        })) { Order = ToolbarItemOrder.Secondary });
    }

    private Dictionary<string, int> ShownIds() => VisibleItems().Select((d, i) => (d.Id, i)).ToDictionary(x => x.Id, x => x.i);

    private async Task RecalcAsync()
    {
        _reg = await Task.Run(() => new DrawingService(AppServices.Db).ComputeAsync(AppServices.ProjectId));
        await ReloadAsync();
    }

    protected override Task<ExchangeSheet<Drawing>>? ExchangeSheet() => ExchangeFlow.Sheets.DrawingsAsync(AppServices.ProjectId);

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
            p == null ? "" : $"{p.TotalsText} · allocated {p.AllocatedPercent:0.#} % · completed {p.Percent:0.#} % · {p.Agencies} agencies" +
                             (p.Value > 0 ? $"\nBOQ value {PdfReports.M(p.Value)} · unallocated {PdfReports.M(p.UnallocatedValue)} · payable {PdfReports.M(p.PayableValue)}" : ""),
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
        if (p is { Value: > 0 } or { AllocatedValue: > 0 })
            _body.Children.Add(Ui.Columns(5,
                Ui.Stat("BOQ value", PdfReports.M(p.Value)),
                Ui.Stat("Completed value", PdfReports.M(p.CompletedValue), Ui.Success),
                Ui.Stat("Unallocated value", PdfReports.M(p.UnallocatedValue), Ui.Danger),
                Ui.Stat("Allocated (work orders)", PdfReports.M(p.AllocatedValue), Ui.Accent),
                Ui.Stat("Payable for work done", PdfReports.M(p.PayableValue), Ui.Primary)));

        _body.Children.Add(Ui.SectionHeader("Drawing file (PDF / image / DWG)"));
        var fileLabel = Ui.Caption("");
        async Task ShowFileAsync()
        {
            var m = _d.FileMediaId == "" ? null : await AppServices.Db.GetAsync<MediaItem>(_d.FileMediaId);
            fileLabel.Text = m == null ? "No file attached yet." : $"Attached: {(m.Caption == "" ? m.FileName : m.Caption)} · {m.CapturedAtUtc.ToLocalTime():dd-MMM-yyyy HH:mm}";
        }
        await ShowFileAsync();
        _body.Children.Add(fileLabel);
        _body.Children.Add(new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Children =
            {
                Gap(Ui.Button("📎 Attach drawing file", async () =>
                {
                    var f = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Drawing file" });
                    if (f == null) return;
                    var m = await MediaService.ImportAsync(f, MediaOwner.Drawing, _d.Id, MediaKind.Document);
                    if (m == null) return;
                    m.Caption = f.FileName;
                    await AppServices.Db.SaveAsync(m);
                    _d.FileMediaId = m.Id;
                    if (!string.IsNullOrWhiteSpace(_d.Number)) { _d.ProjectId = AppServices.ProjectId; await AppServices.Db.SaveAsync(_d); }
                    await ShowFileAsync();
                }, false)),
                Gap(Ui.Button("📂 Open drawing", async () =>
                {
                    var m = _d.FileMediaId == "" ? null : await AppServices.Db.GetAsync<MediaItem>(_d.FileMediaId);
                    if (m == null) { await DisplayAlertAsync("Drawing file", "No file attached yet.", "OK"); return; }
                    if (!File.Exists(AppServices.Db.MediaPath(m.FileName))) { await DisplayAlertAsync("Drawing file", "The file is not on this device yet – it arrives with the next sync.", "OK"); return; }
                    await MediaService.OpenAsync(m);
                }, false)),
            }
        });

        _body.Children.Add(Ui.SectionHeader("BOQ & activities"));
        _body.Children.Add(new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Children =
            {
                Gap(Ui.Button("⬆ Import BOQ (Excel / CSV)", ImportBoqAsync, false)),
                Gap(Ui.Button("📄 BOQ template", async () => await FileExport.OfferAsync(this, await FileExport.CreateAsync("BOQ_template.xlsx", BoqService.Template)), false)),
                Gap(Ui.Button("📊 Create activities from items", CreateActivitiesAsync, false)),
            }
        });
        _body.Children.Add(Ui.Caption("BOQ columns: Item no · Description · Type · Unit · Qty · Rate. Activities get the drawing no. / item no. as code; progress entered with ＋ Drawing work counts for the item, the agency and the activity."));

        _body.Children.Add(Ui.SectionHeader("Items – BOQ items, equipment and parts on this drawing"));
        _body.Children.Add(new RowList<DrawingItem>(_items, ItemRow, () => { }, "No items yet.",
            ("＋ BOQ item", RowList<DrawingItem>.Now(() => new DrawingItem { ProjectId = AppServices.ProjectId, DrawingId = _d.Id, Unit = "MT", SortOrder = _items.Count })),
            ("＋ Equipment", RowList<DrawingItem>.Now(() => new DrawingItem { ProjectId = AppServices.ProjectId, DrawingId = _d.Id, Kind = DrawingItemKind.Equipment, Unit = "Nos", SortOrder = _items.Count }))));
        _body.Children.Add(Ui.Caption("Completed quantities come from the daily reports (＋ Drawing work). One item can be split between several agencies."));
        _body.Children.Add(Ui.Button("💾 Save drawing", SaveAsync));
    }

    private static View Gap(View v) { v.Margin = new Thickness(0, 0, 8, 6); return v; }

    /// <summary>Saves the drawing with its items and allocations without leaving the page (needed before import / create activities).</summary>
    private async Task<bool> PersistAsync()
    {
        if (string.IsNullOrWhiteSpace(_d.Number)) { await DisplayAlertAsync("Drawing", "Enter the drawing number first.", "OK"); return false; }
        _d.ProjectId = AppServices.ProjectId;
        await AppServices.Db.SaveAsync(_d);
        for (var i = 0; i < _items.Count; i++) { _items[i].DrawingId = _d.Id; _items[i].SortOrder = i; }
        await AppServices.Db.SaveAllAsync(_items);
        foreach (var a in _allocs) a.DrawingId = _d.Id;
        await AppServices.Db.SaveAllAsync(_allocs.Where(a => _items.Any(i => i.Id == a.DrawingItemId)));
        foreach (var gone in _origItems.Where(o => !_items.Contains(o))) await AppServices.Db.DeleteAsync(gone);
        foreach (var gone in _origAllocs.Where(o => !_allocs.Contains(o) || _items.All(i => i.Id != o.DrawingItemId))) await AppServices.Db.DeleteAsync(gone);
        return true;
    }

    private async Task ReopenAsync()
    {
        var nav = Navigation;
        nav.InsertPageBefore(new DrawingFormPage(_d), this);
        await nav.PopAsync(false);
    }

    private async Task ImportBoqAsync()
    {
        if (!await PersistAsync()) return;
        var file = await ExchangeFlow.PickSheetAsync($"BOQ for {_d.Number}");
        if (file == null) return;
        var ms = new MemoryStream();
        await using (var src = await file.OpenReadAsync()) await src.CopyToAsync(ms);
        ms.Position = 0;
        var result = await Task.Run(() => BoqService.Import(ms, file.FileName));
        var warn = result.Warnings.Count == 0 ? "" : "\n\n" + string.Join("\n", result.Warnings.Take(6));
        if (result.Items.Count == 0) { await DisplayAlertAsync("BOQ", "No items found." + warn, "OK"); return; }
        if (!await DisplayAlertAsync("Import BOQ", $"Import {result.Items.Count} item(s) into {_d.Number}? Items with the same item no. are updated." + warn, "Import", "Cancel")) return;
        var (added, updated) = await new BoqService(AppServices.Db).SaveImportAsync(_d, result.Items);
        await Ui.Toast($"BOQ imported: {added} added, {updated} updated");
        await ReopenAsync();
    }

    private async Task CreateActivitiesAsync()
    {
        if (!await PersistAsync()) return;
        if (!await DisplayAlertAsync("Create activities", $"Create a planned activity for every item of {_d.Number} that has none?", "Create", "Cancel")) return;
        var n = await new BoqService(AppServices.Db).CreateActivitiesAsync(_d, _d.IssueDate, _d.IssueDate?.AddDays(60));
        await Ui.Toast(n == 0 ? "Every item already has an activity" : $"{n} activities created – see Activities & progress");
        await ReopenAsync();
    }

    private View ItemRow(DrawingItem it)
    {
        var p = _reg.AllItems.FirstOrDefault(x => x.Item.Id == it.Id);
        var mine = _allocs.Where(a => a.DrawingItemId == it.Id).ToList();
        var status = Ui.Label(p == null ? "Not saved yet" :
            $"Allocated {PdfReports.N(p.Allocated)} · unallocated {PdfReports.N(p.Unallocated)} · completed {PdfReports.N(p.Completed)} · balance {PdfReports.N(p.Balance)} {it.Unit}" +
            (p.OverAllocated > 0 ? $" · ⚠ over-allocated {PdfReports.N(p.OverAllocated)}" : "") +
            (p.CompletedWithoutAllocation > 0 ? $" · ⚠ {PdfReports.N(p.CompletedWithoutAllocation)} done without allocation" : "") +
            (p.Excess > 0 ? $" · ⚠ {PdfReports.N(p.Excess)} beyond drawing qty" : "") +
            (p.Value > 0 || p.AllocatedValue > 0 ? $"\nValue {PdfReports.M(p.Value)} · unallocated {PdfReports.M(p.UnallocatedValue)} · completed {PdfReports.M(p.CompletedValue)} · allocated (WO) {PdfReports.M(p.AllocatedValue)} · payable {PdfReports.M(p.PayableValue)}" : "") +
            (it.ActivityId != "" ? " · linked to an activity" : ""), 12, true, p is { Excess: > 0 } or { OverAllocated: > 0 } ? Ui.Danger : Ui.Primary);
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
                var g = new Grid { ColumnDefinitions = [new(GridLength.Star), new(new GridLength(90)), new(GridLength.Auto)], ColumnSpacing = 6 };
                g.Add(picker, 0);
                g.Add(Ui.Number(a.AllocatedQty, v => a.AllocatedQty = v, "qty"), 1);
                g.Add(del, 2);
                allocList.Children.Add(g);
                allocList.Children.Add(Ui.Columns(2,
                    Ui.Field("Work order no.", Ui.Entry(a.WorkOrderNo, "e.g. WO-114", changed: v => a.WorkOrderNo = v)),
                    Ui.Field($"Unit price payable / {it.Unit}", Ui.Number((double)a.Rate, v => a.Rate = (decimal)v, "0.00"))));
                if (ap != null) allocList.Children.Add(Ui.Caption($"   done {PdfReports.N(ap.Done)} of {PdfReports.N(ap.Allocated)} ({ap.Percent:0.#} %)" + (ap.Excess > 0 ? $" · ⚠ {PdfReports.N(ap.Excess)} beyond allocation" : "") +
                    (a.Rate > 0 ? $" · WO value {PdfReports.M(ap.AllocatedValue)} · payable {PdfReports.M(ap.PayableValue)} · balance {PdfReports.M(ap.BalanceValue)}" : "")));
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
                    Ui.Field("Drawing qty", Ui.Number(it.Qty, v => it.Qty = v)),
                    Ui.Field("BOQ rate", Ui.Number((double)it.Rate, v => it.Rate = (decimal)v, "0.00"))),
                status,
                Ui.Caption("Allocated to agencies:"),
                allocList,
            }
        };
    }

    private async Task SaveAsync()
    {
        if (!await PersistAsync()) return;
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
