using System.Collections.Concurrent;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core;

public enum WorkState { NotStarted, InProgress, Completed, Delayed }

/// <summary>Everything known about one activity: quantities, dates, forecast and cost.</summary>
public class ActivityProgress
{
    public Activity Activity { get; init; } = new();
    public string WbsPath { get; set; } = "";
    public string Agency { get; set; } = "";
    public string Department { get; set; } = "";
    public double DoneQty { get; set; }
    public double RemainingQty => Math.Max(0, Activity.PlannedQty - DoneQty);
    /// <summary>0–100, capped at 100. Activities without a planned quantity count as 0 until marked done.</summary>
    public double Percent => Activity.PlannedQty <= 0 ? 0 : Math.Min(100, Math.Round(DoneQty / Activity.PlannedQty * 100, 2));
    public DateTime? ActualStart { get; set; }
    public DateTime? LastWorked { get; set; }
    public int DaysWorked { get; set; }
    /// <summary>Average quantity per working day actually achieved.</summary>
    public double AvgPerDay => DaysWorked == 0 ? 0 : Math.Round(DoneQty / DaysWorked, 3);
    public DateTime? ForecastFinish { get; set; }
    public int PlannedDurationDays { get; set; }
    /// <summary>Planned % that should be done by today according to the planned dates and calendar.</summary>
    public double PlannedPercentToday { get; set; }
    public WorkState State { get; set; }
    public CostBreakdown Actual { get; } = new();
    public decimal PlannedCost { get; set; }
    public decimal CostPerUnit => DoneQty <= 0 ? 0 : CostCalculator.Round(Actual.Total / (decimal)DoneQty);
    public decimal CostPerDay => DaysWorked == 0 ? 0 : CostCalculator.Round(Actual.Total / DaysWorked);
    public decimal CostPerManHour => Actual.ManHours <= 0 ? 0 : CostCalculator.Round(Actual.Manpower / (decimal)Actual.ManHours);
    /// <summary>Planned cost × % complete (earned value).</summary>
    public decimal EarnedValue => CostCalculator.Round(PlannedCost * (decimal)(Percent / 100));
    public double ManDays { get; set; }
}

public record PartySummary(string Name, double ManDays, double ManHours, decimal Cost, int Activities, string ActivitiesDone);

public class ProjectProgress
{
    public List<ActivityProgress> Activities { get; } = [];
    public double OverallPercent { get; set; }
    public double PlannedPercent { get; set; }
    public CostBreakdown Actual { get; } = new();
    public decimal PlannedCost { get; set; }
    public CostBreakdown Unallocated { get; } = new();
    public List<PartySummary> BySubcontractor { get; } = [];
    public List<PartySummary> ByDepartment { get; } = [];
    public WorkCalendar Calendar { get; set; } = new();
}

/// <summary>
/// Rolls up every daily report into activity progress and cost. Progress is the sum of
/// "today's quantity" across all reports (never typed twice), so it updates itself and stops at 100 %.
/// Heavy aggregation runs in parallel across CPU cores.
/// </summary>
public class ProgressService(LocalDatabase db)
{
    public async Task<WorkCalendar> CalendarAsync(string projectId) =>
        (await db.ListForProjectAsync<WorkCalendar>(projectId)).OrderByDescending(c => c.UpdatedAtUtc).FirstOrDefault()
        ?? new WorkCalendar { ProjectId = projectId };

