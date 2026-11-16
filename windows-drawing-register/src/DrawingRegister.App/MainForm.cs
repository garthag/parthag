using DrawingRegister.Core;

namespace DrawingRegister.App;

sealed class MainForm : Form
{
    readonly AppSettings _settings;
    SqlDrawingStore _store;
    DrawingLibrary _library;
    List<Drawing> _all = [];
    List<PlantGroup> _groups = [];

    readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ItemHeight = 22 };
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true,
        RowHeadersVisible = false, AutoGenerateColumns = false, BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
    };
    readonly ViewerPanel _viewer = new() { Dock = DockStyle.Fill };
    readonly ToolStripTextBox _search = new() { Width = 220, ToolTipText = "Search drawing no., title, unit, revision, file" };
    readonly ToolStripButton _superseded = new("Show superseded") { CheckOnClick = true, ToolTipText = "List older revisions under each drawing" };
    readonly ToolStripComboBox _typeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _where = new() { ForeColor = SystemColors.GrayText };
    readonly System.Windows.Forms.Timer _searchDelay = new() { Interval = 250 };
    Font _bold = null!;

    sealed record Filter(string? Plant, string? Unit, string? Type);

    sealed class Row(Drawing d, bool current, int? sn, string earlier)
    {
        public Drawing Drawing { get; } = d;
        public bool IsCurrent { get; } = current;
        public string SNo { get; } = sn?.ToString() ?? "";
        public string DrawingNo => IsCurrent ? Drawing.DrawingNo : "   ↳ superseded";
        public string Title => Drawing.DisplayTitle;
        public string Type => Drawing.Type.Code;
        public string Rev => Revisions.Label(Drawing.Revision);
        public string Status => IsCurrent ? Drawing.Status.Code() : "OLD";
        public string Earlier { get; } = earlier;
        public string Plant => Register.PlantLabel(Drawing.Plant);
        public string Unit => Register.UnitLabel(Drawing.Unit);
        public string Kind => Drawing.Kind;
        public string Received => Drawing.ImportedOn.ToString("dd-MM-yyyy");
        public string File => Drawing.FileName;
        public string Size => Ui.Size(Drawing.SizeBytes);
    }

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _store = new SqlDrawingStore(settings.ConnectionString);
        _library = new DrawingLibrary(settings.LibraryFolder);
        _viewer.Library = _library;

        Text = $"Drawing Register – {settings.ProjectName}";
        Font = new Font("Segoe UI", 9f);
        _bold = new Font(Font, FontStyle.Bold);
        Size = new Size(1500, 900);
        MinimumSize = new Size(1000, 600);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        KeyPreview = true;
        AllowDrop = true;

        BuildToolbar();
        BuildGrid();

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, FixedPanel = FixedPanel.None };
        right.Panel1.Controls.Add(_grid);
        right.Panel2.Controls.Add(_viewer);
        var main = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        main.Panel1.Controls.Add(_tree);
        main.Panel2.Controls.Add(right);
        Controls.Add(main);
        Controls.Add(new StatusStrip { Items = { _status, _where } });
        main.BringToFront(); // the Fill panel must dock last, after the toolbar and status bar
        Load += (_, _) =>
        {
            try
            {
                main.SplitterDistance = 300;
                right.SplitterDistance = (int)(right.Width * 0.55);
            }
            catch (InvalidOperationException) { }
            Reload();
        };

        _tree.AfterSelect += (_, _) => FillGrid();
        _grid.SelectionChanged += (_, _) => ShowSelected();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) _viewer.OpenExternal(Selected); };
        _viewer.RevisionSelected += SelectDrawing;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); FillGrid(); };
        _search.TextChanged += (_, _) => { _searchDelay.Stop(); _searchDelay.Start(); };
        _superseded.Checked = settings.ShowSuperseded;
        _superseded.CheckedChanged += (_, _) => { _settings.ShowSuperseded = _superseded.Checked; _settings.Save(); FillGrid(); };
        _typeFilter.SelectedIndexChanged += (_, _) => FillGrid();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Import(files); };
        KeyDown += OnKey;
    }

    // ------------------------------------------------------------------ layout

    void BuildToolbar()
    {
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2), ImageScalingSize = new Size(20, 20) };
        ToolStripButton B(string text, string tip, EventHandler click)
        {
            var b = new ToolStripButton(text) { ToolTipText = tip };
            b.Click += click;
            bar.Items.Add(b);
            return b;
        }
        B("Import ZIP…", "Unzip a drawing package and file it Plant > Unit > Type (Ctrl+I)", (_, _) => PickAndImport(zip: true));
        B("Add files…", "Add PDF / DWG / image drawings", (_, _) => PickAndImport(zip: false));
        bar.Items.Add(new ToolStripSeparator());
        B("Open", "Open in the default program (Enter)", (_, _) => _viewer.OpenExternal(Selected));
        B("Show in folder", "Show the file in Explorer", (_, _) => _viewer.ShowInFolder(Selected));
        B("Edit…", "Edit drawing details (F2)", (_, _) => EditSelected());
        B("Delete", "Delete the selected drawing(s) (Del)", (_, _) => DeleteSelected());
        bar.Items.Add(new ToolStripSeparator());
        var export = new ToolStripDropDownButton("Export");
        export.DropDownItems.Add("Drawing register PDF (print)…", null, (_, _) => ExportPdf());
        export.DropDownItems.Add("Drawing register for Excel (CSV)…", null, (_, _) => ExportCsv());
        bar.Items.Add(export);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(new ToolStripLabel("Type:"));
        _typeFilter.Items.Add("All types");
        _typeFilter.Items.AddRange(DrawingType.All.Cast<object>().ToArray());
        _typeFilter.SelectedIndex = 0;
        bar.Items.Add(_typeFilter);
        bar.Items.Add(new ToolStripLabel("Search:"));
        bar.Items.Add(_search);
        bar.Items.Add(_superseded);
        var settings = new ToolStripButton("Settings…") { Alignment = ToolStripItemAlignment.Right };
        settings.Click += (_, _) => OpenSettings();
        var refresh = new ToolStripButton("Refresh") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Reload from the database (F5)" };
        refresh.Click += (_, _) => Reload();
        bar.Items.Add(settings);
        bar.Items.Add(refresh);
        Controls.Add(bar);
    }

    void BuildGrid()
    {
        void Col(string prop, string header, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                DataPropertyName = prop, HeaderText = header, Name = prop, Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = 120; }
            _grid.Columns.Add(c);
        }
        Col(nameof(Row.SNo), "S.No", 44);
        Col(nameof(Row.DrawingNo), "Drawing No.", 170);
        Col(nameof(Row.Title), "Title", 200, fill: true);
        Col(nameof(Row.Type), "Type", 42);
        Col(nameof(Row.Rev), "Rev", 52);
        Col(nameof(Row.Status), "Status", 48);
        Col(nameof(Row.Earlier), "Earlier revs", 80);
        Col(nameof(Row.Unit), "Unit / Area", 110);
        Col(nameof(Row.Plant), "Plant", 120);
        Col(nameof(Row.Kind), "Sheet kind", 120);
        Col(nameof(Row.Received), "Received", 78);
        Col(nameof(Row.File), "File", 200);
        Col(nameof(Row.Size), "Size", 64);
        _grid.ColumnHeadersDefaultCellStyle.Font = _bold;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(255, 224, 178);
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(255, 204, 128);
        _grid.DefaultCellStyle.SelectionForeColor = Color.Black;
        _grid.CellFormatting += FormatCell;
    }

    void FormatCell(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Rows[e.RowIndex].DataBoundItem is not Row r || e.CellStyle == null) return;
        var col = _grid.Columns[e.ColumnIndex].Name;
        if (!r.IsCurrent)
        {
            e.CellStyle.ForeColor = Color.Gray;
            e.CellStyle.BackColor = Color.FromArgb(250, 250, 250);
            return;
        }
        if (col == nameof(Row.Type))
        {
            e.CellStyle.BackColor = Ui.ParseColour(r.Drawing.Type.Colour);
            e.CellStyle.ForeColor = Color.White;
            e.CellStyle.Font = _bold;
            e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        }
        else if (col is nameof(Row.DrawingNo) or nameof(Row.Rev))
            e.CellStyle.Font = _bold;
        else if (col == nameof(Row.Status))
            e.CellStyle.ForeColor = r.Drawing.Status switch
            {
                DrawingStatus.ForConstruction => Color.FromArgb(46, 125, 50),
                DrawingStatus.AsBuilt => Color.FromArgb(21, 101, 192),
                DrawingStatus.ForApproval or DrawingStatus.ForReview => Color.FromArgb(230, 81, 0),
                _ => Color.SlateGray,
            };
    }

    // ------------------------------------------------------------------ data

    void Reload(int? selectId = null)
    {
        try
        {
            Cursor = Cursors.WaitCursor;
            selectId ??= Selected?.Id;
            _all = _store.All();
            _groups = Register.Organise(_all);
            BuildTree();
            FillGrid(selectId);
        }
        catch (Exception e) { Ui.Error("Could not load the register from SQL Server.", e); }
        finally { Cursor = Cursors.Default; }
    }

    void BuildTree()
    {
        var selected = _tree.SelectedNode?.Tag as Filter;
        var expanded = new HashSet<string>();
        void Collect(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes) { if (n.IsExpanded) expanded.Add(n.Name); Collect(n.Nodes); }
        }
        Collect(_tree.Nodes);
        var firstBuild = _tree.Nodes.Count == 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var current = _groups.Sum(g => g.Count);
        var root = _tree.Nodes.Add("all", $"All drawings ({current})");
        root.Tag = new Filter(null, null, null);
        root.NodeFont = _bold;
        foreach (var p in _groups)
        {
            var pn = root.Nodes.Add($"p|{p.Plant}", $"PLANT: {Register.PlantLabel(p.Plant)} ({p.Count})");
            pn.Tag = new Filter(p.Plant, null, null);
            pn.NodeFont = _bold;
            foreach (var u in p.Units)
            {
                var un = pn.Nodes.Add($"u|{p.Plant}|{u.Unit}", $"{Register.UnitLabel(u.Unit)} ({u.Count})");
                un.Tag = new Filter(p.Plant, u.Unit, null);
                foreach (var t in u.Types)
                {
                    var tn = un.Nodes.Add($"t|{p.Plant}|{u.Unit}|{t.Type.Code}", $"{t.Type.Code} – {t.Type.Name} ({t.Drawings.Count})");
                    tn.Tag = new Filter(p.Plant, u.Unit, t.Type.Code);
                    tn.ForeColor = Ui.ParseColour(t.Type.Colour);
                }
                if (firstBuild || expanded.Contains(un.Name)) un.Expand();
            }
            if (firstBuild || expanded.Contains(pn.Name)) pn.Expand();
        }
        root.Expand();
        _tree.SelectedNode = FindNode(_tree.Nodes, selected) ?? root;
        _tree.EndUpdate();
    }

    static TreeNode? FindNode(TreeNodeCollection nodes, Filter? f)
    {
        if (f == null) return null;
        foreach (TreeNode n in nodes)
        {
            if (Equals(n.Tag, f)) return n;
            if (FindNode(n.Nodes, f) is { } c) return c;
        }
        return null;
    }

    bool Matches(Drawing d)
    {
        var f = _tree.SelectedNode?.Tag as Filter;
        if (f?.Plant != null && d.Plant != f.Plant) return false;
        if (f?.Unit != null && d.Unit != f.Unit) return false;
        if (f?.Type != null && d.TypeCode != f.Type) return false;
        if (_typeFilter.SelectedItem is DrawingType t && d.TypeCode != t.Code) return false;
        var q = _search.Text.Trim();
        if (q.Length == 0) return true;
        return new[] { d.DrawingNo, d.Title, d.Kind, d.Plant, d.Unit, d.FileName, d.Revision, d.Type.Name, d.Remarks }
            .Any(s => s.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    void FillGrid(int? selectId = null)
    {
        selectId ??= Selected?.Id;
        var rows = new List<Row>();
        var sn = 0;
        foreach (var (_, _, _, e) in Register.Flatten(_groups))
        {
            // A drawing is listed when its current revision (or any revision, when searching) matches.
            if (!e.Revisions.Any(Matches)) continue;
            rows.Add(new Row(e.Current, true, ++sn, string.Join(", ", e.Superseded.Select(x => x.Revision))));
            if (_superseded.Checked)
                rows.AddRange(e.Superseded.Select(s => new Row(s, false, null, "")));
        }
        _grid.DataSource = rows;
        var current = _groups.Sum(g => g.Count);
        _status.Text = $"{sn} of {current} drawings shown   ·   {_all.Count} files   ·   {_groups.Count} plants   ·   " +
                       $"{_groups.Sum(g => g.Units.Count)} units   ·   {_all.Count - current} superseded revisions";
        _where.Text = $"Library: {_library.Root}";

        var idx = selectId == null ? -1 : rows.FindIndex(r => r.Drawing.Id == selectId);
        if (idx < 0 && selectId != null)
        {
            // Selected revision is hidden (superseded) – select its drawing's row.
            var d = _all.FirstOrDefault(x => x.Id == selectId);
            if (d != null) idx = rows.FindIndex(r => r.Drawing.RegisterKey == d.RegisterKey);
        }
        _grid.ClearSelection();
        if (idx >= 0)
        {
            _grid.Rows[idx].Selected = true;
            _grid.CurrentCell = _grid.Rows[idx].Cells[1];
        }
        ShowSelected();
    }

    Drawing? Selected => _grid.CurrentRow?.DataBoundItem is Row r && _grid.CurrentRow.Selected ? r.Drawing : null;

    List<Drawing> SelectedDrawings => _grid.SelectedRows.Cast<DataGridViewRow>()
        .Select(r => r.DataBoundItem).OfType<Row>().Select(r => r.Drawing).ToList();

    List<Drawing> RevisionsOf(Drawing d) => _all
        .Where(x => x.RegisterKey == d.RegisterKey)
        .OrderByDescending(x => x.Revision, Comparer<string>.Create(Revisions.Compare))
        .ThenByDescending(x => x.ImportedOn)
        .ToList();

    Drawing? _viewed;

    void ShowSelected()
    {
        var d = Selected;
        if (d != null && d.Id == _viewed?.Id) return;
        _viewed = d;
        _viewer.Display(d, d == null ? [] : RevisionsOf(d));
    }

    /// <summary>Shows a specific revision, even one hidden from the list.</summary>
    void SelectDrawing(Drawing d)
    {
        var idx = _grid.Rows.Cast<DataGridViewRow>().ToList().FindIndex(r => (r.DataBoundItem as Row)?.Drawing.Id == d.Id);
        if (idx >= 0)
        {
            _grid.ClearSelection();
            _grid.Rows[idx].Selected = true;
            _grid.CurrentCell = _grid.Rows[idx].Cells[1];
            return;
        }
        _viewed = d;
        _viewer.Display(d, RevisionsOf(d));
    }

    // ------------------------------------------------------------------ actions

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.I) { PickAndImport(zip: true); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); e.Handled = true; }
        else if (e.KeyCode == Keys.F5) { Reload(); e.Handled = true; }
        else if (_grid.Focused && e.KeyCode == Keys.F2) { EditSelected(); e.Handled = true; }
        else if (_grid.Focused && e.KeyCode == Keys.Delete) { DeleteSelected(); e.Handled = true; }
        else if (_grid.Focused && e.KeyCode == Keys.Enter) { _viewer.OpenExternal(Selected); e.Handled = true; }
    }

    void PickAndImport(bool zip)
    {
        using var d = new OpenFileDialog
        {
            Title = zip ? "Choose drawing ZIP package(s)" : "Choose drawing files",
            Multiselect = true,
            Filter = zip
                ? "ZIP packages (*.zip)|*.zip|All drawing files|*.zip;*.pdf;*.dwg;*.dxf;*.dgn;*.dwf;*.png;*.jpg;*.jpeg;*.tif;*.tiff"
                : "Drawings (*.pdf;*.dwg;*.dxf;*.dgn;*.dwf;*.png;*.jpg;*.tif)|*.pdf;*.dwg;*.dxf;*.dgn;*.dwf;*.dwfx;*.png;*.jpg;*.jpeg;*.tif;*.tiff|All files|*.*",
        };
        if (d.ShowDialog(this) == DialogResult.OK) Import(d.FileNames);
    }

    async void Import(string[] files)
    {
        if (files.Length == 0) return;
        using var options = new ImportOptionsForm(files, _all.Select(x => x.Plant));
        if (options.ShowDialog(this) != DialogResult.OK) return;
        var plant = options.DefaultPlant;

        using var progress = new ProgressForm("Unzipping drawings");
        var reporter = new Progress<ImportProgress>(progress.Report);
        var importer = new DrawingImporter(_store, _library);
        Task<ImportResult>? task = null;
        // Start once the dialog is on screen so it can always be closed when the work ends.
        progress.Shown += (_, _) =>
        {
            task = Task.Run(() => importer.Import(files, plant, reporter, progress.Cancellation.Token));
            task.ContinueWith(_ => progress.BeginInvoke(progress.Close), TaskScheduler.Default);
        };
        progress.ShowDialog(this);

        ImportResult r;
        try { r = await task!; }
        catch (OperationCanceledException) { Ui.Info("Import cancelled. Drawings filed before cancelling are kept."); Reload(); return; }
        catch (Exception e) { Ui.Error("Import failed.", e); Reload(); return; }

        Reload(r.Added.FirstOrDefault()?.Id);
        var lines = new List<string>();
        if (r.Added.Count > 0)
        {
            lines.Add($"{r.Added.Count} drawing file(s) filed into {r.Plants} plant(s), {r.Units} unit(s), {r.Types} drawing type(s):");
            lines.AddRange(DrawingType.All.Where(t => r.Added.Any(d => d.TypeCode == t.Code))
                .Select(t => $"   • {t.Label}: {r.Added.Count(d => d.TypeCode == t.Code)}"));
        }
        else lines.Add("Nothing new was imported.");
        if (r.Unclassified > 0) lines.Add($"\n{r.Unclassified} file(s) could not be classified and are under \"OT – Other\". Use Edit to set the type.");
        if (r.Duplicates.Count > 0) lines.Add($"\n{r.Duplicates.Count} file(s) already in the register were skipped.");
        if (r.JunkSkipped > 0) lines.Add($"{r.JunkSkipped} system file(s) ignored.");
        if (r.Errors.Count > 0) lines.Add($"\n{r.Errors.Count} error(s):\n" + string.Join("\n", r.Errors.Take(8)));
        MessageBox.Show(this, string.Join("\n", lines), "Import finished", MessageBoxButtons.OK,
            r.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    void EditSelected()
    {
        var d = Selected;
        if (d == null) return;
        var revs = RevisionsOf(d);
        using var f = new EditDrawingForm(d, revs.Count, _all);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var targets = f.ApplyToAllRevisions ? revs : [d];
            foreach (var x in targets) f.ApplyShared(x);
            f.ApplyOwn(d);
            foreach (var x in targets.Append(d).Distinct())
            {
                _library.Relocate(x); // keep the folders in Plant\Unit\Type order
                _store.Update(x);
            }
        }
        catch (Exception e) { Ui.Error("Could not save the drawing.", e); }
        Reload(d.Id);
    }

    void DeleteSelected()
    {
        var picked = SelectedDrawings;
        if (picked.Count == 0) return;
        List<Drawing> targets;
        if (picked.Count == 1 && RevisionsOf(picked[0]) is { Count: > 1 } revs)
        {
            var answer = MessageBox.Show(this,
                $"{picked[0].DrawingNo} has {revs.Count} revisions.\n\nYes = delete ALL revisions\nNo = delete only Rev {picked[0].Revision}",
                "Delete drawing", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel) return;
            targets = answer == DialogResult.Yes ? revs : picked;
        }
        else
        {
            if (!Ui.Confirm($"Delete {picked.Count} drawing file(s) from the register and the library folder?")) return;
            targets = picked;
        }
        try
        {
            foreach (var d in targets)
            {
                _store.Delete(d.Id);
                _library.Delete(d);
            }
        }
        catch (Exception e) { Ui.Error("Could not delete.", e); }
        _viewed = null;
        Reload();
    }

    string? SaveAs(string filter, string name)
    {
        using var d = new SaveFileDialog { Filter = filter, FileName = name };
        return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
    }

    string SafeProject => string.Join("_", _settings.ProjectName.Split(Path.GetInvalidFileNameChars()));

    void ExportPdf()
    {
        var path = SaveAs("PDF (*.pdf)|*.pdf", $"Drawing Register - {SafeProject}.pdf");
        if (path == null) return;
        try
        {
            File.WriteAllBytes(path, RegisterExport.Pdf(_all, _settings.ProjectName));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) { Ui.Error("Could not create the PDF register.", e); }
    }

    void ExportCsv()
    {
        var path = SaveAs("CSV for Excel (*.csv)|*.csv", $"Drawing Register - {SafeProject}.csv");
        if (path == null) return;
        try
        {
            RegisterExport.WriteCsv(_all, path);
            Ui.Info($"Saved {path}");
        }
        catch (Exception e) { Ui.Error("Could not save the CSV.", e); }
    }

    void OpenSettings()
    {
        using var f = new SettingsForm(_settings);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var store = new SqlDrawingStore(_settings.ConnectionString);
            store.EnsureDatabase();
            Directory.CreateDirectory(_settings.LibraryFolder);
            _store = store;
            _library = new DrawingLibrary(_settings.LibraryFolder);
            _viewer.Library = _library;
            _settings.Save();
            Text = $"Drawing Register – {_settings.ProjectName}";
            _viewed = null;
            Reload();
        }
        catch (Exception e) { Ui.Error("Could not connect with the new settings. The previous connection is still in use.", e); }
    }
}
