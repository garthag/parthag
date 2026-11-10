using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SiteNotes.Core;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop.Views;

public class DrawingRow(Drawing d) : LiveRow
{
    public Drawing D { get; } = d;
    public DrawingProgress? P { get; set; }
    public string Quantity => P?.TotalsText ?? "";
    public double AllocatedPercent => P?.AllocatedPercent ?? 0;
    public double Percent => P?.Percent ?? 0;
    public int Agencies => P?.Agencies ?? 0;
    public string State => P == null ? "" : DrawingReports.StateText(P.State);
    public string Wbs => P?.WbsPath ?? "";
}

public class ItemRow(DrawingItem i) : LiveRow
{
    public DrawingItem I { get; } = i;
    public DrawingItemProgress? P { get; set; }
    public double Allocated => P?.Allocated ?? 0;
    public double Unallocated => P?.Unallocated ?? I.Qty;
    public double Completed => P?.Completed ?? 0;
    public double Balance => P?.Balance ?? I.Qty;
    public double Percent => P?.Percent ?? 0;
    public double Excess => P?.Excess ?? 0;
    public string Warning => P == null ? "" : string.Join(" · ", new[]
    {
        P.OverAllocated > 0 ? $"over-allocated by {P.OverAllocated:0.##}" : "",
        P.CompletedWithoutAllocation > 0 ? $"{P.CompletedWithoutAllocation:0.##} done without allocation" : "",
        P.Excess > 0 ? $"{P.Excess:0.##} beyond drawing qty" : "",
        P.Agencies.Any(a => a.Excess > 0) ? "agency beyond its allocation" : "",
    }.Where(x => x != ""));
    public string State => P == null ? "" : DrawingReports.StateText(P.State);
}

public class AllocRow(DrawingAllocation a) : LiveRow
{
    public DrawingAllocation A { get; } = a;
    public AllocationProgress? P { get; set; }
    public double Done => P?.Done ?? 0;
    public double Balance => P?.Balance ?? A.AllocatedQty;
    public double Percent => P?.Percent ?? 0;
    public double Excess => P?.Excess ?? 0;
    public DateTime? LastWorked => P?.LastWorked;
}

/// <summary>
/// Drawing register &amp; work completion. Top: drawings. Middle: items (BOQ / equipment / parts) of the selected drawing.
/// Bottom: allocations of the selected item to agencies. Completed quantities come from the daily reports
/// ("Add drawing work…" in the daily report) and update here automatically.
/// </summary>
public class DrawingsView : ViewBase, IPrintable
{
    private readonly ObservableCollection<DrawingRow> _drawings = [];
    private readonly ObservableCollection<ItemRow> _items = [];
    private readonly ObservableCollection<AllocRow> _allocs = [];
    private readonly DataGrid _dGrid, _iGrid, _aGrid;
    private readonly TextBlock _itemTitle = W.Text("", 13, true, Theme.Primary);
    private readonly TextBlock _allocTitle = W.Text("", 13, true, Theme.Primary);
    private readonly TabControl _summary = new() { Margin = new Thickness(10, 4, 10, 10) };
    private readonly WrapPanel _kpis = new() { Margin = new Thickness(10, 6, 0, 0) };
    private readonly TextBox _search = new() { Width = 240 };
    private DrawingRegister _reg = new();
    private List<Pick> _subs = [];

    public override string Title => "Drawing register";

