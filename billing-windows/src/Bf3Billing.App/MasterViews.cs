using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>A searchable list with New, Edit, Delete, Export and Print. Rows carry their id in "_Id".</summary>
abstract class RecordListView : Panel, ITabView
{
    protected readonly IAppHost Host;
    protected readonly DataGridView Grid = Ui.Grid();
    protected readonly FlowLayoutPanel Toolbar = Ui.Bar();
    readonly TextBox _search = new() { Width = 240, Margin = new Padding(0, 3, 8, 0) };
    readonly Label _total = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft };
    DataTable _table = new();
    readonly string _title;

    protected RecordListView(IAppHost host, string title, string newText)
    {
        Host = host;
        _title = title;
        BackColor = Ui.Back;
        _search.PlaceholderText = "Search " + title.ToLowerInvariant();
        _search.AccessibleName = _search.PlaceholderText;
        Toolbar.Controls.AddRange(new Control[]
        {
            _search,
            Ui.Btn(newText, (_, _) => { if (Edit(null)) Host.DataChanged(); }, primary: true),
            Ui.Btn("Edit", (_, _) => EditSelected()),
            Ui.Btn("Delete", (_, _) => DeleteSelected()),
            Ui.Btn("Export", (_, _) => Ui.ExportTables(Host.Window, _title, VisibleTable())),
            Ui.Btn("Print PDF", (_, _) => Ui.Preview(Host.Window, new TablePrinter(VisibleTable(), _title, _total.Text))),
        });
        Controls.Add(Grid);
        Controls.Add(_total);
        Controls.Add(Toolbar);
        Grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditSelected(); };
        Grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; EditSelected(); }
            if (e.KeyCode == Keys.Delete) { e.Handled = true; DeleteSelected(); }
        };
        _search.TextChanged += (_, _) => ApplySearch();
    }

    protected abstract DataTable Load();
    /// <summary>Opens the edit dialog. Returns true when saved.</summary>
    protected abstract bool Edit(long? id);
    protected abstract void Delete(long id);
    protected virtual string DeletePrompt(DataRowView row) => $"Delete {row[1]}?";
    protected virtual string Summary(DataTable t) => $"{t.DefaultView.Count} row(s)";

    public void RefreshData()
    {
        _table = Load();
        Ui.Bind(Grid, _table, "N0");
        ApplySearch();
    }

    void ApplySearch()
    {
        var s = _search.Text.Trim().Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
        var cols = _table.Columns.Cast<DataColumn>().Where(c => c.DataType == typeof(string)).Select(c => $"[{c.ColumnName}] LIKE '%{s}%'");
        _table.DefaultView.RowFilter = s.Length == 0 ? "" : string.Join(" OR ", cols);
        _total.Text = Summary(_table);
    }

    DataTable VisibleTable()
    {
        var t = _table.DefaultView.ToTable();
        t.TableName = _title;
        return t;
    }

    void EditSelected()
    {
        if (Ui.SelectedId(Grid) is long id && Edit(id)) Host.DataChanged();
    }

    void DeleteSelected()
    {
        if (Ui.SelectedId(Grid) is not long id || Grid.CurrentRow?.DataBoundItem is not DataRowView row) return;
        if (!Ui.Confirm(Host.Window, DeletePrompt(row))) return;
        if (Ui.Try(Host.Window, () => Delete(id))) Host.DataChanged();
    }
}

sealed class WorkOrdersView : RecordListView
{
    public WorkOrdersView(IAppHost host) : base(host, "Work orders", "+ New work order")
    {
        Toolbar.Controls.Add(Ui.Btn("Bills of this WO", (_, _) => { if (Ui.SelectedId(Grid) is long id) Host.ShowBills(new BillFilter { WorkOrderId = id }); }));
        Toolbar.Controls.Add(Ui.Btn("+ New bill on this WO", (_, _) => { if (Ui.SelectedId(Grid) is long id) Host.OpenBill(null, id); }));
    }

    protected override DataTable Load()
    {
        var d = Host.Repo.GetDashboard().WorkOrders.ToDictionary(w => w.WorkOrderId);
        var t = new DataTable();
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("Work order"); t.Columns.Add("Contractor"); t.Columns.Add("Service");
        t.Columns.Add("WO date", typeof(DateTime)); t.Columns.Add("Status"); t.Columns.Add("Value ₹", typeof(decimal));
        t.Columns.Add("Gross billed ₹", typeof(decimal)); t.Columns.Add("Balance ₹", typeof(decimal)); t.Columns.Add("Used %", typeof(decimal));
        t.Columns.Add("Remarks");
        foreach (var w in Host.Repo.WorkOrders())
        {
            var u = d.GetValueOrDefault(w.Id);
            t.Rows.Add(w.Id, w.WoNo, w.ContractorName, w.Service, (object?)w.WoDate ?? DBNull.Value, w.Status, w.Value,
                u?.Gross ?? 0m, u?.Balance ?? w.Value, (object?)u?.UsedPercent ?? DBNull.Value, w.Remarks);
        }
        return t;
    }

