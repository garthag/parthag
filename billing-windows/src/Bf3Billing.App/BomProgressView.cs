using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

sealed class BomProgressView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly ComboBox _level = Ui.Combo(150);
    readonly ComboBox _system = Ui.Combo(240);
    readonly TextBox _search = new() { Width = 220, PlaceholderText = "Search", Margin = new Padding(0, 3, 8, 0), AccessibleName = "Search system, drawing or item" };
    readonly CheckBox _billed = new() { Text = "Only items with billing", AutoSize = true, Margin = new Padding(0, 6, 10, 0) };
    readonly DataGridView _grid = Ui.Grid();
    readonly Label _total = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft };
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    List<BomProgressRow> _rows = new();
    List<Activity> _acts = new();
    List<CustomField> _extra = new();
    Dictionary<long, BomItem> _bom = new();
    bool _loading;

    public BomProgressView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;
        _level.AccessibleName = "Level";
        _system.AccessibleName = "System";
        _level.Items.AddRange(new object[] { "Systems", "Drawings", "Items" });
        _level.SelectedIndex = 1;

        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[]
        {
            Ui.Lbl("Level"), _level, _system, _search, _billed,
            Ui.Btn("Reset", (_, _) => { _loading = true; _level.SelectedIndex = 1; _system.SelectedIndex = 0; _search.Text = ""; _billed.Checked = false; _loading = false; Reload(); }),
            Ui.Btn("Export", (_, _) => Ui.ExportTables(_host.Window, "BOM progress", Table())),
            Ui.Btn("Print PDF", (_, _) => Ui.Preview(_host.Window, new TablePrinter(Table(), "BOM progress", _total.Text))),
        });
        Controls.Add(_grid);
        Controls.Add(_total);
        Controls.Add(bar);

        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].DataBoundItem is DataRowView v && v["Scope"] as string == "Over")
                e.CellStyle!.ForeColor = Ui.Bad;
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowLines(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; ShowLines(); } };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Reload(); };
        _search.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _level.SelectedIndexChanged += (_, _) => { if (!_loading) Reload(); };
        _system.SelectedIndexChanged += (_, _) => { if (!_loading) Reload(); };
        _billed.CheckedChanged += (_, _) => { if (!_loading) Reload(); };
    }

    public void RefreshData()
    {
        _loading = true;
        var keep = _system.SelectedItem as string;
        _system.Items.Clear();
        _system.Items.Add("All systems");
        _system.Items.AddRange(_host.Repo.BomSystems().Cast<object>().ToArray());
        _system.SelectedItem = keep != null && _system.Items.Contains(keep) ? keep : "All systems";
        _acts = _host.Repo.Activities().Where(a => a.IsBom).ToList();
        _loading = false;
        Reload();
    }

    void Reload()
    {
        Ui.Try(_host.Window, () =>
        {
            var level = (BomLevel)_level.SelectedIndex;
            _rows = _host.Repo.BomProgress(level, _system.SelectedIndex > 0 ? _system.SelectedItem as string : null, _search.Text, _billed.Checked);
            _extra = level == BomLevel.Items ? _host.Repo.CustomFields(Entities.BomItem) : new();
            _bom = level == BomLevel.Items ? _host.Repo.BomItems().ToDictionary(b => b.Id) : new();
            Ui.Bind(_grid, Table(), after: g => g.Columns["Item"]!.Visible = level == BomLevel.Items);
            var all = _host.Repo.BomCount();
            _total.Text = all == 0
                ? "The BOM is empty. Import it on the Data tab (Import BOM from Excel)."
                : $"{_rows.Count} row(s) · {Fmt.Tonnes(_rows.Sum(r => r.BomTonnes))} t BOM · double-click a row for the bill lines behind it";
        });
    }

    DataTable Table()
    {
        var t = new DataTable("BOM progress");
        t.Columns.Add("_Row", typeof(int));
        t.Columns.Add("System"); t.Columns.Add("Drawing"); t.Columns.Add("Item"); t.Columns.Add("BOM t", typeof(decimal));
        foreach (var a in _acts) { t.Columns.Add($"{a.Name} t", typeof(decimal)); t.Columns.Add($"{a.Name} %", typeof(decimal)); }
        t.Columns.Add("Scope");
        var extraCols = _extra.Select(f => t.Columns.Contains(f.Name) ? f.Name + " (added)" : f.Name).ToList();
        foreach (var c in extraCols) t.Columns.Add(c);
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            var vals = new List<object> { i, r.System, r.Drawing, r.Item, r.BomTonnes };
            foreach (var a in _acts)
            {
                var q = r.Billed.GetValueOrDefault(a.Id);
                vals.Add(q);
                vals.Add(r.BomTonnes > 0 ? q * 100m / r.BomTonnes : 0m);
            }
            vals.Add(r.Over ? "Over" : "");
            if (_extra.Count > 0)
            {
                var x = r.BomItemIds.Count == 1 && _bom.TryGetValue(r.BomItemIds[0], out var b) ? Extras.Parse(b.Extra) : new();
                vals.AddRange(_extra.Select(f => (object)x.GetValueOrDefault(f.Name, "")));
            }
            t.Rows.Add(vals.ToArray());
        }
        return t;
    }

    void ShowLines()
    {
        if (_grid.CurrentRow?.DataBoundItem is not DataRowView v) return;
        var row = _rows[(int)v["_Row"]];
        var lines = _host.Repo.LineDetails(row.BomItemIds);
        var t = new DataTable("Bill lines");
        t.Columns.Add("Date", typeof(DateTime)); t.Columns.Add("Bill no."); t.Columns.Add("Status"); t.Columns.Add("Contractor"); t.Columns.Add("Work order");
        t.Columns.Add("#", typeof(int)); t.Columns.Add("Item"); t.Columns.Add("Activity"); t.Columns.Add("Qty", typeof(decimal));
        t.Columns.Add("Rate ₹", typeof(decimal)); t.Columns.Add("Amount ₹", typeof(decimal));
        foreach (var l in lines)
            t.Rows.Add(l.BillDate, l.BillNo, l.Status, l.Contractor, l.WorkOrder, l.LineNo, l.Item, l.Activity, l.Qty, l.Rate, l.Amount);
        var title = row.Item.Length > 0 ? $"{row.System} › {row.Drawing} › {row.Item}" : $"{row.System} › {row.Drawing}";
        using var d = new TableDialog("Bill lines behind " + title, $"{lines.Count} line(s) · BOM {Fmt.Tonnes(row.BomTonnes)} t", t);
        d.ShowDialog(_host.Window);
    }
}
