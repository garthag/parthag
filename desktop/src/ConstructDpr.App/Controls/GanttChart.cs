using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ConstructDpr.Core.Scheduling;

namespace ConstructDpr.App.Controls;

/// <summary>
/// Custom Gantt chart. Geometry comes from <see cref="GanttLayout"/> (shared with the PDF report);
/// this control only draws it: activity table, timeline header, baseline and current bars,
/// progress, milestones, dependency arrows, critical path in red and the data-date line.
/// </summary>
public sealed class GanttChart : FrameworkElement
{
    public const double TableWidth = 480;
    public const double HeaderHeight = 46;
    private static readonly (string Title, double Width)[] Columns = [("Code", 64), ("Activity", 210), ("Start", 74), ("Finish", 74), ("TF", 38)];

    private static readonly Brush HeaderBg = Frozen(Color.FromRgb(0xE6, 0xEE, 0xF4));
    private static readonly Brush HeaderFg = Frozen(Color.FromRgb(0x0B, 0x22, 0x36));
    private static readonly Brush Alt = Frozen(Color.FromRgb(0xF7, 0xF9, 0xFB));
    private static readonly Brush Text = Frozen(Color.FromRgb(0x22, 0x2B, 0x33));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x5B, 0x66, 0x70));
    private static readonly Brush Normal = Frozen(Color.FromRgb(0x1F, 0x6F, 0xA3));
    private static readonly Brush NormalDone = Frozen(Color.FromRgb(0x0F, 0x3D, 0x5E));
    private static readonly Brush Critical = Frozen(Color.FromRgb(0xD8, 0x4B, 0x4B));
    private static readonly Brush CriticalDone = Frozen(Color.FromRgb(0x9B, 0x1C, 0x1C));
    private static readonly Brush Weekend = Frozen(Color.FromArgb(0x10, 0x0B, 0x22, 0x36));
    private static readonly Brush TodayText = Frozen(Color.FromRgb(0xB2, 0x6B, 0x00));
    private static readonly Brush Baseline = Frozen(Color.FromRgb(0xB5, 0xBE, 0xC7));
    private static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xE4, 0xE8, 0xEC), 1);
    private static readonly Pen MajorPen = FrozenPen(Color.FromRgb(0xC5, 0xCE, 0xD6), 1);
    private static readonly Pen LinkPen = FrozenPen(Color.FromRgb(0x6B, 0x77, 0x82), 1);
    private static readonly Pen CriticalLinkPen = FrozenPen(Color.FromRgb(0xC6, 0x28, 0x28), 1.2);
    private static readonly Pen TodayPen = MakeTodayPen();

    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(GanttModel), typeof(GanttChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public GanttModel? Model
    {
        get => (GanttModel?)GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    public GanttChart()
    {
        ToolTipService.SetInitialShowDelay(this, 150);
        SnapsToDevicePixels = true;
    }

    protected override Size MeasureOverride(Size availableSize) => Model is null
        ? new Size(0, 0)
        : new Size(TableWidth + Model.TotalWidth, HeaderHeight + Model.Rows.Count * Model.RowHeight + 1);

    protected override void OnRender(DrawingContext dc)
    {
        var m = Model;
        if (m is null) return;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var height = HeaderHeight + m.Rows.Count * m.RowHeight;
        var width = TableWidth + m.TotalWidth;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

        // Row shading
        for (var i = 0; i < m.Rows.Count; i++)
            if (i % 2 == 1) dc.DrawRectangle(Alt, null, new Rect(0, HeaderHeight + i * m.RowHeight, width, m.RowHeight));

        // Weekend shading (Sundays) and timeline grid
        for (var d = m.From; d <= m.To; d = d.AddDays(1))
            if (d.DayOfWeek == DayOfWeek.Sunday)
                dc.DrawRectangle(Weekend, null,
                    new Rect(TableWidth + (d.DayNumber - m.From.DayNumber) * m.DayWidth, HeaderHeight, m.DayWidth, height - HeaderHeight));

        dc.DrawRectangle(HeaderBg, null, new Rect(0, 0, width, HeaderHeight));
        foreach (var t in m.Ticks)
        {
            var x = TableWidth + t.X;
            dc.DrawLine(t.Major ? MajorPen : GridPen, new Point(x, t.Major ? 0 : HeaderHeight / 2), new Point(x, height));
            Draw(dc, t.Label, x + 3, t.Major ? 5 : HeaderHeight / 2 + 5, t.Major ? 12 : 11, t.Major ? HeaderFg : Muted, dip, t.Major ? FontWeights.SemiBold : FontWeights.Normal);
        }
        dc.DrawLine(MajorPen, new Point(TableWidth, HeaderHeight / 2), new Point(width, HeaderHeight / 2));

        // Table header + rows
        double cx = 0;
        foreach (var (title, w) in Columns)
        {
            Draw(dc, title, cx + 6, 15, 12, HeaderFg, dip, FontWeights.SemiBold);
            cx += w;
        }
        for (var i = 0; i < m.Rows.Count; i++)
        {
            var r = m.Rows[i];
            var y = HeaderHeight + i * m.RowHeight + (m.RowHeight - 15) / 2;
            var fg = r.Critical ? CriticalDone : Text;
            string[] cells = [r.Code, r.Name, r.Start?.ToString("dd-MMM-yy") ?? "", r.Finish?.ToString("dd-MMM-yy") ?? "", r.TotalFloat?.ToString() ?? ""];
            cx = 0;
            for (var c = 0; c < cells.Length; c++)
            {
                Draw(dc, cells[c], cx + 6, y, 12, fg, dip, c == 0 ? FontWeights.SemiBold : FontWeights.Normal, Columns[c].Width - 10);
                cx += Columns[c].Width;
            }
        }
        dc.DrawLine(MajorPen, new Point(TableWidth - 0.5, 0), new Point(TableWidth - 0.5, height));
        dc.DrawLine(MajorPen, new Point(0, HeaderHeight), new Point(width, HeaderHeight));

        // Bars
        foreach (var b in m.Bars)
        {
            var top = HeaderHeight + b.Row * m.RowHeight;
            if (b.BaselineX is { } bx && b.BaselineWidth is { } bw)
                dc.DrawRoundedRectangle(Baseline, null, new Rect(TableWidth + bx, top + m.RowHeight - 7, bw, 4), 1.5, 1.5);
            var x = TableWidth + b.X;
            if (b.Milestone)
            {
                var cy = top + m.RowHeight / 2 - 2;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(x, cy - 7), true, true);
                    ctx.PolyLineTo([new Point(x + 7, cy), new Point(x, cy + 7), new Point(x - 7, cy)], true, true);
                }
                g.Freeze();
                dc.DrawGeometry(b.Critical ? CriticalDone : NormalDone, null, g);
                Draw(dc, b.Label, x + 11, cy - 8, 11, Muted, dip);
                continue;
            }
            var rect = new Rect(x, top + 5, b.Width, m.RowHeight - 14);
            dc.DrawRoundedRectangle(b.Critical ? Critical : Normal, null, rect, 3, 3);
            if (b.ProgressFraction > 0)
                dc.DrawRoundedRectangle(b.Critical ? CriticalDone : NormalDone, null,
                    new Rect(rect.X, rect.Y, Math.Max(2, rect.Width * Math.Min(1, b.ProgressFraction)), rect.Height), 3, 3);
            Draw(dc, b.Label, rect.Right + 5, rect.Y - 1, 11, Muted, dip);
        }

        // Dependency arrows (elbow connectors)
        foreach (var l in m.Links)
        {
            var pen = l.Critical ? CriticalLinkPen : LinkPen;
            var x1 = TableWidth + l.X1; var x2 = TableWidth + l.X2;
            var y1 = HeaderHeight + l.Y1 - 2; var y2 = HeaderHeight + l.Y2 - 2;
            var elbow = x1 + 6;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x1, y1), false, false);
                if (x2 - 6 >= x1 + 6)
                    ctx.PolyLineTo([new Point(elbow, y1), new Point(elbow, y2), new Point(x2, y2)], true, false);
                else
                {
                    var mid = y1 + (y2 > y1 ? m.RowHeight / 2 : -m.RowHeight / 2);
                    ctx.PolyLineTo([new Point(x1 + 6, y1), new Point(x1 + 6, mid), new Point(x2 - 6, mid), new Point(x2 - 6, y2), new Point(x2, y2)], true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(new Point(x2, y2), true, true);
                ctx.PolyLineTo([new Point(x2 - 5, y2 - 3.5), new Point(x2 - 5, y2 + 3.5)], true, true);
            }
            head.Freeze();
            dc.DrawGeometry(pen.Brush, null, head);
        }

        if (m.TodayX is { } tx)
        {
            dc.DrawLine(TodayPen, new Point(TableWidth + tx, HeaderHeight), new Point(TableWidth + tx, height));
            Draw(dc, "Today", TableWidth + tx + 3, HeaderHeight + 2, 10, TodayText, dip, FontWeights.SemiBold);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var m = Model;
        if (m is null) return;
        var p = e.GetPosition(this);
        var row = (int)((p.Y - HeaderHeight) / m.RowHeight);
        if (p.Y < HeaderHeight || row < 0 || row >= m.Rows.Count) { ToolTip = null; return; }
        var r = m.Rows[row];
        var tip = $"{r.Code}  {r.Name}\nStart {r.Start:dd MMM yyyy} · Finish {r.Finish:dd MMM yyyy}\nTotal float {r.TotalFloat?.ToString() ?? "—"} d · progress {r.ProgressPct:0.#}%" + (r.Critical ? "\nOn the critical path" : "");
        if (ToolTip as string != tip) ToolTip = tip;
    }

    private static void Draw(DrawingContext dc, string text, double x, double y, double size, Brush brush, double dip, FontWeight? weight = null, double maxWidth = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush, dip);
        if (maxWidth > 0) { ft.MaxTextWidth = maxWidth; ft.MaxLineCount = 1; ft.Trimming = TextTrimming.CharacterEllipsis; }
        dc.DrawText(ft, new Point(x, y));
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(Frozen(c), w); p.Freeze(); return p; }
    private static Pen MakeTodayPen()
    {
        var p = new Pen(Frozen(Color.FromRgb(0xF5, 0xA5, 0x24)), 1.6) { DashStyle = new DashStyle([4, 3], 0) };
        p.Freeze();
        return p;
    }
}
