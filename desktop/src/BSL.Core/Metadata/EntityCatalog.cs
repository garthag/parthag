namespace BSL.Core.Metadata;

public enum FieldKind { Text, Memo, Int, Decimal, Money, Date, Bool, Lookup, Choice }

/// <summary>Foreign-key lookup: SQL must return two columns, Id and Display.</summary>
public sealed record LookupDef(string Entity, string Sql);

/// <summary>One column of an entity. Drives the grid column, the form control, Excel and PDF.</summary>
public sealed class FieldDef
{
    public required string Name { get; init; }
    public required string Caption { get; init; }
    public FieldKind Kind { get; init; } = FieldKind.Text;
    public bool Required { get; init; }
    /// <summary>True for columns that exist only in the view (joined / computed) – never saved.</summary>
    public bool ReadOnly { get; init; }
    public int MaxLength { get; init; }
    public int Width { get; init; } = 110;
    public string Group { get; init; } = "General";
    public LookupDef? Lookup { get; init; }
    /// <summary>LookupValue.Category used to fill a drop-down (user may still type a new value).</summary>
    public string? ChoiceCategory { get; init; }
    public bool HiddenInGrid { get; init; }
    public bool IsUdf { get; init; }
    public string? Formula { get; init; }

    public Type ClrType => Kind switch
    {
        FieldKind.Int or FieldKind.Lookup => typeof(int),
        FieldKind.Decimal or FieldKind.Money => typeof(decimal),
        FieldKind.Date => typeof(DateTime),
        FieldKind.Bool => typeof(bool),
        _ => typeof(string)
    };

    public string? DisplayFormat => Kind switch
    {
        FieldKind.Money => "#,##,##0.00",
        FieldKind.Decimal => "0.###",
        FieldKind.Date => "dd-MM-yyyy",
        _ => null
    };

    public bool IsNumeric => Kind is FieldKind.Int or FieldKind.Decimal or FieldKind.Money;
}

public sealed class EntityDef
{
    public required string Name { get; init; }
    public required string Caption { get; init; }
    public required string Table { get; init; }
    public string View => "vw_" + Table;
    public string Key { get; init; } = "Id";
    public string OrderBy { get; init; } = "Id";
    public string Module { get; init; } = "Masters";
    public bool HasModifiedOn { get; init; }
    /// <summary>Column used as the record title in form view / record card PDF.</summary>
    public string TitleField { get; init; } = "Id";
    /// <summary>Default grouping column for the PDF of the list.</summary>
    public string? ReportGroupBy { get; init; }
    public required IReadOnlyList<FieldDef> Fields { get; init; }
    /// <summary>Related records shown under the form view: (child entity, foreign-key column).</summary>
    public IReadOnlyList<(string Entity, string ForeignKey)> Children { get; init; } = [];

    public IEnumerable<FieldDef> WritableFields => Fields.Where(f => !f.ReadOnly && !f.IsUdf);
    public FieldDef? Field(string name) => Fields.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public override string ToString() => Caption;
}

public static class EntityCatalog
{
    // ---- helpers to keep the definitions compact ----
    static FieldDef T(string n, string c, int len = 100, bool req = false, string grp = "General", int w = 110)
        => new() { Name = n, Caption = c, MaxLength = len, Required = req, Group = grp, Width = w };
    static FieldDef M(string n, string c, string grp = "Other", int len = 400)
        => new() { Name = n, Caption = c, Kind = FieldKind.Memo, MaxLength = len, Group = grp, Width = 200 };
    static FieldDef N(string n, string c, FieldKind k = FieldKind.Decimal, string grp = "Quantities", bool req = false)
        => new() { Name = n, Caption = c, Kind = k, Group = grp, Required = req, Width = 95 };
    static FieldDef D(string n, string c, string grp = "General") => new() { Name = n, Caption = c, Kind = FieldKind.Date, Group = grp, Width = 90 };
    static FieldDef B(string n, string c, string grp = "General") => new() { Name = n, Caption = c, Kind = FieldKind.Bool, Group = grp, Width = 60 };
    static FieldDef C(string n, string c, string cat, string grp = "General", bool req = false)
        => new() { Name = n, Caption = c, Kind = FieldKind.Choice, ChoiceCategory = cat, Group = grp, Required = req, MaxLength = 100, Width = 130 };
    static FieldDef L(string n, string c, LookupDef lk, string grp = "General", bool req = false)
        => new() { Name = n, Caption = c, Kind = FieldKind.Lookup, Lookup = lk, Group = grp, Required = req, Width = 170 };
    static FieldDef R(string n, string c, FieldKind k = FieldKind.Text, string grp = "Linked detail (read only)")
        => new() { Name = n, Caption = c, Kind = k, ReadOnly = true, Group = grp, Width = k == FieldKind.Text ? 120 : 100 };

