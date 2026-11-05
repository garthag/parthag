using ConstructDpr.Core.Data;
using ConstructDpr.Core.Model;
using ConstructDpr.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Tests;

/// <summary>Each test gets its own SQLite file with the demo project loaded.</summary>
public sealed class DemoDb : IDisposable
{
    public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cdpr-test-{Guid.NewGuid():N}.db");
    public readonly AppDbContext Db;
    public readonly Project Project;
    public readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    public DemoDb()
    {
        Db = AppDbContext.Open(Path);
        Project = DemoData.Create(Db, Today);
    }

    public int ActivityId(string code) => Db.Activities.Single(a => a.ProjectId == Project.Id && a.Code == code).Id;

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(Path);
    }
}

public class WorkflowTests
{
    [Fact]
    public void Demo_project_is_complete()
    {
        using var t = new DemoDb();
        Assert.Equal(7, t.Db.Dprs.Count(d => d.Status == DprStatus.Approved));
        Assert.Equal(1, t.Db.Dprs.Count(d => d.Status == DprStatus.Submitted));
        Assert.Contains(t.Db.Activities, a => a.Critical);
        Assert.All(t.Db.Activities.ToList(), a => Assert.NotNull(a.BaselineFinish));
        var stats = new DashboardService(t.Db).Build(t.Project.Id, t.Today);
        Assert.InRange(stats.ActualPct, 0.1, 100);
        Assert.NotEmpty(stats.SCurve);
        Assert.Equal(1, stats.PendingApprovals);
    }

    [Fact]
    public void Cumulative_quantities_come_from_approved_reports_only()
    {
        using var t = new DemoDb();
        var a1000 = t.Db.Activities.Single(a => a.Code == "A1000");
        Assert.Equal(6 * 85 + 40, a1000.AcceptedQty); // pending report not counted yet
        var pending = new DprService(t.Db, "planning.engineer").Load(t.Db.Dprs.Single(d => d.Status == DprStatus.Submitted).Id);
        var line = pending.Activities.Single(l => l.ActivityId == a1000.Id);
        Assert.Equal(550, line.PrevCum);
    }

    [Fact]
    public void Approval_with_accepted_quantity_updates_progress_and_locks_the_report()
    {
        using var t = new DemoDb();
        var svc = new DprService(t.Db, "planning.engineer");
        var d = svc.Load(t.Db.Dprs.Single(x => x.Status == DprStatus.Submitted).Id);
        d.Activities.Single(l => l.Activity!.Code == "A1000").QtyAccepted = 80; // reviewer accepts less than reported (90)
        svc.Approve(d);
        Assert.Equal(DprStatus.Approved, d.Status);
        Assert.Equal("planning.engineer", d.ApprovedBy);
        Assert.Equal(550 + 80, t.Db.Activities.Single(a => a.Code == "A1000").AcceptedQty);
        Assert.Equal(90, d.Activities.Single(l => l.Activity!.Code == "A1000").QtyToday); // reported quantity kept
        Assert.True(svc.VerifyIntegrity(d));
        Assert.Throws<WorkflowException>(() => svc.Save(d));
    }

    [Fact]
    public void Self_approval_is_blocked_unless_allowed()
    {
        using var t = new DemoDb();
        t.Project.AllowSelfApproval = false;
        t.Db.SaveChanges();
        var site = new DprService(t.Db, "site.supervisor");
        var d = site.Load(t.Db.Dprs.Single(x => x.Status == DprStatus.Submitted).Id);
        var ex = Assert.Throws<WorkflowException>(() => site.Approve(d));
        Assert.Contains("prepared", ex.Message);
    }

    [Fact]
    public void Overrun_beyond_BOQ_quantity_is_refused()
    {
        using var t = new DemoDb();
        var svc = new DprService(t.Db, "site.supervisor");
        var d = svc.Create(t.Project.Id, t.Today, Shift.Night);
        d.Activities.Add(new DprActivityLine { ActivityId = t.ActivityId("A1000"), QtyToday = 1_000_000 });
        var ex = Assert.Throws<WorkflowException>(() => svc.Submit(d));
        Assert.Contains("exceeds", ex.Message);
        Assert.Equal(DprStatus.Draft, d.Status);
    }

    [Fact]
    public void Duplicate_report_for_same_date_and_shift_is_refused()
    {
        using var t = new DemoDb();
        Assert.Throws<WorkflowException>(() => new DprService(t.Db, "x").Create(t.Project.Id, t.Today, Shift.Day));
    }

    [Fact]
    public void Reject_needs_a_reason_and_report_can_be_resubmitted()
    {
        using var t = new DemoDb();
        var svc = new DprService(t.Db, "planning.engineer");
        var d = svc.Load(t.Db.Dprs.Single(x => x.Status == DprStatus.Submitted).Id);
        Assert.Throws<WorkflowException>(() => svc.Reject(d, "  "));
        svc.Reject(d, "Check crane hours");
        Assert.Equal(DprStatus.Rejected, d.Status);
        Assert.True(d.IsEditable);
        svc.Submit(d);
        Assert.Equal(DprStatus.Submitted, d.Status);
    }

    [Fact]
    public void Revision_keeps_the_approved_original()
    {
        using var t = new DemoDb();
        var svc = new DprService(t.Db, "planning.engineer");
        var original = svc.Load(t.Db.Dprs.Where(x => x.Status == DprStatus.Approved).OrderBy(x => x.ReportDate).First().Id);
        Assert.Throws<WorkflowException>(() => svc.StartRevision(original, ""));
        var rev = svc.StartRevision(original, "Wrong excavation quantity");
        Assert.Equal(original.Number, rev.Number);
        Assert.Equal(1, rev.Revision);
        Assert.Equal(DprStatus.Draft, rev.Status);
        Assert.Equal(DprStatus.Approved, original.Status);
        Assert.Equal(original.Activities.Count, rev.Activities.Count);
    }

    [Fact]
    public void Tampering_with_an_approved_report_is_detected()
    {
        using var t = new DemoDb();
        var id = t.Db.Dprs.First(x => x.Status == DprStatus.Approved).Id;
        t.Db.Database.ExecuteSql($"UPDATE DprActivityLines SET QtyToday = '999' WHERE DprId = {id}");
        t.Db.ChangeTracker.Clear();
        var svc = new DprService(t.Db, "auditor");
        Assert.False(svc.VerifyIntegrity(svc.Load(id)));
    }

    [Fact]
    public void Baselines_are_kept_as_revisions()
    {
        using var t = new DemoDb();
        var s = new ScheduleService(t.Db);
        var b = s.FreezeBaseline(t.Project.Id, "Re-plan");
        Assert.Equal(1, b.Revision);
        Assert.Equal(2, t.Db.Baselines.Count());
    }

    [Fact]
    public void Planned_percent_is_linear_between_baseline_dates()
    {
        var a = new Activity { BaselineStart = new(2026, 10, 1), BaselineFinish = new(2026, 10, 10) };
        Assert.Equal(0, DprService.PlannedPct(a, new(2026, 9, 30)));
        Assert.Equal(50, DprService.PlannedPct(a, new(2026, 10, 5)));
        Assert.Equal(100, DprService.PlannedPct(a, new(2026, 10, 10)));
    }
}
