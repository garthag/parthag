using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>What every tab needs from the main window.</summary>
interface IAppHost
{
    BillingRepository Repo { get; }
    IWin32Window Window { get; }
    /// <summary>Opens the bill editor. Returns true if the bill was saved or deleted.</summary>
    bool OpenBill(long? billId, long? workOrderId = null);
    void ShowBills(BillFilter filter);
    void ShowTab(string name);
    void SwitchDataFile(string path);
    /// <summary>Marks every tab as out of date (after data changed).</summary>
    void DataChanged();
}

interface ITabView
{
    void RefreshData();
}

sealed class MainForm : Form, IAppHost
{
    readonly AppSettings _settings;
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Font = Ui.Base, Padding = new Point(14, 5) };
    readonly Label _dataFile = new() { AutoSize = true, ForeColor = Ui.BlueText, Font = Ui.Small, Margin = new Padding(0, 2, 0, 0) };
    readonly HashSet<TabPage> _stale = new();
    readonly Dictionary<string, TabPage> _pages = new();
    BillsView _bills = null!;

    public BillingRepository Repo { get; private set; }
    public IWin32Window Window => this;

    public MainForm(BillingRepository repo, AppSettings settings)
    {
        Repo = repo;
        _settings = settings;
        Text = "BF#3 Billing Database";
        Font = Ui.Base;
        BackColor = Ui.Back;
        MinimumSize = new Size(900, 600);
        Size = new Size(1320, 900);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

        var header = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Ui.Blue, Padding = new Padding(16, 8, 16, 8) };
        var titles = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Blue };
        titles.Controls.Add(new Label { Text = "BF#3 Billing Database", AutoSize = true, ForeColor = Color.White, Font = Ui.Title, Margin = new Padding(0) });
        titles.Controls.Add(_dataFile);
        var newBill = Ui.Btn("+ New bill  (Ctrl+N)", (_, _) => OpenBill(null));
        newBill.Dock = DockStyle.Right;
        newBill.UseVisualStyleBackColor = true;
        newBill.Margin = new Padding(0);
        header.Controls.Add(titles);
        header.Controls.Add(newBill);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 8), BackColor = Ui.Back };
        body.Controls.Add(_tabs);
        Controls.Add(body);
        Controls.Add(header);

        BuildTabs();
        _tabs.SelectedIndexChanged += (_, _) => RefreshIfStale(_tabs.SelectedTab);
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.N) { e.Handled = true; OpenBill(null); }
            if (e.KeyCode == Keys.F5) { e.Handled = true; DataChanged(); }
        };
        ShowDataFile();
    }

    void BuildTabs()
    {
        _tabs.TabPages.Clear();
        _pages.Clear();
        _bills = new BillsView(this);
        Add("Dashboard", new DashboardView(this));
        Add("Bills", _bills);
        Add("BOM progress", new BomProgressView(this));
        Add("BOM", new BomView(this));
        Add("BOQ", new BoqView(this));
        Add("Work orders", new WorkOrdersView(this));
        Add("Contractors", new ContractorsView(this));
        Add("Reports", new ReportsView(this));
        Add("Masters", new MastersView(this));
        Add("Data", new DataView(this, _settings));
        DataChanged();
    }

    void Add(string name, Control view)
    {
        var page = new TabPage(name) { BackColor = Ui.Back, Padding = new Padding(8) };
        view.Dock = DockStyle.Fill;
        page.Controls.Add(view);
        _tabs.TabPages.Add(page);
        _pages[name] = page;
    }

    void ShowDataFile() => _dataFile.Text = "Data file: " + Repo.Db.FilePath;

    void RefreshIfStale(TabPage? page)
    {
        if (page == null || !_stale.Remove(page)) return;
        if (page.Controls.Count > 0 && page.Controls[0] is ITabView v)
        {
            Cursor = Cursors.WaitCursor;
            try { Ui.Try(this, v.RefreshData); }
            finally { Cursor = Cursors.Default; }
        }
    }

    public void DataChanged()
    {
        foreach (TabPage p in _tabs.TabPages) _stale.Add(p);
        RefreshIfStale(_tabs.SelectedTab);
    }

    public bool OpenBill(long? billId, long? workOrderId = null)
    {
        using var f = new BillEditorForm(Repo, billId, workOrderId);
        var changed = false;
        f.Saved += () => { changed = true; DataChanged(); };
        f.ShowDialog(this);
        return changed;
    }

    public void ShowBills(BillFilter filter)
    {
        _bills.ApplyFilter(filter);
        ShowTab("Bills");
    }

    public void ShowTab(string name)
    {
        if (_pages.TryGetValue(name, out var p)) _tabs.SelectedTab = p;
        RefreshIfStale(p);
    }

    public void SwitchDataFile(string path)
    {
        var repo = new BillingRepository(new BillingDb(path));
        Repo = repo;
        _settings.DataFile = repo.Db.FilePath;
        _settings.Save();
        ShowDataFile();
        var selected = _tabs.SelectedIndex;
        BuildTabs();
        _tabs.SelectedIndex = Math.Max(0, selected);
        DataChanged();
    }
}
