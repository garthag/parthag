using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;

namespace ConstructDpr.Shared.Calculations;

/// <summary>Progress arithmetic shared by the server, the apps and the reports.</summary>
public static class Progress
{
    public static decimal? PlannedPct(Activity a, DateOnly on)
    {
        var s = a.BaselineStart ?? a.PlannedStart;
        var f = a.BaselineFinish ?? a.PlannedFinish;
        if (s is null || f is null) return null;
        if (on < s) return 0;
        if (on >= f) return 100;
        return Math.Round((on.DayNumber - s.Value.DayNumber + 1) * 100m / (f.Value.DayNumber - s.Value.DayNumber + 1), 2);
    }

    /// <summary>
    /// Fills the computed columns of a report line from the accepted quantity before this report (<paramref name="prevCum"/>).
    /// The apps call it while typing; the server repeats it on every save, so its numbers are the ones that count.
    /// </summary>
    public static void ApplyLine(DprActivityLine l, Activity a, decimal prevCum, DateOnly reportDate, bool useAccepted)
    {
        var qty = useAccepted ? l.QtyAccepted ?? l.QtyToday : l.QtyToday;
        l.PrevCum = prevCum;
        l.CumQty = prevCum + qty;
        l.Balance = a.TotalQty - l.CumQty;
        l.ActualPct = a.TotalQty > 0 ? Math.Round(l.CumQty * 100m / a.TotalQty, 2) : null;
        l.PlannedPct = PlannedPct(a, reportDate);
    }

    /// <summary>Weighted planned (linear over baseline/planned dates) vs actual (accepted quantities) progress.</summary>
    public static List<CurvePoint> SCurve(IReadOnlyList<Activity> acts, IReadOnlyList<(int ActivityId, DateOnly Date, decimal Qty)> progress, DateOnly today)
    {
        var measured = acts.Where(a => a.Weight > 0).ToList();
        var total = (double)measured.Sum(a => a.Weight);
        var starts = measured.Select(a => a.BaselineStart ?? a.PlannedStart).Where(d => d is not null).Select(d => d!.Value).ToList();
        var ends = measured.Select(a => a.BaselineFinish ?? a.PlannedFinish).Where(d => d is not null).Select(d => d!.Value).ToList();
        if (total == 0 || starts.Count == 0) return new();
        var from = starts.Min();
        var to = ends.Append(today).Max();
        var step = Math.Max(1, (to.DayNumber - from.DayNumber) / 120);
        var points = new List<CurvePoint>();
        for (var d = from; d <= to; d = d.AddDays(step))
        {
            double planned = 0, actual = 0;
            foreach (var a in measured)
            {
                var s = a.BaselineStart ?? a.PlannedStart;
                var f = a.BaselineFinish ?? a.PlannedFinish;
                if (s is not null && f is not null)
                {
                    var span = f.Value.DayNumber - s.Value.DayNumber + 1;
                    var done = Math.Clamp(d.DayNumber - s.Value.DayNumber + 1, 0, span);
                    planned += (double)a.Weight * done / span;
                }
                if (a.TotalQty > 0)
                {
                    var cum = progress.Where(p => p.ActivityId == a.Id && p.Date <= d).Sum(p => p.Qty);
                    actual += (double)a.Weight * Math.Min(1, (double)(cum / a.TotalQty));
                }
            }
            points.Add(new CurvePoint(d, Math.Round(planned * 100 / total, 2), d <= today ? Math.Round(actual * 100 / total, 2) : null));
        }
        return points;
    }
}
