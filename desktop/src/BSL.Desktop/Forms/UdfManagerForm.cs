using System.Data;
using BSL.Core.Data;
using BSL.Core.Metadata;
using BSL.Desktop.Controls;

namespace BSL.Desktop.Forms;

/// <summary>Add / edit / delete user defined fields of one entity (Text, Number, Date, Yes/No, List, Formula).</summary>
public sealed class UdfManagerForm : Form
{
    readonly EntityDef _entity;
    readonly DataTable _schema;
    readonly UdfRepository _repo = new(Program.Db);
    readonly DataGridView _grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window };
    List<UdfDefinition> _defs = [];

    public UdfManagerForm(EntityDef entity, DataTable schema)
    {
        _entity = entity;
        _schema = schema;
        Text = $"User defined fields – {entity.Caption}";
        Width = 980; Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        var info = new Label
        {
            Dock = DockStyle.Top, Height = 44, Padding = new Padding(8), ForeColor = Color.DimGray,
            Text = "User defined fields are extra columns you add yourself. They appear in the table and the form, in Excel export / import and in PDF. " +
                   "A Formula field calculates live from other fields – including contractor details and internal / external rates."
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(4) };
        bar.Controls.AddRange(
        [
            Ui.Button("+ Add field", (_, _) => Edit(null)),
            Ui.Button("Edit", (_, _) => Edit(Selected)),
            Ui.Button("Delete", (_, _) => DeleteSel()),
            Ui.Button("Close", (_, _) => Close())
        ]);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(Selected); };
        Controls.Add(_grid); Controls.Add(info); Controls.Add(bar);
        Load += (_, _) => Fill();
    }

    UdfDefinition? Selected => _grid.CurrentRow?.Index is int i && i >= 0 && i < _defs.Count ? _defs[i] : null;

    void Fill()
    {
        _defs = _repo.GetDefinitions(_entity.Name, activeOnly: false);
        _grid.DataSource = _defs.Select(d => new
        {
            d.Caption, Key = d.FieldKey, Type = d.DataType.ToString(), Formula_or_List = d.DataType == UdfType.Formula ? d.Formula : d.ListValues,
            Decimals = d.DecimalPlaces, Required = d.IsRequired, InGrid = d.ShowInGrid, Order = d.SortOrder, Active = d.IsActive
        }).ToList();
        if (_grid.Columns.Count > 3) _grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
    }

    void Edit(UdfDefinition? d)
    {
        using var f = new UdfEditForm(_entity, _schema, d ?? new UdfDefinition { EntityName = _entity.Name, SortOrder = (_defs.Count + 1) * 10 },
                                      _defs.Where(x => x != d).Select(x => x.FieldKey).ToHashSet(StringComparer.OrdinalIgnoreCase));
        if (f.ShowDialog(this) == DialogResult.OK) { _repo.Save(f.Definition); Fill(); }
    }

    void DeleteSel()
    {
        if (Selected is not { } d) return;
        if (!Ui.Confirm($"Delete field '{d.Caption}' and all values stored in it?")) return;
        _repo.Delete(d.Id);
        Fill();
    }
}

public sealed class UdfEditForm : Form
{
    public UdfDefinition Definition { get; }
    readonly TextBox _caption = new() { Width = 300 };
    readonly TextBox _key = new() { Width = 300 };
    readonly ComboBox _type = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _list = new() { Width = 520, PlaceholderText = "values separated by ;  e.g.  Pending;In progress;Done" };
    readonly NumericUpDown _dec = new() { Width = 60, Minimum = 0, Maximum = 6 };
    readonly NumericUpDown _sort = new() { Width = 80, Minimum = 0, Maximum = 9999 };
    readonly CheckBox _req = new() { Text = "Required", AutoSize = true };
    readonly CheckBox _grid = new() { Text = "Show in table view", AutoSize = true, Checked = true };
    readonly CheckBox _active = new() { Text = "Active", AutoSize = true, Checked = true };
    readonly FormulaBuilder _builder;
    readonly Panel _formulaHost = new() { Dock = DockStyle.Fill };
    readonly HashSet<string> _otherKeys;
    bool _keyEdited;

