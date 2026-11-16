using System.Data;
using BSL.Core.Metadata;
using BSL.Desktop.Controls;
using Microsoft.Data.SqlClient;

namespace BSL.Desktop.Forms;

/// <summary>Main window: menu, navigation tree, tabbed screens, dashboard.</summary>
public sealed class MainForm : StyledForm
{
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 4) };
    readonly TreeView _nav = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ItemHeight = 24, Font = new Font("Arial", 10f), BorderStyle = BorderStyle.None };
    readonly ToolStripStatusLabel _db = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm()
    {
        Text = "BSL Project Control – Drawings, BOM, Contractors & RA Bills";
        Width = 1400; Height = 860; StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized;
        Font = new Font("Arial", 10f);
        KeyPreview = true;

        MainMenuStrip = BuildMenu();
        BuildNav();
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(_nav);
        split.Panel2.Controls.Add(_tabs);
        var status = new StatusStrip();
        status.Items.Add(_db);
        status.Items.Add(new ToolStripStatusLabel("Ctrl+S save • Ctrl+N new • F2 table/form • F5 refresh • Ctrl+F find • Ctrl+P PDF • Ctrl+E Excel"));
        Controls.Add(split);
        Controls.Add(MainMenuStrip);
        Controls.Add(status);

        _tabs.MouseUp += (_, e) =>
        {
            for (int i = 0; i < _tabs.TabPages.Count; i++)
                if (_tabs.GetTabRect(i).Contains(e.Location) && e.Button == MouseButtons.Middle) CloseTab(_tabs.TabPages[i]);
        };
        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Close tab", null, (_, _) => { if (_tabs.SelectedTab != null) CloseTab(_tabs.SelectedTab); });
        ctx.Items.Add("Close all other tabs", null, (_, _) => { foreach (var p in _tabs.TabPages.Cast<TabPage>().Where(p => p != _tabs.SelectedTab && p.Tag is not "home").ToList()) CloseTab(p); });
        _tabs.ContextMenuStrip = ctx;
        FormClosing += (_, e) =>
        {
            var dirty = _tabs.TabPages.Cast<TabPage>().Select(p => p.Controls.OfType<EntityBrowser>().FirstOrDefault()).Where(b => b?.HasChanges == true).ToList();
            if (dirty.Count > 0 && !Ui.Confirm($"{dirty.Count} screen(s) have unsaved changes:\n{string.Join("\n", dirty.Select(d => d!.Title))}\n\nExit without saving?")) e.Cancel = true;
        };
        Ui.SplitterWhenSized(split, 320);
        Load += (_, _) => { UpdateDbLabel(); ShowHome(); };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.W) && _tabs.SelectedTab is { Tag: not "home" } t) { CloseTab(t); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    const string SettingsModule = "7. SETTINGS";
    static IEnumerable<IGrouping<string, EntityDef>> Modules => EntityCatalog.All.GroupBy(e => e.Module).OrderBy(g => g.Key);

    /// <summary>ERP-style menu: FILE · 1 MASTERS · 2 CONTRACT & BILLING · 3 ENGINEERING · 4 DOCUMENTS · 5 REPORTS · 6 IMPORT/EXPORT · 7 SETTINGS · HELP</summary>
    MenuStrip BuildMenu()
    {
        var m = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Dashboard", null, (_, _) => ShowHome());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("New &contractor…", null, (_, _) => OpenNew(EntityCatalog.Contractor));
        file.DropDownItems.Add("New &work order…", null, (_, _) => OpenNew(EntityCatalog.WorkOrder));
        file.DropDownItems.Add("New &activity…", null, (_, _) => OpenNew(EntityCatalog.Activity));
        file.DropDownItems.Add("Link contractor to activity…", null, (_, _) => OpenNew(EntityCatalog.ContractorActivity));
        file.DropDownItems.Add("New work-order activity rate…", null, (_, _) => OpenNew(EntityCatalog.RateMaster));
        file.DropDownItems.Add("New drawing…", null, (_, _) => OpenNew(EntityCatalog.DrawingMaster));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());
        m.Items.Add(file);

        foreach (var module in Modules.Where(g => g.Key != SettingsModule))
        {
            var mi = new ToolStripMenuItem(module.Key.Replace("&", "&&"));
            foreach (var e in module) mi.DropDownItems.Add(e.Caption.Replace("&", "&&"), null, (_, _) => Open(e));
            if (module.Key.StartsWith("2."))
            {
                mi.DropDownItems.Add(new ToolStripSeparator());
                mi.DropDownItems.Add("Apply activity rates to BOQ – blank rates only", null, (_, _) => ApplyRates(false));
                mi.DropDownItems.Add("Apply activity rates to BOQ – overwrite all item rates", null, (_, _) => ApplyRates(true));
            }
            m.Items.Add(mi);
        }

        var rep = new ToolStripMenuItem("5. &Reports");
        rep.DropDownItems.Add("Open reports (PDF / Excel)…", null, (_, _) => OpenReports());
        m.Items.Add(rep);

        var io = new ToolStripMenuItem("6. &Import / Export");
        io.DropDownItems.Add("Import / standardise &BOM workbook…", null, (_, _) => Wizard(WorkbookKind.Bom));
        io.DropDownItems.Add("Import &contractor workbook (contractors, WOs, BOQ, RA)…", null, (_, _) => Wizard(WorkbookKind.Contractor));
        io.DropDownItems.Add(new ToolStripSeparator());
        io.DropDownItems.Add("Each screen has Excel > Export / Import / Template", null, null!).Enabled = false;
        m.Items.Add(io);

        var set = new ToolStripMenuItem("7. &Settings");
        set.DropDownItems.Add("Company, logo && database…", null, (_, _) => OpenSettings());
        set.DropDownItems.Add("User defined fields – choose screen…", null, (_, _) => OpenUdfManager());
        set.DropDownItems.Add("Auto-link activities", null, (_, _) => LinkActivities());
        set.DropDownItems.Add(new ToolStripSeparator());
        foreach (var e in Modules.Where(g => g.Key == SettingsModule).SelectMany(g => g)) set.DropDownItems.Add(e.Caption, null, (_, _) => Open(e));
        m.Items.Add(set);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => Ui.Info(
            "BSL Project Control 1.1\n\nMasters, contract & billing (BOQ, RA bills, internal / external rates), engineering (drawing master, " +
            "drawing register with uploaded drawings, standardised BOM), notes and attachments on every record, user defined fields with " +
            "formulas, Excel import / export and PDF reports with company logo.\n\n" +
            "Database: " + new SqlConnectionStringBuilder(Program.Db.ConnectionString).InitialCatalog));
        m.Items.Add(help);
        return m;
    }

    void OpenUdfManager()
    {
        var e = PickDialog.Show("User defined fields", "Screen:", EntityCatalog.All.Where(x => x.Fields.Any(f => !f.ReadOnly)).Select(x => x.Caption).ToList());
        if (e == null) return;
        var def = EntityCatalog.All.First(x => x.Caption == e);
        using var f = new UdfManagerForm(def, Program.Repo.Load(def, "1 = 0"));
        f.ShowDialog(this);
        foreach (var b in Browsers().Where(b => b.Entity == def)) b.Reload();
    }

    void BuildNav()
    {
        var bold = new Font("Arial", 10f, FontStyle.Bold);
        var home = _nav.Nodes.Add("Dashboard");
        home.Tag = "home"; home.NodeFont = bold;
        foreach (var module in Modules.Where(g => g.Key != SettingsModule))
        {
            var n = _nav.Nodes.Add(module.Key);
            n.NodeFont = bold;
            foreach (var e in module) n.Nodes.Add(new TreeNode(e.Caption) { Tag = e });
            n.Expand();
        }
        var rep = _nav.Nodes.Add("5. Reports (PDF / Excel)");
        rep.NodeFont = bold; rep.Tag = "reports";
        var io = _nav.Nodes.Add("6. Import / Export");
        io.NodeFont = bold;
        io.Nodes.Add(new TreeNode("Import BOM workbook") { Tag = WorkbookKind.Bom });
        io.Nodes.Add(new TreeNode("Import contractor workbook") { Tag = WorkbookKind.Contractor });
        io.Expand();
        var set = _nav.Nodes.Add(SettingsModule);
        set.NodeFont = bold;
        set.Nodes.Add(new TreeNode("Company, logo & database") { Tag = "settings" });
        set.Nodes.Add(new TreeNode("User defined fields") { Tag = "udf" });
        foreach (var e in Modules.Where(g => g.Key == SettingsModule).SelectMany(g => g)) set.Nodes.Add(new TreeNode(e.Caption) { Tag = e });
        set.Expand();
        _nav.NodeMouseClick += (_, e) =>
        {
            switch (e.Node.Tag)
            {
                case EntityDef d: Open(d); break;
                case WorkbookKind k: Wizard(k); break;
                case "home": ShowHome(); break;
                case "reports": OpenReports(); break;
                case "settings": OpenSettings(); break;
                case "udf": OpenUdfManager(); break;
            }
        };
    }

    IEnumerable<EntityBrowser> Browsers() => _tabs.TabPages.Cast<TabPage>().SelectMany(p => p.Controls.OfType<EntityBrowser>());

    public void Open(EntityDef e) => OpenBrowser(new EntityBrowser(e), e.Name);

    /// <summary>Opens the screen (or switches to it) and starts a new record in form view.</summary>
    public void OpenNew(EntityDef e)
    {
        Open(e);
        if (_tabs.SelectedTab?.Controls.OfType<EntityBrowser>().FirstOrDefault() is { } b) b.StartWithNewRecord();
    }

    void LinkActivities()
    {
        Ui.Busy(this, () =>
        {
            var text = new BSL.Core.Data.ActivityService(Program.Db).LinkActivities();
            foreach (var b in Browsers()) if (!b.HasChanges) b.Reload();
            RefreshHome();
            Ui.Info(text);
        });
    }

    void ApplyRates(bool overwrite)
    {
        if (overwrite && !Ui.Confirm("Replace the internal and external rate of EVERY BOQ item that has an activity with the work-order activity rate (or the activity default)?")) return;
        Ui.Busy(this, () =>
        {
            var n = new BSL.Core.Data.ActivityService(Program.Db).ApplyRates(overwrite);
            foreach (var b in Browsers()) if (!b.HasChanges) b.Reload();
            RefreshHome();
            Ui.Info($"{n} BOQ items updated from activity rates.");
        });
    }

    void OpenRelated(EntityDef child, string fk, int id, string title) =>
        OpenBrowser(new EntityBrowser(child, title, $"{fk} = @fk", [("@fk", id)], (fk, id)), $"{child.Name}:{fk}:{id}");

    void OpenBrowser(EntityBrowser b, string key)
    {
        var existing = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => (p.Tag as string) == key);
        if (existing != null) { _tabs.SelectedTab = existing; b.Dispose(); return; }
        b.OpenRelated += OpenRelated;
        var page = new TabPage(b.Title + "   ") { Tag = key };
        page.Controls.Add(b);
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
    }

    void CloseTab(TabPage p)
    {
        if (p.Tag is "home") return;
        if (p.Controls.OfType<EntityBrowser>().FirstOrDefault() is { HasChanges: true } b && !Ui.Confirm($"'{b.Title}' has unsaved changes. Close anyway?")) return;
        _tabs.TabPages.Remove(p);
        p.Dispose();
    }

    void OpenReports()
    {
        var existing = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => (p.Tag as string) == "reports");
        if (existing != null) { _tabs.SelectedTab = existing; return; }
        var page = new TabPage("Reports   ") { Tag = "reports" };
        page.Controls.Add(new ReportsForm());
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
    }

    void Wizard(WorkbookKind k)
    {
        using var f = new ImportWizardForm(k);
        f.ShowDialog(this);
        foreach (var b in Browsers()) if (!b.HasChanges) b.Reload();
        RefreshHome();
    }

    void OpenSettings()
    {
        using var f = new SettingsForm(Program.Settings);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            Program.Connect(Program.Settings.ConnectionString);
            foreach (var p in _tabs.TabPages.Cast<TabPage>().Where(p => p.Tag is not "home").ToList()) { _tabs.TabPages.Remove(p); p.Dispose(); }
            UpdateDbLabel();
            RefreshHome();
        }
        catch (Exception ex) { Ui.Error(ex); }
    }

    void UpdateDbLabel()
    {
        var b = new SqlConnectionStringBuilder(Program.Db.ConnectionString);
        _db.Text = $"DB: {b.InitialCatalog} ON {b.DataSource} • {Program.Settings.PreparedBy}";
    }

    // ------------------------------------------------------------------ dashboard
    void ShowHome()
    {
        var page = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Tag is "home");
        if (page == null)
        {
            page = new TabPage("Dashboard") { Tag = "home" };
            _tabs.TabPages.Insert(0, page);
        }
        _tabs.SelectedTab = page;
        RefreshHome();
    }

    void RefreshHome()
    {
        var page = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Tag is "home");
        if (page == null) return;
        page.Controls.Clear();
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16) };
        page.Controls.Add(flow);
        try
        {
            var k = Program.Db.Query("""
                SELECT (SELECT COUNT(*) FROM dbo.Contractor WHERE ContractorType = N'Executing Agency') AS Agencies,
                       (SELECT COUNT(*) FROM dbo.WorkOrder) AS WorkOrders,
                       (SELECT SUM(WOValue) FROM dbo.WorkOrder) AS WOValue,
                       (SELECT COUNT(*) FROM dbo.DrawingMaster) AS Drawings,
                       (SELECT COUNT(*) FROM dbo.DrawingRegister) AS Sheets,
                       (SELECT COUNT(*) FROM dbo.Package) AS Packages,
                       (SELECT COUNT(*) FROM dbo.BomItem) AS BomLines,
                       (SELECT SUM(WeightAllSetsTon) FROM dbo.vw_Package) AS BomTon,
                       (SELECT SUM(R1WeightTon) FROM dbo.ContractItem WHERE IsGroupHeader = 0) AS ScopeTon,
                       (SELECT SUM(QtyTon) FROM dbo.RaBillLine) AS ExecTon,
                       (SELECT SUM(ExternalAmount) FROM dbo.ContractItem WHERE IsGroupHeader = 0) AS ExtAmt,
                       (SELECT SUM(InternalAmount) FROM dbo.ContractItem WHERE IsGroupHeader = 0) AS IntAmt,
                       (SELECT SUM(Margin) FROM dbo.ContractItem WHERE IsGroupHeader = 0) AS MarginAmt,
                       (SELECT COUNT(*) FROM dbo.ContractItem WHERE IsGroupHeader = 0 AND (InternalRate IS NULL OR ExternalRate IS NULL)) AS NoRate,
                       (SELECT COUNT(*) FROM dbo.BomItem WHERE QualityFlags IS NOT NULL AND QualityFlags NOT LIKE N'detail of%') AS Flags,
                       (SELECT COUNT(*) FROM dbo.Activity WHERE IsActive = 1) AS Activities,
                       (SELECT COUNT(*) FROM dbo.ContractorActivity WHERE IsApproved = 1) AS ConAct,
                       (SELECT COUNT(*) FROM dbo.ContractItem WHERE IsGroupHeader = 0 AND ActivityId IS NULL) AS NoActItems
                """).Rows[0];
            decimal D(string c) => k[c] is DBNull ? 0 : Convert.ToDecimal(k[c]);
            // header: company / dashboard title on the left, company logo on the right
            var head = new Panel { Dock = DockStyle.Top, Height = 72, Padding = new Padding(20, 8, 20, 4) };
            var title = new Label
            {
                Text = Program.Settings.CompanyName + "\nProject dashboard" + (Program.Settings.CompanyAddress.Length > 0 ? "  –  " + Program.Settings.CompanyAddress : ""),
                Font = new Font("Arial", 14f, FontStyle.Bold), ForeColor = Ui.Accent, Dock = DockStyle.Fill
            };
            head.Controls.Add(title);
            if (Program.Settings.LogoPath is { } lp && File.Exists(lp))
            {
                var pic = new PictureBox { Dock = DockStyle.Right, Width = 220, SizeMode = PictureBoxSizeMode.Zoom };
                using (var fs = File.OpenRead(lp)) pic.Image = Image.FromStream(fs).Clone() as Image;
                head.Controls.Add(pic);
            }
            page.Controls.Add(head);
            var quick = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 4, 0, 4) };
            quick.Controls.AddRange(
            [
                Ui.Button("+ New contractor", (_, _) => OpenNew(EntityCatalog.Contractor), 140),
                Ui.Button("+ New work order", (_, _) => OpenNew(EntityCatalog.WorkOrder), 140),
                Ui.Button("+ New activity", (_, _) => OpenNew(EntityCatalog.Activity), 130),
                Ui.Button("Link contractor to activity", (_, _) => OpenNew(EntityCatalog.ContractorActivity), 190),
                Ui.Button("+ WO activity rate", (_, _) => OpenNew(EntityCatalog.RateMaster), 150),
                Ui.Button("Auto-link activities", (_, _) => LinkActivities(), 150),
                Ui.Button("Apply activity rates to BOQ", (_, _) => ApplyRates(false), 190)
            ]);
            page.Controls.Add(quick);
            quick.BringToFront();
            flow.BringToFront();
            void Tile(string title, string value, string sub, Action? click = null)
            {
                var p = new Panel { Width = 250, Height = 110, Margin = new Padding(8), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Cursor = click != null ? Cursors.Hand : Cursors.Default };
                p.Controls.Add(new Label { Text = sub, Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(10, 2, 0, 0) });
                p.Controls.Add(new Label { Text = value, Dock = DockStyle.Top, Height = 46, Font = new Font("Arial", 20f, FontStyle.Bold), ForeColor = Ui.Accent, Padding = new Padding(8, 0, 0, 0) });
                p.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = new Font("Arial", 10f, FontStyle.Bold), Padding = new Padding(10, 6, 0, 0) });
                if (click != null) foreach (Control c in p.Controls.Cast<Control>().Append(p)) c.Click += (_, _) => click();
                flow.Controls.Add(p);
            }
            Tile("Contractors / agencies", $"{k["Agencies"]}", $"{k["WorkOrders"]} work orders", () => Open(EntityCatalog.Contractor));
            Tile("Work order value", $"₹ {D("WOValue") / 10000000m:N2} Cr", "sum of all WOs", () => Open(EntityCatalog.WorkOrder));
            Tile("Drawings", $"{k["Drawings"]:N0}", $"{k["Sheets"]:N0} sheets / revisions", () => Open(EntityCatalog.DrawingMaster));
            Tile("BOM weight (all sets)", $"{D("BomTon"):N1} MT", $"{k["Packages"]} packages • {k["BomLines"]:N0} lines", () => Open(EntityCatalog.Package));
            Tile("Contract scope (R1)", $"{D("ScopeTon"):N1} MT", $"executed {D("ExecTon"):N1} MT", () => Open(EntityCatalog.ContractItem));
            Tile("Margin (both rates known)", $"₹ {D("MarginAmt") / 100000m:N1} L", $"{k["NoRate"]} items missing a rate", OpenReports);
            Tile("Activities", $"{k["Activities"]}", $"{k["ConAct"]} links • {k["NoActItems"]} w/o activity", () => Open(EntityCatalog.Activity));
            Tile("BOM lines with flags", $"{k["Flags"]:N0}", "corrected / to check", OpenReports);

            var help = new Label
            {
                AutoSize = false, Width = 1150, Height = 200, UseMnemonic = false, Margin = new Padding(8, 24, 8, 8), ForeColor = Color.DimGray, Font = new Font("Arial", 10f),
                Text = "Getting started\n" +
                       "1.  6. Import / Export > Import contractor workbook – contractors, work orders, BOQ with external (supply) and internal (agency) rates, RA bills, drawing scope. Activities are linked automatically.\n" +
                       "2.  6. Import / Export > Import BOM workbook – all BOM sheets in one standard layout; packages, drawing master and drawing register are created.\n" +
                       "3.  3. Engineering > Drawing Master / Drawing Register – Upload drawing (or Bulk upload drawings: files are matched by drawing number). Every record has NOTES and ATTACHMENTS tabs.\n" +
                       "4.  Any screen: Table view / Form view (F2), Fields & formulas for your own fields, Excel import / export, PDF list and record card.\n" +
                       "5.  5. Reports – preview, filter by package / agency, export to PDF or Excel (with your logo from 7. Settings > Company, logo & database)."
            };
            flow.SetFlowBreak(flow.Controls[^1], true);
            flow.Controls.Add(help);
        }
        catch (Exception ex) { flow.Controls.Add(new Label { Text = "Dashboard error: " + ex.Message, AutoSize = true }); }
    }
}
