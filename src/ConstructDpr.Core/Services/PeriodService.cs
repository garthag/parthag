using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Calculations;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Core.Services;

/// <summary>
/// Summary of a period (a week, a month, the time since the last client meeting) built from the approved daily
/// reports only: quantities and value, manpower, equipment, materials, delays and safety notes.
/// </summary>
public sealed class PeriodService(AppDbContext db)
{
    public PeriodSummary Build(int projectId, DateOnly from, DateOnly to)
    {
        if (to < from) throw new WorkflowException("The end date is before the start date.");
        if (to.DayNumber - from.DayNumber > 400) throw new WorkflowException("Choose a period of at most about a year.");
        var p = db.Projects.AsNoTracking().First(x => x.Id == projectId);
        var acts = db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId && a.Status != ActivityStatus.Cancelled)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Code).ToList();

        // Latest approved revision of every report up to the end date.
        var all = db.Dprs.AsNoTracking().Where(d => d.ProjectId == projectId && d.ReportDate <= to).ToList();
        var approved = all.Where(d => d.Status == DprStatus.Approved).GroupBy(d => d.Number)
            .Select(g => g.OrderByDescending(d => d.Revision).First()).ToList();
        var approvedIds = approved.Select(d => d.Id).ToList();
        var inPeriod = approved.Where(d => d.ReportDate >= from).OrderBy(d => d.ReportDate).ThenBy(d => d.Shift).ToList();
        var periodIds = inPeriod.Select(d => d.Id).ToHashSet();
        var notApproved = all.Where(d => d.ReportDate >= from && d.Status != DprStatus.Approved)
            .Select(d => d.Number).Distinct().Count(n => !approved.Any(a => a.Number == n));

        var lines = db.DprActivityLines.AsNoTracking().Where(l => approvedIds.Contains(l.DprId)).ToList();
        var dateOf = approved.ToDictionary(d => d.Id, d => d.ReportDate);
        var cal = new WorkCalendar(WorkCalendar.ParseWorkDays(p.WorkDays), db.Holidays.Where(h => h.ProjectId == projectId).Select(h => h.Date).ToList(), p.StartDate);

        var rows = acts.Select(a =>
        {
            var mine = lines.Where(l => l.ActivityId == a.Id).ToList();
            var period = mine.Where(l => periodIds.Contains(l.DprId)).Sum(l => l.QtyAccepted ?? l.QtyToday);
            var cum = mine.Sum(l => l.QtyAccepted ?? l.QtyToday);
            var days = mine.Where(l => periodIds.Contains(l.DprId) && (l.QtyAccepted ?? l.QtyToday) > 0).Select(l => dateOf[l.DprId]).Distinct().Count();
            return new PeriodActivityRow(a.Code, a.Name, a.Unit, a.TotalQty, period, cum,
                a.TotalQty > 0 ? Math.Round(Math.Min(cum, a.TotalQty) * 100m / a.TotalQty, 1) : null, Progress.PlannedPct(a, to), days);
        }).Where(r => r.InPeriod > 0 || r.Cumulative > 0 || (r.PlannedPct ?? 0) > 0).ToList();

        var rate = acts.ToDictionary(a => a.Code, a => a.Rate);
        var valuePeriod = rows.Sum(r => r.InPeriod * rate[r.Code]);
        var valueToDate = rows.Sum(r => Math.Min(r.Cumulative, r.TotalQty > 0 ? r.TotalQty : r.Cumulative) * rate[r.Code]);

        var crews = db.ManpowerLines.AsNoTracking().Where(m => periodIds.Contains(m.DprId)).ToList()
            .GroupBy(m => (m.Agency, m.Trade)).OrderBy(g => g.Key.Agency).ThenBy(g => g.Key.Trade)
            .Select(g => new PeriodCrewRow(g.Key.Agency, g.Key.Trade, g.Sum(m => m.Headcount), g.Sum(m => m.ManHours), g.Max(m => m.Headcount))).ToList();

        var equipment = db.EquipmentLines.AsNoTracking().Where(e => periodIds.Contains(e.DprId)).ToList()
            .GroupBy(e => (e.Code, e.Type)).OrderBy(g => g.Key.Code)
            .Select(g =>
            {
                var (w, i, b) = (g.Sum(e => e.WorkingHours), g.Sum(e => e.IdleHours), g.Sum(e => e.BreakdownHours));
                return new PeriodEquipmentRow(g.Key.Code, g.Key.Type, w, i, b, w + i + b == 0 ? null : Math.Round(w * 100m / (w + i + b), 1));
            }).ToList();

        var materialLines = db.MaterialLines.AsNoTracking().Where(m => periodIds.Contains(m.DprId)).ToList();
        var materials = materialLines.GroupBy(m => (m.Code, m.Description, m.Unit)).OrderBy(g => g.Key.Code)
            .Select(g => new PeriodMaterialRow(g.Key.Code, g.Key.Description, g.Key.Unit, g.Sum(m => m.Received), g.Sum(m => m.Issued),
                g.OrderByDescending(m => dateOf[m.DprId]).Select(m => (decimal?)m.Closing).FirstOrDefault())).ToList();

        var notes = new List<PeriodNote>();
        var actName = acts.ToDictionary(a => a.Id, a => a.Code);
        foreach (var d in inPeriod)
        {
            if (!string.IsNullOrWhiteSpace(d.DelayReasons)) notes.Add(new PeriodNote(d.ReportDate, d.Number, "Delay", d.DelayReasons!.Trim()));
            foreach (var l in lines.Where(l => l.DprId == d.Id && !string.IsNullOrWhiteSpace(l.DelayReason)))
                notes.Add(new PeriodNote(d.ReportDate, d.Number, "Delay", $"{actName.GetValueOrDefault(l.ActivityId)}: {l.DelayReason!.Trim()}"));
            if (!string.IsNullOrWhiteSpace(d.SafetyNotes) && !d.SafetyNotes!.Contains("No incidents", StringComparison.OrdinalIgnoreCase))
                notes.Add(new PeriodNote(d.ReportDate, d.Number, "Safety", d.SafetyNotes.Trim()));
        }
        foreach (var e in db.EquipmentLines.AsNoTracking().Where(e => periodIds.Contains(e.DprId) && e.BreakdownHours > 0).ToList())
        {
            var d = inPeriod.First(x => x.Id == e.DprId);
            notes.Add(new PeriodNote(d.ReportDate, d.Number, "Breakdown", $"{e.Code} {e.BreakdownHours:0.#} h: {e.BreakdownReason}"));
        }

        var curve = Progress.SCurve(acts, lines.Select(l => (l.ActivityId, dateOf[l.DprId], l.QtyAccepted ?? l.QtyToday)).ToList(), to);
        var at = curve.LastOrDefault(c => c.Date <= to);
        return new PeriodSummary(p.Code, p.Name, p.Client, p.Contractor, from, to, inPeriod.Count, notApproved,
            at?.Planned ?? 0, at?.Actual ?? 0, valuePeriod, valueToDate, rows, crews, equipment, materials,
            notes.OrderBy(n => n.Date).ThenBy(n => n.Kind).ToList(),
            inPeriod.Select(d => new DprSummary(d.Id, d.Number, d.Revision, d.ReportDate, d.Shift, d.Status, d.PreparedBy, d.ApprovedBy, d.UpdatedAt)).ToList());
    }
}