    protected override string Summary(DataTable t) => $"{t.DefaultView.Count} work order(s) · value ₹ " +
        Fmt.Money(t.DefaultView.Cast<DataRowView>().Sum(r => (decimal)r["Value ₹"]));

    protected override bool Edit(long? id)
    {
        var w = id is long i ? Host.Repo.WorkOrders().First(x => x.Id == i) : new WorkOrder();
        var contractors = Host.Repo.Contractors();
        if (contractors.Count == 0) { Ui.Info(Host.Window, "Add a contractor first (Contractors tab)."); return false; }
        using var d = new EditDialog(id == null ? "New work order" : "Work order " + w.WoNo);
        var no = d.AddText("Work order no.", w.WoNo);
        var k = d.AddCombo("Contractor", contractors, contractors.FirstOrDefault(c => c.Id == w.ContractorId));
        var services = Host.Repo.WorkOrders().Select(x => x.Service).Where(s => s.Length > 0).Distinct().OrderBy(s => s).Cast<object>();
        var service = d.AddCombo("Service", services, w.Service, editable: true);
        var value = d.AddNumber("Value ₹", w.Value, "#,##,##0.00");
        var date = d.AddDate("WO date", w.WoDate);
        var status = d.AddCombo("Status", new object[] { "Open", "Closed", "Short closed", "On hold" }, w.Status, editable: true, width: 150);
        var remarks = d.AddMultiline("Remarks", w.Remarks);
        d.OnSave = () => Ui.Try(d, () =>
        {
            w.WoNo = no.Text; w.ContractorId = (k.SelectedItem as Contractor)?.Id ?? 0; w.Service = service.Text.Trim();
            w.Value = EditDialog.Number(value); w.WoDate = Ui.Value(date); w.Status = status.Text.Trim(); w.Remarks = remarks.Text.Trim();
            Host.Repo.SaveWorkOrder(w);
        });
        return d.ShowDialog(Host.Window) == DialogResult.OK;
    }

    protected override void Delete(long id) => Host.Repo.DeleteWorkOrder(id);
}

sealed class ContractorsView : RecordListView
{
    public ContractorsView(IAppHost host) : base(host, "Contractors", "+ New contractor")
    {
        Toolbar.Controls.Add(Ui.Btn("Bills of this contractor", (_, _) => { if (Ui.SelectedId(Grid) is long id) Host.ShowBills(new BillFilter { ContractorId = id }); }));
    }

    protected override DataTable Load()
    {
        var bills = Host.Repo.BillSummaries().Where(b => b.Status != BillStatus.Cancelled).ToList();
        var wos = Host.Repo.WorkOrders();
        var t = new DataTable();
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("Name"); t.Columns.Add("Code"); t.Columns.Add("GSTIN"); t.Columns.Add("PAN");
        t.Columns.Add("Phone"); t.Columns.Add("Work orders", typeof(int)); t.Columns.Add("WO value ₹", typeof(decimal));
        t.Columns.Add("Gross billed ₹", typeof(decimal)); t.Columns.Add("Outstanding ₹", typeof(decimal)); t.Columns.Add("Address"); t.Columns.Add("Remarks");
        foreach (var k in Host.Repo.Contractors())
        {
            var kw = wos.Where(w => w.ContractorId == k.Id).ToList();
            var kb = bills.Where(b => b.ContractorId == k.Id).ToList();
            t.Rows.Add(k.Id, k.Name, k.Code, k.Gstin, k.Pan, k.Phone, kw.Count, kw.Sum(w => w.Value), kb.Sum(b => b.Gross), kb.Sum(b => b.Due), k.Address, k.Remarks);
        }
        return t;
    }

    protected override string Summary(DataTable t) => $"{t.DefaultView.Count} contractor(s)";

    protected override bool Edit(long? id)
    {
        var k = id is long i ? Host.Repo.Contractors().First(x => x.Id == i) : new Contractor();
        using var d = new EditDialog(id == null ? "New contractor" : k.Name);
        var name = d.AddText("Name", k.Name);
        var code = d.AddText("Code", k.Code, 100);
        var gstin = d.AddText("GSTIN", k.Gstin, 200);
        var pan = d.AddText("PAN", k.Pan, 150);
        var phone = d.AddText("Phone", k.Phone, 200);
        var address = d.AddMultiline("Address", k.Address);
        var remarks = d.AddMultiline("Remarks", k.Remarks);
        d.OnSave = () => Ui.Try(d, () =>
        {
            var g = gstin.Text.Trim().ToUpperInvariant();
            if (g.Length > 0 && g.Length != 15) throw new BillingException("A GSTIN has 15 characters.");
            var p = pan.Text.Trim().ToUpperInvariant();
            if (p.Length > 0 && p.Length != 10) throw new BillingException("A PAN has 10 characters.");
            k.Name = name.Text; k.Code = code.Text.Trim().ToUpperInvariant(); k.Gstin = g; k.Pan = p;
            k.Phone = phone.Text.Trim(); k.Address = address.Text.Trim(); k.Remarks = remarks.Text.Trim();
            Host.Repo.SaveContractor(k);
        });
        return d.ShowDialog(Host.Window) == DialogResult.OK;
    }

    protected override void Delete(long id) => Host.Repo.DeleteContractor(id);
}
