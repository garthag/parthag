using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

sealed class DashboardView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly FlowLayoutPanel _kpis = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    readonly Ui.Card _start = new() { Dock = DockStyle.Top };
    readonly Ui.Card _attention = new() { Dock = DockStyle.Top };
    readonly Ui.Card _progress = new() { Dock = DockStyle.Top };
    readonly Ui.Card _contractors = new() { Dock = DockStyle.Top };
    readonly Ui.Card _workOrders = new() { Dock = DockStyle.Top };
    readonly DataGridView _contractorGrid = Ui.Grid(false);
    readonly DataGridView _woGrid = Ui.Grid(false);

    public DashboardView(IAppHost host)
    {
        _host = host;
        AutoScroll = true;
        BackColor = Ui.Back;
        Padding = new Padding(4, 4, 12, 4);

        _contractorGrid.Dock = DockStyle.Top;
        _contractorGrid.ScrollBars = ScrollBars.Horizontal;
        _woGrid.Dock = DockStyle.Top;
        _woGrid.ScrollBars = ScrollBars.Horizontal;
        _woGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && Ui.SelectedId(_woGrid) is long id) _host.ShowBills(new BillFilter { WorkOrderId = id });
        };

        // Docked Top: added bottom-up.
        foreach (var c in new Control[] { Spacer(), _workOrders, Spacer(), _contractors, Spacer(), _progress, Spacer(), _attention, Spacer(), _start, _kpis })
            Controls.Add(c);
        Resize += (_, _) => FitCards();
        VisibleChanged += (_, _) => { if (Visible) FitCards(); };
    }

    static Panel Spacer() => new() { Dock = DockStyle.Top, Height = 12 };

    static Label Title(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = true, Font = Ui.Section, ForeColor = Ui.Ink, Padding = new Padding(0, 0, 0, 2) };
    static Label Hint(string text) => new() { Text = text, Dock = DockStyle.Top, AutoSize = true, Font = Ui.Small, ForeColor = Ui.Muted, Padding = new Padding(0, 0, 0, 8) };

    static Control Kpi(string value, string caption, bool warn = false)
    {
        var card = new Ui.Card { Width = 210, Height = 74, Margin = new Padding(0, 0, 12, 12), Padding = new Padding(14, 10, 10, 8) };
        var v = new Label { Text = value, AutoSize = true, Font = Ui.Kpi, ForeColor = warn ? Ui.Bad : Ui.Ink, Location = new Point(12, 8) };
        var c = new Label { Text = caption, AutoSize = false, Font = Ui.Small, ForeColor = Ui.Muted, Location = new Point(14, 42), Size = new Size(190, 30) };
        card.Controls.Add(v);
        card.Controls.Add(c);
        return card;
    }

    static void SetChildren(Control parent, params Control[] topDown)
    {
        parent.SuspendLayout();
        parent.Controls.Clear();
        for (var i = topDown.Length - 1; i >= 0; i--) parent.Controls.Add(topDown[i]);
        parent.ResumeLayout();
    }

    void FitCards()
    {
        foreach (var card in new Control[] { _start, _attention, _progress, _contractors, _workOrders })
        {
            if (!card.Visible) continue;
            foreach (var g in card.Controls.OfType<DataGridView>()) Ui.FitHeight(g);
            card.Height = card.Padding.Vertical + card.Controls.Cast<Control>().Where(c => c.Visible).Sum(c => c.Height + c.Margin.Vertical);
        }
    }

    public void RefreshData()
    {
        var repo = _host.Repo;
        var d = repo.GetDashboard();
        SuspendLayout();

        _kpis.Controls.Clear();
        _kpis.Controls.Add(Kpi(d.BillCount.ToString(Fmt.Culture), "bills entered"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Gross), "gross billed"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Net), "net payable (after GST and deductions)"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Paid), "paid"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Outstanding), "outstanding", d.Outstanding > 0));

        // First start: no bills yet.
        _start.Visible = d.BillCount == 0;
        if (_start.Visible)
        {
            var text = d.BomDrawings > 0
                ? $"The contractors, work orders, activities and the BOM ({d.BomDrawings:N0} drawings, {Fmt.Tonnes(d.BomTonnes)} t) are loaded. Enter the first bill, or bring bills in from Excel."
                : "The contractors, work orders and activities are loaded. The BOM is not loaded yet: import it from Excel so BOM lines can be billed against drawings. Then enter the first bill, or bring bills in from Excel.";
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = new Padding(0) };
            buttons.Controls.Add(Ui.Btn("+ New bill", (_, _) => _host.OpenBill(null), primary: true));
            buttons.Controls.Add(Ui.Btn("Import from Excel…", (_, _) => DataView.OpenWizard(_host)));
            buttons.Controls.Add(Ui.Btn("Set rates and defaults", (_, _) => _host.ShowTab("Masters")));
            var msg = new Label { Text = text, Dock = DockStyle.Top, AutoSize = false, Height = 44, ForeColor = Ui.Muted, Padding = new Padding(0, 6, 0, 6) };
            SetChildren(_start, new Label { Text = "No bills yet", Dock = DockStyle.Top, AutoSize = true, Font = new Font("Segoe UI Semibold", 12f) }, msg, buttons);
        }

        // Needs attention.
        _attention.Visible = d.BillCount > 0;
        if (_attention.Visible)
        {
            var links = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            void Link(string text, BillFilter f)
            {
                var l = new LinkLabel { Text = text, AutoSize = true, LinkColor = Ui.Link, Margin = new Padding(0, 0, 24, 0) };
                l.LinkClicked += (_, _) => _host.ShowBills(f);
                links.Controls.Add(l);
            }
            if (d.OverScopeLines > 0) Link($"{d.OverScopeLines} bill line(s) above BOM scope", new BillFilter { OnlyOverScope = true });
            if (d.DraftBills > 0) Link($"{d.DraftBills} bill(s) still in Draft", new BillFilter { Status = BillStatus.Draft });
            var submitted = repo.BillSummaries(new BillFilter { Status = BillStatus.Submitted }).Count;
            if (submitted > 0) Link($"{submitted} bill(s) submitted, not certified", new BillFilter { Status = BillStatus.Submitted });
            var certifiedDue = repo.BillSummaries(new BillFilter { Status = BillStatus.Certified }).Count(b => b.Due > 0.005m);
            if (certifiedDue > 0) Link($"{certifiedDue} certified bill(s) awaiting payment", new BillFilter { Status = BillStatus.Certified });
            if (links.Controls.Count == 0) links.Controls.Add(new Label { Text = "Nothing needs attention.", AutoSize = true, ForeColor = Ui.Muted });
            SetChildren(_attention, Title("Needs attention"), links);
        }

        // BOM progress by activity.
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 2, 0, 0) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        foreach (var p in d.Progress)
        {
            table.Controls.Add(new Label { Text = p.Activity, AutoSize = true, Margin = new Padding(0, 4, 0, 4) });
            table.Controls.Add(new ProgressStrip { Percent = p.Percent, Dock = DockStyle.Fill, Margin = new Padding(6, 6, 6, 4), AccessibleName = $"{p.Activity} {Fmt.Pct(p.Percent)} percent" });
            table.Controls.Add(new Label { Text = $"{Fmt.Tonnes(p.Tonnes)} t · {Fmt.Pct(p.Percent)}%", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) });
        }
        var bomHint = d.BomTonnes > 0 ? $"Tonnes billed against the {Fmt.Tonnes(d.BomTonnes)} t BOM" : "No BOM loaded yet (Data › Import from Excel)";
        SetChildren(_progress, Title("BOM progress by activity"), Hint(bomHint), table);

        // By contractor.
        var ct = new DataTable();
        ct.Columns.Add("Contractor"); ct.Columns.Add("Work order value ₹", typeof(decimal)); ct.Columns.Add("Bills", typeof(int));
        ct.Columns.Add("Gross billed ₹", typeof(decimal)); ct.Columns.Add("% of WO value", typeof(decimal)); ct.Columns.Add("Net payable ₹", typeof(decimal));
        ct.Columns.Add("Paid ₹", typeof(decimal)); ct.Columns.Add("Outstanding ₹", typeof(decimal));
        foreach (var c in d.ByContractor) ct.Rows.Add(c.Contractor, c.WoValue, c.Bills, c.Gross, c.PercentOfWo, c.Net, c.Paid, c.Outstanding);
        Ui.Bind(_contractorGrid, ct, "N0");
        SetChildren(_contractors, Title("By contractor"), new Panel { Dock = DockStyle.Top, Height = 8 }, _contractorGrid);

        // Work order utilisation.
        var wt = new DataTable();
        wt.Columns.Add("_Id", typeof(long)); wt.Columns.Add("Work order"); wt.Columns.Add("Contractor"); wt.Columns.Add("Service");
        wt.Columns.Add("Value ₹", typeof(decimal)); wt.Columns.Add("Gross billed ₹", typeof(decimal)); wt.Columns.Add("Balance ₹", typeof(decimal));
        wt.Columns.Add("Used", typeof(decimal));
        foreach (var w in d.WorkOrders) wt.Rows.Add(w.WorkOrderId, w.WoNo, w.Contractor, w.Service, w.Value, w.Gross, w.Balance, (object?)w.UsedPercent ?? DBNull.Value);
        Ui.Bind(_woGrid, wt, "N0", g => g.Columns["Used"]!.DefaultCellStyle.Format = "0.0'%'");
        SetChildren(_workOrders, Title("Work order utilisation"), Hint("Double-click a work order to see its bills"), _woGrid);

        ResumeLayout(true);
        FitCards();
        if (IsHandleCreated) BeginInvoke(FitCards);
    }
}