    public static readonly LookupDef ContractorLookup = new("Contractor",
        "SELECT Id, ContractorCode + N' - ' + Name AS Display FROM dbo.Contractor ORDER BY ContractorCode");
    public static readonly LookupDef WorkOrderLookup = new("WorkOrder",
        "SELECT w.Id, w.UniqueNo + N' | ' + c.ContractorCode + N' | ' + ISNULL(w.ServiceType,N'') + N' | ' + ISNULL(w.WorkOrderNo,N'') AS Display " +
        "FROM dbo.WorkOrder w JOIN dbo.Contractor c ON c.Id = w.ContractorId ORDER BY w.UniqueNo");
    public static readonly LookupDef PackageLookup = new("Package",
        "SELECT Id, PackageCode AS Display FROM dbo.Package ORDER BY PackageCode");
    public static readonly LookupDef DrawingLookup = new("DrawingMaster",
        "SELECT Id, DrawingNo + ISNULL(N' - ' + LEFT(Title,60), N'') AS Display FROM dbo.DrawingMaster ORDER BY DrawingNo");
    public static readonly LookupDef ContractItemLookup = new("ContractItem",
        "SELECT Id, ISNULL(TechSpecItem + N' ', N'') + LEFT(Description, 80) + N'  [#' + CAST(Id AS NVARCHAR(10)) + N']' AS Display " +
        "FROM dbo.ContractItem WHERE IsGroupHeader = 0 ORDER BY SortOrder, Id");
    public static readonly LookupDef RaBillLookup = new("RaBill",
        "SELECT b.Id, N'RA-' + CAST(b.RaNo AS NVARCHAR(10)) + ISNULL(N' / ' + w.UniqueNo, N'') AS Display " +
        "FROM dbo.RaBill b LEFT JOIN dbo.WorkOrder w ON w.Id = b.WorkOrderId ORDER BY w.UniqueNo, b.RaNo");

    public static readonly EntityDef Contractor = new()
    {
        Name = "Contractor", Caption = "Contractor Master", Table = "Contractor", OrderBy = "ContractorCode",
        HasModifiedOn = true, TitleField = "Name", ReportGroupBy = "ContractorType",
        Children = [("WorkOrder", "ContractorId"), ("DrawingMaster", "OriginatorId"), ("DrawingRegister", "IssuedToId")],
        Fields =
        [
            T("ContractorCode", "Contractor Code", 20, true, w: 90), T("Name", "Contractor Name", 200, true, w: 220),
            C("ContractorType", "Type", "ContractorType", req: true),
            T("ContactPerson", "Contact Person", grp: "Contact"), T("ContactNo", "Contact No", 30, grp: "Contact"),
            T("Email", "E-mail", 150, grp: "Contact", w: 180), M("Address", "Address", "Contact"), T("State", "State", 50, grp: "Contact"),
            T("PAN", "PAN", 10, grp: "Statutory & bank", w: 95), T("GSTIN", "GSTIN", 15, grp: "Statutory & bank", w: 130),
            T("BankName", "Bank", grp: "Statutory & bank"), T("BankAccountNo", "Account No", 30, grp: "Statutory & bank"),
            T("IFSC", "IFSC", 11, grp: "Statutory & bank"), B("IsActive", "Active"), M("Remarks", "Remarks"),
            R("WorkOrderCount", "No. of WOs", FieldKind.Int), R("TotalWOValue", "Total WO Value", FieldKind.Money),
            R("TotalPaid", "Total Paid", FieldKind.Money)
        ]
    };

