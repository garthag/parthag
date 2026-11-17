using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>The BOM: drawings (top) and the part lines of the selected drawing (bottom).</summary>
sealed class BomView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly ComboBox _system = Ui.Combo(260);
    readonly TextBox _search = new() { Width = 260, PlaceholderText = "Search drawing, title or system", Margin = new Padding(0, 3, 8, 0), AccessibleName = "Search BOM" };
    readonly DataGridView _drawings = Ui.Grid();
    readonly DataGridView _parts = Ui.Grid();
    readonly Label _total = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _partsTitle = new() { Dock = DockStyle.Top, AutoSize = true, Font = Ui.Section, Padding = new Padding(0, 6, 0, 6) };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    List<BomItem> _items = new();
    Dictionary<long, int> _partCounts = new();
    bool _loading;

    public BomView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;
        _system.AccessibleName = "System";
        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[]
        {
            _system, _search,
            Ui.Btn("Export drawings", (_, _) => Ui.ExportTables(_host.Window, "BOM drawings", DrawingTable())),
            Ui.Btn("Export parts of this drawing", (_, _) => { if (_parts.DataSource is DataTable t) Ui.ExportTables(_host.Window, "BOM parts", t); }),
            Ui.Btn("Bill lines of this drawing", (_, _) => ShowLines()),
        });
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Ui.Back };
        split.Panel1.Controls.Add(_drawings);
        split.Panel2.Controls.Add(_parts);
        split.Panel2.Controls.Add(_partsTitle);
        Controls.Add(split);
        Controls.Add(_total);
        Controls.Add(bar);
        HandleCreated += (_, _) => { try { split.SplitterDistance = Math.Max(150, Height / 2); } catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { } };

        _drawings.SelectionChanged += (_, _) => { if (!_loading) ShowParts(); };
        _drawings.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowLines(); };
        _parts.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _parts.Rows[e.RowIndex].DataBoundItem is DataRowView v && v["Counted"] as string == "No")
                e.CellStyle!.ForeColor = Ui.Muted;
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Fill(); };
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _system.SelectedIndexChanged += (_, _) => { if (!_loading) Fill(); };
    }

    public void RefreshData()
    {
        _loading = true;
        var keep = _system.SelectedItem as string;
        _items = _host.Repo.BomItems();
        _partCounts = _host.Repo.BomPartCounts();
        _system.Items.Clear();
        _system.Items.Add("All systems");
        _system.Items.AddRange(_items.Select(i => i.System).Distinct().Cast<object>().ToArray());
        _system.SelectedItem = keep != null && _system.Items.Contains(keep) ? keep : "All systems";
        _loading = false;
        Fill();
    }

    IEnumerable<BomItem> VisibleItems()
    {
        var sys = _system.SelectedIndex > 0 ? _system.SelectedItem as string : null;
        var s = _search.Text.Trim();
        return _items.Where(i => (sys == null || i.System == sys) &&
            (s.Length == 0 || (i.System + " " + i.DrawingLabel + " " + i.Description + " " + i.Item).Contains(s, StringComparison.OrdinalIgnoreCase)));
    }

    DataTable DrawingTable()
    {
        var t = new DataTable("BOM drawings");
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("System"); t.Columns.Add("Drawing no."); t.Columns.Add("Sheet"); t.Columns.Add("Rev");
        t.Columns.Add("Item"); t.Columns.Add("Title"); t.Columns.Add("Part lines", typeof(int)); t.Columns.Add("Sets", typeof(int)); t.Columns.Add("BOM t", typeof(decimal));
        foreach (var i in VisibleItems())
            t.Rows.Add(i.Id, i.System, i.DrawingNo, i.Sheet, i.Rev, i.Item, i.Description, _partCounts.GetValueOrDefault(i.Id), i.Sets, i.Tonnes);
        return t;
    }

    void Fill()
    {
        _loading = true;
        var t = DrawingTable();
        Ui.Bind(_drawings, t);
        _loading = false;
        _total.Text = _items.Count == 0
            ? "The BOM is empty. Data › Import BSL workbooks (or Import from Excel) loads it."
            : $"{t.Rows.Count:N0} of {_items.Count:N0} drawings · {Fmt.Tonnes(t.AsEnumerable().Sum(r => (decimal)r["BOM t"]))} t · select a drawing to see its part lines";
        ShowParts();
    }

    void ShowParts()
    {
        if (Ui.SelectedId(_drawings) is not long id) { _parts.DataSource = null; _partsTitle.Text = "Part lines"; return; }
        var item = _items.FirstOrDefault(i => i.Id == id);
        var t = new DataTable("BOM parts");
        foreach (var n in new[] { "Sheet / row", "Type", "Counted", "Assembly", "Part", "Item no.", "Description", "Section", "Section (std)" }) t.Columns.Add(n);
        foreach (var n in new[] { "Length mm", "Width mm", "Thk mm", "Qty" }) t.Columns.Add(n, typeof(decimal));
        t.Columns.Add("Unit wt kg", typeof(decimal)); t.Columns.Add("Total kg", typeof(decimal));
        t.Columns.Add("Grade"); t.Columns.Add("Remarks"); t.Columns.Add("Notes");
        foreach (var p in _host.Repo.BomParts(id))
            t.Rows.Add($"{p.SourceSheet} r{p.SourceRow}", p.RowType, p.Counted ? "Yes" : "No", p.AssemblyMark, p.PartMark, p.ItemNo, p.Description, p.Section, p.SectionStd,
                (object?)p.LengthMm ?? DBNull.Value, (object?)p.WidthMm ?? DBNull.Value, (object?)p.ThicknessMm ?? DBNull.Value,
                p.Qty is { } q ? q : DBNull.Value, (object?)p.UnitWeightKg ?? DBNull.Value, (object?)p.TotalWeightKg ?? DBNull.Value,
                p.Grade, p.Remarks, p.Flags);
        Ui.Bind(_parts, t);
        foreach (var c in new[] { "Length mm", "Width mm", "Thk mm" }) _parts.Columns[c]!.DefaultCellStyle.Format = "0.##";
        foreach (var c in new[] { "Unit wt kg", "Total kg" }) _parts.Columns[c]!.DefaultCellStyle.Format = "N2";
        _partsTitle.Text = item == null ? "Part lines" :
            $"Part lines of {item.Label}{(item.Description.Length > 0 ? " – " + item.Description : "")}: {t.Rows.Count} line(s)" +
            (item.Sets > 1 ? $" · weights are for one set; the BOM row counts {item.Sets} sets" : "") + " · grey lines are details already in their assembly's weight";
    }

    void ShowLines()
    {
        if (Ui.SelectedId(_drawings) is not long id) return;
        var item = _items.First(i => i.Id == id);
        var lines = _host.Repo.LineDetails(new[] { id });
        var t = new DataTable("Bill lines");
        t.Columns.Add("Date", typeof(DateTime)); t.Columns.Add("Bill no."); t.Columns.Add("Status"); t.Columns.Add("Contractor"); t.Columns.Add("Work order");
        t.Columns.Add("Activity"); t.Columns.Add("Qty", typeof(decimal)); t.Columns.Add("Rate ₹", typeof(decimal)); t.Columns.Add("Amount ₹", typeof(decimal));
        foreach (var l in lines) t.Rows.Add(l.BillDate, l.BillNo, l.Status, l.Contractor, l.WorkOrder, l.Activity, l.Qty, l.Rate, l.Amount);
        using var d = new TableDialog("Bill lines of " + item.Label, $"{lines.Count} line(s) · BOM {Fmt.Tonnes(item.Tonnes)} t", t);
        d.ShowDialog(_host.Window);
    }
}

