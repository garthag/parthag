using System.Data;
using BSL.Core.Metadata;
using BSL.Desktop.Controls;
using Microsoft.Data.SqlClient;

namespace BSL.Desktop.Forms;

/// <summary>Main window: menu, navigation tree, tabbed screens, dashboard.</summary>
public sealed class MainForm : Form
{
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Padding = new Point(14, 4) };
    readonly TreeView _nav = new() { Dock = DockStyle.Fill, HideSelection = false, FullRowSelect = true, ItemHeight = 24, Font = new Font("Segoe UI", 9.75f), BorderStyle = BorderStyle.None };
    readonly ToolStripStatusLabel _db = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public MainForm()
    {
        Text = "BSL Project Control – Drawings, BOM, Contractors & RA Bills";
        Width = 1400; Height = 860; StartPosition = FormStartPosition.CenterScreen; WindowState = FormWindowState.Maximized;
        Font = new Font("Segoe UI", 9f);
        KeyPreview = true;

        MainMenuStrip = BuildMenu();
        BuildNav();
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(_nav);
        split.Panel2.Controls.Add(_tabs);
        var status = new StatusStrip();
        status.Items.Add(_db);
        status.Items.Add(new ToolStripStatusLabel("Ctrl+S save • Ctrl+N new • F2 table/form • F5 refresh • Ctrl+F search • Ctrl+W close tab"));
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
        Load += (_, _) => { split.SplitterDistance = 270; UpdateDbLabel(); ShowHome(); };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.W) && _tabs.SelectedTab is { Tag: not "home" } t) { CloseTab(t); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    MenuStrip BuildMenu()
    {
        var m = new MenuStrip();
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Home / dashboard", null, (_, _) => ShowHome());
        file.DropDownItems.Add("&Settings (database, report header)…", null, (_, _) => OpenSettings());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());
        m.Items.Add(file);

        foreach (var module in EntityCatalog.All.GroupBy(e => e.Module))
        {
            var mi = new ToolStripMenuItem(module.Key.Replace("&", "&&"));
            foreach (var e in module) mi.DropDownItems.Add(e.Caption.Replace("&", "&&"), null, (_, _) => Open(e));
            m.Items.Add(mi);
        }

        var io = new ToolStripMenuItem("&Import / Export");
        io.DropDownItems.Add("Import / standardise &BOM workbook…", null, (_, _) => Wizard(WorkbookKind.Bom));
        io.DropDownItems.Add("Import &contractor workbook (contractors, WOs, BOQ, RA)…", null, (_, _) => Wizard(WorkbookKind.Contractor));
        io.DropDownItems.Add(new ToolStripSeparator());
        io.DropDownItems.Add("Each screen has Excel > Export / Import / Template", null, null!).Enabled = false;
        m.Items.Add(io);

        var rep = new ToolStripMenuItem("&Reports");
        rep.DropDownItems.Add("Open &reports…", null, (_, _) => OpenReports());
        m.Items.Add(rep);

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("User defined fields – choose screen…", null, (_, _) =>
        {
            var e = PickDialog.Show("User defined fields", "Screen:", EntityCatalog.All.Where(x => x.Fields.Any(f => !f.ReadOnly)).Select(x => x.Caption).ToList());
            if (e == null) return;
            var def = EntityCatalog.All.First(x => x.Caption == e);
            using var f = new UdfManagerForm(def, Program.Repo.Load(def, "1 = 0"));
            f.ShowDialog(this);
            foreach (var b in Browsers().Where(b => b.Entity == def)) b.Reload();
        });
        tools.DropDownItems.Add("Drop-down lists…", null, (_, _) => Open(EntityCatalog.LookupValue));
        tools.DropDownItems.Add("Import history…", null, (_, _) => Open(EntityCatalog.ImportBatch));
        m.Items.Add(tools);

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&About", null, (_, _) => Ui.Info(
            "BSL Project Control 1.0\n\nDrawing register & master, standardised BOM, contractor & work order master, " +
            "internal / external rates, BOQ, RA bills, user defined fields with formulas, Excel import/export and PDF reports.\n\n" +
            "Database: " + new SqlConnectionStringBuilder(Program.Db.ConnectionString).InitialCatalog));
        m.Items.Add(help);
        return m;
    }

    void BuildNav()
    {
        var home = _nav.Nodes.Add("Dashboard");
        home.Tag = "home";
        foreach (var module in EntityCatalog.All.GroupBy(e => e.Module))
        {
            var n = _nav.Nodes.Add(module.Key);
            n.NodeFont = new Font("Segoe UI", 9.75f, FontStyle.Bold);
            foreach (var e in module) n.Nodes.Add(new TreeNode(e.Caption) { Tag = e });
            n.Expand();
        }
        var io = _nav.Nodes.Add("Import / Export");
        io.NodeFont = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        io.Nodes.Add(new TreeNode("Import / standardise BOM workbook") { Tag = WorkbookKind.Bom });
        io.Nodes.Add(new TreeNode("Import contractor workbook") { Tag = WorkbookKind.Contractor });
        io.Expand();
        var rep = _nav.Nodes.Add("Reports (PDF / Excel)");
        rep.NodeFont = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        home.NodeFont = new Font("Segoe UI", 9.75f, FontStyle.Bold);
        rep.Tag = "reports";
        _nav.NodeMouseClick += (_, e) =>
        {
            switch (e.Node.Tag)
            {
                case EntityDef d: Open(d); break;
                case WorkbookKind k: Wizard(k); break;
                case "home": ShowHome(); break;
                case "reports": OpenReports(); break;
            }
        };
    }

    IEnumerable<EntityBrowser> Browsers() => _tabs.TabPages.Cast<TabPage>().SelectMany(p => p.Controls.OfType<EntityBrowser>());

    public void Open(EntityDef e) => OpenBrowser(new EntityBrowser(e), e.Name);

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
        _db.Text = $"Database: {b.InitialCatalog} on {b.DataSource}   •   User: {Program.Settings.PreparedBy}";
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
                       (SELECT COUNT(*) FROM dbo.BomItem WHERE QualityFlags IS NOT NULL AND QualityFlags NOT LIKE N'detail of%') AS Flags
                """).Rows[0];
            decimal D(string c) => k[c] is DBNull ? 0 : Convert.ToDecimal(k[c]);
            var head = new Label { Text = "Project dashboard", Font = new Font("Segoe UI", 16f, FontStyle.Bold), ForeColor = Ui.Accent, Dock = DockStyle.Top, Height = 48, Padding = new Padding(20, 12, 0, 0) };
            page.Controls.Add(head);
            void Tile(string title, string value, string sub, Action? click = null)
            {
                var p = new Panel { Width = 250, Height = 110, Margin = new Padding(8), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Cursor = click != null ? Cursors.Hand : Cursors.Default };
                p.Controls.Add(new Label { Text = sub, Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(10, 2, 0, 0) });
                p.Controls.Add(new Label { Text = value, Dock = DockStyle.Top, Height = 46, Font = new Font("Segoe UI", 20f, FontStyle.Bold), ForeColor = Ui.Accent, Padding = new Padding(8, 0, 0, 0) });
                p.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Padding = new Padding(10, 6, 0, 0) });
                if (click != null) foreach (Control c in p.Controls.Cast<Control>().Append(p)) c.Click += (_, _) => click();
                flow.Controls.Add(p);
            }
            Tile("Contractors / agencies", $"{k["Agencies"]}", $"{k["WorkOrders"]} work orders", () => Open(EntityCatalog.Contractor));
            Tile("Work order value", $"₹ {D("WOValue") / 10000000m:N2} Cr", "sum of all WOs", () => Open(EntityCatalog.WorkOrder));
            Tile("Drawings", $"{k["Drawings"]:N0}", $"{k["Sheets"]:N0} sheets / revisions", () => Open(EntityCatalog.DrawingMaster));
            Tile("BOM weight (all sets)", $"{D("BomTon"):N1} MT", $"{k["Packages"]} packages • {k["BomLines"]:N0} lines", () => Open(EntityCatalog.Package));
            Tile("Contract scope (R1)", $"{D("ScopeTon"):N1} MT", $"executed {D("ExecTon"):N1} MT", () => Open(EntityCatalog.ContractItem));
            Tile("External vs internal", $"₹ {(D("ExtAmt") - D("IntAmt")) / 100000m:N1} L", $"margin • ext ₹{D("ExtAmt") / 100000m:N1} L", OpenReports);
            Tile("BOM lines with flags", $"{k["Flags"]:N0}", "corrected / to check", OpenReports);

            var help = new Label
            {
                AutoSize = false, Width = 1100, Height = 170, UseMnemonic = false, Margin = new Padding(8, 24, 8, 8), ForeColor = Color.DimGray, Font = new Font("Segoe UI", 9.5f),
                Text = "Getting started\n" +
                       "1.  Import / Export > Import contractor workbook – loads contractors, work orders (BM01…), BOQ with external (supply) and internal (agency) rates, RA bills and the drawing scope list.\n" +
                       "2.  Import / Export > Import / standardise BOM workbook – all BOM sheets are mapped to one standard layout; packages, drawing master and drawing register are created.\n" +
                       "3.  Open any screen: switch between Table view and Form view (F2). Add your own fields under 'Fields & formulas' – formulas can use contractor details and rates.\n" +
                       "4.  Reports: preview, filter by package / agency and export to PDF or Excel. Every screen also has Excel export / import and PDF list / record card."
            };
            flow.SetFlowBreak(flow.Controls[^1], true);
            flow.Controls.Add(help);
        }
        catch (Exception ex) { flow.Controls.Add(new Label { Text = "Dashboard error: " + ex.Message, AutoSize = true }); }
    }
}
