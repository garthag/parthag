using System.Data;
using System.Globalization;
using BSL.Core.Data;
using BSL.Core.Excel;
using BSL.Core.Import;
using BSL.Core.Metadata;
using BSL.Core.Reports;
using BSL.Desktop.Forms;

namespace BSL.Desktop.Controls;

/// <summary>
/// One screen for any entity: TABLE view (grid, multi-row editing, filter, sort) and FORM view
/// (one record, grouped fields, related records) share the same BindingSource, so switching keeps the
/// current record. User defined fields and formula fields appear in both views.
/// </summary>
public sealed class EntityBrowser : UserControl
{
    public EntityDef Entity { get; }
    public string Title { get; }
    public event Action<EntityDef, string, int, string>? OpenRelated;   // child entity, fk column, id, title

    readonly string? _where;
    readonly (string, object?)[] _whereParams;
    readonly (string Column, int Value)? _fixedFk;

    DataTable _table = new();
    readonly BindingSource _bs = new();
    List<FieldDef> _fields = [];
    List<UdfDefinition> _udfs = [];
    readonly Dictionary<LookupDef, DataTable> _lookups = [];
    readonly HashSet<string> _hiddenCols = [];
    string _advancedFilter = "";

    readonly ToolStrip _tools = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(4, 2, 4, 2), ImageScalingSize = new Size(16, 16) };
    readonly ToolStripButton _btnGrid = new("▦ Table view") { CheckOnClick = false, Checked = true };
    readonly ToolStripButton _btnForm = new("▤ Form view") { CheckOnClick = false };
    readonly ToolStripTextBox _search = new() { Width = 180, ToolTipText = "Quick search in all text columns" };
    readonly ToolStripLabel _pos = new("0 / 0");
    readonly DataGridView _grid = new();
    readonly Panel _formHost = new() { Dock = DockStyle.Fill, Visible = false };
    readonly FlowLayoutPanel _formFields = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
    readonly TabControl _children = new() { Dock = DockStyle.Fill };
    readonly SplitContainer _formSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    readonly StatusStrip _status = new() { SizingGrip = false };
    readonly ToolStripStatusLabel _statusText = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _changes = new() { ForeColor = Color.DarkRed };
    readonly System.Windows.Forms.Timer _childTimer = new() { Interval = 350 };
    readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 300 };

    public EntityBrowser(EntityDef entity, string? title = null, string? where = null, (string, object?)[]? whereParams = null,
                         (string Column, int Value)? fixedFk = null)
    {
        Entity = entity;
        Title = title ?? entity.Caption;
        _where = where;
        _whereParams = whereParams ?? [];
        _fixedFk = fixedFk;
        Dock = DockStyle.Fill;
        BuildToolbar();
        BuildGrid();

        _formSplit.Panel1.Controls.Add(_formFields);
        _formSplit.Panel2.Controls.Add(_children);
        _formHost.Controls.Add(_formSplit);
        _status.Items.AddRange(new ToolStripItem[] { _statusText, _changes });

        Controls.Add(_grid);
        Controls.Add(_formHost);
        Controls.Add(_tools);
        Controls.Add(_status);

        _bs.PositionChanged += (_, _) => { UpdatePosition(); _childTimer.Stop(); _childTimer.Start(); };
        _bs.ListChanged += (_, _) => UpdatePosition();
        _childTimer.Tick += (_, _) => { _childTimer.Stop(); LoadChildren(); };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
        Load += (_, _) =>
        {
            try { _formSplit.SplitterDistance = Math.Max(200, (int)(Height * 0.62)); } catch (InvalidOperationException) { }
            Ui.Busy(this, () => Reload());
        };
    }

    // ================================================================== toolbar
    void BuildToolbar()
    {
        ToolStripButton B(string text, string tip, Action a, Keys keys = Keys.None)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip + (keys != Keys.None ? $"  ({keys})" : "") };
            b.Click += (_, _) => Ui.Busy(this, a);
            return b;
        }

        _btnGrid.Click += (_, _) => ShowGrid(true);
        _btnForm.Click += (_, _) => ShowGrid(false);
        _search.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };

        var excel = new ToolStripDropDownButton("Excel");
        excel.DropDownItems.Add("Export all rows…", null, (_, _) => Ui.Busy(this, () => ExportExcel(false)));
        excel.DropDownItems.Add("Export filtered rows…", null, (_, _) => Ui.Busy(this, () => ExportExcel(true)));
        excel.DropDownItems.Add(new ToolStripSeparator());
        excel.DropDownItems.Add("Import from Excel…", null, (_, _) => Ui.Busy(this, ImportExcel));
        excel.DropDownItems.Add("Download import template…", null, (_, _) => Ui.Busy(this, Template));

        var pdf = new ToolStripDropDownButton("PDF");
        pdf.DropDownItems.Add("List report (filtered rows)…", null, (_, _) => Ui.Busy(this, () => ListPdf()));
        pdf.DropDownItems.Add("Record card (current record)…", null, (_, _) => Ui.Busy(this, RecordPdf));

        var fields = new ToolStripDropDownButton("Fields && formulas");
        fields.DropDownItems.Add("User defined fields…", null, (_, _) => ManageUdfs());
        fields.DropDownItems.Add("Calculate a column with a formula…", null, (_, _) => BulkCalculate());
        fields.DropDownItems.Add(new ToolStripSeparator());
        fields.DropDownItems.Add("Show / hide columns…", null, (_, _) => ChooseColumns());
        fields.DropDownItems.Add("Advanced filter…", null, (_, _) => AdvancedFilter());
        fields.DropDownItems.Add("Clear filters", null, (_, _) => { _advancedFilter = ""; _search.Text = ""; ApplyFilter(); });

        _tools.Items.AddRange(new ToolStripItem[]
        {
            B("＋ New", "Add a record", AddNew, Keys.Control | Keys.N),
            B("💾 Save", "Save all changes", Save, Keys.Control | Keys.S),
            B("✖ Delete", "Delete selected record(s)", Delete),
            B("⟳ Refresh", "Reload from database", () => Reload(true), Keys.F5),
            new ToolStripSeparator(), _btnGrid, _btnForm, new ToolStripSeparator(),
            B("⏮", "First", () => _bs.MoveFirst()), B("◀", "Previous", () => _bs.MovePrevious()), _pos,
            B("▶", "Next", () => _bs.MoveNext()), B("⏭", "Last", () => _bs.MoveLast()),
            new ToolStripSeparator(), new ToolStripLabel("Search:"), _search,
            new ToolStripSeparator(), excel, pdf, fields
        });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.S: Ui.Busy(this, Save); return true;
            case Keys.Control | Keys.N: Ui.Busy(this, AddNew); return true;
            case Keys.F5: Ui.Busy(this, () => Reload(true)); return true;
            case Keys.Control | Keys.F: _search.Focus(); return true;
            case Keys.F2: ShowGrid(!_grid.Visible); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ================================================================== data
    public bool HasChanges => _table.GetChanges() != null;

    public void Reload(bool askIfDirty = false)
    {
        if (askIfDirty && HasChanges && !Ui.Confirm("Discard unsaved changes and reload?")) return;
        int? currentId = Current?[Entity.Key] as int?;
        _udfs = Program.Repo.Udfs(Entity);
        _fields = Program.Repo.AllFields(Entity, _udfs);
        _lookups.Clear();
        _table = Program.Repo.Load(Entity, _where, _whereParams);
        _table.RowChanged += (_, _) => UpdateChanges();
        _table.RowDeleted += (_, _) => UpdateChanges();
        _bs.DataSource = _table;
        BuildGridColumns();
        BuildForm();
        ApplyFilter();
        if (currentId != null) { var i = _bs.Find(Entity.Key, currentId); if (i >= 0) _bs.Position = i; }
        UpdateChanges();
        _statusText.Text = $"{Entity.Caption}: {_table.Rows.Count:N0} records loaded.";
    }

    DataRow? Current => _bs.Current is DataRowView v ? v.Row : null;

    DataTable LookupTable(LookupDef lk)
    {
        if (_lookups.TryGetValue(lk, out var t)) return t;
        t = Program.Repo.LookupData(lk);
        var none = t.NewRow();
        none[1] = "(none)";
        t.Rows.InsertAt(none, 0);
        return _lookups[lk] = t;
    }

    string Display(FieldDef? f, object? v)
    {
        if (v is null or DBNull) return "";
        if (f?.Kind == FieldKind.Lookup)
        {
            var t = LookupTable(f.Lookup!);
            var r = t.Rows.Cast<DataRow>().FirstOrDefault(x => x[0] is not DBNull && Convert.ToInt32(x[0]) == Convert.ToInt32(v));
            return r?[1]?.ToString() ?? v.ToString()!;
        }
        var dp = f?.IsUdf == true ? _udfs.FirstOrDefault(u => u.FieldKey == f.Name)?.DecimalPlaces ?? 2 : 2;
        return v switch
        {
            DateTime d => d.ToString("dd-MM-yyyy"),
            bool b => b ? "Yes" : "No",
            decimal m when f?.Kind == FieldKind.Money => m.ToString("#,##,##0.00", new CultureInfo("en-IN")),
            decimal m => m.ToString("#,##0." + new string('#', Math.Max(dp, 3)), CultureInfo.InvariantCulture),
            double d => d.ToString("#,##0." + new string('#', Math.Max(dp, 3)), CultureInfo.InvariantCulture),
            _ => v.ToString()!
        };
    }

    void UpdatePosition() => _pos.Text = $"{(_bs.Count == 0 ? 0 : _bs.Position + 1)} / {_bs.Count}";

    void UpdateChanges()
    {
        var ch = _table.GetChanges();
        _changes.Text = ch == null ? "" : $"● {ch.Rows.Count} unsaved change(s) – press Save (Ctrl+S)";
    }

    void Save()
    {
        _grid.EndEdit();
        Validate();
        _bs.EndEdit();
        if (!HasChanges) { _statusText.Text = "Nothing to save."; return; }
        var (i, u, d) = Program.Repo.Save(Entity, _table);
        Reload();
        _statusText.Text = $"Saved: {i} added, {u} updated, {d} deleted.";
    }

    void AddNew()
    {
        if (!Entity.WritableFields.Any()) { _statusText.Text = "This list is read only."; return; }
        var v = (DataRowView)_bs.AddNew()!;
        foreach (var f in _fields.Where(f => !f.ReadOnly))
        {
            if (f.Kind == FieldKind.Bool && f.Name is "IsActive") v[f.Name] = true;
            else if (f.Kind == FieldKind.Bool && !f.IsUdf) v[f.Name] = false;
            if (f.Name == "NoOfSets") v[f.Name] = 1;
            if (f.Name == "Status" && f.ChoiceCategory != null && Program.Repo.Choices(f.ChoiceCategory).FirstOrDefault() is { } st) v[f.Name] = st;
            if (f.Name == "ContractorType") v[f.Name] = "Executing Agency";
        }
        if (_fixedFk is { } fk && _table.Columns.Contains(fk.Column)) v[fk.Column] = fk.Value;
        _bs.EndEdit();
        _bs.Position = _bs.Find(Entity.Key, v[Entity.Key]);
        ShowGrid(false);
        _formFields.Controls.OfType<GroupBox>().FirstOrDefault()?.Controls[0].Controls.OfType<Control>()
                   .FirstOrDefault(c => c is TextBox { ReadOnly: false } or ComboBox)?.Focus();
    }

    void Delete()
    {
        var rows = _grid.Visible && _grid.SelectedRows.Count > 0
            ? _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (r.DataBoundItem as DataRowView)?.Row).OfType<DataRow>().ToList()
            : Current is { } c ? [c] : [];
        if (rows.Count == 0) return;
        var title = rows.Count == 1 ? Display(Entity.Field(Entity.TitleField), rows[0][Entity.TitleField]) : $"{rows.Count} records";
        if (!Ui.Confirm($"Delete {title}?\nThis is saved to the database immediately.")) return;
        foreach (var r in rows) r.Delete();
        try { Save(); }
        catch { _table.RejectChanges(); throw; }
    }

    // ================================================================== filter / search
    void ApplyFilter()
    {
        var parts = new List<string>();
        var s = _search.Text.Trim();
        if (s.Length > 0)
        {
            var esc = s.Replace("'", "''").Replace("[", "[[]").Replace("*", "[*]").Replace("%", "[%]");
            var cols = _table.Columns.Cast<DataColumn>().Where(c => c.DataType == typeof(string) && c.Expression.Length == 0).Select(c => c.ColumnName).ToList();
            // also search lookup display text: match ids whose display contains the text
            foreach (var f in _fields.Where(f => f.Kind == FieldKind.Lookup && _table.Columns.Contains(f.Name)))
            {
                var ids = LookupTable(f.Lookup!).Rows.Cast<DataRow>()
                    .Where(r => r[0] is not DBNull && (r[1]?.ToString() ?? "").Contains(s, StringComparison.OrdinalIgnoreCase))
                    .Select(r => Convert.ToInt32(r[0])).Take(500).ToList();
                if (ids.Count > 0) parts.Add($"[{f.Name}] IN ({string.Join(",", ids)})");
            }
            var textParts = cols.Select(c => $"[{c}] LIKE '%{esc}%'").ToList();
            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out _) && _table.Columns.Contains(Entity.Key))
                textParts.Add($"Convert([{Entity.Key}], 'System.String') = '{esc}'");
            var anyOf = textParts.Concat(parts).ToList();
            parts.Clear();
            if (anyOf.Count > 0) parts.Add("(" + string.Join(" OR ", anyOf) + ")");
        }
        if (_advancedFilter.Length > 0) parts.Add("(" + _advancedFilter + ")");
        try { _bs.Filter = string.Join(" AND ", parts); }
        catch (Exception ex) { _statusText.Text = "Filter error: " + ex.Message; _advancedFilter = ""; _bs.Filter = ""; }
        UpdatePosition();
        _statusText.Text = _bs.Filter?.Length > 0 ? $"Filtered: {_bs.Count:N0} of {_table.Rows.Count:N0} records" : $"{_table.Rows.Count:N0} records";
    }

    void AdvancedFilter()
    {
        var help = "Filter with field names in [brackets], e.g.\n" +
                   "  [TotalWeightKg] > 1000 AND [MaterialGrade] LIKE 'IS 2062%'\n" +
                   "  [Status] = 'IFC' OR [Revision] = 'R2'\n" +
                   "  [U_FabStatus] = 'Welding'\n\nFields: " +
                   string.Join(", ", _table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Take(60));
        var f = InputDialog.Show("Advanced filter", help, _advancedFilter, multiline: true);
        if (f == null) return;
        _advancedFilter = f.Trim();
        ApplyFilter();
    }

    // ================================================================== grid view
    void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToOrderColumns = true;
        _grid.SelectionMode = DataGridViewSelectionMode.RowHeaderSelect;
        _grid.RowHeadersWidth = 28;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Ui.Accent;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(204, 228, 247);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.DataSource = _bs;
        _grid.DataError += (_, e) =>
        {
            _statusText.Text = $"Invalid value in '{_grid.Columns[e.ColumnIndex].HeaderText}': {e.Exception?.Message}";
            e.ThrowException = false;
        };
        _grid.CellParsing += (_, e) =>
        {
            if (_grid.Columns[e.ColumnIndex].Tag is FieldDef { Kind: FieldKind.Date } && e.Value is string s)
            {
                if (string.IsNullOrWhiteSpace(s)) { e.Value = DBNull.Value; e.ParsingApplied = true; }
                else if (ExcelUtil.ToDate(s.Trim()) is { } d) { e.Value = d; e.ParsingApplied = true; }
            }
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].ReadOnly) ShowGrid(false); };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && _grid.SelectedRows.Count > 0 && !_grid.IsCurrentCellInEditMode) { Ui.Busy(this, Delete); e.Handled = true; }
        };
    }

    void BuildGridColumns()
    {
        _grid.Columns.Clear();
        var idCol = new DataGridViewTextBoxColumn
        {
            DataPropertyName = Entity.Key, HeaderText = "Id", Width = 50, ReadOnly = true, Frozen = false,
            DefaultCellStyle = { BackColor = Ui.ReadOnlyBack, ForeColor = Color.Gray }
        };
        _grid.Columns.Add(idCol);
        foreach (var f in _fields.Where(f => _table.Columns.Contains(f.Name)))
        {
            DataGridViewColumn col;
            switch (f.Kind)
            {
                case FieldKind.Lookup when !f.ReadOnly:
                    col = new DataGridViewComboBoxColumn
                    {
                        DataSource = LookupTable(f.Lookup!), ValueMember = LookupTable(f.Lookup!).Columns[0].ColumnName,
                        DisplayMember = LookupTable(f.Lookup!).Columns[1].ColumnName,
                        DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, FlatStyle = FlatStyle.Flat
                    };
                    break;
                case FieldKind.Choice when !f.ReadOnly:
                {
                    var cb = new DataGridViewComboBoxColumn { DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing, FlatStyle = FlatStyle.Flat };
                    var items = Program.Repo.Choices(f.ChoiceCategory!)
                        .Concat(_table.Rows.Cast<DataRow>().Select(r => r[f.Name]).OfType<string>())
                        .Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();
                    cb.Items.AddRange(items);
                    col = cb;
                    break;
                }
                case FieldKind.Bool:
                    col = new DataGridViewCheckBoxColumn { ThreeState = false };
                    break;
                default:
                    col = new DataGridViewTextBoxColumn();
                    break;
            }
            col.Name = f.Name;
            col.Tag = f;
            col.DataPropertyName = f.Name;
            col.HeaderText = f.Caption + (f.Required ? " *" : "") + (f.Formula != null ? " ƒ" : "");
            col.Width = f.Width;
            col.ReadOnly = f.ReadOnly;
            col.SortMode = DataGridViewColumnSortMode.Automatic;
            col.Visible = !f.HiddenInGrid && !_hiddenCols.Contains(f.Name);
            if (f.ReadOnly) col.DefaultCellStyle.BackColor = Ui.ReadOnlyBack;
            if (f.IsUdf) col.HeaderCell.Style.BackColor = Color.FromArgb(84, 130, 53);
            if (f.IsNumeric || (f.IsUdf && f.Formula != null))
            {
                col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                var dp = f.IsUdf ? _udfs.FirstOrDefault(u => u.FieldKey == f.Name)?.DecimalPlaces ?? 2 : -1;
                col.DefaultCellStyle.Format = f.Kind == FieldKind.Money ? "N2" : f.Kind == FieldKind.Int ? "0" : dp >= 0 ? "N" + dp : "#,##0.###";
            }
            if (f.Kind == FieldKind.Date) col.DefaultCellStyle.Format = "dd-MM-yyyy";
            if (f.Kind == FieldKind.Memo) col.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            _grid.Columns.Add(col);
        }
    }

    void ShowGrid(bool grid)
    {
        if (!grid && _bs.Count == 0) { _statusText.Text = "No record to show – use New to add one."; }
        _grid.EndEdit();
        _grid.Visible = grid;
        _formHost.Visible = !grid;
        _btnGrid.Checked = grid;
        _btnForm.Checked = !grid;
        if (!grid) LoadChildren();
    }

    void ChooseColumns()
    {
        using var f = new Form { Text = "Show / hide columns", Width = 360, Height = 520, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.SizableToolWindow };
        var list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        var cols = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Tag is FieldDef).ToList();
        foreach (var c in cols) list.Items.Add(c.HeaderText, c.Visible);
        var ok = Ui.Button("OK", (_, _) => f.DialogResult = DialogResult.OK);
        var panel = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.RightToLeft };
        panel.Controls.Add(ok);
        f.Controls.Add(list); f.Controls.Add(panel);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        for (int i = 0; i < cols.Count; i++)
        {
            cols[i].Visible = list.GetItemChecked(i);
            if (cols[i].Visible) _hiddenCols.Remove(cols[i].Name); else _hiddenCols.Add(cols[i].Name);
        }
    }

    // ================================================================== form view
    void BuildForm()
    {
        _formFields.SuspendLayout();
        static void Unbind(Control c) { c.DataBindings.Clear(); foreach (Control k in c.Controls) Unbind(k); }
        foreach (var c in _formFields.Controls.Cast<Control>().ToList()) { Unbind(c); c.Dispose(); }
        _formFields.Controls.Clear();
        var width = Math.Max(760, _formFields.ClientSize.Width - 40);

        var title = new Label { Text = Entity.Caption, Font = Ui.HeaderFont, ForeColor = Ui.Accent, AutoSize = true, Margin = new Padding(3, 3, 3, 8) };
        _formFields.Controls.Add(title);

        foreach (var g in _fields.Where(f => _table.Columns.Contains(f.Name)).GroupBy(f => f.Group))
        {
            var box = new GroupBox { Text = g.Key, Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8), Margin = new Padding(3, 3, 3, 10) };
            var tl = new TableLayoutPanel { ColumnCount = 4, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
            tl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            int i = 0;
            foreach (var f in g)
            {
                var lbl = new Label
                {
                    Text = f.Caption + (f.Required ? " *" : "") + (f.Formula != null ? "  ƒ" : ""), AutoSize = false, Width = 155, Height = 24,
                    TextAlign = ContentAlignment.MiddleLeft, ForeColor = f.ReadOnly ? Color.DimGray : SystemColors.ControlText,
                    Font = f.Required ? new Font(Font, FontStyle.Bold) : Font
                };
                if (f.Formula != null) new ToolTip().SetToolTip(lbl, "Formula: " + f.Formula);
                var ctl = CreateEditor(f);
                bool wide = f.Kind == FieldKind.Memo;
                if (wide && i % 2 == 1) i++;      // memo starts on a new row and spans the row
                tl.Controls.Add(lbl, (i % 2) * 2, i / 2);
                tl.Controls.Add(ctl, (i % 2) * 2 + 1, i / 2);
                if (wide) { tl.SetColumnSpan(ctl, 3); i += 2; } else i++;
            }
            box.Controls.Add(tl);
            _formFields.Controls.Add(box);
        }
        _formFields.ResumeLayout();
        _formFields.Resize -= FormFieldsResize;
        _formFields.Resize += FormFieldsResize;

        _children.TabPages.Clear();
        foreach (var (child, fk) in Entity.Children)
        {
            var def = EntityCatalog.Get(child);
            var page = new TabPage(def.Caption) { Tag = (def, fk) };
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.None, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
            };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34 };
            bar.Controls.Add(Ui.Button("Open / edit in new tab", (_, _) =>
            {
                if (Current?[Entity.Key] is int id && id > 0)
                    OpenRelated?.Invoke(def, fk, id, $"{def.Caption} – {Display(Entity.Field(Entity.TitleField), Current[Entity.TitleField])}");
            }, 170));
            var info = new Label { AutoSize = true, Padding = new Padding(6, 8, 0, 0), ForeColor = Color.DimGray };
            bar.Controls.Add(info);
            page.Controls.Add(grid);
            page.Controls.Add(bar);
            _children.TabPages.Add(page);
        }
        _formSplit.Panel2Collapsed = Entity.Children.Count == 0;
    }

    void FormFieldsResize(object? sender, EventArgs e)
    {
        var w = Math.Max(760, _formFields.ClientSize.Width - 40);
        foreach (var gb in _formFields.Controls.OfType<GroupBox>()) gb.Width = w;
    }

    Control CreateEditor(FieldDef f)
    {
        var mode = f.ReadOnly ? DataSourceUpdateMode.Never : DataSourceUpdateMode.OnValidation;
        Control c;
        switch (f.Kind)
        {
            case FieldKind.Lookup:
            {
                var lt = LookupTable(f.Lookup!);
                var cb = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                    AutoCompleteSource = AutoCompleteSource.ListItems, DataSource = new DataView(lt),   // own view: own currency manager
                    DisplayMember = lt.Columns[1].ColumnName, ValueMember = lt.Columns[0].ColumnName, Enabled = !f.ReadOnly
                };
                cb.DataBindings.Add(new Binding("SelectedValue", _bs, f.Name, true, mode, DBNull.Value));
                c = cb;
                break;
            }
            case FieldKind.Choice:
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
                cb.Items.AddRange(Program.Repo.Choices(f.ChoiceCategory!).Cast<object>().ToArray());
                cb.DataBindings.Add(new Binding("Text", _bs, f.Name, true, mode, "") { DataSourceNullValue = DBNull.Value });
                cb.Enabled = !f.ReadOnly;
                c = cb;
                break;
            }
            case FieldKind.Bool:
            {
                var ch = new CheckBox { AutoSize = true, Enabled = !f.ReadOnly };
                ch.DataBindings.Add(new Binding("Checked", _bs, f.Name, true, DataSourceUpdateMode.OnPropertyChanged, false));
                c = ch;
                break;
            }
            default:
            {
                var tb = new TextBox { ReadOnly = f.ReadOnly, BackColor = f.ReadOnly ? Ui.ReadOnlyBack : SystemColors.Window };
                if (f.Kind == FieldKind.Memo) { tb.Multiline = true; tb.Height = 52; tb.ScrollBars = ScrollBars.Vertical; }
                if (f.IsNumeric || f.Formula != null) tb.TextAlign = HorizontalAlignment.Right;
                if (f.MaxLength > 0 && !f.ReadOnly) tb.MaxLength = f.MaxLength;
                var dp = f.IsUdf ? _udfs.FirstOrDefault(u => u.FieldKey == f.Name)?.DecimalPlaces ?? 2 : 3;
                var fmt = f.Kind switch
                {
                    FieldKind.Money => "N2", FieldKind.Int => "0", FieldKind.Decimal => f.Formula != null ? "N" + dp : "0.###",
                    FieldKind.Date => "dd-MM-yyyy", _ => ""
                };
                var b = new Binding("Text", _bs, f.Name, true, mode, "", fmt) { DataSourceNullValue = DBNull.Value };
                if (f.Kind == FieldKind.Date)
                {
                    b.Parse += (_, e) =>
                    {
                        var s = e.Value as string;
                        e.Value = string.IsNullOrWhiteSpace(s) ? DBNull.Value : ExcelUtil.ToDate(s!.Trim()) ?? (DateTime.TryParse(s, out var d) ? d : (object)DBNull.Value);
                    };
                    tb.PlaceholderText = "dd-mm-yyyy";
                }
                else if (f.IsNumeric && !f.ReadOnly)
                {
                    b.Parse += (_, e) =>
                    {
                        var s = (e.Value as string)?.Replace(",", "").Trim();
                        if (string.IsNullOrEmpty(s)) { e.Value = DBNull.Value; return; }
                        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                        {
                            _statusText.Text = $"'{e.Value}' is not a number ({f.Caption}).";
                            e.Value = DBNull.Value;
                            return;
                        }
                        e.Value = f.Kind == FieldKind.Int ? (object)(int)Math.Round(d) : d;
                    };
                }
                tb.DataBindings.Add(b);
                c = tb;
                break;
            }
        }
        c.Dock = DockStyle.Fill;
        c.Margin = new Padding(3, 3, 12, 3);
        return c;
    }

    void LoadChildren()
    {
        if (!_formHost.Visible || _children.TabPages.Count == 0) return;
        var id = Current?[Entity.Key] as int?;
        foreach (TabPage page in _children.TabPages)
        {
            var (def, fk) = ((EntityDef, string))page.Tag!;
            var grid = page.Controls.OfType<DataGridView>().First();
            var info = page.Controls.OfType<FlowLayoutPanel>().First().Controls.OfType<Label>().First();
            if (id is not > 0) { grid.DataSource = null; info.Text = "Save the record first."; page.Text = def.Caption; continue; }
            try
            {
                var t = Program.Db.Query($"SELECT TOP 2000 * FROM dbo.{def.View} WHERE {fk} = @id ORDER BY {def.OrderBy}", ("@id", id));
                grid.DataSource = t;
                foreach (DataGridViewColumn col in grid.Columns)
                {
                    var f = def.Field(col.DataPropertyName);
                    col.HeaderText = f?.Caption ?? col.HeaderText;
                    col.Visible = f != null && f.Kind != FieldKind.Lookup && f.Kind != FieldKind.Memo || col.DataPropertyName == "Id";
                    if (f?.IsNumeric == true) { col.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; col.DefaultCellStyle.Format = f.Kind == FieldKind.Money ? "N2" : "#,##0.###"; }
                    if (f?.Kind == FieldKind.Date) col.DefaultCellStyle.Format = "dd-MM-yyyy";
                    col.Width = f?.Width ?? 50;
                }
                page.Text = $"{def.Caption} ({t.Rows.Count})";
                info.Text = t.Rows.Count == 2000 ? "first 2000 rows" : "";
            }
            catch (Exception ex) { info.Text = ex.Message; }
        }
    }

    // ================================================================== Excel / PDF
    IEnumerable<DataRow> FilteredRows() => _bs.List.Cast<DataRowView>().Select(v => v.Row);

    List<FieldDef> VisibleFields()
    {
        var order = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible && c.Tag is FieldDef).OrderBy(c => c.DisplayIndex).Select(c => (FieldDef)c.Tag!).ToList();
        return order.Count > 0 ? order : _fields;
    }

    void ExportExcel(bool filteredOnly)
    {
        var path = Ui.Save($"{Entity.Name}_{DateTime.Now:yyyyMMdd}.xlsx");
        if (path == null) return;
        TableExcel.Export(path, Title, _table, VisibleFields(), filteredOnly ? FilteredRows() : null,
            filteredOnly && !string.IsNullOrEmpty(_bs.Filter) ? $"Filter: {_search.Text} {_advancedFilter}".Trim() : null);
        Ui.OpenFile(path);
    }

    void Template()
    {
        var path = Ui.Save($"{Entity.Name}_template.xlsx");
        if (path == null) return;
        TableExcel.Template(path, Entity, _fields);
        Ui.OpenFile(path);
    }

    void ImportExcel()
    {
        var path = Ui.OpenExcel($"Import {Entity.Caption} from Excel");
        if (path == null) return;
        var sheets = TableExcel.SheetNames(path);
        var sheet = sheets.Count == 1 ? sheets[0] : PickDialog.Show("Select sheet", "Which sheet contains the data?", sheets);
        if (sheet == null) return;
        var src = TableExcel.Read(path, sheet);
        var res = TableImporter.Merge(_table, _fields, src, Program.Repo.LookupData);
        ApplyFilter();
        UpdateChanges();
        var msg = $"{res.Added} rows added, {res.Updated} updated, {res.Skipped} skipped.\nNothing is saved yet – check the rows and press Save.\n\n" +
                  string.Join("\n", res.Messages.Take(25)) + (res.Messages.Count > 25 ? $"\n… {res.Messages.Count - 25} more" : "");
        Ui.Info(msg);
    }

    void ListPdf()
    {
        var fields = VisibleFields().Where(f => f.Kind != FieldKind.Memo).ToList();
        var groupOptions = new List<string> { "(no grouping)" };
        groupOptions.AddRange(fields.Select(f => f.Caption));
        var defaultGroup = Entity.ReportGroupBy != null ? Entity.Field(Entity.ReportGroupBy)?.Caption : null;
        var pick = PickDialog.Show("PDF list report", "Group rows by:", groupOptions, defaultGroup ?? groupOptions[0]);
        if (pick == null) return;
        var path = Ui.SavePdf($"{Entity.Name}_{DateTime.Now:yyyyMMdd}.pdf");
        if (path == null) return;

        // build a plain table with display values (lookups resolved, formulas evaluated)
        var t = new DataTable();
        var cols = new List<ReportColumn>();
        foreach (var f in fields)
        {
            var textFormula = f.Formula != null && _table.Rows.Cast<DataRow>().Select(r => r[f.Name]).FirstOrDefault(v => v is not DBNull) is string;
            var kind = textFormula ? ColKind.Text : f.Kind switch
            {
                FieldKind.Money => ColKind.Money, FieldKind.Int => ColKind.Int, FieldKind.Decimal => ColKind.Number,
                FieldKind.Date => ColKind.Date, _ => ColKind.Text
            };
            var numeric = kind is ColKind.Money or ColKind.Number or ColKind.Int;
            t.Columns.Add(f.Name, numeric ? typeof(decimal) : kind == ColKind.Date ? typeof(DateTime) : typeof(string));
            var total = numeric && kind != ColKind.Int && !f.Name.Contains("Rate") && !f.Name.Contains("Pct");
            cols.Add(new ReportColumn(f.Name, f.Caption, kind, kind == ColKind.Text ? Math.Max(1, f.Width / 80.0) : 0.9, total));
        }
        foreach (var r in FilteredRows())
        {
            var nr = t.NewRow();
            foreach (var f in fields)
            {
                var v = r[f.Name];
                if (v is DBNull) continue;
                var dc = t.Columns[f.Name]!;
                if (dc.DataType == typeof(decimal)) nr[f.Name] = decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : DBNull.Value;
                else if (dc.DataType == typeof(DateTime)) nr[f.Name] = v;
                else nr[f.Name] = Display(f, v);
            }
            t.Rows.Add(nr);
        }
        var group = pick == "(no grouping)" ? null : fields.First(f => f.Caption == pick).Name;
        var sub = string.IsNullOrEmpty(_bs.Filter) ? $"{t.Rows.Count} records" : $"{t.Rows.Count} records (filtered: {_search.Text} {_advancedFilter})".Trim();
        PdfReport.Table(path, Title, sub, t, cols, group, cols.Count > 6);
        Ui.OpenFile(path);
    }

    void RecordPdf()
    {
        var r = Current;
        if (r == null) return;
        var name = Display(Entity.Field(Entity.TitleField), r[Entity.TitleField]);
        var path = Ui.SavePdf($"{Entity.Name}_{name}.pdf");
        if (path == null) return;
        var sections = _fields.Where(f => _table.Columns.Contains(f.Name)).GroupBy(f => f.Group)
            .Select(g => (g.Key, (IList<(string, string)>)g.Select(f => (f.Caption, Display(f, r[f.Name]))).ToList()));
        PdfReport.RecordCard(path, $"{Entity.Caption.Split('(')[0].Trim()} – {name}", $"Record Id {r[Entity.Key]}", sections);
        Ui.OpenFile(path);
    }

    // ================================================================== UDFs & formulas
    void ManageUdfs()
    {
        if (HasChanges && !Ui.Confirm("Unsaved changes will be lost when the screen reloads. Continue?")) return;
        using var f = new UdfManagerForm(Entity, _table);
        f.ShowDialog(this);
        Reload();
    }

    void BulkCalculate()
    {
        using var f = new BulkCalcForm(Entity, _fields, _table, FilteredRows().ToList(),
            _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (r.DataBoundItem as DataRowView)?.Row).OfType<DataRow>().ToList());
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            UpdateChanges();
            _statusText.Text = f.ResultText;
        }
    }

    public void FocusSearch() => _search.Focus();
}