    public static readonly EntityDef WorkOrder = new()
    {
        Name = "WorkOrder", Caption = "Work Orders (Contractor WO)", Table = "WorkOrder", OrderBy = "UniqueNo",
        HasModifiedOn = true, TitleField = "UniqueNo", ReportGroupBy = "ContractorCode",
        Children = [("ContractItem", "WorkOrderId"), ("RaBill", "WorkOrderId"), ("RateMaster", "WorkOrderId"), ("Package", "WorkOrderId")],
        Fields =
        [
            T("UniqueNo", "Unique No", 20, true, w: 70), L("ContractorId", "Contractor", ContractorLookup, req: true),
            C("ServiceType", "Type of Service", "ServiceType"), C("WorkCategory", "Work Category", "WorkCategory"),
            T("WorkOrderNo", "Work Order No", grp: "Work order", w: 200), D("WODate", "WO Date", "Work order"),
            N("WOValue", "WO Value", FieldKind.Money, "Work order"), N("ActualWorkDone", "Actual Work Done", FieldKind.Money, "Work order"),
            N("ActualPaid", "Actual Paid", FieldKind.Money, "Work order"),
            new() { Name = "Difference", Caption = "Difference", Kind = FieldKind.Money, ReadOnly = true, Group = "Work order" },
            C("Status", "Status", "WOStatus"), M("Remarks", "Remarks"),
            R("ContractorCode", "Contr. Code", grp: "Contractor detail"), R("ContractorName", "Contractor Name", grp: "Contractor detail"),
            R("ContractorType", "Contractor Type", grp: "Contractor detail"), R("ContactPerson", "Contact Person", grp: "Contractor detail"),
            R("ContactNo", "Contact No", grp: "Contractor detail"), R("Email", "E-mail", grp: "Contractor detail"),
            R("PAN", "PAN", grp: "Contractor detail"), R("GSTIN", "GSTIN", grp: "Contractor detail"),
            R("ScopeTon", "Scope (MT)", FieldKind.Decimal, "Progress"), R("ScopeInternalAmount", "Scope Amt (Internal)", FieldKind.Money, "Progress"),
            R("ScopeExternalAmount", "Scope Amt (External)", FieldKind.Money, "Progress"),
            R("ExecutedTon", "Executed (MT)", FieldKind.Decimal, "Progress"), R("ExecutedInternalAmount", "Executed Amt (Internal)", FieldKind.Money, "Progress"),
            R("ExecutedExternalAmount", "Executed Amt (External)", FieldKind.Money, "Progress"),
            R("BalanceTon", "Balance (MT)", FieldKind.Decimal, "Progress"), R("WOUtilisedPct", "WO Utilised %", FieldKind.Decimal, "Progress")
        ]
    };

    public static readonly EntityDef RateMaster = new()
    {
        Name = "RateMaster", Caption = "Rate Master (Internal / External)", Table = "RateMaster", OrderBy = "WorkOrderId, ItemCode",
        Module = "Contract", TitleField = "Description", ReportGroupBy = "ContractorCode",
        Fields =
        [
            L("WorkOrderId", "Work Order", WorkOrderLookup), C("ActivityType", "Activity", "ServiceType"),
            T("ItemCode", "Item Code", 50, w: 80), T("Description", "Description", 300, true, w: 250), C("Unit", "Unit", "Unit"),
            N("InternalRate", "Internal Rate", FieldKind.Money, "Rates"), N("ExternalRate", "External Rate", FieldKind.Money, "Rates"),
            new() { Name = "Margin", Caption = "Margin / unit", Kind = FieldKind.Money, ReadOnly = true, Group = "Rates" },
            new() { Name = "MarginPct", Caption = "Margin %", Kind = FieldKind.Decimal, ReadOnly = true, Group = "Rates" },
            D("EffectiveFrom", "Effective From", "Rates"), M("Remarks", "Remarks"),
            R("UniqueNo", "WO Unique No"), R("WorkOrderNo", "Work Order No"), R("ContractorCode", "Contr. Code"), R("ContractorName", "Contractor")
        ]
    };

