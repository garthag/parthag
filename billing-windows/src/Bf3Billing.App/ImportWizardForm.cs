using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>
/// Import from any Excel workbook: choose the sheet and what it holds, check how its columns fit the
/// app's fields (adding new fields where nothing fits), check for duplicates, then add the ticked rows.
/// </summary>
sealed class ImportWizardForm : Form
{
    readonly BillingRepository _repo;
    List<RawSheet> _sheets = new();
    readonly Dictionary<RawSheet, SheetState> _state = new();
    RawSheet? _sheet;
    bool _loading;
    public bool Changed { get; private set; }
    /// <summary>Self test only: answers the "add new fields?" question without showing it.</summary>
    internal bool? AutoAddNewFields { get; set; }
    internal ImportPreview? CurrentCheck => _previewData;
    internal IEnumerable<(string Sheet, ImportTarget Target)> SheetTargets => _sheets.Select(x => (x.Name, _state[x].Target));
    internal void CheckAllForTest()
    {
        foreach (DataGridViewRow r in _sheetGrid.Rows)
        {
            _sheetGrid.CurrentCell = r.Cells[1];
            SelectSheet();
            Check();
            if (_previewData == null) throw new InvalidOperationException("Check gave no result for " + _sheet?.Name);
        }
    }

    sealed class SheetState
    {
        public int HeaderRow;
        public ImportTarget Target;
        public List<ColumnMap>? Map;
        public string Result = "";
    }

