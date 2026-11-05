using ConstructDpr.Core.Data;
using ConstructDpr.Core.Model;
using ConstructDpr.Core.Scheduling;

namespace ConstructDpr.Core.Services;

public sealed class ScheduleService(AppDbContext db)
{
    public WorkCalendar CalendarFor(Project p) =>
        new(WorkCalendar.ParseWorkDays(p.WorkDays), db.Holidays.Where(h => h.ProjectId == p.Id).Select(h => h.Date).ToList(), p.StartDate);

    /// <summary>Runs CPM and stores early/late dates, floats and the critical flag on each activity.</summary>
    public CpmOutput Run(int projectId, DateOnly? dataDate = null)
    {
        var p = db.Projects.First(x => x.Id == projectId);
        var acts = db.Activities.Where(a => a.ProjectId == projectId && a.Status != ActivityStatus.Cancelled).ToList();
        var deps = db.Dependencies.Where(d => d.ProjectId == projectId).ToList();
        var output = CpmScheduler.Schedule(
            acts.Select(a => new CpmActivity(a.Id, a.Code, a.Duration, a.IsMilestone, a.ActualStart, a.ActualFinish)).ToList(),
            deps.Select(d => new CpmLink(d.PredecessorId, d.SuccessorId, d.Type, d.Lag)).ToList(),
            p.StartDate, dataDate, CalendarFor(p));
        foreach (var a in acts)
        {
            var r = output.Activities[a.Id];
            (a.EarlyStart, a.EarlyFinish, a.LateStart, a.LateFinish) = (r.EarlyStart, r.EarlyFinish, r.LateStart, r.LateFinish);
            (a.TotalFloat, a.FreeFloat, a.Critical) = (r.TotalFloat, r.FreeFloat, r.Critical);
        }
        db.SaveChanges();
        return output;
    }

    /// <summary>Copies the CPM early dates into the planned dates (the baseline is not touched).</summary>
    public void ApplyToPlan(int projectId)
    {
        foreach (var a in db.Activities.Where(a => a.ProjectId == projectId && a.EarlyStart != null))
            (a.PlannedStart, a.PlannedFinish) = (a.EarlyStart, a.EarlyFinish);
        db.SaveChanges();
    }

    /// <summary>Freezes the planned dates as a new baseline revision; earlier revisions are kept.</summary>
    public Baseline FreezeBaseline(int projectId, string name)
    {
        var acts = db.Activities.Where(a => a.ProjectId == projectId && a.Status != ActivityStatus.Cancelled).ToList();
        var missing = acts.Where(a => a.PlannedStart is null || a.PlannedFinish is null).Select(a => a.Code).ToList();
        if (missing.Count > 0) throw new WorkflowException("Planned dates missing for: " + string.Join(", ", missing) + ". Run the schedule and apply it first.");
        var rev = db.Baselines.Where(b => b.ProjectId == projectId).Select(b => (int?)b.Revision).Max() + 1 ?? 0;
        var b = new Baseline { ProjectId = projectId, Revision = rev, Name = name, Items = acts.Select(a => new BaselineItem { ActivityId = a.Id, Start = a.PlannedStart, Finish = a.PlannedFinish }).ToList() };
        db.Baselines.Add(b);
        foreach (var a in acts) (a.BaselineStart, a.BaselineFinish) = (a.PlannedStart, a.PlannedFinish);
        db.SaveChanges();
        return b;
    }
}
