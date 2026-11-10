using System.Text.Json.Serialization;
using SQLite;

namespace SiteNotes.Core.Models;

public enum ResourceType { Manpower, Material, Machinery, Other }

/// <summary>How a resource's rate is charged.</summary>
public enum RateBasis { PerHour, PerDay, PerUnit, LumpSum }

public enum WbsLevel { Project, Plant, Unit, Area, System, WorkPackage }

/// <summary>
/// SiteResource master: a trade/crew (manpower), a material, a machine or any other non-material
/// cost (scaffolding hire, testing, transport…), with its rate. Money is decimal so totals are exact.
/// </summary>
public class SiteResource : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public ResourceType Type { get; set; } = ResourceType.Manpower;
    public string Unit { get; set; } = "Nos";
    public decimal Rate { get; set; }
    public RateBasis Basis { get; set; } = RateBasis.PerDay;
    /// <summary>For manpower/machines: the agency that supplies it (optional).</summary>
    public string SubcontractorId { get; set; } = "";
    /// <summary>For own staff: the department (optional).</summary>
    public string DepartmentId { get; set; } = "";
    public string Remarks { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} {Name}";
}

/// <summary>Work breakdown structure node: Project › Plant › Unit › Area › System › Work package.</summary>
public class WbsNode : SyncEntity
{
    public string ParentId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public WbsLevel Level { get; set; } = WbsLevel.Area;
    public int SortOrder { get; set; }
    public override string ToString() => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} {Name}";
}

/// <summary>Planned resource for an activity (e.g. 6 masons for 10 days, 40 m³ concrete).</summary>
public class ActivityResource : SyncEntity
{
    [Indexed] public string ActivityId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public ResourceType Type { get; set; }
    /// <summary>Manpower/machines: number of people or machines. Materials: total quantity.</summary>
    public double PlannedQty { get; set; }
    /// <summary>Manpower/machines: hours each (blank = whole activity duration from the calendar).</summary>
    public double PlannedHours { get; set; }
    public decimal Rate { get; set; }
    public RateBasis Basis { get; set; } = RateBasis.PerDay;
}

/// <summary>Project work calendar: working hours, working weekdays and holidays.</summary>
public class WorkCalendar : SyncEntity
{
    public double HoursPerDay { get; set; } = 8;
    public TimeSpan ShiftStart { get; set; } = new(8, 0, 0);
    /// <summary>Working weekdays as a string of digits, 0 = Sunday … 6 = Saturday. Default Mon–Sat.</summary>
    public string WorkingDays { get; set; } = "123456";
    /// <summary>Holidays as yyyy-MM-dd separated by commas, e.g. "2026-11-14,2026-12-25".</summary>
    public string Holidays { get; set; } = "";
    /// <summary>Overtime multiplier used when manpower hours exceed HoursPerDay.</summary>
    public decimal OvertimeFactor { get; set; } = 1.0m;
    public string Currency { get; set; } = "₹";

    [JsonIgnore, Ignore]
    public HashSet<DateTime> HolidaySet =>
        Holidays.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => DateTime.TryParse(s, out var d) ? d.Date : (DateTime?)null)
            .Where(d => d != null).Select(d => d!.Value).ToHashSet();

    public bool IsWorkingDay(DateTime d, HashSet<DateTime>? holidays = null) =>
        WorkingDays.Contains(((int)d.DayOfWeek).ToString()) && !(holidays ?? HolidaySet).Contains(d.Date);

    /// <summary>Working days from start to finish inclusive.</summary>
    public int WorkingDaysBetween(DateTime start, DateTime finish)
    {
        if (finish.Date < start.Date) return 0;
        var hol = HolidaySet;
        var n = 0;
        for (var d = start.Date; d <= finish.Date; d = d.AddDays(1)) if (IsWorkingDay(d, hol)) n++;
        return n;
    }

    /// <summary>Date reached after adding <paramref name="workingDays"/> working days (start counts as day 1).</summary>
    public DateTime AddWorkingDays(DateTime start, int workingDays)
    {
        var hol = HolidaySet;
        var d = start.Date;
        while (!IsWorkingDay(d, hol)) d = d.AddDays(1);
        for (var left = Math.Max(1, workingDays) - 1; left > 0;)
        {
            d = d.AddDays(1);
            if (IsWorkingDay(d, hol)) left--;
        }
        return d;
    }
}

/// <summary>Saved report-manager choices (logos, header, footer, sections, sorting) for a project.</summary>
public class ReportProfile : SyncEntity
{
    public string Name { get; set; } = "Default";
    public string OptionsJson { get; set; } = "";
}

public enum DrawingStatus { Issued, ForConstruction, Revised, Superseded, Cancelled }

public enum DrawingItemKind { BoqItem, Equipment, Part }

/// <summary>Drawing register entry (GFC / construction drawing).</summary>
public class Drawing : SyncEntity
{
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public string Revision { get; set; } = "R0";
    public string Discipline { get; set; } = "Civil";
    public string WbsId { get; set; } = "";
    public DrawingStatus Status { get; set; } = DrawingStatus.ForConstruction;
    public DateTime? IssueDate { get; set; }
    public string Remarks { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(Title) ? $"{Number} {Revision}" : $"{Number} {Revision} – {Title}";
}

/// <summary>A BOQ item, equipment or part shown on a drawing, with its total quantity (e.g. item 1457: 100 MT).</summary>
public class DrawingItem : SyncEntity
{
    [Indexed] public string DrawingId { get; set; } = "";
    public string ItemNo { get; set; } = "";
    public string Description { get; set; } = "";
    public DrawingItemKind Kind { get; set; } = DrawingItemKind.BoqItem;
    public string Unit { get; set; } = "MT";
    public double Qty { get; set; }
    /// <summary>Optional: progress on this item also counts for this planned activity.</summary>
    public string ActivityId { get; set; } = "";
    public string Remarks { get; set; } = "";
    public int SortOrder { get; set; }
    public override string ToString() => string.IsNullOrWhiteSpace(ItemNo) ? Description : $"{ItemNo} {Description}";
}

/// <summary>Part of a drawing item given to one agency (an item can be split between several agencies).</summary>
public class DrawingAllocation : SyncEntity
{
    [Indexed] public string DrawingItemId { get; set; } = "";
    public string DrawingId { get; set; } = "";
    public string SubcontractorId { get; set; } = "";
    public string SubcontractorName { get; set; } = "";
    public double AllocatedQty { get; set; }
    public DateTime AllocatedOn { get; set; } = DateTime.Today;
    public string WorkOrderNo { get; set; } = "";
    public string Remarks { get; set; } = "";
}
