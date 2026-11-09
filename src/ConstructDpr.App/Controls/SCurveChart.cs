using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ConstructDpr.Shared.Api;

namespace ConstructDpr.App.Controls;

/// <summary>Planned vs actual cumulative progress (S-curve).</summary>
public sealed class SCurveChart : FrameworkElement
{
    private static readonly Pen Axis = Make(Color.FromRgb(0x52, 0x51, 0x4E), 1);
    private static readonly Pen Grid = Make(Color.FromRgb(0xE4, 0xE3, 0xDF), 1);
    private static readonly Pen Planned = Make(Color.FromRgb(0x2A, 0x78, 0xD6), 2.2);
    private static readonly Pen Actual = Make(Color.FromRgb(0xEB, 0x68, 0x34), 2.6);
    private static readonly Brush Label = Solid(Color.FromRgb(0x5B, 0x66, 0x70));

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IReadOnlyList<CurvePoint>), typeof(SCurveChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<CurvePoint>? Points
    {
        get => (IReadOnlyList<CurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var pts = Points;
        if (pts is null || pts.Count < 2) { Text(dc, "No baseline yet — run the schedule and freeze a baseline.", 10, 10, dip); return; }
        const double left = 44, top = 30, bottom = 28, right = 14;
        var w = ActualWidth - left - right;
        var h = ActualHeight - top - bottom;
        if (w <= 20 || h <= 20) return;
        var from = pts[0].Date.DayNumber;
        var span = Math.Max(1, pts[^1].Date.DayNumber - from);
        double X(DateOnly d) => left + (d.DayNumber - from) * w / span;
        double Y(double pct) => top + h - pct * h / 100;

        for (var p = 0; p <= 100; p += 25)
        {
            dc.DrawLine(Grid, new Point(left, Y(p)), new Point(left + w, Y(p)));
            Text(dc, p + "%", 6, Y(p) - 8, dip);
        }
        dc.DrawLine(Axis, new Point(left, top + h), new Point(left + w, top + h));
        var lastMonth = -1;
        foreach (var c in pts)
        {
            if (c.Date.Month == lastMonth) continue;
            lastMonth = c.Date.Month;
            Text(dc, c.Date.ToString("MMM yy"), X(c.Date) - 2, top + h + 6, dip);
        }

        Line(dc, Planned, pts.Select(c => new Point(X(c.Date), Y(c.Planned))));
        var actual = pts.Where(c => c.Actual is not null).ToList();
        Line(dc, Actual, actual.Select(c => new Point(X(c.Date), Y(c.Actual!.Value))));
        if (actual.Count > 0)
        {
            var last = actual[^1];
            dc.DrawEllipse(Actual.Brush, null, new Point(X(last.Date), Y(last.Actual!.Value)), 4, 4);
            Text(dc, $"{last.Actual:0.0}% actual", X(last.Date) + 7, Y(last.Actual.Value) - 18, dip, Actual.Brush);
            var planNow = pts.LastOrDefault(c => c.Date <= last.Date);
            if (planNow is not null) Text(dc, $"{planNow.Planned:0.0}% planned", X(last.Date) + 7, Y(planNow.Planned) - 4, dip, Planned.Brush);
        }

        // Legend
        dc.DrawLine(Planned, new Point(left, 12), new Point(left + 22, 12));
        Text(dc, "Planned (baseline)", left + 27, 4, dip);
        dc.DrawLine(Actual, new Point(left + 160, 12), new Point(left + 182, 12));
        Text(dc, "Actual (approved DPRs)", left + 187, 4, dip);
    }

    private static void Line(DrawingContext dc, Pen pen, IEnumerable<Point> points)
    {
        var list = points.ToList();
        if (list.Count < 2) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(list[0], false, false);
            ctx.PolyLineTo(list.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static void Text(DrawingContext dc, string s, double x, double y, double dip, Brush? brush = null) =>
        dc.DrawText(new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush ?? Label, dip), new Point(x, y));

    private static Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen Make(Color c, double w) { var p = new Pen(Solid(c), w) { LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }
}
