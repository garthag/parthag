using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>Chooses one or more BOM rows (drawings or items) for BOM bill lines.</summary>
sealed class BomPickerForm : Form
{
    readonly List<BomItem> _all;
    readonly TextBox _search = new() { Width = 300, PlaceholderText = "Search system, drawing or item", Margin = new Padding(0, 3, 8, 0), AccessibleName = "Search" };
    readonly ComboBox _system = Ui.Combo(240);
    readonly DataGridView _grid = Ui.Grid();
    public List<BomItem> Selected { get; } = new();

    public BomPickerForm(BillingRepository repo, bool single = false)
    {
        _all = repo.BomItems();
        Text = single ? "Choose a BOM drawing or item" : "Add BOM lines – select one or more rows (Ctrl or Shift + click)";
        Font = Ui.Base;
        BackColor = Ui.Back;
        Size = new Size(900, 620);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Padding = new Padding(12);
        _grid.MultiSelect = !single;
        _system.AccessibleName = "System";

        _system.Items.Add("All systems");
        _system.Items.AddRange(_all.Select(b => b.System).Distinct().Cast<object>().ToArray());
        _system.SelectedIndex = 0;

        var bar = Ui.Bar();
        bar.Controls.AddRange(new Control[] { _search, _system });
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var ok = Ui.Btn(single ? "Choose" : "Add lines", (_, _) => Accept(), primary: true);
        buttons.Controls.Add(ok);
        buttons.Controls.Add(Ui.Btn("Cancel", (_, _) => { DialogResult = DialogResult.Cancel; Close(); }));
        CancelButton = buttons.Controls[1] as Button;

        Controls.Add(_grid);
        Controls.Add(bar);
        Controls.Add(buttons);

        _search.TextChanged += (_, _) => Fill();
        _system.SelectedIndexChanged += (_, _) => Fill();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Accept(); };
        _grid.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; Accept(); } };
        _search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Down) { e.Handled = true; _grid.Focus(); } if (e.KeyCode == Keys.Enter) { e.Handled = true; Accept(); } };
        Fill();
        Shown += (_, _) => _search.Focus();
    }

    void Fill()
    {
        var s = _search.Text.Trim();
        var sys = _system.SelectedIndex > 0 ? _system.SelectedItem as string : null;
        var t = new DataTable();
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("System"); t.Columns.Add("Drawing"); t.Columns.Add("Item"); t.Columns.Add("BOM t", typeof(decimal));
        foreach (var b in _all)
        {
            if (sys != null && b.System != sys) continue;
            if (s.Length > 0 && !(b.System + " " + b.DrawingLabel + " " + b.Item).Contains(s, StringComparison.OrdinalIgnoreCase)) continue;
            t.Rows.Add(b.Id, b.System, b.DrawingLabel, b.Item, b.Tonnes);
        }
        Ui.Bind(_grid, t);
    }

    void Accept()
    {
        Selected.Clear();
        var rows = _grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).ToList();
        if (rows.Count == 0 && _grid.CurrentRow != null) rows.Add(_grid.CurrentRow);
        foreach (var r in rows)
            if (r.DataBoundItem is DataRowView v && _all.FirstOrDefault(b => b.Id == (long)v["_Id"]) is { } item)
                Selected.Add(item);
        if (Selected.Count == 0) return;
        DialogResult = DialogResult.OK;
        Close();
    }
}
