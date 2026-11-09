using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;

namespace ConstructDpr.Tests;

public class CpmTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);
    private static WorkCalendar MonFri(params DateOnly[] holidays) =>
        new([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], holidays, Monday);
    private static CpmActivity A(int id, int dur, DateOnly? actualStart = null, bool milestone = false) => new(id, "A" + id, dur, milestone, actualStart);
    private static CpmLink L(int p, int s, DependencyType t = DependencyType.FS, int lag = 0) => new(p, s, t, lag);
    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Fact]
    public void Calendar_skips_weekends_and_holidays()
    {
        var cal = new WorkCalendar([DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], [D(10, 2)], D(9, 28));
        Assert.Equal(D(9, 28), cal.DateAt(0));
        Assert.Equal(D(10, 5), cal.DateAt(4)); // Fri 2 Oct is a holiday
        Assert.Equal(4, cal.IndexOf(D(10, 3))); // Saturday snaps to Monday
        Assert.Equal(D(9, 25), cal.DateAt(-1));
    }

    [Fact]
    public void Work_days_text_is_parsed_safely()
    {
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Saturday], WorkCalendar.ParseWorkDays("1, 6, x, 9, 1"));
        Assert.Empty(WorkCalendar.ParseWorkDays(""));
    }

    [Fact]
    public void Forward_backward_pass_floats_and_critical_path()
    {
        // A(3) → B(2) → D(1);  A → C(4) → D
        var r = CpmScheduler.Schedule([A(1, 3), A(2, 2), A(3, 4), A(4, 1)], [L(1, 2), L(1, 3), L(2, 4), L(3, 4)], Monday, null, MonFri());
        Assert.Equal((D(10, 5), D(10, 7), 0, true), (r.Activities[1].EarlyStart, r.Activities[1].EarlyFinish, r.Activities[1].TotalFloat, r.Activities[1].Critical));
        Assert.Equal((D(10, 8), D(10, 13), 0, true), (r.Activities[3].EarlyStart, r.Activities[3].EarlyFinish, r.Activities[3].TotalFloat, r.Activities[3].Critical));
        Assert.Equal((D(10, 8), D(10, 9), 2, 2, false), (r.Activities[2].EarlyStart, r.Activities[2].EarlyFinish, r.Activities[2].TotalFloat, r.Activities[2].FreeFloat, r.Activities[2].Critical));
        Assert.Equal(D(10, 14), r.Activities[4].EarlyFinish);
        Assert.Equal(D(10, 14), r.ProjectFinish);
        Assert.Equal([1, 3, 4], r.CriticalPath);
    }

    [Fact]
    public void SS_FF_SF_relationships_with_lags_and_leads()
    {
        var r = CpmScheduler.Schedule([A(1, 10), A(2, 5), A(3, 4), A(4, 2)],
            [L(1, 2, DependencyType.SS, 3), L(1, 3, DependencyType.FF, 2), L(1, 4, DependencyType.SF, 1)], Monday, null, MonFri());
        Assert.Equal(D(10, 8), r.Activities[2].EarlyStart);
        Assert.Equal(D(10, 20), r.Activities[3].EarlyFinish);
        Assert.Equal(D(10, 6), r.Activities[4].EarlyFinish); // nothing starts before the project start

        var lead = CpmScheduler.Schedule([A(1, 5), A(2, 3)], [L(1, 2, DependencyType.FS, -2)], Monday, null, MonFri());
        Assert.Equal(D(10, 8), lead.Activities[2].EarlyStart);
    }

    [Fact]
    public void Circular_logic_is_reported()
    {
        var ex = Assert.Throws<ScheduleException>(() => CpmScheduler.Schedule([A(1, 1), A(2, 1)], [L(1, 2), L(2, 1)], Monday, null, MonFri()));
        Assert.Contains("A1", ex.Message);
        Assert.Throws<ScheduleException>(() => CpmScheduler.Schedule([A(1, 1)], [L(1, 1)], Monday, null, MonFri()));
    }

    [Fact]
    public void Actuals_and_data_date_push_remaining_work()
    {
        var r = CpmScheduler.Schedule([A(1, 5, actualStart: Monday), A(2, 3)], [L(1, 2)], Monday, D(10, 14), MonFri());
        Assert.Equal(D(10, 14), r.Activities[1].EarlyFinish); // still running on the data date
        Assert.Equal(D(10, 15), r.Activities[2].EarlyStart);
    }

    [Fact]
    public void Milestone_has_zero_duration()
    {
        var r = CpmScheduler.Schedule([A(1, 5), A(2, 0, milestone: true)], [L(1, 2)], Monday, null, MonFri());
        Assert.Equal(D(10, 12), r.Activities[2].EarlyStart);
        Assert.Equal(D(10, 12), r.Activities[2].EarlyFinish);
    }

    [Fact]
    public void Gantt_layout_matches_schedule()
    {
        var acts = new List<Activity>
        {
            new() { Id = 1, Code = "A", Duration = 3, EarlyStart = D(10, 5), EarlyFinish = D(10, 7), Critical = true, TotalQty = 10, AcceptedQty = 5 },
            new() { Id = 2, Code = "M", Duration = 0, IsMilestone = true, EarlyStart = D(10, 8), EarlyFinish = D(10, 8) },
        };
        var m = GanttLayout.Build(acts, [new Dependency { PredecessorId = 1, SuccessorId = 2 }], D(10, 6), dayWidth: 10, rowHeight: 20);
        Assert.Equal(DayOfWeek.Monday, m.From.DayOfWeek);
        var bar = m.Bars.Single(b => b.ActivityId == 1);
        Assert.Equal(30, bar.Width, 3);           // 3 days × 10 px
        Assert.Equal(0.5, bar.ProgressFraction, 3);
        Assert.True(bar.Critical);
        Assert.True(m.Bars.Single(b => b.ActivityId == 2).Milestone);
        Assert.Single(m.Links);
        Assert.Equal(bar.X + bar.Width, m.Links[0].X1, 3); // FS starts at the predecessor's finish
        Assert.NotNull(m.TodayX);
    }
}