    public DrawingsView()
    {
        _subs = MasterViews.Subs().Where(p => p.Id != "").ToList();
        var wbs = Task.Run(async () =>
        {
            var all = await Desk.Db.ListForProjectAsync<WbsNode>(Desk.ProjectId);
            var map = all.ToDictionary(w => w.Id);
            return all.Select(w => new Pick(w.Id, ProgressService.WbsPath(w.Id, map))).OrderBy(p => p.Name).Prepend(new Pick("", "(no WBS)")).ToList();
        }).GetAwaiter().GetResult();
        var acts = Task.Run(async () => (await Desk.Db.ListForProjectAsync<Activity>(Desk.ProjectId)).OrderBy(a => a.Code)
            .Select(a => new Pick(a.Id, a.ToString())).Prepend(new Pick("", "(none)")).ToList()).GetAwaiter().GetResult();

        _dGrid = new GridBuilder(_drawings, multiSelect: false)
            .Text("Drawing no.", "D.Number", 150).Text("Title", "D.Title", 230).Text("Rev", "D.Revision", 45)
            .Suggest("Discipline", "D.Discipline", ["Civil", "Structural", "Mechanical", "Electrical", "Piping", "Instrumentation", "Architectural"], 100)
            .Enum<DrawingStatus>("Status", "D.Status", 110).Lookup("WBS", "D.WbsId", wbs, 170).Date("Issued", "D.IssueDate")
            .Text("Quantity", nameof(DrawingRow.Quantity), 170, readOnly: true)
            .Progress("Allocated", nameof(DrawingRow.AllocatedPercent), 105).Progress("Completed", nameof(DrawingRow.Percent), 105)
            .Number("Agencies", nameof(DrawingRow.Agencies), 65, "0", readOnly: true).Status("Work status", nameof(DrawingRow.State), 110)
            .OnEdited(async o => await Dialogs.Guard(async () => { await Desk.Db.SaveAsync(((DrawingRow)o).D); await RecalcAsync(); })).Grid;
        _iGrid = new GridBuilder(_items, multiSelect: false)
            .Text("Item / BOQ no.", "I.ItemNo", 95).Text("Description", "I.Description", 230).Enum<DrawingItemKind>("Type", "I.Kind", 95)
            .Suggest("Unit", "I.Unit", Lists.Units, 55).Number("Drawing qty", "I.Qty", 85)
            .Number("Allocated", nameof(ItemRow.Allocated), 80, readOnly: true).Number("Unallocated", nameof(ItemRow.Unallocated), 85, readOnly: true)
            .Number("Completed", nameof(ItemRow.Completed), 85, readOnly: true).Number("Balance", nameof(ItemRow.Balance), 80, readOnly: true)
            .Progress("Progress", nameof(ItemRow.Percent), 105).Status("Status", nameof(ItemRow.State), 105)
            .Text("Check", nameof(ItemRow.Warning), 200, readOnly: true).Lookup("Counts for activity", "I.ActivityId", acts, 170)
            .OnEdited(async o => await Dialogs.Guard(async () => { await Desk.Db.SaveAsync(((ItemRow)o).I); await RecalcAsync(); })).Grid;
        _aGrid = new GridBuilder(_allocs, multiSelect: false)
            .Lookup("Agency (subcontractor)", "A.SubcontractorId", _subs, 200).Number("Allocated qty", "A.AllocatedQty", 95)
            .Text("Work order", "A.WorkOrderNo", 110).Date("Allocated on", "A.AllocatedOn")
            .Number("Done", nameof(AllocRow.Done), 80, readOnly: true).Number("Balance", nameof(AllocRow.Balance), 80, readOnly: true)
            .Progress("Progress", nameof(AllocRow.Percent), 105).Number("Beyond allocation", nameof(AllocRow.Excess), 105, readOnly: true)
            .Date("Last worked", nameof(AllocRow.LastWorked), readOnly: true).Text("Remarks", "A.Remarks")
            .OnEdited(async o => await Dialogs.Guard(async () =>
            {
                var a = ((AllocRow)o).A;
                a.SubcontractorName = _subs.FirstOrDefault(s => s.Id == a.SubcontractorId)?.Name ?? "";
                await Desk.Db.SaveAsync(a);
                await RecalcAsync();
            })).Grid;
        _dGrid.SelectionChanged += async (_, _) => await Dialogs.Guard(ShowItemsAsync);
        _iGrid.SelectionChanged += async (_, _) => await Dialogs.Guard(ShowAllocsAsync);
        _search.TextChanged += (_, _) => System.Windows.Data.CollectionViewSource.GetDefaultView(_drawings).Refresh();
        System.Windows.Data.CollectionViewSource.GetDefaultView(_drawings).Filter = o =>
            _search.Text.Trim() == "" || $"{((DrawingRow)o).D.Number} {((DrawingRow)o).D.Title} {((DrawingRow)o).Wbs}".Contains(_search.Text.Trim(), StringComparison.OrdinalIgnoreCase);

        var tb = W.ToolBar(
            (Glyph.Add, "Add drawing", AddDrawingAsync),
            (Glyph.Add, "Add item", AddItemAsync),
            (Glyph.People, "Allocate to agency", AddAllocAsync),
            (Glyph.Accept, "Allocate the rest…", AllocateRestAsync),
            null,
            (Glyph.Delete, "Delete selected…", DeleteAsync),
            null,
            (Glyph.Print, "PDF", PdfAsync),
            (Glyph.Excel, "Excel", ExcelAsync),
            (Glyph.Refresh, "Recalculate", RefreshAsync));
        var search = W.Wrap(W.Text("🔍 "), _search, W.Muted("   Completed quantities come from the daily reports (“Add drawing work…”). One item can be split between several agencies."));
        search.Margin = new Thickness(10, 6, 10, 4);

        Grid Pane(UIElement header, DataGrid grid)
        {
            var g = new Grid { Margin = new Thickness(10, 2, 10, 2) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition());
            g.Children.Add(header);
            Grid.SetRow(grid, 1);
            g.Children.Add(grid);
            return g;
        }
        var split = new Grid();
        foreach (var h in new[] { 3.0, 0, 2.5, 0, 2.0 })
            split.RowDefinitions.Add(new RowDefinition { Height = h == 0 ? GridLength.Auto : new GridLength(h, GridUnitType.Star) });
        var dPane = Pane(W.Text("Drawings", 13, true, Theme.Primary), _dGrid);
        var iPane = Pane(_itemTitle, _iGrid);
        var bottom = new TabControl { Margin = new Thickness(10, 2, 10, 10) };
        bottom.Items.Add(new TabItem { Header = "Allocations of the selected item", Content = Pane(_allocTitle, _aGrid) });
        bottom.Items.Add(new TabItem { Header = "Subcontractor-wise", Content = _summary });
        Grid.SetRow(iPane, 2);
        Grid.SetRow(bottom, 4);
        split.Children.Add(dPane);
        var s1 = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Line };
        Grid.SetRow(s1, 1);
        split.Children.Add(s1);
        split.Children.Add(iPane);
        var s2 = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Theme.Line };
        Grid.SetRow(s2, 3);
        split.Children.Add(s2);
        split.Children.Add(bottom);

        var dock = new DockPanel();
        foreach (var top in new UIElement[] { tb, _kpis, search }) { DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top); }
        dock.Children.Add(split);
        Content = dock;
    }

    public override async Task RefreshAsync()
    {
        var keep = (_dGrid.SelectedItem as DrawingRow)?.D.Id;
        var list = await Desk.Db.ListForProjectAsync<Drawing>(Desk.ProjectId);
        _drawings.Clear();
        foreach (var d in list.OrderBy(d => d.Number)) _drawings.Add(new DrawingRow(d));
        await RecalcAsync();
        _dGrid.SelectedItem = _drawings.FirstOrDefault(r => r.D.Id == keep) ?? _drawings.FirstOrDefault();
    }

    /// <summary>Recalculates the register and repaints the calculated cells (no grid reload).</summary>
    private async Task RecalcAsync()
    {
        _reg = await Task.Run(() => new DrawingService(Desk.Db).ComputeAsync(Desk.ProjectId));
        var byId = _reg.Drawings.ToDictionary(d => d.Drawing.Id);
        foreach (var r in _drawings) { r.P = byId.GetValueOrDefault(r.D.Id); r.Notify(); }
        var items = _reg.AllItems.ToDictionary(i => i.Item.Id);
        foreach (var r in _items) { r.P = items.GetValueOrDefault(r.I.Id); r.Notify(); }
        foreach (var r in _allocs)
        {
            r.P = items.GetValueOrDefault(r.A.DrawingItemId)?.Agencies.FirstOrDefault(a => a.SubcontractorId == r.A.SubcontractorId);
            r.Notify();
        }
        var all = _reg.AllItems.ToList();
        _kpis.Children.Clear();
        _kpis.Children.Add(W.Stat("Drawings", _reg.Drawings.Count.ToString()));
        _kpis.Children.Add(W.Stat("Completed drawings", _reg.Drawings.Count(d => d.State == DrawingState.Completed).ToString(), Theme.Success));
        _kpis.Children.Add(W.Stat("Items / equipment", all.Count.ToString()));
        _kpis.Children.Add(W.Stat("Not fully allocated", all.Count(i => i.Unallocated > 1e-9).ToString(), Theme.Accent));
        _kpis.Children.Add(W.Stat("Over-allocated", all.Count(i => i.OverAllocated > 1e-9).ToString(), Theme.Danger));
        _kpis.Children.Add(W.Stat("Done without allocation", all.Count(i => i.CompletedWithoutAllocation > 0).ToString(), Theme.Danger));
        _kpis.Children.Add(W.Stat("Beyond drawing qty", all.Count(i => i.Excess > 0).ToString(), Theme.Danger));
        _summary.Items.Clear();
        _summary.Items.Add(new TabItem
        {
            Header = "By agency",
            Content = new GridBuilder(_reg.ByAgency, editable: false)
                .Text("Agency", nameof(AgencyDrawingSummary.Agency), 200).Text("Unit", nameof(AgencyDrawingSummary.Unit), 60)
                .Number("Allocated", nameof(AgencyDrawingSummary.Allocated), 90).Number("Done", nameof(AgencyDrawingSummary.Done), 90)
                .Number("Balance", nameof(AgencyDrawingSummary.Balance), 90).Progress("Progress", nameof(AgencyDrawingSummary.Percent), 120)
                .Number("Drawings", nameof(AgencyDrawingSummary.Drawings), 70, "0").Number("Items", nameof(AgencyDrawingSummary.Items), 60, "0")
                .Number("Done without allocation", nameof(AgencyDrawingSummary.WithoutAllocation), 150).Grid,
        });
    }

    private async Task ShowItemsAsync()
    {
        _items.Clear();
        _allocs.Clear();
        if (_dGrid.SelectedItem is not DrawingRow d) { _itemTitle.Text = "Items"; return; }
        _itemTitle.Text = $"Items of {d.D.Number} {d.D.Revision} – BOQ items, equipment and parts";
        foreach (var i in (await Desk.Db.ListForProjectAsync<DrawingItem>(Desk.ProjectId, x => x.DrawingId == d.D.Id)).OrderBy(i => i.SortOrder).ThenBy(i => i.ItemNo))
            _items.Add(new ItemRow(i) { P = _reg.AllItems.FirstOrDefault(p => p.Item.Id == i.Id) });
        _iGrid.SelectedItem = _items.FirstOrDefault();
    }

    private async Task ShowAllocsAsync()
    {
        _allocs.Clear();
        if (_iGrid.SelectedItem is not ItemRow i) { _allocTitle.Text = "Allocations"; return; }
        _allocTitle.Text = $"Item {i.I.ItemNo}: {i.I.Qty:#,0.##} {i.I.Unit} · allocated {i.Allocated:#,0.##} · unallocated {i.Unallocated:#,0.##} · completed {i.Completed:#,0.##}";
        foreach (var a in await Desk.Db.ListForProjectAsync<DrawingAllocation>(Desk.ProjectId, x => x.DrawingItemId == i.I.Id))
            _allocs.Add(new AllocRow(a) { P = i.P?.Agencies.FirstOrDefault(x => x.SubcontractorId == a.SubcontractorId) });
        // agencies that worked without an allocation are shown too, so they can be allocated
        foreach (var x in i.P?.Agencies.Where(x => x.Unallocated && x.Done > 0) ?? [])
            _allocs.Add(new AllocRow(new DrawingAllocation
            {
                ProjectId = Desk.ProjectId, DrawingId = i.I.DrawingId, DrawingItemId = i.I.Id, SubcontractorId = x.SubcontractorId,
                SubcontractorName = x.Agency, Remarks = "worked without allocation – enter a quantity to allocate",
            }) { P = x });
    }

    private async Task AddDrawingAsync()
    {
        var d = new Drawing { ProjectId = Desk.ProjectId, Number = $"DWG-{_drawings.Count + 1:000}", Title = "New drawing", IssueDate = DateTime.Today };
        await Desk.Db.SaveAsync(d);
        var row = new DrawingRow(d);
        _drawings.Add(row);
        await RecalcAsync();
        _dGrid.SelectedItem = row;
        _dGrid.ScrollIntoView(row);
    }

    private async Task AddItemAsync()
    {
        if (_dGrid.SelectedItem is not DrawingRow d) { Dialogs.Info("Select a drawing first."); return; }
        var i = new DrawingItem { ProjectId = Desk.ProjectId, DrawingId = d.D.Id, Description = "New item", Unit = "MT", SortOrder = _items.Count };
        await Desk.Db.SaveAsync(i);
        var row = new ItemRow(i);
        _items.Add(row);
        await RecalcAsync();
        _iGrid.SelectedItem = row;
    }

    private async Task AddAllocAsync()
    {
        if (_iGrid.SelectedItem is not ItemRow i) { Dialogs.Info("Select an item first."); return; }
        if (_subs.Count == 0) { Dialogs.Info("Add subcontractors first (Registers & masters › Subcontractors)."); return; }
        var k = Dialogs.Choose("Allocate", $"Allocate item {i.I.ItemNo} ({i.Unallocated:#,0.##} {i.I.Unit} unallocated) to:", _subs.Select(s => s.Name).ToList());
        if (k < 0) return;
        var a = new DrawingAllocation
        {
            ProjectId = Desk.ProjectId, DrawingId = i.I.DrawingId, DrawingItemId = i.I.Id, SubcontractorId = _subs[k].Id, SubcontractorName = _subs[k].Name,
            AllocatedQty = Math.Max(0, i.Unallocated),
        };
        await Desk.Db.SaveAsync(a);
        await RecalcAsync();
        await ShowAllocsAsync();
    }

    private async Task AllocateRestAsync()
    {
        if (_iGrid.SelectedItem is not ItemRow i || i.Unallocated <= 0) { Dialogs.Info("Select an item that still has an unallocated quantity."); return; }
        await AddAllocAsync();
    }

    private async Task DeleteAsync()
    {
        if (_aGrid.IsKeyboardFocusWithin && _aGrid.SelectedItem is AllocRow a && a.A.Id != "" && await Desk.Db.GetAsync<DrawingAllocation>(a.A.Id) != null)
        {
            if (!Dialogs.Confirm($"Remove the allocation to {a.A.SubcontractorName}? Work already reported stays.")) return;
            await Desk.Db.DeleteAsync(a.A);
        }
        else if (_iGrid.IsKeyboardFocusWithin && _iGrid.SelectedItem is ItemRow i)
        {
            if (!Dialogs.Confirm($"Delete item {i.I.ItemNo} and its allocations?")) return;
            foreach (var x in await Desk.Db.ListForProjectAsync<DrawingAllocation>(Desk.ProjectId, x => x.DrawingItemId == i.I.Id)) await Desk.Db.DeleteAsync(x);
            await Desk.Db.DeleteAsync(i.I);
        }
        else if (_dGrid.SelectedItem is DrawingRow d)
        {
            if (!Dialogs.Confirm($"Delete drawing {d.D.Number} with all its items and allocations?")) return;
            foreach (var it in await Desk.Db.ListForProjectAsync<DrawingItem>(Desk.ProjectId, x => x.DrawingId == d.D.Id))
            {
                foreach (var x in await Desk.Db.ListForProjectAsync<DrawingAllocation>(Desk.ProjectId, x => x.DrawingItemId == it.Id)) await Desk.Db.DeleteAsync(x);
                await Desk.Db.DeleteAsync(it);
            }
            await Desk.Db.DeleteAsync(d.D);
        }
        await RefreshAsync();
    }

    public async Task PdfAsync()
    {
        var opt = await Desk.ReportOptionsAsync();
        await Dialogs.CreateAndOpenAsync($"Drawing_register_{DateTime.Now:yyyyMMdd_HHmm}.pdf", s => DrawingReports.Pdf(s, Desk.Project, _reg, "All drawings", opt));
    }

    private Task ExcelAsync() =>
        Dialogs.CreateAndOpenAsync($"Drawing_register_{DateTime.Now:yyyyMMdd_HHmm}.xlsx", s => DrawingReports.Excel(s, Desk.Project, _reg, "All drawings"));
}
