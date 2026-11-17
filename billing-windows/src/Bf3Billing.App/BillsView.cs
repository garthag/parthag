using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

sealed class BillsView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly TextBox _search = new() { Width = 220, PlaceholderText = "Search bills", Margin = new Padding(0, 3, 8, 0) };
    readonly ComboBox _status = Ui.Combo(130);
    readonly ComboBox _contractor = Ui.Combo(200);
    readonly ComboBox _workOrder = Ui.Combo(260);
    readonly DateTimePicker _from = Ui.Date(true);
    readonly DateTimePicker _to = Ui.Date(true);
    readonly CheckBox _over = new() { Text = "Over BOM scope", AutoSize = true, Margin = new Padding(0, 6, 10, 0) };
    readonly DataGridView _grid = Ui.Grid();
    readonly Label _total = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    bool _loading;
    BillFilter? _pending;

    sealed record Item(string Text, long? Id) { public override string ToString() => Text; }

    public BillsView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;
        _search.AccessibleName = "Search bills";
        _status.AccessibleName = "Status";
        _contractor.AccessibleName = "Contractor";
        _workOrder.AccessibleName = "Work order";
        _from.AccessibleName = "From date";
        _to.AccessibleName = "To date";

        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[]
        {
            _search, _status, _contractor, _workOrder, Ui.Lbl("From"), _from, Ui.Lbl("To"), _to, _over,
            Ui.Btn("Reset", (_, _) => Reset()),
            Ui.Btn("Export", (_, _) => Ui.ExportTables(_host.Window, "Bills", CurrentTable())),
            Ui.Btn("Print PDF", (_, _) => Ui.Preview(_host.Window, new TablePrinter(CurrentTable(), "Bills", _total.Text))),
            Ui.Btn("+ New bill", (_, _) => _host.OpenBill(null, SelectedWorkOrder()), primary: true),
        });

        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenSelected(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; OpenSelected(); } };
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not DataRowView v) return;
            if (v["Scope"] is string s && s.Length > 0) e.CellStyle!.ForeColor = Ui.Bad;
        };

        Controls.Add(_grid);
        Controls.Add(_total);
        Controls.Add(bar);

        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        foreach (var c in new[] { _status, _workOrder }) c.SelectedIndexChanged += (_, _) => { if (!_loading) Reload(); };
        _contractor.SelectedIndexChanged += (_, _) => { if (_loading) return; FillWorkOrders(); Reload(); };
        _from.ValueChanged += (_, _) => { if (!_loading) Reload(); };
        _to.ValueChanged += (_, _) => { if (!_loading) Reload(); };
        _over.CheckedChanged += (_, _) => { if (!_loading) Reload(); };
    }

    long? SelectedWorkOrder() => (_workOrder.SelectedItem as Item)?.Id;

    void OpenSelected()
    {
        if (Ui.SelectedId(_grid) is long id) _host.OpenBill(id);
    }

    public void ApplyFilter(BillFilter f) => _pending = f;

    void Reset()
    {
        _loading = true;
        _search.Text = "";
        _status.SelectedIndex = 0;
        _contractor.SelectedIndex = 0;
        FillWorkOrders();
        _from.Checked = false;
        _to.Checked = false;
        _over.Checked = false;
        _loading = false;
        Reload();
    }

    void FillWorkOrders()
    {
        var keep = SelectedWorkOrder();
        var k = (_contractor.SelectedItem as Item)?.Id;
        _workOrder.Items.Clear();
        _workOrder.Items.Add(new Item("All work orders", null));
        foreach (var w in _host.Repo.WorkOrders().Where(w => k == null || w.ContractorId == k))
            _workOrder.Items.Add(new Item(w.WoNo, w.Id));
        _workOrder.SelectedItem = _workOrder.Items.Cast<Item>().FirstOrDefault(i => i.Id == keep) ?? _workOrder.Items[0];
    }

    public void RefreshData()
    {
        _loading = true;
        var keepStatus = _status.SelectedItem as string;
        var keepK = (_contractor.SelectedItem as Item)?.Id;
        _status.Items.Clear();
        _status.Items.Add("All statuses");
        _status.Items.AddRange(BillStatus.All);
        _status.SelectedItem = keepStatus != null && _status.Items.Contains(keepStatus) ? keepStatus : "All statuses";
        _contractor.Items.Clear();
        _contractor.Items.Add(new Item("All contractors", null));
        foreach (var k in _host.Repo.Contractors()) _contractor.Items.Add(new Item(k.Name, k.Id));
        _contractor.SelectedItem = _contractor.Items.Cast<Item>().FirstOrDefault(i => i.Id == keepK) ?? _contractor.Items[0];
        FillWorkOrders();

        if (_pending != null)
        {
            var f = _pending;
            _pending = null;
            _search.Text = f.Search ?? "";
            _status.SelectedItem = f.Status ?? "All statuses";
            _contractor.SelectedItem = _contractor.Items.Cast<Item>().FirstOrDefault(i => i.Id == f.ContractorId) ?? _contractor.Items[0];
            FillWorkOrders();
            _workOrder.SelectedItem = _workOrder.Items.Cast<Item>().FirstOrDefault(i => i.Id == f.WorkOrderId) ?? _workOrder.Items[0];
            Ui.Set(_from, f.From);
            Ui.Set(_to, f.To);
            _over.Checked = f.OnlyOverScope;
        }
        _loading = false;
        Reload();
    }

    BillFilter Filter() => new()
    {
        Search = _search.Text,
        Status = _status.SelectedIndex > 0 ? _status.SelectedItem as string : null,
        ContractorId = (_contractor.SelectedItem as Item)?.Id,
        WorkOrderId = SelectedWorkOrder(),
        From = Ui.Value(_from),
        To = Ui.Value(_to),
        OnlyOverScope = _over.Checked,
    };

    List<BillSummary> _rows = new();

    void Reload()
    {
        Ui.Try(_host.Window, () =>
        {
            var keep = Ui.SelectedId(_grid);
            _rows = _host.Repo.BillSummaries(Filter());
            Ui.Bind(_grid, CurrentTable());
            if (keep is long id)
                foreach (DataGridViewRow r in _grid.Rows)
                    if (r.DataBoundItem is DataRowView v && (long)v["_Id"] == id) { _grid.CurrentCell = r.Cells[1]; break; }
            _total.Text = $"{_rows.Count} bill(s) · gross ₹ {Fmt.Money(_rows.Sum(b => b.Gross))} · net ₹ {Fmt.Money(_rows.Sum(b => b.Net))} · " +
                          $"paid ₹ {Fmt.Money(_rows.Sum(b => b.Paid))} · due ₹ {Fmt.Money(_rows.Sum(b => b.Due))}";
        });
    }

    DataTable CurrentTable()
    {
        var t = new DataTable("Bills");
        t.Columns.Add("_Id", typeof(long));
        t.Columns.Add("Bill no."); t.Columns.Add("Date", typeof(DateTime));
        foreach (var n in new[] { "Contractor", "Work order", "Type" }) t.Columns.Add(n);
        t.Columns.Add("Lines", typeof(int));
        foreach (var n in new[] { "Gross ₹", "Net payable ₹", "Paid ₹", "Due ₹" }) t.Columns.Add(n, typeof(decimal));
        t.Columns.Add("Status"); t.Columns.Add("Scope");
        foreach (var b in _rows)
            t.Rows.Add(b.Id, b.BillNo, b.BillDate, b.Contractor, b.WorkOrder, b.BillType, b.Lines, b.Gross, b.Net, b.Paid, b.Due,
                b.Status, b.OverScopeLines > 0 ? $"⚠ {b.OverScopeLines} over" : "");
        return t;
    }
}
