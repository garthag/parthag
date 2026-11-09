using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Calculations;
using ConstructDpr.Shared.Model;

namespace ConstructDpr.Core.Services;

public sealed class DashboardService(AppDbContext db)
{
    public DashboardStats Build(int projectId, DateOnly today)
    {
        var acts = db.Activities.Where(a => a.ProjectId == projectId && a.Status != ActivityStatus.Cancelled).ToList();
        var dprs = db.Dprs.Where(d => d.ProjectId == projectId).ToList();
        var approvedIds = dprs.Where(d => d.Status == DprStatus.Approved).GroupBy(d => d.Number).Select(g => g.OrderByDescending(x => x.Revision).First()).ToDictionary(d => d.Id, d => d.ReportDate);
        var lines = db.DprActivityLines.Where(l => approvedIds.Keys.Contains(l.DprId)).ToList();
        var curve = Progress.SCurve(acts, lines.Select(l => (l.ActivityId, approvedIds[l.DprId], l.QtyAccepted ?? l.QtyToday)).ToList(), today);
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
}