    public UdfEditForm(EntityDef entity, DataTable schema, UdfDefinition def, HashSet<string> otherKeys)
    {
        Definition = def;
        _otherKeys = otherKeys;
        Text = (def.Id == 0 ? "New" : "Edit") + $" user defined field – {entity.Caption}";
        Width = 980; Height = 640; StartPosition = FormStartPosition.CenterParent;

        _type.Items.AddRange(Enum.GetNames<UdfType>());
        _caption.Text = def.Caption; _key.Text = def.FieldKey; _type.SelectedItem = def.DataType.ToString();
        _list.Text = def.ListValues ?? ""; _dec.Value = def.DecimalPlaces; _sort.Value = def.SortOrder;
        _req.Checked = def.IsRequired; _grid.Checked = def.ShowInGrid; _active.Checked = def.IsActive;
        _keyEdited = def.Id != 0;

        var sample = schema.Rows.Count > 0 ? schema.Rows[0] : null;
        _builder = new FormulaBuilder(schema, sample, def.FieldKey.Length > 0 ? def.FieldKey : null) { Formula = def.Formula ?? "" };
        _formulaHost.Controls.Add(_builder);

        _caption.TextChanged += (_, _) => { if (!_keyEdited) _key.Text = UdfDefinition.NormaliseKey(_caption.Text); };
        _key.KeyPress += (_, _) => _keyEdited = true;
        _type.SelectedIndexChanged += (_, _) => UpdateVisibility();

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 150, ColumnCount = 4, Padding = new Padding(8) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        void Row(int r, string l1, Control c1, string? l2 = null, Control? c2 = null)
        {
            top.Controls.Add(new Label { Text = l1, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, r); top.Controls.Add(c1, 1, r);
            if (l2 != null) { top.Controls.Add(new Label { Text = l2, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 2, r); top.Controls.Add(c2!, 3, r); }
        }
        Row(0, "Caption *", _caption, "Field key", _key);
        Row(1, "Type", _type, "Decimals", _dec);
        Row(2, "List values", _list, "Order", _sort);
        var flags = new FlowLayoutPanel { AutoSize = true }; flags.Controls.AddRange([_req, _grid, _active]);
        Row(3, "Options", flags);
        top.SetColumnSpan(_list, 1);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        var cancel = Ui.Button("Cancel", (_, _) => DialogResult = DialogResult.Cancel);
        bar.Controls.AddRange([cancel, Ui.Button("Save", (_, _) => SaveDef())]);
        CancelButton = cancel;
        Controls.Add(_formulaHost); Controls.Add(top); Controls.Add(bar);
        UpdateVisibility();
    }

    void UpdateVisibility()
    {
        var t = Enum.Parse<UdfType>((string)_type.SelectedItem!);
        _list.Enabled = t == UdfType.List;
        _formulaHost.Visible = t == UdfType.Formula;
        _dec.Enabled = t is UdfType.Number or UdfType.Formula;
        _req.Enabled = t != UdfType.Formula;
    }

    void SaveDef()
    {
        var t = Enum.Parse<UdfType>((string)_type.SelectedItem!);
        var key = UdfDefinition.NormaliseKey(_key.Text.Trim().Length > 0 ? _key.Text : _caption.Text);
        if (_caption.Text.Trim().Length == 0) { Ui.Error("Caption is required."); return; }
        if (_otherKeys.Contains(key)) { Ui.Error($"Field key {key} already exists."); return; }
        if (t == UdfType.List && _list.Text.Split(';', StringSplitOptions.RemoveEmptyEntries).Length == 0) { Ui.Error("Enter the list values separated by ;"); return; }
        if (t == UdfType.Formula && !_builder.Check()) { Ui.Error("The formula is not valid – see the message under the formula."); return; }
        Definition.Caption = _caption.Text.Trim();
        Definition.FieldKey = key;
        Definition.DataType = t;
        Definition.ListValues = t == UdfType.List ? _list.Text.Trim() : null;
        Definition.Formula = t == UdfType.Formula ? _builder.Formula : null;
        Definition.DecimalPlaces = (int)_dec.Value;
        Definition.SortOrder = (int)_sort.Value;
        Definition.IsRequired = _req.Checked && t != UdfType.Formula;
        Definition.ShowInGrid = _grid.Checked;
        Definition.IsActive = _active.Checked;
        DialogResult = DialogResult.OK;
    }
}