    public static readonly EntityDef Package = new()
    {
        Name = "Package", Caption = "Packages / BOM Sheets", Table = "Package", OrderBy = "PackageCode",
        Module = "Drawings & BOM", TitleField = "PackageCode", ReportGroupBy = "Area",
        Children = [("DrawingMaster", "PackageId"), ("BomItem", "PackageId")],
        Fields =
        [
            T("PackageCode", "Package Code", 60, true, w: 170), T("PackageName", "Package Name", 200, w: 220), T("Area", "Area"),
            N("NoOfSets", "No. of Sets", FieldKind.Int, "General", true), L("WorkOrderId", "Executing WO", WorkOrderLookup),
            T("SourceSheet", "Source Sheet"), M("Remarks", "Remarks"),
            R("AgencyCode", "Agency"), R("DrawingCount", "Drawings", FieldKind.Int), R("BomLines", "BOM Lines", FieldKind.Int),
            R("BomWeightKg", "BOM Wt / set (kg)", FieldKind.Decimal), R("WeightPerSetTon", "Wt / set (MT)", FieldKind.Decimal),
            R("WeightAllSetsTon", "Wt all sets (MT)", FieldKind.Decimal)
        ]
    };

    public static readonly EntityDef ContractItem = new()
    {
        Name = "ContractItem", Caption = "Contract BOQ Items", Table = "ContractItem", OrderBy = "SortOrder, Id",
        Module = "Contract", TitleField = "Description", ReportGroupBy = "GroupName",
        Children = [("RaBillLine", "ContractItemId"), ("DrawingMaster", "ContractItemId")],
        Fields =
        [
            T("TechSpecItem", "Tech. Spec. Item", 30, w: 80), T("GroupName", "Group", 200, w: 150), B("IsGroupHeader", "Group header"),
            T("Description", "Description", 400, true, w: 260), C("Unit", "M.U.", "Unit"),
            N("Qty", "Qty"), N("UnitWeightKg", "Unit Wt (kg)"),
            new() { Name = "TotalWeightTon", Caption = "Total Wt (MT)", Kind = FieldKind.Decimal, ReadOnly = true, Group = "Quantities" },
            N("R1WeightTon", "R1 Wt (MT)"),
            N("ExternalRate", "External Rate (Supply)", FieldKind.Money, "Rates"), N("InternalRate", "Internal Rate (Agency)", FieldKind.Money, "Rates"),
            new() { Name = "ExternalAmount", Caption = "External Amount", Kind = FieldKind.Money, ReadOnly = true, Group = "Rates" },
            new() { Name = "InternalAmount", Caption = "Internal Amount", Kind = FieldKind.Money, ReadOnly = true, Group = "Rates" },
            new() { Name = "Margin", Caption = "Margin", Kind = FieldKind.Money, ReadOnly = true, Group = "Rates" },
            L("WorkOrderId", "Agency WO", WorkOrderLookup, "Agency"), T("AgencyRef", "Agency Ref (raw)", 50, grp: "Agency"),
            C("ActivityType", "Activity", "ServiceType", "Agency"), T("DrawingQty", "Drawing Qty", 50, grp: "Agency"),
            M("Remarks", "Remarks"), N("SortOrder", "Sort", FieldKind.Int, "Other"),
            R("UniqueNo", "WO Unique No"), R("WorkOrderNo", "Work Order No"), R("AgencyCode", "Agency Code"), R("AgencyName", "Agency Name"),
            R("ExecutedTon", "Executed (MT)", FieldKind.Decimal, "Progress"), R("BalanceTon", "Balance (MT)", FieldKind.Decimal, "Progress"),
            R("ProgressPct", "Progress %", FieldKind.Decimal, "Progress"),
            R("ExecutedInternalAmount", "Executed Amt (Int.)", FieldKind.Money, "Progress"),
            R("ExecutedExternalAmount", "Executed Amt (Ext.)", FieldKind.Money, "Progress")
        ]
    };

    public static readonly EntityDef RaBill = new()
    {
        Name = "RaBill", Caption = "RA Bills", Table = "RaBill", OrderBy = "WorkOrderId, RaNo",
        Module = "Contract", TitleField = "RaNo", ReportGroupBy = "AgencyCode",
        Children = [("RaBillLine", "RaBillId")],
        Fields =
        [
            N("RaNo", "RA No", FieldKind.Int, "General", true), L("WorkOrderId", "Work Order", WorkOrderLookup),
            D("BillDate", "Bill Date"), C("Status", "Status", "RaStatus"), M("Remarks", "Remarks"),
            R("UniqueNo", "WO Unique No"), R("AgencyCode", "Agency"), R("LineCount", "Lines", FieldKind.Int),
            R("TotalTon", "Total (MT)", FieldKind.Decimal), R("TotalAmount", "Amount", FieldKind.Money)
        ]
    };

