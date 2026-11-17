using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

sealed class ReportsView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly ComboBox _report = Ui.Combo(260);
    readonly DateTimePicker _from = Ui.Date(true);
    readonly DateTimePicker _to = Ui.Date(true);
    readonly DataGridView _grid = Ui.Grid();
    readonly Label _note = new() { Dock = DockStyle.Bottom, Height = 26, ForeColor = Ui.Muted, Font = Ui.Small, TextAlign = ContentAlignment.MiddleLeft };
    DataTable _table = new();

    public ReportsView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;
        _report.AccessibleName = "Report";
        _from.AccessibleName = "From bill date";
        _to.AccessibleName = "To bill date";
        _report.Items.AddRange(Reports.Names.Cast<object>().ToArray());
        _report.SelectedIndex = 0;

        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[]
        {
            Ui.Lbl("Report"), _report, Ui.Lbl("Bill date from"), _from, Ui.Lbl("to"), _to,
            Ui.Btn("Run", (_, _) => RefreshData(), primary: true),
            Ui.Btn("Export", (_, _) => Ui.ExportTables(_host.Window, _table.TableName, _table)),
            Ui.Btn("Print PDF", (_, _) => Ui.Preview(_host.Window, new TablePrinter(_table, _table.TableName, Subtitle()))),
        });
        Controls.Add(_grid);
        Controls.Add(_note);
        Controls.Add(bar);
        _report.SelectedIndexChanged += (_, _) => RefreshData();
        _grid.DataBindingComplete += (_, _) =>
        {
            foreach (DataGridViewRow r in _grid.Rows)
                if (r.Cells[0].Value as string == "Total") r.DefaultCellStyle.Font = Ui.Bold;
        };
    }

    string Subtitle()
    {
        var f = Ui.Value(_from); var t = Ui.Value(_to);
        var period = f == null && t == null ? "all bills" : $"bills dated {(f == null ? "from the start" : Fmt.Date(f))} to {(t == null ? "today" : Fmt.Date(t))}";
        return $"{_host.Repo.GetSetting("ProjectName") ?? "BF#3"} · {period} · cancelled bills left out";
    }

    public void RefreshData()
    {
        Ui.Try(_host.Window, () =>
        {
            _table = Reports.Run(_host.Repo, (string)_report.SelectedItem!, Ui.Value(_from), Ui.Value(_to));
            Ui.Bind(_grid, _table);
            _note.Text = $"{Subtitle()} · {_table.Rows.Count} row(s)";
        });
    }
}

