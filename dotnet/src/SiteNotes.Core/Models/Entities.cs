using System.Text.Json.Serialization;
using SQLite;

namespace SiteNotes.Core.Models;

/// <summary>
/// Base for every row that syncs between devices. Ids are GUIDs created on the device,
/// so records can be made offline and merged later; the newest UpdatedAtUtc wins.
/// </summary>
public abstract class SyncEntity
{
    [PrimaryKey] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [Indexed] public string ProjectId { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string UpdatedBy { get; set; } = "";
    public bool IsDeleted { get; set; }

    /// <summary>Local only: true while the row still has to be pushed to the server.</summary>
    [Indexed, JsonIgnore] public bool IsDirty { get; set; } = true;
}

public class Project : SyncEntity
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string Client { get; set; } = "";
    public string Contractor { get; set; } = "";
    public string Consultant { get; set; } = "";
    public string Location { get; set; } = "";
    public DateTime StartDate { get; set; } = DateTime.Today;
    public DateTime? EndDate { get; set; }
    public string Description { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} – {Name}";
}

public class Subcontractor : SyncEntity
{
    public string Name { get; set; } = "";
    public string Trade { get; set; } = "";
    public string ContactPerson { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public override string ToString() => Name;
}

public class Department : SyncEntity
{
    public string Name { get; set; } = "";
    public string Head { get; set; } = "";
    public override string ToString() => Name;
}

/// <summary>Activity master (BOQ / schedule item) that daily entries report progress against.</summary>
public class Activity : SyncEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string Unit { get; set; } = "Nos";
    public double PlannedQty { get; set; }
    public string SubcontractorId { get; set; } = "";
    public DateTime? PlannedStart { get; set; }
    public DateTime? PlannedFinish { get; set; }
    public override string ToString() => string.IsNullOrWhiteSpace(Code) ? Name : $"{Code} {Name}";
}

public class DailyReport : SyncEntity
{
    public int ReportNo { get; set; }
    [Indexed] public DateTime Date { get; set; } = DateTime.Today;
    public Shift Shift { get; set; } = Shift.Day;
    public string WeatherMorning { get; set; } = "Clear";
    public string WeatherEvening { get; set; } = "Clear";
    public double? TemperatureC { get; set; }
    public double? RainfallMm { get; set; }
    public double WorkingHours { get; set; } = 8;
    public ReportStatus Status { get; set; } = ReportStatus.Draft;
    public string PreparedBy { get; set; } = "";
    public string CheckedBy { get; set; } = "";
    public string ApprovedBy { get; set; } = "";
    public string ToolboxTalkTopic { get; set; } = "";
    public int ToolboxTalkAttendees { get; set; }
    public string SafetyRemarks { get; set; } = "";
    public string Visitors { get; set; } = "";
    public string PlanForTomorrow { get; set; } = "";
    public string Remarks { get; set; } = "";

    // Totals cached on save so lists can filter and sort without loading children.
    public int TotalManpower { get; set; }
    public double TotalManHours { get; set; }
    public int TotalMachines { get; set; }
    public int ActivityCount { get; set; }
    public int HindranceCount { get; set; }

    [JsonIgnore, Ignore] public string Title => $"DPR #{ReportNo} · {Date:dd-MMM-yyyy}";
}

public class ReportActivity : SyncEntity
{
    [Indexed] public string ReportId { get; set; } = "";
    public string ActivityId { get; set; } = "";
    public string ActivityName { get; set; } = "";
    public string Location { get; set; } = "";
    public string Unit { get; set; } = "Nos";
    public double PlannedQty { get; set; }
    public double TodayQty { get; set; }
    public double CumulativeQty { get; set; }
    public string SubcontractorId { get; set; } = "";
    public string SubcontractorName { get; set; } = "";
    public string Remarks { get; set; } = "";
    public int SortOrder { get; set; }

    [JsonIgnore, Ignore]
    public double PercentComplete => PlannedQty <= 0 ? 0 : Math.Round(CumulativeQty / PlannedQty * 100, 1);
}

public class ReportManpower : SyncEntity
{
    [Indexed] public string ReportId { get; set; } = "";
    public ManpowerSource Source { get; set; } = ManpowerSource.Subcontractor;
    public string PartyId { get; set; } = "";
    public string PartyName { get; set; } = "";
    public string Trade { get; set; } = "";
    public int Count { get; set; }
    public double Hours { get; set; } = 8;
    public string Remarks { get; set; } = "";
    public int SortOrder { get; set; }

