namespace ConstructDpr.Core.Model;

public enum DependencyType { FS, SS, FF, SF }
public enum ActivityStatus { NotStarted, InProgress, Completed, Cancelled }
public enum DprStatus { Draft, Submitted, Approved, Rejected }
public enum Shift { Day, Night, General }

/// <summary>Common audit columns.</summary>
public abstract class Entity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class Project : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Client { get; set; }
    public string? Contractor { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? SiteAddress { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly? CompletionDate { get; set; }
    public string Currency { get; set; } = "INR";
    /// <summary>Working weekdays, e.g. "1,2,3,4,5,6" (0 = Sunday).</summary>
    public string WorkDays { get; set; } = "1,2,3,4,5,6";
    public bool AllowSelfApproval { get; set; }
    public List<Activity> Activities { get; set; } = new();
    public List<Holiday> Holidays { get; set; } = new();
}

public class Holiday : Entity
{
    public int ProjectId { get; set; }
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public class Activity : Entity
{
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Wbs { get; set; }
    public string? Area { get; set; }
    public string? Location { get; set; }
    public string? Unit { get; set; }
    public decimal TotalQty { get; set; }
    public decimal Rate { get; set; }
    public decimal Weight { get; set; } = 1;
    /// <summary>Original duration in working days (0 = milestone).</summary>
    public int Duration { get; set; }
    public bool IsMilestone { get; set; }
    public DateOnly? PlannedStart { get; set; }
    public DateOnly? PlannedFinish { get; set; }
    public DateOnly? BaselineStart { get; set; }
    public DateOnly? BaselineFinish { get; set; }
    public DateOnly? ActualStart { get; set; }
    public DateOnly? ActualFinish { get; set; }
    public ActivityStatus Status { get; set; } = ActivityStatus.NotStarted;
    public string? Responsible { get; set; }
    public int SortOrder { get; set; }

    // Results of the last CPM run (stored so the Gantt and reports need no recalculation).
    public DateOnly? EarlyStart { get; set; }
    public DateOnly? EarlyFinish { get; set; }
    public DateOnly? LateStart { get; set; }
    public DateOnly? LateFinish { get; set; }
    public int? TotalFloat { get; set; }
    public int? FreeFloat { get; set; }
    public bool Critical { get; set; }

    /// <summary>Accepted cumulative quantity from approved DPRs.</summary>
    public decimal AcceptedQty { get; set; }

    public decimal ProgressPct => TotalQty > 0 ? Math.Min(100m, Math.Round(AcceptedQty * 100m / TotalQty, 2)) : (Status == ActivityStatus.Completed ? 100m : 0m);
}

public class Dependency : Entity
{
    public int ProjectId { get; set; }
    public int PredecessorId { get; set; }
    public int SuccessorId { get; set; }
    public DependencyType Type { get; set; } = DependencyType.FS;
    public int Lag { get; set; }
}

public class Baseline : Entity
{
    public int ProjectId { get; set; }
    public int Revision { get; set; }
    public string Name { get; set; } = "";
    public List<BaselineItem> Items { get; set; } = new();
}

public class BaselineItem
{
    public int Id { get; set; }
    public int BaselineId { get; set; }
    public int ActivityId { get; set; }
    public DateOnly? Start { get; set; }
    public DateOnly? Finish { get; set; }
}

public class Dpr : Entity
{
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public string Number { get; set; } = "";
    public int Revision { get; set; }
    public DateOnly ReportDate { get; set; }
    public Shift Shift { get; set; } = Shift.Day;
    public string? Weather { get; set; }
    public string? SiteConditions { get; set; }
    public DprStatus Status { get; set; } = DprStatus.Draft;
    public string PreparedBy { get; set; } = "";
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ReviewComment { get; set; }
    public string? WorkCompleted { get; set; }
    public string? DelayReasons { get; set; }
    public string? TomorrowPlan { get; set; }
    public string? SafetyNotes { get; set; }
    /// <summary>SHA-256 of the approved content; detects later tampering with the SQLite file.</summary>
    public string? SnapshotHash { get; set; }
    public List<DprActivityLine> Activities { get; set; } = new();
    public List<ManpowerLine> Manpower { get; set; } = new();
    public List<EquipmentLine> Equipment { get; set; } = new();
    public List<MaterialLine> Materials { get; set; } = new();

    public bool IsEditable => Status is DprStatus.Draft or DprStatus.Rejected;
}

public class DprActivityLine
{
    public int Id { get; set; }
    public int DprId { get; set; }
    public int ActivityId { get; set; }
    public Activity? Activity { get; set; }
    public decimal QtyToday { get; set; }
    public decimal? QtyAccepted { get; set; }
    public decimal PrevCum { get; set; }
    public decimal CumQty { get; set; }
    public decimal Balance { get; set; }
    public decimal? ActualPct { get; set; }
    public decimal? PlannedPct { get; set; }
    public string? DelayReason { get; set; }
    public string? Remarks { get; set; }
}

public class ManpowerLine
{
    public int Id { get; set; }
    public int DprId { get; set; }
    public string Agency { get; set; } = "";
    public string Trade { get; set; } = "";
    public int Skilled { get; set; }
    public int SemiSkilled { get; set; }
    public int Unskilled { get; set; }
    public int Supervisors { get; set; }
    public decimal Hours { get; set; } = 8;
    public int Headcount => Skilled + SemiSkilled + Unskilled + Supervisors;
    public decimal ManHours => Headcount * Hours;
}

public class EquipmentLine
{
    public int Id { get; set; }
    public int DprId { get; set; }
    public string Code { get; set; } = "";
    public string Type { get; set; } = "";
    public decimal WorkingHours { get; set; }
    public decimal IdleHours { get; set; }
    public decimal BreakdownHours { get; set; }
    public string? BreakdownReason { get; set; }
    public decimal? UtilisationPct => WorkingHours + IdleHours + BreakdownHours == 0 ? null : Math.Round(WorkingHours * 100m / (WorkingHours + IdleHours + BreakdownHours), 1);
}

public class MaterialLine
{
    public int Id { get; set; }
    public int DprId { get; set; }
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Opening { get; set; }
    public decimal Received { get; set; }
    public decimal Issued { get; set; }
    public decimal Closing => Opening + Received - Issued;
    public string? ChallanNo { get; set; }
}

public class AuditEntry
{
    public int Id { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public string User { get; set; } = "";
    public string Entity { get; set; } = "";
    public int EntityId { get; set; }
    public string Action { get; set; } = "";
    public string? Details { get; set; }
}
