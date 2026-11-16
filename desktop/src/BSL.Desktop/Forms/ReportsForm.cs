using System.Data;
using BSL.Core.Excel;
using BSL.Core.Reports;

namespace BSL.Desktop.Forms;

/// <summary>Standard reports: preview in a grid, filter by package / agency / text, export to PDF or Excel.</summary>
public sealed class ReportsForm : UserControl
{
    readonly TreeView _list = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, Font = new Font("Arial", 10f) };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, BackgroundColor = SystemColors.Window,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, EnableHeadersVisualStyles = false
    };
    readonly ComboBox _pkg = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox _agency = new() { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox _group = new() { Width = 170, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox _search = new() { Width = 180, PlaceholderText = "search…" };
    readonly Label _title = new() { Dock = DockStyle.Top, Height = 46, Font = Ui.HeaderFont, ForeColor = Ui.Accent, Padding = new Padding(6, 4, 0, 0) };
    readonly DataView _view = new();
    ReportDef? _current;
    DataTable? _data;

    public ReportsForm()
    {
        Dock = DockStyle.Fill;
        foreach (var cat in ReportCatalog.All.GroupBy(r => r.Category))
        {
            var n = _list.Nodes.Add(cat.Key);
            foreach (var r in cat) n.Nodes.Add(new TreeNode(r.Name) { Tag = r, ToolTipText = r.Description });
            n.Expand();
        }
        _list.ShowNodeToolTips = true;
        _list.AfterSelect += (_, e) => { if (e.Node?.Tag is ReportDef r) Ui.Busy(this, () => Run(r)); };

        _grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Accent;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.DataSource = _view;

        _pkg.SelectedIndexChanged += (_, _) => Filter();
        _agency.SelectedIndexChanged += (_, _) => Filter();
        _search.TextChanged += (_, _) => Filter();

        // buttons first; the bar grows to a second line on narrow screens instead of hiding controls
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4) };
        bar.Controls.AddRange(
        [
            Ui.Button("Run", (_, _) => { if (_current != null) Ui.Busy(this, () => Run(_current)); }, 70),
            Ui.Button("PDF…", (_, _) => Ui.Busy(this, Pdf), 70),
            Ui.Button("Excel…", (_, _) => Ui.Busy(this, Excel), 70),
            L("Package:"), _pkg, L("Agency:"), _agency, L("Group by:"), _group, _search
        ]);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_grid); right.Controls.Add(bar); right.Controls.Add(_title);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        Ui.SplitterWhenSized(split, 300);
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);
        _title.Text = "Select a report on the left";
    }

    static Label L(string t) => new() { Text = t, AutoSize = true, Padding = new Padding(4, 7, 0, 0) };

    void Run(ReportDef r)
    {
        _current = r;
        _data = Program.Db.Query(r.Sql);
        _data.TableName = "Report";   // a DataView can only bind to a named table
        _grid.DataSource = null;
        _view.Table = _data;
        _view.RowFilter = "";
        _grid.DataSource = _view;
        _title.Text = r.Name + (r.Description != null ? "\n" : "");
        if (r.Description != null) _title.Text = $"{r.Name}  –  {r.Description}";
        Fill(_pkg, "Package"); Fill(_agency, "Agency");
        _group.Items.Clear();
        _group.Items.Add("(none)");
        foreach (DataColumn c in _data.Columns) if (c.DataType == typeof(string)) _group.Items.Add(c.ColumnName);
        _group.SelectedItem = r.GroupBy != null && _group.Items.Contains(r.GroupBy) ? r.GroupBy : "(none)";
        foreach (DataGridViewColumn c in _grid.Columns)
            if (_data.Columns[c.DataPropertyName]?.DataType is { } t && (t == typeof(decimal) || t == typeof(double)))
            {
                c.DefaultCellStyle.Format = c.HeaderText.Contains("Amount") || c.HeaderText.Contains("Rate") || c.HeaderText.Contains("Value") || c.HeaderText.Contains("Margin") ? "N2" : "#,##0.###";
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            else if (_data.Columns[c.DataPropertyName]?.DataType == typeof(DateTime)) c.DefaultCellStyle.Format = "dd-MM-yyyy";
        Filter();
    }

    void Fill(ComboBox cb, string col)
    {
        cb.Items.Clear();
        cb.Items.Add("(all)");
        if (_data?.Columns.Contains(col) == true)
            cb.Items.AddRange(_data.Rows.Cast<DataRow>().Select(r => r[col]?.ToString() ?? "").Where(s => s.Length > 0).Distinct().OrderBy(s => s).Cast<object>().ToArray());
        cb.SelectedIndex = 0;
        cb.Enabled = cb.Items.Count > 1;
    }

    void Filter()
    {
        if (_data == null) return;
        var parts = new List<string>();
        if (_pkg.SelectedIndex > 0) parts.Add($"[Package] = '{Esc((string)_pkg.SelectedItem!)}'");
        if (_agency.SelectedIndex > 0) parts.Add($"[Agency] = '{Esc((string)_agency.SelectedItem!)}'");
        var s = _search.Text.Trim();
        if (s.Length > 0)
        {
            var like = Esc(s).Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
            var cols = _data.Columns.Cast<DataColumn>().Where(c => c.DataType == typeof(string)).Select(c => $"[{c.ColumnName}] LIKE '%{like}%'").ToList();
            if (cols.Count > 0) parts.Add("(" + string.Join(" OR ", cols) + ")");
        }
        _view.RowFilter = string.Join(" AND ", parts);
    }

    static string Esc(string s) => s.Replace("'", "''");

    string Subtitle()
    {
        var f = new List<string>();
        if (_pkg.SelectedIndex > 0) f.Add("Package: " + _pkg.SelectedItem);
        if (_agency.SelectedIndex > 0) f.Add("Agency: " + _agency.SelectedItem);
        if (_search.Text.Trim().Length > 0) f.Add("Search: " + _search.Text.Trim());
        return (f.Count > 0 ? string.Join("  •  ", f) + "  •  " : "") + $"{_view.Count} rows";
    }

    void Pdf()
    {
        if (_current == null || _data == null) return;
        var path = Ui.SavePdf($"{_current.Name}_{DateTime.Now:yyyyMMdd}.pdf");
        if (path == null) return;
        var group = _group.SelectedItem as string is { } g && g != "(none)" ? g : null;
        var cols = PdfReport.AutoColumns(_data);
        PdfReport.Table(path, _current.Name, Subtitle(), _data, cols, group, _current.Landscape || cols.Count > 7,
                        _view.Cast<DataRowView>().Select(v => v.Row));
        Ui.OpenFile(path);
    }

    void Excel()
    {
        if (_current == null || _data == null) return;
        var path = Ui.Save($"{_current.Name}_{DateTime.Now:yyyyMMdd}.xlsx");
        if (path == null) return;
        var fields = _data.Columns.Cast<DataColumn>().Select(c => new BSL.Core.Metadata.FieldDef
        {
            Name = c.ColumnName, Caption = c.ColumnName, Width = c.DataType == typeof(string) ? 160 : 100,
            Kind = c.DataType == typeof(decimal) || c.DataType == typeof(double)
                ? (c.ColumnName.Contains("Amount") || c.ColumnName.Contains("Rate") || c.ColumnName.Contains("Value") || c.ColumnName.Contains("Margin")
                    ? BSL.Core.Metadata.FieldKind.Money : BSL.Core.Metadata.FieldKind.Decimal)
                : c.DataType == typeof(int) ? BSL.Core.Metadata.FieldKind.Int
                : c.DataType == typeof(DateTime) ? BSL.Core.Metadata.FieldKind.Date : BSL.Core.Metadata.FieldKind.Text
        }).ToList();
        TableExcel.Export(path, _current.Name, _data, fields, _view.Cast<DataRowView>().Select(v => v.Row), Subtitle(), includeIdColumn: false);
        Ui.OpenFile(path);
    }
}