    public static readonly EntityDef RaBillLine = new()
    {
        Name = "RaBillLine", Caption = "RA Bill Lines", Table = "RaBillLine", OrderBy = "RaBillId, ContractItemId",
        Module = "Contract", TitleField = "ItemDescription", ReportGroupBy = "RaNo",
        Fields =
        [
            L("RaBillId", "RA Bill", RaBillLookup, req: true), L("ContractItemId", "BOQ Item", ContractItemLookup, req: true),
            N("QtyTon", "Qty (MT)", FieldKind.Decimal, "Quantities", true), N("Rate", "Rate (blank = internal)", FieldKind.Money),
            M("Remarks", "Remarks"),
            R("RaNo", "RA No", FieldKind.Int), R("AgencyCode", "Agency"), R("TechSpecItem", "Spec Item"), R("ItemDescription", "Item"),
            R("EffectiveRate", "Rate used", FieldKind.Money), R("Amount", "Amount", FieldKind.Money),
            R("ExternalAmount", "External Amt", FieldKind.Money)
        ]
    };

    public static readonly EntityDef DrawingMaster = new()
    {
        Name = "DrawingMaster", Caption = "Drawing Master", Table = "DrawingMaster", OrderBy = "DrawingNo",
        Module = "Drawings & BOM", TitleField = "DrawingNo", ReportGroupBy = "PackageCode",
        Children = [("DrawingRegister", "DrawingId"), ("BomItem", "DrawingId")],
        Fields =
        [
            T("DrawingNo", "Drawing No", 100, true, w: 210), T("Title", "Title", 300, w: 250),
            L("PackageId", "Package", PackageLookup), T("Area", "Area"), T("SubArea", "Sub Area", 150),
            L("OriginatorId", "Originator (ERC/SMS...)", ContractorLookup, "Classification"),
            C("Discipline", "Discipline", "Discipline", "Classification"), C("DrawingType", "Drawing Type", "DrawingType", "Classification"),
            N("TotalSheets", "Total Sheets", FieldKind.Int, "Classification"), T("CurrentRevision", "Current Rev", 10, grp: "Classification", w: 60),
            C("Status", "Status", "DrawingStatus", "Classification"),
            L("ContractItemId", "BOQ Item", ContractItemLookup, "Contract link"), L("AgencyWorkOrderId", "Executing WO", WorkOrderLookup, "Contract link"),
            T("EstQty", "Est. Qty", 30, grp: "Contract link"), N("EstWeightKg", "Est. Wt (kg)", grp: "Contract link"),
            M("Remarks", "Remarks"),
            R("PackageCode", "Package Code"), R("OriginatorCode", "Originator"), R("AgencyCode", "Agency"),
            R("TechSpecItem", "Spec Item"), R("SheetCount", "Sheets in register", FieldKind.Int), R("LatestRevision", "Latest Rev"),
            R("LastReceived", "Last received", FieldKind.Date), R("BomLines", "BOM Lines", FieldKind.Int),
            R("BomWeightKg", "BOM Wt (kg)", FieldKind.Decimal), R("BomWeightTon", "BOM Wt (MT)", FieldKind.Decimal)
        ]
    };

    public static readonly EntityDef DrawingRegister = new()
    {
        Name = "DrawingRegister", Caption = "Drawing Register (Sheets / Revisions)", Table = "DrawingRegister", OrderBy = "DrawingId, SheetNo, Revision",
        Module = "Drawings & BOM", TitleField = "DrawingNo", ReportGroupBy = "PackageCode",
        Fields =
        [
            L("DrawingId", "Drawing", DrawingLookup, req: true), T("SheetNo", "Sheet", 20, w: 60), T("Revision", "Rev", 10, w: 50),
            D("RevisionDate", "Rev Date"), T("TransmittalNo", "Transmittal No", grp: "Transmittal"), D("ReceivedDate", "Received", "Transmittal"),
            C("Purpose", "Purpose", "Purpose", "Transmittal"), L("IssuedToId", "Issued To", ContractorLookup, "Transmittal"),
            D("IssuedDate", "Issued On", "Transmittal"), C("Status", "Status", "RegisterStatus"),
            T("SourceRef", "Source text", 150, grp: "Other", w: 220), M("Remarks", "Remarks"),
            R("DrawingNo", "Drawing No"), R("Title", "Title"), R("PackageCode", "Package"), R("IssuedToCode", "Issued To Code")
        ]
    };

