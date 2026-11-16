using System.Data;
using BSL.Core.Formula;

namespace BSL.Desktop.Controls;

/// <summary>
/// Formula editor: expression box + list of fields (incl. contractor detail, internal / external rates,
/// other user fields) + functions + ready-made templates + live check against the current record.
/// </summary>
public sealed class FormulaBuilder : UserControl
{
    readonly TextBox _formula = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), ScrollBars = ScrollBars.Vertical };
    readonly ListBox _fields = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox _fieldFilter = new() { Dock = DockStyle.Top, PlaceholderText = "find field…" };
    readonly ListBox _functions = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly ComboBox _templates = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly Label _result = new() { Dock = DockStyle.Bottom, Height = 40, ForeColor = Color.DarkGreen, Padding = new Padding(2) };
    readonly DataTable _schema;
    readonly DataRow? _sample;
    readonly string? _self;
    List<(string Name, string Caption)> _allFields = [];

    public string Formula { get => _formula.Text.Trim(); set => _formula.Text = value; }

    public FormulaBuilder(DataTable schema, DataRow? sample, string? selfColumn)
    {
        _schema = schema;
        _sample = sample;
        _self = selfColumn;
        Dock = DockStyle.Fill;

        _allFields = schema.Columns.Cast<DataColumn>()
            .Where(c => c.ColumnName != selfColumn && !c.ColumnName.StartsWith("__"))
            .Select(c => (c.ColumnName, string.IsNullOrEmpty(c.Caption) ? c.ColumnName : c.Caption)).ToList();
        FillFields("");
        _fieldFilter.TextChanged += (_, _) => FillFields(_fieldFilter.Text);
        _fields.DoubleClick += (_, _) => { if (_fields.SelectedItem is string s) Insert("[" + s.Split('\t')[0] + "]"); };
        _functions.Items.AddRange(FormulaEngine.Functions.Concat(["+  -  *  /  %", "=  <>  <  >  <=  >=", "AND  OR  NOT", "'text' + [Field]"]).Cast<object>().ToArray());
        _functions.DoubleClick += (_, _) => { if (_functions.SelectedItem is string s) Insert(s.Contains('(') ? s[..(s.IndexOf('(') + 1)] : " " + s.Split(' ')[0] + " "); };
        _templates.Items.Add("— insert a template —");
        foreach (var (n, f) in FormulaEngine.Templates) _templates.Items.Add($"{n}:  {f}");
        _templates.SelectedIndex = 0;
        _templates.SelectedIndexChanged += (_, _) =>
        {
            if (_templates.SelectedIndex > 0) _formula.Text = FormulaEngine.Templates[_templates.SelectedIndex - 1].Formula;
            _templates.SelectedIndex = 0;
        };
        _formula.TextChanged += (_, _) => Check();

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260, FixedPanel = FixedPanel.Panel1 };
        var left = new TabControl { Dock = DockStyle.Fill };
        var tf = new TabPage("Fields (double-click)"); tf.Controls.Add(_fields); tf.Controls.Add(_fieldFilter);
        var tfn = new TabPage("Functions"); tfn.Controls.Add(_functions);
        left.TabPages.AddRange([tf, tfn]);
        split.Panel1.Controls.Add(left);

        var help = new Label
        {
            Dock = DockStyle.Top, Height = 54, ForeColor = Color.DimGray,
            Text = "Use field names in [brackets]. Examples:  ([ExternalRate] - [InternalRate]) * [R1WeightTon]   •   " +
                   "IIF([TotalWeightKg] > 1000, 'Heavy', 'Light')   •   [ContractorCode] + ' / ' + [WorkOrderNo]   •   ISNULL([Qty], 1)"
        };
        split.Panel2.Controls.Add(_formula);
        split.Panel2.Controls.Add(_templates);
        split.Panel2.Controls.Add(help);
        split.Panel2.Controls.Add(_result);
        Controls.Add(split);
    }

    void FillFields(string filter)
    {
        _fields.BeginUpdate();
        _fields.Items.Clear();
        foreach (var (n, c) in _allFields.Where(x => filter.Length == 0 || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || x.Caption.Contains(filter, StringComparison.OrdinalIgnoreCase)))
            _fields.Items.Add(n == c ? n : $"{n}\t({c})");
        _fields.EndUpdate();
    }

    void Insert(string text)
    {
        var i = _formula.SelectionStart;
        _formula.Text = _formula.Text.Insert(i, text);
        _formula.SelectionStart = i + text.Length;
        _formula.Focus();
    }

    /// <summary>Validates; shows the value for the current record. Returns true when valid.</summary>
    public bool Check()
    {
        if (Formula.Length == 0) { _result.Text = ""; return false; }
        var err = FormulaEngine.Validate(_schema, Formula, _self);
        if (err != null) { _result.ForeColor = Color.DarkRed; _result.Text = "✖ " + err; return false; }
        _result.ForeColor = Color.DarkGreen;
        try
        {
            var v = _sample != null ? FormulaEngine.Evaluate(_sample, Formula) : null;
            _result.Text = "✔ Valid." + (_sample != null ? $"  Current record → {v ?? "(blank)"}" : "");
        }
        catch (Exception ex) { _result.Text = "✔ Valid syntax. (preview: " + ex.Message + ")"; }
        return true;
    }
}
