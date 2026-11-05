namespace ConstructDpr.Core.Scheduling;

public sealed record GanttBar(int ActivityId, int Row, double X, double Width, bool Milestone, bool Critical,
    double ProgressFraction, double? BaselineX, double? BaselineWidth, string Label);

public sealed record GanttLink(double X1, double Y1, double X2, double Y2, bool Critical);

public sealed record GanttTick(double X, string Label, bool Major);

public sealed record GanttRow(int ActivityId, string Code, string Name, DateOnly? Start, DateOnly? Finish, int? TotalFloat, bool Critical, decimal ProgressPct);

public sealed record GanttModel(DateOnly From, DateOnly To, double DayWidth, double RowHeight, double TotalWidth,
    IReadOnlyList<GanttRow> Rows, IReadOnlyList<GanttBar> Bars, IReadOnlyList<GanttLink> Links,
    IReadOnlyList<GanttTick> Ticks, double? TodayX);

/// <summary>
/// Turns activities into Gantt geometry (pixels). Kept free of WPF so it can be tested and
/// reused by the PDF report; the WPF control only draws what this returns.
/// </summary>
public static class GanttLayout
{
    public static GanttModel Build(IReadOnlyList<Model.Activity> activities, IReadOnlyList<Model.Dependency> links,
        DateOnly today, double dayWidth = 18, double rowHeight = 26)
    {
        var rows = activities.OrderBy(a => a.EarlyStart ?? a.PlannedStart ?? DateOnly.MaxValue).ThenBy(a => a.SortOrder).ThenBy(a => a.Code).ToList();
        var dates = rows.SelectMany(a => new[] { a.EarlyStart ?? a.PlannedStart, a.EarlyFinish ?? a.PlannedFinish, a.BaselineStart, a.BaselineFinish })
            .Where(d => d is not null).Select(d => d!.Value).ToList();
        var from = (dates.Count > 0 ? dates.Min() : today).AddDays(-3);
        var to = (dates.Count > 0 ? dates.Max() : today.AddDays(30)).AddDays(7);
        from = from.AddDays(-(((int)from.DayOfWeek + 6) % 7)); // align to Monday
        double X(DateOnly d) => (d.DayNumber - from.DayNumber) * dayWidth;

        var rowOf = new Dictionary<int, int>();
        var bars = new List<GanttBar>();
        for (var i = 0; i < rows.Count; i++)
        {
            var a = rows[i];
            rowOf[a.Id] = i;
            var s = a.EarlyStart ?? a.PlannedStart;
            var f = a.EarlyFinish ?? a.PlannedFinish;
            if (s is null || f is null) continue;
            var milestone = a.IsMilestone || a.Duration == 0;
            double? bx = a.BaselineStart is { } bs && a.BaselineFinish is { } bf ? X(bs) : null;
            double? bw = a.BaselineStart is { } bs2 && a.BaselineFinish is { } bf2 ? Math.Max(dayWidth / 2, X(bf2.AddDays(1)) - X(bs2)) : null;
            bars.Add(new GanttBar(a.Id, i, X(s.Value), milestone ? 0 : Math.Max(dayWidth / 2, X(f.Value.AddDays(1)) - X(s.Value)),
                milestone, a.Critical, (double)(a.ProgressPct / 100m), bx, bw, $"{a.Code} {a.ProgressPct:0}%"));
        }

        var byAct = bars.ToDictionary(b => b.ActivityId);
        var linkGeo = new List<GanttLink>();
        foreach (var l in links)
        {
            if (!byAct.TryGetValue(l.PredecessorId, out var p) || !byAct.TryGetValue(l.SuccessorId, out var s)) continue;
            var fromEnd = l.Type is Model.DependencyType.FS or Model.DependencyType.FF;
            var toEnd = l.Type is Model.DependencyType.FF or Model.DependencyType.SF;
            linkGeo.Add(new GanttLink(fromEnd ? p.X + p.Width : p.X, (p.Row + 0.5) * rowHeight,
                toEnd ? s.X + s.Width : s.X, (s.Row + 0.5) * rowHeight, p.Critical && s.Critical));
        }

        var ticks = new List<GanttTick>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.Day == 1) ticks.Add(new GanttTick(X(d), d.ToString("MMM yyyy"), true));
            else if (d.DayOfWeek == DayOfWeek.Monday) ticks.Add(new GanttTick(X(d), d.Day.ToString(), false));
        }
        double? todayX = today >= from && today <= to ? X(today) + dayWidth / 2 : null;
        var gRows = rows.Select(a => new GanttRow(a.Id, a.Code, a.Name, a.EarlyStart ?? a.PlannedStart, a.EarlyFinish ?? a.PlannedFinish, a.TotalFloat, a.Critical, a.ProgressPct)).ToList();
        return new GanttModel(from, to, dayWidth, rowHeight, X(to) + dayWidth, gRows, bars, linkGeo, ticks, todayX);
    }
}