/// <summary>BOQ (contract items): R1 scope, rates, tonnes billed and balance, per work order.</summary>
sealed class BoqView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly ComboBox _wo = Ui.Combo(320);
    readonly TextBox _search = new() { Width = 240, PlaceholderText = "Search BOQ", Margin = new Padding(0, 3, 8, 0), AccessibleName = "Search BOQ" };
    readonly CheckBox _over = new() { Text = "Over R1 scope only", AutoSize = true, Margin = new Padding(0, 6, 10, 0) };
    readonly DataGridView _grid = Ui.Grid();
    readonly Label _total = new() { Dock = DockStyle.Bottom, Height = 24, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    sealed record WoItem(string Text, long? Id) { public override string ToString() => Text; }
    bool _loading;

    public BoqView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;
        _wo.AccessibleName = "Work order";
        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[]
        {
            _wo, _search, _over,
            Ui.Btn("Edit item", (_, _) => Edit()),
            Ui.Btn("Bill lines of this item", (_, _) => ShowLines()),
            Ui.Btn("Export", (_, _) => Ui.ExportTables(_host.Window, "BOQ", (DataTable)_grid.DataSource)),
            Ui.Btn("Print PDF", (_, _) => Ui.Preview(_host.Window, new TablePrinter((DataTable)_grid.DataSource, "BOQ", _total.Text))),
        });
        Controls.Add(_grid);
        Controls.Add(_total);
        Controls.Add(bar);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowLines(); };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is DataRowView v && v["Scope"] as string == "Over")
                e.CellStyle!.ForeColor = Ui.Bad;
        };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Fill(); };
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _wo.SelectedIndexChanged += (_, _) => { if (!_loading) Fill(); };
        _over.CheckedChanged += (_, _) => Fill();
    }

    public void RefreshData()
    {
        _loading = true;
        var keep = (_wo.SelectedItem as WoItem)?.Id;
        _wo.Items.Clear();
        _wo.Items.Add(new WoItem("All work orders", null));
        _wo.Items.Add(new WoItem("(not linked to a work order)", 0));
        foreach (var w in _host.Repo.WorkOrders()) _wo.Items.Add(new WoItem($"{w.WoNo} – {w.ContractorName}", w.Id));
        _wo.SelectedItem = _wo.Items.Cast<WoItem>().FirstOrDefault(i => i.Id == keep) ?? _wo.Items[0];
        _loading = false;
        Fill();
    }

    void Fill()
    {
        var wo = (_wo.SelectedItem as WoItem)?.Id;
        var billed = _host.Repo.BoqBilled();
        var s = _search.Text.Trim();
        var t = new DataTable("BOQ");
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("Work order"); t.Columns.Add("Group"); t.Columns.Add("Spec item"); t.Columns.Add("Description");
        t.Columns.Add("Unit"); t.Columns.Add("Qty", typeof(decimal)); t.Columns.Add("R1 scope t", typeof(decimal)); t.Columns.Add("Billed t", typeof(decimal));
        t.Columns.Add("Balance t", typeof(decimal)); t.Columns.Add("Billed %", typeof(decimal)); t.Columns.Add("Client rate ₹", typeof(decimal));
        t.Columns.Add("Agency rate ₹", typeof(decimal)); t.Columns.Add("Billed ₹", typeof(decimal)); t.Columns.Add("Activity"); t.Columns.Add("Agency ref");
        t.Columns.Add("Source"); t.Columns.Add("Remarks"); t.Columns.Add("Scope");
        foreach (var q in _host.Repo.BoqItems())
        {
            if (wo == 0 && q.WorkOrderId != null) continue;
            if (wo is > 0 && q.WorkOrderId != wo) continue;
            if (s.Length > 0 && !(q.Description + " " + q.GroupName + " " + q.SpecItem + " " + q.AgencyRef + " " + q.WorkOrderNo).Contains(s, StringComparison.OrdinalIgnoreCase)) continue;
            var (bt, ba, _) = billed.GetValueOrDefault(q.Id);
            var over = q.ScopeTonnes is { } sc && bt > sc + 0.0005m;
            if (_over.Checked && !over) continue;
            t.Rows.Add(q.Id, q.WorkOrderNo, q.GroupName, q.SpecItem, q.Description, q.Unit, (object?)q.Qty ?? DBNull.Value,
                (object?)q.ScopeTonnes ?? DBNull.Value, bt, q.ScopeTonnes is { } s1 ? s1 - bt : DBNull.Value,
                q.ScopeTonnes is > 0 ? bt * 100m / q.ScopeTonnes.Value : DBNull.Value,
                (object?)q.ExternalRate ?? DBNull.Value, (object?)q.InternalRate ?? DBNull.Value, ba, q.Activity, q.AgencyRef, q.Source, q.Remarks, over ? "Over" : "");
        }
        Ui.Bind(_grid, t);
        var rows = t.AsEnumerable().ToList();
        _total.Text = $"{rows.Count} item(s) · R1 scope {Fmt.Tonnes(rows.Where(r => !r.IsNull("R1 scope t")).Sum(r => (decimal)r["R1 scope t"]))} t · " +
                      $"billed {Fmt.Tonnes(rows.Sum(r => (decimal)r["Billed t"]))} t · ₹ {Fmt.Money(rows.Sum(r => (decimal)r["Billed ₹"]))} · " +
                      $"{rows.Count(r => r["Scope"] as string == "Over")} over R1 scope · double-click for the bill lines";
    }

    void Edit()
    {
        if (Ui.SelectedId(_grid) is not long id || _host.Repo.GetBoqItem(id) is not { } q) return;
        var wos = new List<WoItem> { new("(not linked)", null) };
        wos.AddRange(_host.Repo.WorkOrders().Select(w => new WoItem($"{w.WoNo} – {w.ContractorName}", w.Id)));
        using var d = new EditDialog("BOQ item");
        var wo = d.AddCombo("Work order", wos, wos.FirstOrDefault(w => w.Id == q.WorkOrderId), width: 420);
        var desc = d.AddText("Description", q.Description, 420);
        var unit = d.AddText("Unit", q.Unit, 100);
        var scope = d.AddNumber("R1 scope (t)", q.ScopeTonnes ?? 0, "0.###");
        var ext = d.AddNumber("Client rate ₹/t", q.ExternalRate ?? 0, "#,##,##0.00");
        var inr = d.AddNumber("Agency rate ₹/t", q.InternalRate ?? 0, "#,##,##0.00");
        var act = d.AddCombo("Activity", _host.Repo.Activities().Select(a => (object)a.Name), q.Activity, editable: true, width: 200);
        var rem = d.AddMultiline("Remarks", q.Remarks);
        d.OnSave = () => Ui.Try(d, () =>
        {
            q.WorkOrderId = (wo.SelectedItem as WoItem)?.Id; q.Description = desc.Text; q.Unit = unit.Text.Trim();
            q.ScopeTonnes = EditDialog.Number(scope) is var s1 && s1 > 0 ? s1 : null;
            q.ExternalRate = EditDialog.Number(ext) is var e1 && e1 > 0 ? e1 : null;
            q.InternalRate = EditDialog.Number(inr) is var i1 && i1 > 0 ? i1 : null;
            q.Activity = act.Text.Trim(); q.Remarks = rem.Text.Trim();
            _host.Repo.SaveBoqItem(q);
        });
        if (d.ShowDialog(_host.Window) == DialogResult.OK) _host.DataChanged();
    }

    void ShowLines()
    {
        if (Ui.SelectedId(_grid) is not long id || _host.Repo.GetBoqItem(id) is not { } q) return;
        var lines = _host.Repo.BoqLineDetails(id);
        var t = new DataTable("Bill lines");
        t.Columns.Add("Date", typeof(DateTime)); t.Columns.Add("Bill no."); t.Columns.Add("Status"); t.Columns.Add("Contractor"); t.Columns.Add("Work order");
        t.Columns.Add("Qty", typeof(decimal)); t.Columns.Add("Rate ₹", typeof(decimal)); t.Columns.Add("Amount ₹", typeof(decimal)); t.Columns.Add("Remarks");
        foreach (var l in lines) t.Rows.Add(l.BillDate, l.BillNo, l.Status, l.Contractor, l.WorkOrder, l.Qty, l.Rate, l.Amount, l.Remarks);
        using var d = new TableDialog("Bill lines of " + q.Description, $"{lines.Count} line(s) · {lines.Sum(l => l.Qty):0.###} t billed · R1 scope {(q.ScopeTonnes is { } s ? Fmt.Tonnes(s) + " t" : "not given")}", t);
        d.ShowDialog(_host.Window);
    }
}