    public async Task<ProjectProgress> ComputeAsync(string projectId, DateTime? from = null, DateTime? to = null, DateTime? asOf = null)
    {
        var today = (asOf ?? DateTime.Today).Date;
        var cal = await CalendarAsync(projectId);
        var activities = await db.ListForProjectAsync<Activity>(projectId);
        var wbs = (await db.ListForProjectAsync<WbsNode>(projectId)).ToDictionary(w => w.Id);
        var subs = (await db.ListForProjectAsync<Subcontractor>(projectId)).ToDictionary(s => s.Id, s => s.Name);
        var depts = (await db.ListForProjectAsync<Department>(projectId)).ToDictionary(d => d.Id, d => d.Name);
        var plan = (await db.ListForProjectAsync<ActivityResource>(projectId)).ToLookup(r => r.ActivityId);

        bool InRange(DateTime d) => (from == null || d >= from.Value.Date) && (to == null || d <= to.Value.Date) && d <= today;
        var reports = (await db.ListForProjectAsync<DailyReport>(projectId)).Where(r => InRange(r.Date.Date)).ToDictionary(r => r.Id);
        var progressRows = (await db.ListForProjectAsync<ReportActivity>(projectId)).Where(a => reports.ContainsKey(a.ReportId)).ToList();
        var manpower = (await db.ListForProjectAsync<ReportManpower>(projectId)).Where(a => reports.ContainsKey(a.ReportId)).ToList();
        var machines = (await db.ListForProjectAsync<ReportMachine>(projectId)).Where(a => reports.ContainsKey(a.ReportId)).ToList();
        var materials = (await db.ListForProjectAsync<ReportMaterial>(projectId)).Where(a => reports.ContainsKey(a.ReportId)).ToList();

        var qtyByActivity = progressRows.Where(r => r.ActivityId != "").ToLookup(r => r.ActivityId);
        var mpByActivity = manpower.ToLookup(m => m.ActivityId);
        var mcByActivity = machines.ToLookup(m => m.ActivityId);
        var mtByActivity = materials.ToLookup(m => m.ActivityId);

        var result = new ProjectProgress { Calendar = cal };
        var bag = new ConcurrentBag<ActivityProgress>();

        Parallel.ForEach(activities, a =>
        {
            var p = new ActivityProgress
            {
                Activity = a,
                WbsPath = WbsPath(a.WbsId, wbs),
                Agency = subs.GetValueOrDefault(a.SubcontractorId, ""),
                Department = depts.GetValueOrDefault(a.DepartmentId, ""),
            };
            var rows = qtyByActivity[a.Id].ToList();
            p.DoneQty = Math.Round(rows.Sum(r => r.TodayQty), 4);
            var workedDates = rows.Where(r => r.TodayQty > 0).Select(r => reports[r.ReportId].Date.Date)
                .Concat(mpByActivity[a.Id].Where(m => m.Count > 0).Select(m => reports[m.ReportId].Date.Date))
                .Distinct().OrderBy(d => d).ToList();
            p.DaysWorked = workedDates.Count;
            p.ActualStart = workedDates.FirstOrDefault() is var s && s != default ? s : null;
            p.LastWorked = workedDates.LastOrDefault() is var l && l != default ? l : null;

            foreach (var m in mpByActivity[a.Id])
            {
                p.Actual.Manpower += CostCalculator.Cost(m, cal);
                p.Actual.ManHours += m.Count * m.Hours;
                p.ManDays += cal.HoursPerDay > 0 ? m.Count * m.Hours / cal.HoursPerDay : m.Count;
            }
            foreach (var m in mcByActivity[a.Id])
            {
                p.Actual.Machinery += CostCalculator.Cost(m, cal);
                p.Actual.MachineHours += m.Count * CostCalculator.ChargeableHours(m);
            }
            foreach (var m in mtByActivity[a.Id])
            {
                if (m.Kind == ResourceType.Other) p.Actual.Other += CostCalculator.Cost(m);
                else p.Actual.Material += CostCalculator.Cost(m);
            }

            // Plan, forecast and state
            if (a.PlannedStart is { } ps && a.PlannedFinish is { } pf)
            {
                p.PlannedDurationDays = cal.WorkingDaysBetween(ps, pf);
                var elapsed = cal.WorkingDaysBetween(ps, today);
                p.PlannedPercentToday = p.PlannedDurationDays == 0 ? 0 : Math.Min(100, Math.Round(100.0 * elapsed / p.PlannedDurationDays, 2));
            }
            else if (a.PlannedRatePerDay > 0 && a.PlannedQty > 0)
            {
                p.PlannedDurationDays = (int)Math.Ceiling(a.PlannedQty / a.PlannedRatePerDay);
            }
            p.PlannedCost = plan[a.Id].Sum(r => CostCalculator.PlannedCost(r, p.PlannedDurationDays, cal));

            if (p.Percent >= 100) p.ForecastFinish = p.LastWorked;
            else if (p.AvgPerDay > 0) p.ForecastFinish = cal.AddWorkingDays(today.AddDays(1), (int)Math.Ceiling(p.RemainingQty / p.AvgPerDay));
            else if (a.PlannedRatePerDay > 0) p.ForecastFinish = cal.AddWorkingDays(today.AddDays(1), (int)Math.Ceiling(p.RemainingQty / a.PlannedRatePerDay));

            p.State = p.Percent >= 100 ? WorkState.Completed
                : (a.PlannedFinish is { } fin && (p.ForecastFinish ?? today) > fin.Date) || p.Percent + 0.001 < p.PlannedPercentToday - 10 ? WorkState.Delayed
                : p.DoneQty > 0 ? WorkState.InProgress
                : WorkState.NotStarted;
            bag.Add(p);
        });

        result.Activities.AddRange(bag.OrderBy(p => p.WbsPath).ThenBy(p => p.Activity.Code).ThenBy(p => p.Activity.Name));

        // Overall % weighted by Weightage (falls back to planned cost, then equal weights).
        var withQty = result.Activities.Where(p => p.Activity.PlannedQty > 0).ToList();
        double Weight(ActivityProgress p) => p.Activity.Weightage > 0 ? p.Activity.Weightage : p.PlannedCost > 0 ? (double)p.PlannedCost : 1;
        var totalW = withQty.Sum(Weight);
        result.OverallPercent = totalW == 0 ? 0 : Math.Round(withQty.Sum(p => Weight(p) * p.Percent) / totalW, 2);
        result.PlannedPercent = totalW == 0 ? 0 : Math.Round(withQty.Sum(p => Weight(p) * p.PlannedPercentToday) / totalW, 2);

        foreach (var p in result.Activities) result.Actual.Add(p.Actual);
        result.PlannedCost = result.Activities.Sum(p => p.PlannedCost);

        // Cost booked without an activity (general / overheads)
        var known = activities.Select(a => a.Id).ToHashSet();
        foreach (var m in manpower.Where(m => !known.Contains(m.ActivityId))) { result.Unallocated.Manpower += CostCalculator.Cost(m, cal); result.Unallocated.ManHours += m.Count * m.Hours; }
        foreach (var m in machines.Where(m => !known.Contains(m.ActivityId))) result.Unallocated.Machinery += CostCalculator.Cost(m, cal);
        foreach (var m in materials.Where(m => !known.Contains(m.ActivityId)))
        {
            if (m.Kind == ResourceType.Other) result.Unallocated.Other += CostCalculator.Cost(m);
            else result.Unallocated.Material += CostCalculator.Cost(m);
        }
        result.Actual.Add(result.Unallocated);

        // Subcontractor-wise: manpower + machines they supplied, and the activities they did.
        var actById = result.Activities.ToDictionary(p => p.Activity.Id);
        result.BySubcontractor.AddRange(Party(
            manpower.Where(m => m.Source == ManpowerSource.Subcontractor), m => Label(m.PartyName),
            progressRows.Where(r => r.SubcontractorName != "" || actById.ContainsKey(r.ActivityId)),
            r => Label(r.SubcontractorName != "" ? r.SubcontractorName : actById.TryGetValue(r.ActivityId, out var ap) ? ap.Agency : ""),
            actById, cal));
        result.ByDepartment.AddRange(Party(
            manpower.Where(m => m.Source == ManpowerSource.Department), m => Label(m.PartyName),
            progressRows.Where(r => actById.TryGetValue(r.ActivityId, out var ap) && ap.Department != ""),
            r => actById[r.ActivityId].Department, actById, cal));
        return result;
    }