    readonly Label _file = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(0, 8, 0, 0) };
    readonly DataGridView _sheetGrid = Ui.Grid();
    readonly ComboBox _target = Ui.Combo(170);
    readonly NumericUpDown _header = new() { Minimum = 1, Maximum = 100000, Width = 70, Margin = new Padding(0, 3, 8, 0) };
    readonly DataGridView _mapGrid = Ui.Grid();
    readonly DataGridView _checkGrid = Ui.Grid();
    readonly Label _summary = new() { Dock = DockStyle.Top, AutoSize = false, Height = 40, ForeColor = Ui.Ink, Padding = new Padding(0, 4, 0, 0) };
    readonly ComboBox _existing = Ui.Combo(260);
    readonly CheckBox _createContractors = new() { Text = "Add contractors that are not in the list", Checked = true, AutoSize = true, Margin = new Padding(0, 6, 12, 0) };
    readonly Button _importBtn;
    readonly Button _checkBtn;
    readonly Button _exportBtn;

    const string LeaveOut = "(leave out)";
    const string NewPrefix = "➕ New field: ";
    const string ExtraPrefix = "Added field: ";
    const string Choose = "⚠ choose…";

    public ImportWizardForm(BillingRepository repo)
    {
        _repo = repo;
        Text = "Import from Excel – check and match columns";
        Font = Ui.Base;
        BackColor = Ui.Back;
        Size = new Size(1300, 860);
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        _target.AccessibleName = "Import this sheet as";
        _header.AccessibleName = "Header row";
        _existing.AccessibleName = "Rows that are already saved";

        // ── Top: file ──
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 10, 12, 6) };
        top.Controls.Add(Ui.Btn("Choose Excel file…", (_, _) => ChooseFile(), primary: true));
        top.Controls.Add(_file);

        // ── Left: sheets ──
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 6, 0) };
        left.Controls.Add(_sheetGrid);
        left.Controls.Add(new Label { Text = "1. Sheets in the file", Dock = DockStyle.Top, AutoSize = true, Font = Ui.Section, Padding = new Padding(0, 0, 0, 6) });
        _sheetGrid.SelectionChanged += (_, _) => { if (!_loading) SelectSheet(); };

        // ── Right top: columns ──
        var mapBar = Ui.Bar();
        mapBar.Controls.AddRange(new Control[] { Ui.Lbl("Import this sheet as"), _target, Ui.Lbl("Header row"), _header });
        _target.Items.AddRange(new object[] { ImportTargets.Label(ImportTarget.None) }.Concat(ImportTargets.All.Select(t => (object)ImportTargets.Label(t))).ToArray());
        _target.SelectedIndexChanged += (_, _) => { if (!_loading) TargetChanged(); };
        _header.ValueChanged += (_, _) => { if (!_loading) HeaderChanged(); };
        BuildMapGrid();
        var mapPanel = new Panel { Dock = DockStyle.Fill };
        mapPanel.Controls.Add(_mapGrid);
        mapPanel.Controls.Add(new Label
        {
            Text = "Each Excel column is fitted to a field of the app by its name. Columns marked ⚠ fit no field: choose one, add a new field for them, or leave them out.",
            Dock = DockStyle.Top, AutoSize = false, Height = 22, ForeColor = Ui.Muted, Font = Ui.Small,
        });
        mapPanel.Controls.Add(mapBar);
        mapPanel.Controls.Add(new Label { Text = "2. Columns", Dock = DockStyle.Top, AutoSize = true, Font = Ui.Section, Padding = new Padding(0, 0, 0, 6) });

        // ── Right bottom: check ──
        var checkBar = Ui.Bar();
        _checkBtn = Ui.Btn("Check data", (_, _) => Check(), primary: true);
        _exportBtn = Ui.Btn("Save check to Excel…", (_, _) => ExportCheck());
        _importBtn = Ui.Btn("Import ticked rows", (_, _) => Import(), primary: true);
        _existing.Items.AddRange(new object[] { "Already saved: skip them", "Already saved: update with the file's values" });
        _existing.SelectedIndex = 0;
        _existing.SelectedIndexChanged += (_, _) => ExistingChanged();
        checkBar.Controls.AddRange(new Control[] { _checkBtn, _existing, _createContractors, _exportBtn, _importBtn });
        var legend = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        foreach (var s in new[] { RowStatus.New, RowStatus.Exists, RowStatus.Repeated, RowStatus.Invalid, RowStatus.Total })
            legend.Controls.Add(new Label
            {
                Text = SmartImport.StatusText(s), AutoSize = true, BackColor = ColorTranslator.FromHtml(SmartImport.StatusColor(s)),
                Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 0, 6, 0), Font = Ui.Small,
            });
        legend.Controls.Add(new Label { Text = "Rows with the same group (D1, D2 …) are the same record. Untick or tick rows as you need.", AutoSize = true, ForeColor = Ui.Muted, Font = Ui.Small, Margin = new Padding(8, 3, 0, 0) });
        BuildPreviewGrid();
        var checkPanel = new Panel { Dock = DockStyle.Fill };
        checkPanel.Controls.Add(_checkGrid);
        checkPanel.Controls.Add(_summary);
        checkPanel.Controls.Add(legend);
        checkPanel.Controls.Add(checkBar);
        checkPanel.Controls.Add(new Label { Text = "3. Check for duplicates and problems, then import", Dock = DockStyle.Top, AutoSize = true, Font = Ui.Section, Padding = new Padding(0, 4, 0, 6) });

        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Ui.Back };
        rightSplit.Panel1.Controls.Add(mapPanel);
        rightSplit.Panel2.Controls.Add(checkPanel);
        var mainSplit = new SplitContainer { Dock = DockStyle.Fill, BackColor = Ui.Back };
        mainSplit.Panel1.Controls.Add(left);
        mainSplit.Panel2.Controls.Add(rightSplit);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };
        body.Controls.Add(mainSplit);
        Controls.Add(body);
        Controls.Add(top);
        Shown += (_, _) =>
        {
            try { mainSplit.SplitterDistance = 300; rightSplit.SplitterDistance = Math.Max(200, rightSplit.Height * 4 / 10); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException) { }
        };
        EnableActions();
    }

    // ───────────────────────── File and sheets ─────────────────────────

    public void ChooseFile(string? path = null)
    {
        path ??= Ui.OpenFile(this, "Choose an Excel file to import", "Excel workbook (*.xlsx;*.xlsm)|*.xlsx;*.xlsm");
        if (path == null) return;
        Cursor = Cursors.WaitCursor;
        try
        {
            _sheets = RawSheet.ReadWorkbook(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Cursor = Cursors.Default;
            Ui.Info(this, "The file could not be read. Is it an .xlsx file, and is it closed in Excel?\n\n" + ex.Message);
            return;
        }
        Cursor = Cursors.Default;
        _file.Text = path;
        _state.Clear();
        foreach (var s in _sheets)
            _state[s] = new SheetState
            {
                HeaderRow = s.SuggestedHeaderRow,
                Target = s.Rows.Count == 0 ? ImportTarget.None : SmartImport.SuggestTarget(s, s.SuggestedHeaderRow),
            };
        FillSheets();
    }

    void FillSheets()
    {
        _loading = true;
        var keep = _sheet;
        var t = new System.Data.DataTable();
        t.Columns.Add("_Id", typeof(long)); t.Columns.Add("Sheet"); t.Columns.Add("Rows", typeof(int)); t.Columns.Add("Looks like"); t.Columns.Add("Done");
        // Masters first, so work orders find their contractors and bills their work orders.
        var order = _sheets.Select((s, i) => (s, i)).OrderBy(x => _state[x.s].Target == ImportTarget.None ? 9 : (int)_state[x.s].Target).ThenBy(x => x.i);
        foreach (var (s, i) in order)
            t.Rows.Add((long)i, s.Name, Math.Max(0, s.Rows.Count(r => r.Row > _state[s].HeaderRow)), ImportTargets.Label(_state[s].Target), _state[s].Result);
        Ui.Bind(_sheetGrid, t);
        _loading = false;
        var select = keep ?? order.Select(x => x.s).FirstOrDefault();
        foreach (DataGridViewRow r in _sheetGrid.Rows)
            if (r.DataBoundItem is System.Data.DataRowView v && select != null && _sheets[(int)(long)v["_Id"]] == select) { _sheetGrid.CurrentCell = r.Cells[1]; break; }
        SelectSheet();
    }

    void SelectSheet()
    {
        if (Ui.SelectedId(_sheetGrid) is not long i || i >= _sheets.Count) return;
        var s = _sheets[(int)i];
        if (s == _sheet && _state[s].Map != null) return;
        _sheet = s;
        var st = _state[s];
        _loading = true;
        _target.SelectedIndex = (int)st.Target;
        _header.Maximum = Math.Max(1, s.Rows.Count > 0 ? s.Rows[^1].Row : 1);
        _header.Value = Math.Clamp(st.HeaderRow, (int)_header.Minimum, (int)_header.Maximum);
        _loading = false;
        st.Map ??= MakeMap(s, st);
        FillMap();
        ClearPreview();
    }

    List<ColumnMap> MakeMap(RawSheet s, SheetState st) =>
        st.Target == ImportTarget.None ? new() : SmartImport.AutoMap(s, st.HeaderRow, st.Target, _repo.CustomFields(ImportTargets.Entity(st.Target)));

    void TargetChanged()
    {
        if (_sheet == null) return;
        var st = _state[_sheet];
        st.Target = (ImportTarget)_target.SelectedIndex;
        st.Map = MakeMap(_sheet, st);
        FillMap();
        ClearPreview();
        UpdateSheetRow();
    }

    void HeaderChanged()
    {
        if (_sheet == null) return;
        var st = _state[_sheet];
        st.HeaderRow = (int)_header.Value;
        if (st.Target == ImportTarget.None) st.Target = SmartImport.SuggestTarget(_sheet, st.HeaderRow);
        _loading = true; _target.SelectedIndex = (int)st.Target; _loading = false;
        st.Map = MakeMap(_sheet, st);
        FillMap();
        ClearPreview();
        UpdateSheetRow();
    }

    void UpdateSheetRow()
    {
        if (_sheetGrid.CurrentRow?.DataBoundItem is System.Data.DataRowView v && _sheet != null)
        {
            v["Looks like"] = ImportTargets.Label(_state[_sheet].Target);
            v["Done"] = _state[_sheet].Result;
        }
    }

    // ───────────────────────── Column matching ─────────────────────────

    void BuildMapGrid()
    {
        var g = _mapGrid;
        g.ReadOnly = false;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.SelectionMode = DataGridViewSelectionMode.CellSelect;
        g.EditMode = DataGridViewEditMode.EditOnEnter;
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Col", HeaderText = "Excel column", Width = 70, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Header", HeaderText = "Heading in the file", Width = 220, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sample", HeaderText = "Sample values", Width = 320, ReadOnly = true });
        g.Columns.Add(new DataGridViewComboBoxColumn { Name = "Goes", HeaderText = "Goes into", Width = 300, FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "How", HeaderText = "", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        g.CurrentCellDirtyStateChanged += (_, _) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.CellValueChanged += (_, e) => { if (!_loading && e.RowIndex >= 0 && e.ColumnIndex == 3) ChoiceChanged(e.RowIndex); };
        g.DataError += (_, e) => e.ThrowException = false;
    }

    List<string> Choices(ColumnMap m, ImportTarget target)
    {
        var list = new List<string> { LeaveOut };
        list.AddRange(ImportTargets.FieldsOf(target).Select(f => f.Label + (f.Required ? " *" : "")));
        list.AddRange(_repo.CustomFields(ImportTargets.Entity(target)).Select(f => ExtraPrefix + f.Name));
        var newName = NewName(m.Header);
        if (!list.Contains(ExtraPrefix + newName)) list.Add(NewPrefix + newName);
        if (m.NeedsDecision) list.Insert(0, Choose);
        return list;
    }

    static string NewName(string header) => new string(header.Where(ch => ch is not ('"' or '\\' or '\'')).ToArray()).Trim() is { Length: > 0 } n ? (n.Length > 60 ? n[..60] : n) : "Field";

    string ChoiceOf(ColumnMap m, ImportTarget target)
    {
        if (m.NeedsDecision) return Choose;
        if (m.FieldKey != null)
        {
            var f = ImportTargets.FieldsOf(target).First(x => x.Key == m.FieldKey);
            return f.Label + (f.Required ? " *" : "");
        }
        if (m.CustomField != null) return (m.NewCustomField ? NewPrefix : ExtraPrefix) + m.CustomField;
        return LeaveOut;
    }

    void FillMap()
    {
        _loading = true;
        _mapGrid.Rows.Clear();
        var st = _sheet == null ? null : _state[_sheet];
        if (st?.Map != null && st.Target != ImportTarget.None)
        {
            foreach (var m in st.Map)
            {
                var i = _mapGrid.Rows.Add(RawSheet.ColumnLetter(m.Column + 1), m.Header, m.Sample);
                var cell = (DataGridViewComboBoxCell)_mapGrid.Rows[i].Cells["Goes"];
                cell.Items.AddRange(Choices(m, st.Target).Cast<object>().ToArray());
                cell.Value = ChoiceOf(m, st.Target);
                PaintMapRow(i, m);
            }
        }
        _loading = false;
        EnableActions();
    }

    void PaintMapRow(int i, ColumnMap m)
    {
        var r = _mapGrid.Rows[i];
        var (color, how) = m.NeedsDecision ? ("#FFE9C7", "Fits no field – choose")
            : m.NewCustomField ? ("#E8F0FE", "A new field will be added")
            : m.Ignored ? ("#F2F2F2", m.Sample.Length == 0 ? "Empty column" : "Left out")
            : m.AutoMatched ? ("#FFFFFF", "Matched by name") : ("#FFFFFF", "Chosen");
        r.DefaultCellStyle.BackColor = ColorTranslator.FromHtml(color);
        r.Cells["How"].Value = how;
    }

    void ChoiceChanged(int row)
    {
        if (_sheet == null) return;
        var st = _state[_sheet];
        var m = st.Map![row];
        var choice = _mapGrid.Rows[row].Cells["Goes"].Value as string ?? LeaveOut;
        if (choice == Choose) return;
        m.NeedsDecision = false; m.FieldKey = null; m.CustomField = null; m.NewCustomField = false; m.AutoMatched = false;
        if (choice.StartsWith(NewPrefix)) { m.CustomField = choice[NewPrefix.Length..]; m.NewCustomField = true; }
        else if (choice.StartsWith(ExtraPrefix)) m.CustomField = choice[ExtraPrefix.Length..];
        else if (choice != LeaveOut)
        {
            var label = choice.TrimEnd(' ', '*');
            m.FieldKey = ImportTargets.FieldsOf(st.Target).First(f => f.Label == label).Key;
            // One column per field: free the field from any other column.
            for (var i = 0; i < st.Map.Count; i++)
                if (i != row && st.Map[i].FieldKey == m.FieldKey)
                {
                    st.Map[i].FieldKey = null;
                    _loading = true; _mapGrid.Rows[i].Cells["Goes"].Value = LeaveOut; _loading = false;
                    PaintMapRow(i, st.Map[i]);
                }
        }
        var cell = (DataGridViewComboBoxCell)_mapGrid.Rows[row].Cells["Goes"];
        if (cell.Items.Contains(Choose)) { _loading = true; cell.Items.Remove(Choose); cell.Value = ChoiceOf(m, st.Target); _loading = false; }
        PaintMapRow(row, m);
        ClearPreview();
    }

    /// <summary>Asks what to do with columns that fit no field. Returns false if the user cancels.</summary>
    bool ResolveUndecided()
    {
        var st = _state[_sheet!];
        var open = st.Map!.Where(m => m.NeedsDecision).ToList();
        if (open.Count == 0) return true;
        var list = string.Join(Environment.NewLine, open.Select(m => $"   • {m.Header}   (e.g. {Shorten(m.Sample)})"));
        var answer = AutoAddNewFields is bool auto ? (auto ? DialogResult.Yes : DialogResult.No) : Ui.Ask(this,
            $"{open.Count} column(s) do not fit any field of {ImportTargets.Label(st.Target)}:{Environment.NewLine}{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}" +
            "Add them as new fields so their data is kept?" + Environment.NewLine + Environment.NewLine +
            "Yes = add a new field for each and put the data there" + Environment.NewLine +
            "No = leave these columns out" + Environment.NewLine +
            "Cancel = go back and choose a field for each yourself",
            "Columns that fit no field", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        foreach (var m in open)
        {
            m.NeedsDecision = false;
            if (answer == DialogResult.Yes) { m.CustomField = NewName(m.Header); m.NewCustomField = true; }
        }
        FillMap();
        return true;
    }

    static string Shorten(string s) => s.Length > 50 ? s[..50] + "…" : s;

    // ───────────────────────── Check and import ─────────────────────────

    void BuildPreviewGrid()
    {
        var g = _checkGrid;
        g.ReadOnly = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = true;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
        g.CurrentCellDirtyStateChanged += (_, _) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || g.Columns[e.ColumnIndex].Name != "Import") return;
            _previewData!.Rows[(int)g.Rows[e.RowIndex].Tag!].Include = g.Rows[e.RowIndex].Cells["Import"].Value is true;
            _summary.Text = _previewData.Summary;
            EnableActions();
        };
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space && g.SelectedRows.Count > 1)
            {
                e.Handled = true;
                var on = !(g.SelectedRows[0].Cells["Import"].Value is true);
                foreach (DataGridViewRow r in g.SelectedRows) r.Cells["Import"].Value = on;
            }
        };
    }

    ImportPreview? _previewData;

    void ClearPreview()
    {
        _previewData = null;
        _checkGrid.Rows.Clear();
        _checkGrid.Columns.Clear();
        _summary.Text = _sheet == null ? "Choose an Excel file to start." : "Press 'Check data' to see which rows are new, already saved, repeated or have a problem.";
        EnableActions();
    }

    void Check()
    {
        if (_sheet == null) return;
        var st = _state[_sheet];
        if (st.Target == ImportTarget.None) { Ui.Info(this, "Choose what this sheet holds (Import this sheet as)."); return; }
        if (!ResolveUndecided()) return;
        var fields = ImportTargets.FieldsOf(st.Target);
        var missing = fields.Where(f => f.Required && st.Map!.All(m => m.FieldKey != f.Key)).Select(f => f.Label).ToList();
        if (st.Target == ImportTarget.Bom && st.Map!.All(m => m.FieldKey is not ("tonnes" or "kg"))) missing.Add("Weight (t) or Weight (kg)");
        if (missing.Count > 0)
        {
            Ui.Info(this, "Choose the column for: " + string.Join(", ", missing) + ".\n\nFields marked * are needed.");
            return;
        }
        Cursor = Cursors.WaitCursor;
        try
        {
            if (!Ui.Try(this, () => _previewData = SmartImport.Analyze(_repo, _sheet, st.HeaderRow, st.Target, st.Map!))) return;
            ExistingChanged();
            ShowPreview();
        }
        finally { Cursor = Cursors.Default; }
    }

    void ShowPreview()
    {
        var p = _previewData!;
        _loading = true;
        _checkGrid.SuspendLayout();
        _checkGrid.Rows.Clear();
        _checkGrid.Columns.Clear();
        _checkGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Import", HeaderText = "Import", Width = 56 });
        void Ro(string name, string header) => _checkGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = header, ReadOnly = true, SortMode = DataGridViewColumnSortMode.Automatic });
        Ro("Row", "Excel row"); Ro("Result", "Result"); Ro("Group", "Group"); Ro("Note", "Note");
        var cols = p.ValueColumns().ToList();
        foreach (var (key, title) in cols) Ro("v" + key, title);
        _checkGrid.Columns["Note"]!.Width = 320;
        _checkGrid.Columns["Note"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        var rows = new List<DataGridViewRow>();
        for (var i = 0; i < p.Rows.Count; i++)
        {
            var r = p.Rows[i];
            var gr = new DataGridViewRow();
            gr.CreateCells(_checkGrid, new object[] { r.Include, r.SourceRow, SmartImport.StatusText(r.Status), r.Group.HasValue ? "D" + r.Group : "", r.Note }
                .Concat(cols.Select(c => (object)r.Values.GetValueOrDefault(c.Key, ""))).ToArray());
            gr.Tag = i;
            gr.DefaultCellStyle.BackColor = ColorTranslator.FromHtml(SmartImport.StatusColor(r.Status));
            if (r.Status is RowStatus.Invalid or RowStatus.Total) gr.Cells[0].ReadOnly = true;
            if (r.Status == RowStatus.Exists && p.Target == ImportTarget.BillLines) gr.Cells[0].ReadOnly = true;
            rows.Add(gr);
        }
        _checkGrid.Rows.AddRange(rows.ToArray());
        _checkGrid.ResumeLayout();
        _loading = false;
        _summary.Text = p.Summary;
        EnableActions();
    }

    void ExistingChanged()
    {
        if (_previewData == null) return;
        var update = _existing.SelectedIndex == 1 && _previewData.Target != ImportTarget.BillLines;
        foreach (var r in _previewData.Rows.Where(r => r.Status == RowStatus.Exists)) r.Include = update;
        if (_checkGrid.Rows.Count > 0)
        {
            _loading = true;
            foreach (DataGridViewRow gr in _checkGrid.Rows) gr.Cells["Import"].Value = _previewData.Rows[(int)gr.Tag!].Include;
            _loading = false;
            _summary.Text = _previewData.Summary;
        }
        EnableActions();
    }

    void EnableActions()
    {
        var st = _sheet == null ? null : _state[_sheet];
        _checkBtn.Enabled = st != null && st.Target != ImportTarget.None;
        _exportBtn.Enabled = _previewData != null;
        _importBtn.Enabled = _previewData != null && _previewData.Rows.Any(r => r.Include);
        _createContractors.Visible = st?.Target == ImportTarget.WorkOrders;
        _existing.Enabled = st?.Target is not (null or ImportTarget.BillLines);
    }

    void ExportCheck()
    {
        if (_previewData == null) return;
        var path = Ui.SaveFile(this, "Save the check", Ui.SafeFileName($"Import check – {_previewData.SheetName}.xlsx"));
        if (path != null && Ui.Try(this, () => SmartImport.WritePreview(_previewData, path)))
            Ui.OfferOpen(this, path, "The check was saved, coloured the same way.");
    }

    void Import()
    {
        if (_previewData == null || _sheet == null) return;
        var p = _previewData;
        var n = p.Rows.Count(r => r.Include);
        var newFields = p.Columns.Where(c => c.NewCustomField).Select(c => c.CustomField!).ToList();
        var repeated = p.Rows.Count(r => r.Include && r.Status == RowStatus.Repeated);
        var msg = $"Import {n} row(s) from '{p.SheetName}' as {ImportTargets.Label(p.Target)}?";
        if (newFields.Count > 0) msg += $"{Environment.NewLine}{Environment.NewLine}New field(s) will be added: {string.Join(", ", newFields)}";
        if (repeated > 0) msg += $"{Environment.NewLine}{Environment.NewLine}{repeated} ticked row(s) are repeats in the file and will be added too.";
        if (!Ui.Confirm(this, msg)) return;
        ImportResult? res = null;
        var options = new ImportOptions
        {
            Existing = _existing.SelectedIndex == 1 ? ExistingAction.Update : ExistingAction.Skip,
            CreateMissingContractors = _createContractors.Checked,
        };
        Cursor = Cursors.WaitCursor;
        try { if (!Ui.Try(this, () => res = SmartImport.Apply(_repo, p, options))) return; }
        finally { Cursor = Cursors.Default; }
        Changed = true;
        var st = _state[_sheet];
        st.Result = $"✓ {res!.Added} added, {res.Updated} updated";
        st.Map = MakeMap(_sheet, st); // new fields now match by name
        UpdateSheetRow();
        Ui.Info(this, $"{ImportTargets.Label(p.Target)}: {res.Summary}");
        FillMap();
        Check();
    }
}
