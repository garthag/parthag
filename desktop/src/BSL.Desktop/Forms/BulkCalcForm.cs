using System.Data;
using BSL.Core.Formula;
using BSL.Core.Metadata;
using BSL.Desktop.Controls;

namespace BSL.Desktop.Forms;

/// <summary>
/// "Use a formula" on a normal (stored) column: e.g. set Total Wt = L x W x T x 7.85 for all plates,
/// copy the agency rate from a contractor rule, fill Status with IIF(...). Changes are saved with Save.
/// </summary>
public sealed class BulkCalcForm : StyledForm
{
    public string ResultText { get; private set; } = "";
    readonly DataTable _table;
    readonly List<DataRow> _filtered, _selected;
    readonly ComboBox _target = new() { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly RadioButton _all = new() { Text = "All rows", AutoSize = true };
    readonly RadioButton _filt = new() { Text = "Filtered rows", AutoSize = true, Checked = true };
    readonly RadioButton _sel = new() { Text = "Selected rows", AutoSize = true };
    readonly TextBox _where = new() { Width = 420, PlaceholderText = "optional condition, e.g. [SectionStd] LIKE 'PL-%' AND [TotalWeightKg] IS NULL" };
    readonly FormulaBuilder _builder;
    readonly List<FieldDef> _targets;

    public BulkCalcForm(EntityDef e, List<FieldDef> fields, DataTable table, List<DataRow> filtered, List<DataRow> selected)
    {
        _table = table; _filtered = filtered; _selected = selected;
        Text = $"Calculate a column with a formula – {e.Caption}";
        Width = 1000; Height = 640; StartPosition = FormStartPosition.CenterParent;

        _targets = fields.Where(f => !f.ReadOnly && f.Kind is not (FieldKind.Lookup or FieldKind.Bool) && table.Columns.Contains(f.Name)
                                     && table.Columns[f.Name]!.Expression.Length == 0).ToList();
        _target.Items.AddRange(_targets.Select(f => (object)$"{f.Caption}  [{f.Name}]").ToArray());
        if (_target.Items.Count > 0) _target.SelectedIndex = 0;
        _sel.Text = $"Selected rows ({selected.Count})";
        _filt.Text = $"Filtered rows ({filtered.Count})";
        _all.Text = $"All rows ({table.Rows.Count})";
        _sel.Enabled = selected.Count > 0;

        _builder = new FormulaBuilder(table, filtered.FirstOrDefault() ?? (table.Rows.Count > 0 ? table.Rows[0] : null), null);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(8), WrapContents = true };
        top.Controls.AddRange([new Label { Text = "Write result into:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _target,
                               _filt, _all, _sel,
                               new Label { Text = "Only where:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _where]);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel);
        bar.Controls.AddRange([cancel, Ui.Button("Apply", (_, _) => Apply())]);
        CancelButton = cancel;
        Controls.Add(_builder); Controls.Add(top); Controls.Add(bar);
    }

    void Apply()
    {
        if (_target.SelectedIndex < 0) return;
        if (!_builder.Check()) { Ui.Error("The formula is not valid."); return; }
        var target = _targets[_target.SelectedIndex];
        IEnumerable<DataRow> rows = _all.Checked ? _table.Rows.Cast<DataRow>() : _sel.Checked ? _selected : _filtered;
        if (_where.Text.Trim().Length > 0)
        {
            try
            {
                var keep = _table.Select(_where.Text.Trim()).ToHashSet();
                rows = rows.Where(keep.Contains);
            }
            catch (Exception ex) { Ui.Error("Condition error: " + ex.Message); return; }
        }
        var list = rows.ToList();
        if (!Ui.Confirm($"Calculate '{target.Caption}' for {list.Count} rows?\n\n{_builder.Formula}")) return;
        try
        {
            var n = FormulaEngine.Apply(_table, target.Name, _builder.Formula, onlyRows: list);
            ResultText = $"{n} values of '{target.Caption}' changed by formula – press Save to store them.";
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }
}
