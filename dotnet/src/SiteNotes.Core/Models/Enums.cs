namespace SiteNotes.Core.Models;

public enum ReportStatus { Draft, Submitted, Approved }

public enum Shift { Day, Night, General }

public enum ManpowerSource { Subcontractor, Department }

public enum MachineOwnership { Own, Hired }

public enum MediaKind { Photo, Video, ScanPage, Document }

/// <summary>What a media file is attached to.</summary>
public enum MediaOwner { Note, Report, Hindrance, Requirement, Scan }

public enum HindranceStatus { Open, Resolved }

public enum RequirementStatus { Pending, Approved, Ordered, Received, Cancelled }

public enum Priority { Low, Normal, High, Urgent }

public enum FolderKind { Notes, Scans }

public enum ScanFilter { Original, Enhanced, Grayscale, BlackWhite }

public static class Lists
{
    public static readonly string[] Weather =
        ["Clear", "Sunny", "Cloudy", "Light rain", "Heavy rain", "Windy", "Hot", "Cold", "Fog", "Storm"];

    public static readonly string[] HindranceCategories =
        ["Drawing / design", "Material shortage", "Labour shortage", "Machinery breakdown", "Weather",
         "Client decision", "Payment", "Site access / clearance", "Utility shifting", "Approval / permit",
         "Power / water", "Other"];

    public static readonly string[] ResponsibleParties =
        ["Client", "Consultant / PMC", "Contractor", "Subcontractor", "Supplier", "Authority", "Force majeure"];

    public static readonly string[] RequirementCategories =
        ["Material", "Manpower", "Machinery", "Drawing", "Information / RFI", "Approval", "Tools", "Safety item", "Other"];

    public static readonly string[] Units =
        ["Nos", "m", "Rm", "m²", "m³", "kg", "MT", "Bags", "Ltr", "Set", "LS", "Day", "Hr", "Trip"];

    public static readonly string[] DefaultDepartments =
        ["Civil", "MEP", "Electrical", "Plumbing", "HVAC", "Safety (HSE)", "QA / QC", "Survey", "Planning",
         "Stores", "Plant & Machinery", "Admin"];

    public static readonly string[] Trades =
        ["Mason", "Carpenter", "Bar bender", "Helper", "Welder", "Fitter", "Electrician", "Plumber", "Painter",
         "Operator", "Driver", "Supervisor", "Engineer", "Foreman", "Rigger", "Scaffolder", "Unskilled"];
}