    [JsonIgnore, Ignore] public double ManHours => Count * Hours;
}

public class ReportMachine : SyncEntity
{
    [Indexed] public string ReportId { get; set; } = "";
    public string Name { get; set; } = "";
    public MachineOwnership Ownership { get; set; } = MachineOwnership.Own;
    public int Count { get; set; } = 1;
    public double WorkingHours { get; set; }
    public double IdleHours { get; set; }
    public double FuelLitres { get; set; }
    public string Remarks { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>Material / resource used on the day.</summary>
public class ReportMaterial : SyncEntity
{
    [Indexed] public string ReportId { get; set; } = "";
    public string Material { get; set; } = "";
    public string Unit { get; set; } = "Nos";
    public double Received { get; set; }
    public double Consumed { get; set; }
    public string Remarks { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>Hindrance register entry (project level, optionally logged from a DPR).</summary>
public class Hindrance : SyncEntity
{
    public string ReportId { get; set; } = "";
    public int SerialNo { get; set; }
    [Indexed] public DateTime FromDate { get; set; } = DateTime.Today;
    public DateTime? ToDate { get; set; }
    public string Category { get; set; } = "Other";
    public string Description { get; set; } = "";
    public string AffectedActivity { get; set; } = "";
    public string ResponsibleParty { get; set; } = "Client";
    public double HoursLost { get; set; }
    public HindranceStatus Status { get; set; } = HindranceStatus.Open;
    public string ActionTaken { get; set; } = "";

    [JsonIgnore, Ignore] public int DaysOpen => (int)((ToDate ?? DateTime.Today) - FromDate.Date).TotalDays + 1;
}

/// <summary>Requirement register: material, manpower, drawings or decisions the site needs.</summary>
public class Requirement : SyncEntity
{
    public string ReportId { get; set; } = "";
    public int SerialNo { get; set; }
    [Indexed] public DateTime RaisedDate { get; set; } = DateTime.Today;
    public string Category { get; set; } = "Material";
    public string Item { get; set; } = "";
    public double Quantity { get; set; }
    public string Unit { get; set; } = "Nos";
    public DateTime? RequiredBy { get; set; }
    public Priority Priority { get; set; } = Priority.Normal;
    public string RaisedBy { get; set; } = "";
    public RequirementStatus Status { get; set; } = RequirementStatus.Pending;
    public string Remarks { get; set; } = "";

    [JsonIgnore, Ignore]
    public bool IsOverdue => RequiredBy is { } d && d.Date < DateTime.Today &&
                             Status is RequirementStatus.Pending or RequirementStatus.Approved or RequirementStatus.Ordered;
}

public class Folder : SyncEntity
{
    public string ParentId { get; set; } = "";
    public string Name { get; set; } = "";
    public FolderKind Kind { get; set; } = FolderKind.Notes;
    public string Color { get; set; } = "#F59E0B";
    public override string ToString() => Name;
}

public class Note : SyncEntity
{
    [Indexed] public string FolderId { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>The note body as JSON (see <see cref="NoteDocument"/>).</summary>
    public string ContentJson { get; set; } = "[]";
    /// <summary>Plain text copy of the body used for search and previews.</summary>
    public string PlainText { get; set; } = "";
    public string Tags { get; set; } = "";
    public bool IsPinned { get; set; }
    public string Color { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class ScanDocument : SyncEntity
{
    [Indexed] public string FolderId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Remarks { get; set; } = "";
    public int PageCount { get; set; }
    public ScanFilter Filter { get; set; } = ScanFilter.Enhanced;
    /// <summary>File name of the combined PDF in the media folder.</summary>
    public string PdfFileName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class MediaItem : SyncEntity
{
    public MediaOwner OwnerType { get; set; }
    [Indexed] public string OwnerId { get; set; } = "";
    public MediaKind Kind { get; set; } = MediaKind.Photo;
    /// <summary>File name inside the media folder (same on every device and on the server).</summary>
    public string FileName { get; set; } = "";
    public string Caption { get; set; } = "";
    public string Remarks { get; set; } = "";
    public int PageNo { get; set; }
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public long FileSize { get; set; }

    /// <summary>Local only: true once the file itself is on the server.</summary>
    [JsonIgnore] public bool IsUploaded { get; set; }
}

/// <summary>Local key/value settings (sync cursor, user name, current project…). Never synced.</summary>
public class LocalSetting
{
    [PrimaryKey] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