    private static string Label(string s) => string.IsNullOrWhiteSpace(s) ? "(unassigned)" : s;

    private static IEnumerable<PartySummary> Party(IEnumerable<ReportManpower> mp, Func<ReportManpower, string> mpKey,
        IEnumerable<ReportActivity> work, Func<ReportActivity, string> workKey, Dictionary<string, ActivityProgress> acts, WorkCalendar cal)
    {
        var mpG = mp.GroupBy(mpKey).ToDictionary(g => g.Key, g => g.ToList());
        var workG = work.Where(r => r.TodayQty > 0).GroupBy(workKey).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var name in mpG.Keys.Union(workG.Keys).OrderBy(n => n))
        {
            var rows = mpG.GetValueOrDefault(name) ?? [];
            var done = workG.GetValueOrDefault(name) ?? [];
            var byAct = done.GroupBy(r => r.ActivityId != "" && acts.TryGetValue(r.ActivityId, out var a) ? a.Activity.Name : r.ActivityName)
                .Select(g => $"{g.Key}: {g.Sum(r => r.TodayQty):0.##} {g.First().Unit}");
            yield return new PartySummary(name,
                Math.Round(rows.Sum(m => cal.HoursPerDay > 0 ? m.Count * m.Hours / cal.HoursPerDay : m.Count), 2),
                rows.Sum(m => m.Count * m.Hours),
                rows.Sum(m => CostCalculator.Cost(m, cal)),
                done.Select(r => r.ActivityId + r.ActivityName).Distinct().Count(),
                string.Join("; ", byAct));
        }
    }

    public static string WbsPath(string wbsId, IReadOnlyDictionary<string, WbsNode> wbs)
    {
        var parts = new List<string>();
        var guard = 0;
        while (wbsId != "" && wbs.TryGetValue(wbsId, out var n) && guard++ < 20)
        {
            parts.Insert(0, n.ToString());
            wbsId = n.ParentId;
        }
        return string.Join(" › ", parts);
    }

    /// <summary>Cumulative quantity done for an activity up to (and excluding) a report, for the DPR editor.</summary>
    public async Task<double> DoneBeforeAsync(string projectId, string activityId, DateTime date, string excludeReportId)
    {
        var reports = (await db.ListForProjectAsync<DailyReport>(projectId)).Where(r => r.Date.Date <= date.Date && r.Id != excludeReportId)
            .Select(r => r.Id).ToHashSet();
        return (await db.ListForProjectAsync<ReportActivity>(projectId, r => r.ActivityId == activityId))
            .Where(r => reports.Contains(r.ReportId)).Sum(r => r.TodayQty);
    }
}