    public static readonly EntityDef BomItem = new()
    {
        Name = "BomItem", Caption = "BOM Items (all packages)", Table = "BomItem", OrderBy = "PackageId, SourceRow, Id",
        Module = "Drawings & BOM", TitleField = "ItemDescription", ReportGroupBy = "PackageCode",
        Fields =
        [
            L("PackageId", "Package", PackageLookup, req: true), L("DrawingId", "Drawing", DrawingLookup, "Drawing"),
            T("SheetNo", "Sheet", 20, grp: "Drawing", w: 55), T("Revision", "Rev", 10, grp: "Drawing", w: 45),
            T("DrawingDescription", "Drawing Description", 300, grp: "Drawing", w: 200),
            T("ItemDescription", "Item Description", 300, w: 160), T("AssemblyMark", "Assembly Mark", w: 80), T("PartMark", "Part Mark", w: 70),
            T("ItemNo", "Item No", 50, w: 55), N("PartQty", "Part Qty"),
            T("SectionDrg", "Section (Drg)", 150, grp: "Section", w: 150), T("SectionStd", "Section (Std)", grp: "Section"),
            N("LengthMm", "Length (mm)", grp: "Section"), N("WidthMm", "Width (mm)", grp: "Section"), N("ThicknessMm", "Thk (mm)", grp: "Section"),
            N("Qty", "No. of Pcs"), T("QtyText", "Qty (text)", 30, grp: "Quantities", w: 60),
            N("SourceUnitWt", "Unit (as in sheet)"), N("UnitWeightKg", "Unit Wt (kg/pc)"), N("TotalWeightKg", "Total Wt (kg)"),
            T("MaterialGrade", "Material Grade", grp: "Material", w: 140), M("Remarks", "Remarks", "Material"),
            T("SectionChange", "Section Change", grp: "Material"), T("DimensionStd", "Dimension Std", grp: "Material"),
            C("RowType", "Row Type", "RowType", "Control"), B("IsExcluded", "Excluded from totals", "Control"),
            T("QualityFlags", "Quality flags", 400, grp: "Control", w: 200), T("SourceSheet", "Source sheet", grp: "Control"),
            N("SourceRow", "Source row", FieldKind.Int, "Control"),
            R("PackageCode", "Package Code"), R("NoOfSets", "Sets", FieldKind.Int), R("DrawingNo", "Drawing No"),
            R("TotalWeightAllSetsKg", "Wt all sets (kg)", FieldKind.Decimal)
        ]
    };

    public static readonly EntityDef LookupValue = new()
    {
        Name = "LookupValue", Caption = "Drop-down Lists", Table = "LookupValue", OrderBy = "Category, SortOrder, Value",
        Module = "Settings", TitleField = "Value", ReportGroupBy = "Category",
        Fields = [T("Category", "List", 50, true, w: 150), T("Value", "Value", 100, true, w: 220), N("SortOrder", "Sort", FieldKind.Int, "General")]
    };

    public static readonly EntityDef ImportBatch = new()
    {
        Name = "ImportBatch", Caption = "Import History", Table = "ImportBatch", OrderBy = "Id DESC",
        Module = "Settings", TitleField = "FileName",
        Fields =
        [
            R("ImportType", "Type", grp: "General"), R("FileName", "File", grp: "General"), R("ImportedOn", "On", FieldKind.Date, "General"),
            R("ImportedBy", "By", grp: "General"), R("RowsRead", "Rows", FieldKind.Int, "General"), R("Notes", "Notes", grp: "General")
        ]
    };

    public static IReadOnlyList<EntityDef> All { get; } =
        [Contractor, WorkOrder, RateMaster, ContractItem, RaBill, RaBillLine, Package, DrawingMaster, DrawingRegister, BomItem, LookupValue, ImportBatch];

    public static EntityDef Get(string name) => All.First(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