/// <summary>Chooses BOQ items of a work order for BOQ bill lines.</summary>
sealed class BoqPickerForm : Form
{
    readonly List<BoqItem> _all;
    readonly DataGridView _grid = Ui.Grid();
    readonly TextBox _search = new() { Width = 300, PlaceholderText = "Search", Margin = new Padding(0, 3, 8, 0), AccessibleName = "Search BOQ" };
    readonly CheckBox _allWos = new() { Text = "Show items of all work orders", AutoSize = true, Margin = new Padding(0, 6, 8, 0) };
    readonly long? _woId;
    readonly Dictionary<long, (decimal Tonnes, decimal Amount, int Bills)> _billed;
    public List<BoqItem> Selected { get; } = new();

    public BoqPickerForm(BillingRepository repo, long? workOrderId)
    {
        _all = repo.BoqItems();
        _billed = repo.BoqBilled();
        _woId = workOrderId;
        Text = "Add BOQ lines – select one or more items";
        Font = Ui.Base; BackColor = Ui.Back; Size = new Size(1100, 620); StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; Padding = new Padding(12);
        _grid.MultiSelect = true;
        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[] { _search, _allWos });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(Ui.Btn("Add lines", (_, _) => Accept(), primary: true));
        var cancel = Ui.Btn("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        buttons.Controls.Add(cancel);
        CancelButton = cancel;
        Controls.Add(_grid); Controls.Add(bar); Controls.Add(buttons);
        _allWos.Checked = workOrderId == null || !_all.Any(q => q.WorkOrderId == workOrderId);
        _search.TextChanged += (_, _) => Fill();
        _allWos.CheckedChanged += (_, _) => Fill();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };
        Fill();
    }

    void Fill()
    {
        var s = _search.Text.Trim();
        var t = new DataTable();
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("Work order"); t.Columns.Add("Group"); t.Columns.Add("Description"); t.Columns.Add("Unit");
        t.Columns.Add("R1 scope t", typeof(decimal)); t.Columns.Add("Billed t", typeof(decimal)); t.Columns.Add("Agency rate ₹", typeof(decimal)); t.Columns.Add("Activity");
        foreach (var q in _all)
        {
            if (!_allWos.Checked && q.WorkOrderId != _woId) continue;
            if (s.Length > 0 && !(q.Description + " " + q.GroupName + " " + q.WorkOrderNo).Contains(s, StringComparison.OrdinalIgnoreCase)) continue;
            t.Rows.Add(q.Id, q.WorkOrderNo, q.GroupName, q.Description, q.Unit, (object?)q.ScopeTonnes ?? DBNull.Value,
                _billed.GetValueOrDefault(q.Id).Tonnes, (object?)q.InternalRate ?? DBNull.Value, q.Activity);
        }
        Ui.Bind(_grid, t);
    }

    void Accept()
    {
        Selected.Clear();
        foreach (DataGridViewRow r in _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index))
            if (r.DataBoundItem is DataRowView v && _all.FirstOrDefault(q => q.Id == (long)v["_Id"]) is { } item) Selected.Add(item);
        if (Selected.Count == 0) return;
        DialogResult = DialogResult.OK;
        Close();
    }
}
