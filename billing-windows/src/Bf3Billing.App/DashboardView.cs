using System.Data;
using Bf3Billing.Core;

namespace Bf3Billing.App;

/// <summary>
/// The dashboard. Every block is placed and sized here, in <see cref="Relayout"/>, from the width of the tab:
/// nothing depends on docking or on Control.Visible (which reads false while the tab is not showing).
/// </summary>
sealed class DashboardView : Panel, ITabView
{
    readonly IAppHost _host;
    readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Ui.Back };
    readonly Panel _kpis = new() { BackColor = Ui.Back };
    readonly Block _start = new(), _attention = new(), _progress = new(), _boq = new(), _contractors = new(), _workOrders = new();
    readonly DataGridView _contractorGrid = Ui.Grid(false);
    readonly DataGridView _woGrid = Ui.Grid(false);
    const int Gap = 14, Pad = 14;
    /// <summary>Self test: the number of KPI tiles shown (0 means the dashboard was never filled).</summary>
    internal int KpiTiles => _kpis.Controls.Count;

    /// <summary>A white card whose content is a list of controls laid out top to bottom.</summary>
    sealed class Block
    {
        public readonly Ui.Card Card = new() { Padding = new Padding(0), Margin = new Padding(0) };
        public readonly List<Control> Items = new();
        public bool Show = true;

        public void Set(params Control[] items)
        {
            Card.SuspendLayout();
            foreach (var c in Items) if (c is not DataGridView) c.Dispose();
            Card.Controls.Clear();
            Items.Clear();
            Items.AddRange(items);
            foreach (var c in items) { c.Dock = DockStyle.None; c.Anchor = AnchorStyles.Top | AnchorStyles.Left; Card.Controls.Add(c); }
            Card.ResumeLayout(false);
        }
    }

    public DashboardView(IAppHost host)
    {
        _host = host;
        BackColor = Ui.Back;
        _contractorGrid.ScrollBars = ScrollBars.Horizontal;
        _woGrid.ScrollBars = ScrollBars.Horizontal;
        _woGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && Ui.SelectedId(_woGrid) is long id) _host.ShowBills(new BillFilter { WorkOrderId = id });
        };
        _scroll.Controls.Add(_kpis);
        foreach (var b in Blocks) _scroll.Controls.Add(b.Card);
        Controls.Add(_scroll);
        _scroll.Resize += (_, _) => Relayout();
        VisibleChanged += (_, _) => Relayout();
    }

    IEnumerable<Block> Blocks => new[] { _start, _attention, _progress, _boq, _contractors, _workOrders };

    static Label Title(string text, float size = 10.5f) => new() { Text = text, AutoSize = false, Font = new Font("Segoe UI Semibold", size), ForeColor = Ui.Ink };
    static Label Note(string text, bool muted = true) => new() { Text = text, AutoSize = false, Font = muted ? Ui.Small : Ui.Base, ForeColor = muted ? Ui.Muted : Ui.Ink };

    static Control Kpi(string value, string caption, bool warn = false)
    {
        var card = new Ui.Card { Size = new Size(210, 76), Margin = new Padding(0) };
        card.Controls.Add(new Label { Text = value, AutoSize = true, Font = Ui.Kpi, ForeColor = warn ? Ui.Bad : Ui.Ink, Location = new Point(12, 8) });
        card.Controls.Add(new Label { Text = caption, AutoSize = false, Font = Ui.Small, ForeColor = Ui.Muted, Location = new Point(14, 42), Size = new Size(190, 30) });
        return card;
    }

    /// <summary>Height a grid needs for all its rows, from the table (its rows may not exist yet).</summary>
    static int GridHeight(DataGridView g, int rows) => 34 + Math.Max(1, rows) * g.RowTemplate.Height + 4 + SystemInformation.HorizontalScrollBarHeight;

    void Relayout()
    {
        var w = Math.Max(560, _scroll.ClientSize.Width - 2 * Gap - SystemInformation.VerticalScrollBarWidth);
        var inner = w - 2 * Pad;
        _scroll.SuspendLayout();
        var y = Gap - Math.Abs(_scroll.AutoScrollPosition.Y);
        var x = Gap + _scroll.AutoScrollPosition.X;

        // KPI tiles, wrapping.
        var perRow = Math.Max(1, (w + 12) / (210 + 12));
        var i = 0;
        foreach (Control k in _kpis.Controls)
        {
            k.Location = new Point((i % perRow) * (210 + 12), (i / perRow) * (76 + 12));
            i++;
        }
        var kpiRows = (int)Math.Ceiling(_kpis.Controls.Count / (double)perRow);
        _kpis.Bounds = new Rectangle(x, y, w, Math.Max(0, kpiRows * 88 - 12));
        y += _kpis.Height + Gap;

        foreach (var b in Blocks)
        {
            b.Card.Visible = b.Show;
            if (!b.Show) continue;
            var cy = Pad;
            foreach (var c in b.Items)
            {
                int h;
                switch (c)
                {
                    case DataGridView g:
                        c.Width = inner;
                        h = GridHeight(g, (g.DataSource as DataTable)?.Rows.Count ?? 0);
                        break;
                    case Label l:
                        c.Width = inner;
                        h = l.GetPreferredSize(new Size(inner, 0)).Height + 4;
                        break;
                    case FlowLayoutPanel f:
                        c.Width = inner;
                        h = f.GetPreferredSize(new Size(inner, 0)).Height;
                        break;
                    default:
                        c.Width = inner;
                        h = c.Height;
                        break;
                }
                c.Bounds = new Rectangle(Pad, cy, inner, h);
                cy += h + 6;
            }
            b.Card.Bounds = new Rectangle(x, y, w, cy - 6 + Pad);
            b.Card.Invalidate();
            y += b.Card.Height + Gap;
        }
        _scroll.AutoScrollMinSize = new Size(0, y - _scroll.AutoScrollPosition.Y);
        _scroll.ResumeLayout(true);
    }

    /// <summary>One row per activity: name, bar, tonnes and %.</summary>
    static Control ProgressRows(IEnumerable<(string Name, decimal Tonnes, decimal Pct)> rows)
    {
        var p = new Panel();
        var list = rows.ToList();
        p.Height = list.Count * 26;
        p.Resize += (_, _) =>
        {
            var wBar = Math.Max(60, p.Width - 160 - 200);
            foreach (Control c in p.Controls)
                if (c is ProgressStrip s) s.Width = wBar;
                else if (c.Tag as string == "value") c.Left = p.Width - 195;
        };
        for (var i = 0; i < list.Count; i++)
        {
            var (name, t, pct) = list[i];
            var top = i * 26;
            p.Controls.Add(new Label { Text = name, AutoSize = false, Bounds = new Rectangle(0, top + 4, 155, 20) });
            p.Controls.Add(new ProgressStrip { Percent = pct, Bounds = new Rectangle(160, top + 6, 300, 14), AccessibleName = $"{name} {Fmt.Pct(pct)} percent" });
            p.Controls.Add(new Label { Text = $"{Fmt.Tonnes(t)} t · {Fmt.Pct(pct)}%", AutoSize = false, TextAlign = ContentAlignment.TopRight, Tag = "value", Bounds = new Rectangle(500, top + 4, 195, 20) });
        }
        return p;
    }

    public void RefreshData()
    {
        var repo = _host.Repo;
        var d = repo.GetDashboard();

        foreach (Control c in _kpis.Controls.Cast<Control>().ToList()) c.Dispose();
        _kpis.Controls.Clear();
        _kpis.Controls.Add(Kpi(d.BillCount.ToString(Fmt.Culture), "bills entered"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Gross), "gross billed"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Net), "net payable (after GST and deductions)"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Paid), "paid"));
        _kpis.Controls.Add(Kpi(Fmt.Short(d.Outstanding), "outstanding", d.Outstanding > 0));

        // First start: no bills yet.
        _start.Show = d.BillCount == 0;
        if (_start.Show)
        {
            var text = d.BomDrawings > 0
                ? $"The contractors, work orders, activities and the BOM ({d.BomDrawings:N0} drawings, {Fmt.Tonnes(d.BomTonnes)} t) are loaded. Enter the first bill, or bring bills in from Excel."
                : "The contractors, work orders and activities are loaded. The BOM is not loaded yet: Data › Import BSL workbooks (or Import from Excel) loads it, so BOM lines can be billed against drawings. Then enter the first bill, or bring bills in from Excel.";
            var buttons = new FlowLayoutPanel { WrapContents = true, Margin = new Padding(0) };
            buttons.Controls.Add(Ui.Btn("+ New bill", (_, _) => _host.OpenBill(null), primary: true));
            buttons.Controls.Add(Ui.Btn("Import BSL workbooks…", (_, _) => DataView.OpenBsl(_host)));
            buttons.Controls.Add(Ui.Btn("Import from Excel…", (_, _) => DataView.OpenWizard(_host)));
            buttons.Controls.Add(Ui.Btn("Set rates and defaults", (_, _) => _host.ShowTab("Masters")));
            _start.Set(Title("No bills yet", 12f), Note(text, muted: false), buttons);
        }

        // Needs attention.
        _attention.Show = d.BillCount > 0;
        if (_attention.Show)
        {
            var links = new FlowLayoutPanel { WrapContents = true, Margin = new Padding(0) };
            void Link(string text, BillFilter f)
            {
                var l = new LinkLabel { Text = text, AutoSize = true, LinkColor = Ui.Link, Margin = new Padding(0, 0, 24, 4) };
                l.LinkClicked += (_, _) => _host.ShowBills(f);
                links.Controls.Add(l);
            }
            if (d.OverScopeLines > 0) Link($"{d.OverScopeLines} bill line(s) above BOM or BOQ scope", new BillFilter { OnlyOverScope = true });
            if (d.DraftBills > 0) Link($"{d.DraftBills} bill(s) still in Draft", new BillFilter { Status = BillStatus.Draft });
            var submitted = repo.BillSummaries(new BillFilter { Status = BillStatus.Submitted }).Count;
            if (submitted > 0) Link($"{submitted} bill(s) submitted, not certified", new BillFilter { Status = BillStatus.Submitted });
            var certifiedDue = repo.BillSummaries(new BillFilter { Status = BillStatus.Certified }).Count(b => b.Due > 0.005m);
            if (certifiedDue > 0) Link($"{certifiedDue} certified bill(s) awaiting payment", new BillFilter { Status = BillStatus.Certified });
            var overWo = d.WorkOrders.Count(w => w.Value > 0 && w.Gross > w.Value);
            if (overWo > 0) Link($"{overWo} work order(s) billed above their value", new BillFilter());
            if (links.Controls.Count == 0) links.Controls.Add(new Label { Text = "Nothing needs attention.", AutoSize = true, ForeColor = Ui.Muted });
            _attention.Set(Title("Needs attention"), links);
        }

        // BOM progress by activity.
        var bomHint = d.BomTonnes > 0 ? $"Tonnes billed on BOM lines against the {Fmt.Tonnes(d.BomTonnes)} t BOM ({d.BomDrawings:N0} drawings)" : "No BOM loaded yet (Data › Import BSL workbooks)";
        _progress.Set(Title("BOM progress by activity"), Note(bomHint), ProgressRows(d.Progress.Select(p => (p.Activity, p.Tonnes, p.Percent))));

        // BOQ (contract items) progress.
        var boq = repo.BoqItems();
        _boq.Show = boq.Count > 0;
        if (_boq.Show)
        {
            var billed = repo.BoqBilled();
            var withScope = boq.Where(q => q.ScopeTonnes is > 0 && q.WorkOrderId != null).ToList();
            var scope = withScope.Sum(q => q.ScopeTonnes!.Value);
            var done = withScope.Sum(q => billed.GetValueOrDefault(q.Id).Tonnes);
            var over = withScope.Count(q => billed.GetValueOrDefault(q.Id).Tonnes > q.ScopeTonnes!.Value + 0.0005m);
            var rows = boq.Where(q => q.WorkOrderId != null && q.ScopeTonnes is > 0).GroupBy(q => q.Activity.Length > 0 ? q.Activity : "(no activity)")
                .Select(g => (g.Key, g.Sum(q => billed.GetValueOrDefault(q.Id).Tonnes), g.Sum(q => q.ScopeTonnes!.Value)))
                .Select(x => (x.Key, x.Item2, x.Item3 > 0 ? x.Item2 * 100m / x.Item3 : 0m));
            _boq.Set(Title("BOQ progress (contract items)"),
                Note($"{withScope.Count} items with an R1 scope of {Fmt.Tonnes(scope)} t · {Fmt.Tonnes(done)} t billed ({(scope > 0 ? Fmt.Pct(done * 100m / scope) : "0.0")}%) · {over} item(s) above R1 scope – see the BOQ tab"),
                ProgressRows(rows));
        }

        // By contractor.
        var ct = new DataTable();
        ct.Columns.Add("Contractor"); ct.Columns.Add("Work order value ₹", typeof(decimal)); ct.Columns.Add("Bills", typeof(int));
        ct.Columns.Add("Gross billed ₹", typeof(decimal)); ct.Columns.Add("% of WO value", typeof(decimal)); ct.Columns.Add("Net payable ₹", typeof(decimal));
        ct.Columns.Add("Paid ₹", typeof(decimal)); ct.Columns.Add("Outstanding ₹", typeof(decimal));
        foreach (var c in d.ByContractor) ct.Rows.Add(c.Contractor, c.WoValue, c.Bills, c.Gross, c.PercentOfWo, c.Net, c.Paid, c.Outstanding);
        Ui.Bind(_contractorGrid, ct, "N0");
        _contractors.Set(Title("By contractor"), _contractorGrid);

        // Work order utilisation.
        var wt = new DataTable();
        wt.Columns.Add("_Id", typeof(long)); wt.Columns.Add("Work order"); wt.Columns.Add("Contractor"); wt.Columns.Add("Service");
        wt.Columns.Add("Value ₹", typeof(decimal)); wt.Columns.Add("Gross billed ₹", typeof(decimal)); wt.Columns.Add("Balance ₹", typeof(decimal));
        wt.Columns.Add("Used", typeof(decimal));
        foreach (var w in d.WorkOrders) wt.Rows.Add(w.WorkOrderId, w.WoNo, w.Contractor, w.Service, w.Value, w.Gross, w.Balance, (object?)w.UsedPercent ?? DBNull.Value);
        Ui.Bind(_woGrid, wt, "N0", g => g.Columns["Used"]!.DefaultCellStyle.Format = "0.0'%'");
        _woGrid.CellFormatting -= RedOverValue;
        _woGrid.CellFormatting += RedOverValue;
        _workOrders.Set(Title("Work order utilisation"), Note("Double-click a work order to see its bills. Work orders billed above their value are shown in red."), _woGrid);

        Relayout();
    }

    void RedOverValue(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.RowIndex >= 0 && _woGrid.Rows[e.RowIndex].DataBoundItem is DataRowView v && v["Balance ₹"] is decimal bal && bal < 0)
            e.CellStyle!.ForeColor = Ui.Bad;
    }
}
