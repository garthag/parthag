using ConstructDpr.Core.Data;
using ConstructDpr.Core.Model;

namespace ConstructDpr.Core.Services;

public sealed record CurvePoint(DateOnly Date, double Planned, double? Actual);

public sealed record DashboardStats(double PlannedPct, double ActualPct, int Activities, int Critical, int Delayed,
    int PendingApprovals, int ApprovedReports, DateOnly? ForecastFinish, decimal ContractValue, decimal ExecutedValue,
    int HeadcountToday, IReadOnlyList<CurvePoint> SCurve);

public sealed class DashboardService(AppDbContext db)
{
    public DashboardStats Build(int projectId, DateOnly today)
    {
        var acts = db.Activities.Where(a => a.ProjectId == projectId && a.Status != ActivityStatus.Cancelled).ToList();
        var dprs = db.Dprs.Where(d => d.ProjectId == projectId).ToList();
        var approvedIds = dprs.Where(d => d.Status == DprStatus.Approved).GroupBy(d => d.Number).Select(g => g.OrderByDescending(x => x.Revision).First()).ToDictionary(d => d.Id, d => d.ReportDate);
        var lines = db.DprActivityLines.Where(l => approvedIds.Keys.Contains(l.DprId)).ToList();
        var curve = SCurve(acts, lines.Select(l => (l.ActivityId, approvedIds[l.DprId], l.QtyAccepted ?? l.QtyToday)).ToList(), today);
        var at = curve.LastOrDefault(c => c.Date <= today);
        var lastReport = dprs.Where(d => d.Status == DprStatus.Approved).OrderByDescending(d => d.ReportDate).FirstOrDefault();
        var headcount = lastReport is null ? 0 : db.ManpowerLines.Where(m => m.DprId == lastReport.Id).ToList().Sum(m => m.Headcount);
        return new DashboardStats(
            at?.Planned ?? 0, at?.Actual ?? 0, acts.Count, acts.Count(a => a.Critical),
            acts.Count(a => a.Status != ActivityStatus.Completed && (a.BaselineFinish ?? a.PlannedFinish) < today),
            dprs.Count(d => d.Status == DprStatus.Submitted), approvedIds.Count,
            acts.Select(a => a.EarlyFinish).Where(d => d is not null).DefaultIfEmpty().Max(),
            acts.Sum(a => a.TotalQty * a.Rate), acts.Sum(a => Math.Min(a.AcceptedQty, a.TotalQty) * a.Rate), headcount, curve);
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