sealed class MastersView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly DataGridView _acts = Ui.Grid();
    readonly DataGridView _rates = Ui.Grid();
    readonly TextBox _gst = new() { Width = 70, TextAlign = HorizontalAlignment.Right, AccessibleName = "Default GST percent" };
    readonly TextBox _ret = new() { Width = 70, TextAlign = HorizontalAlignment.Right, AccessibleName = "Default retention percent" };
    readonly TextBox _tds = new() { Width = 70, TextAlign = HorizontalAlignment.Right, AccessibleName = "Default TDS percent" };
    readonly TextBox _project = new() { Width = 200, AccessibleName = "Project name on printouts" };

    public MastersView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;

        var defaults = new Ui.Card { Dock = DockStyle.Top, Height = 110 };
        var df = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };
        df.Controls.AddRange(new[]
        {
            Ui.Field("Default GST % for new bills", _gst), Ui.Field("Default retention %", _ret), Ui.Field("Default TDS %", _tds),
            Ui.Field("Project name on printouts", _project),
        });
        df.Controls.Add(Ui.Btn("Save defaults", (_, _) => SaveDefaults(), primary: true));
        defaults.Controls.Add(df);
        defaults.Controls.Add(new Label { Text = "Defaults", Dock = DockStyle.Top, Font = Ui.Section, AutoSize = true });

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, BackColor = Ui.Back };
        split.Panel1.Controls.Add(Section("Activities", "BOM activities are billed in tonnes against the BOM and appear in BOM progress.",
            _acts, ("+ New activity", (_, _) => EditActivity(null)), ("Edit", (_, _) => EditActivity(Ui.SelectedId(_acts))), ("Delete", (_, _) => DeleteActivity())));
        split.Panel2.Controls.Add(Section("Rates", "Filled in automatically when an activity is chosen on a bill line. A work order's own rate comes before the default rate.",
            _rates, ("+ New rate", (_, _) => EditRate(null)), ("Edit", (_, _) => EditRate(Ui.SelectedId(_rates))), ("Delete", (_, _) => DeleteRate())));
        _acts.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditActivity(Ui.SelectedId(_acts)); };
        _rates.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) EditRate(Ui.SelectedId(_rates)); };

        Controls.Add(split);
        Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12 });
        Controls.Add(defaults);
        var splitSet = false;
        split.SizeChanged += (_, _) =>
        {
            if (splitSet || split.Width < 700) return;
            splitSet = true;
            try { split.SplitterDistance = split.Width * 4 / 10; }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
        };
    }

    static Control Section(string title, string hint, DataGridView grid, params (string Text, EventHandler Click)[] buttons)
    {
        var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 8, 0) };
        var bar = Ui.Bar();
        foreach (var (text, click) in buttons) bar.Controls.Add(Ui.Btn(text, click, primary: text.StartsWith('+')));
        p.Controls.Add(grid);
        p.Controls.Add(bar);
        p.Controls.Add(new Label { Text = hint, Dock = DockStyle.Top, AutoSize = false, Height = 36, ForeColor = Ui.Muted, Font = Ui.Small });
        p.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, AutoSize = true, Font = Ui.Section });
        return p;
    }

    public void RefreshData()
    {
        var r = _host.Repo;
        _gst.Text = r.GetSettingDecimal("DefaultGstPct", 18).ToString("0.##", Fmt.Culture);
        _ret.Text = r.GetSettingDecimal("DefaultRetentionPct", 0).ToString("0.##", Fmt.Culture);
        _tds.Text = r.GetSettingDecimal("DefaultTdsPct", 0).ToString("0.##", Fmt.Culture);
        _project.Text = r.GetSetting("ProjectName") ?? "BF#3";

        var a = new DataTable();
        a.Columns.Add("_Id", typeof(long)); a.Columns.Add("Activity"); a.Columns.Add("Billed against BOM"); a.Columns.Add("Default unit"); a.Columns.Add("Order", typeof(int));
        foreach (var x in r.Activities()) a.Rows.Add(x.Id, x.Name, x.IsBom ? "Yes (tonnes)" : "No", x.DefaultUnit, x.SortOrder);
        Ui.Bind(_acts, a);

        var t = new DataTable();
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("Work order"); t.Columns.Add("Activity"); t.Columns.Add("Unit"); t.Columns.Add("Rate ₹", typeof(decimal));
        foreach (var x in r.Rates()) t.Rows.Add(x.Id, x.WorkOrderNo, x.ActivityName, x.Unit, x.Amount);
        Ui.Bind(_rates, t);
    }

    void SaveDefaults()
    {
        if (!Fmt.TryParse(_gst.Text, out var g) || !Fmt.TryParse(_ret.Text, out var re) || !Fmt.TryParse(_tds.Text, out var td) ||
            new[] { g, re, td }.Any(v => v < 0 || v > 100))
        {
            Ui.Info(_host.Window, "Enter percentages between 0 and 100.");
            return;
        }
        Ui.Try(_host.Window, () =>
        {
            _host.Repo.SetSettingDecimal("DefaultGstPct", g);
            _host.Repo.SetSettingDecimal("DefaultRetentionPct", re);
            _host.Repo.SetSettingDecimal("DefaultTdsPct", td);
            _host.Repo.SetSetting("ProjectName", _project.Text.Trim().Length > 0 ? _project.Text.Trim() : "BF#3");
            Ui.Info(_host.Window, "Defaults saved. They apply to new bills.");
        });
    }

    void EditActivity(long? id)
    {
        var a = id is long i ? _host.Repo.Activities().First(x => x.Id == i) : new Activity { DefaultUnit = "nr" };
        using var d = new EditDialog(id == null ? "New activity" : a.Name);
        var name = d.AddText("Name", a.Name, 220);
        var bom = d.AddCheck("Billed against BOM (tonnes)", a.IsBom);
        var unit = d.AddCombo("Default unit", new object[] { "t", "kg", "nr", "set", "m", "m²", "LS" }, a.DefaultUnit, editable: true, width: 100);
        var order = d.AddNumber("Order in lists", a.SortOrder, "0");
        d.OnSave = () => Ui.Try(d, () =>
        {
            a.Name = name.Text; a.IsBom = bom.Checked; a.DefaultUnit = bom.Checked ? "t" : unit.Text.Trim();
            a.SortOrder = (int)EditDialog.Number(order);
            _host.Repo.SaveActivity(a);
        });
        if (d.ShowDialog(_host.Window) == DialogResult.OK) _host.DataChanged();
    }

    void DeleteActivity()
    {
        if (Ui.SelectedId(_acts) is not long id) return;
        if (Ui.Confirm(_host.Window, "Delete this activity and its rates?") && Ui.Try(_host.Window, () => _host.Repo.DeleteActivity(id))) _host.DataChanged();
    }

    sealed record WoItem(string Text, long Id) { public override string ToString() => Text; }

    void EditRate(long? id)
    {
        var r = id is long i ? _host.Repo.Rates().First(x => x.Id == i) : new Rate();
        var acts = _host.Repo.Activities();
        var wos = new List<WoItem> { new("(all work orders)", 0) };
        wos.AddRange(_host.Repo.WorkOrders().Select(w => new WoItem($"{w.WoNo} – {w.ContractorName}", w.Id)));
        using var d = new EditDialog(id == null ? "New rate" : "Rate");
        var wo = d.AddCombo("Work order", wos, wos.FirstOrDefault(w => w.Id == r.WorkOrderId), width: 420);
        var act = d.AddCombo("Activity", acts, acts.FirstOrDefault(a => a.Id == r.ActivityId), width: 220);
        var unit = d.AddCombo("Unit", new object[] { "t", "kg", "nr", "set", "m", "m²", "LS" }, r.Unit.Length > 0 ? r.Unit : "t", editable: true, width: 100);
        var rate = d.AddNumber("Rate ₹ per unit", r.Amount, "#,##,##0.00");
        d.OnSave = () => Ui.Try(d, () =>
        {
            r.WorkOrderId = (wo.SelectedItem as WoItem)?.Id ?? 0; r.ActivityId = (act.SelectedItem as Activity)?.Id ?? 0;
            r.Unit = unit.Text.Trim(); r.Amount = EditDialog.Number(rate);
            _host.Repo.SaveRate(r);
        });
        if (d.ShowDialog(_host.Window) == DialogResult.OK) _host.DataChanged();
    }

    void DeleteRate()
    {
        if (Ui.SelectedId(_rates) is not long id) return;
        if (Ui.Confirm(_host.Window, "Delete this rate?") && Ui.Try(_host.Window, () => _host.Repo.DeleteRate(id))) _host.DataChanged();
    }
}
