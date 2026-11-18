namespace Bf3Billing.Core;

public static class BillStatus
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Certified = "Certified";
    public const string Paid = "Paid";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = { Draft, Submitted, Certified, Paid, Cancelled };
}

public static class BillTypes
{
    public static readonly string[] All = { "RA bill", "Final bill", "Advance", "Supplementary" };
}

public sealed class Contractor
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Gstin { get; set; } = "";
    public string Pan { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string Remarks { get; set; } = "";
    /// <summary>Values of user-added fields, as JSON: {"Field name": "value"}.</summary>
    public string Extra { get; set; } = "{}";
    public override string ToString() => Name;
}

public sealed class WorkOrder
{
    public long Id { get; set; }
    public string WoNo { get; set; } = "";
    public long ContractorId { get; set; }
    public string ContractorName { get; set; } = "";
    public string Service { get; set; } = "";
    public decimal Value { get; set; }
    public DateTime? WoDate { get; set; }
    public string Status { get; set; } = "Open";
    public string Remarks { get; set; } = "";
    /// <summary>Values of user-added fields, as JSON: {"Field name": "value"}.</summary>
    public string Extra { get; set; } = "{}";
    public override string ToString() => WoNo;
}

public sealed class Activity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>True when lines of this activity are billed in tonnes against the BOM.</summary>
    public bool IsBom { get; set; }
    public string DefaultUnit { get; set; } = "t";
    public int SortOrder { get; set; }
    public override string ToString() => Name;
}

public sealed class BomItem
{
    public long Id { get; set; }
    public string System { get; set; } = "";
    public string DrawingNo { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string Rev { get; set; } = "";
    public string Item { get; set; } = "";
    public decimal Tonnes { get; set; }
    /// <summary>Drawing title (from the BOM).</summary>
    public string Description { get; set; } = "";
    /// <summary>Number of identical sets the tonnes include (e.g. 3 stoves).</summary>
    public int Sets { get; set; } = 1;
    /// <summary>Values of user-added fields, as JSON: {"Field name": "value"}.</summary>
    public string Extra { get; set; } = "{}";

    /// <summary>"16193442 · Sht 3/6 · Rev A"</summary>
    public string DrawingLabel => FormatDrawing(DrawingNo, Sheet, Rev);

    /// <summary>"1 STOVE › 16193442 · Sht 3/6 · Rev A › B1"</summary>
    public string Label => System + " › " + DrawingLabel + (Item.Length > 0 ? " › " + Item : "");

    public static string FormatDrawing(string drawingNo, string sheet, string rev)
    {
        var s = drawingNo;
        if (!string.IsNullOrWhiteSpace(sheet)) s += " · Sht " + sheet;
        if (!string.IsNullOrWhiteSpace(rev)) s += " · Rev " + rev;
        return s;
    }

    public override string ToString() => Label;
}

public sealed class Rate
{
    public long Id { get; set; }
    /// <summary>0 = default rate for every work order.</summary>
    public long WorkOrderId { get; set; }
    public string WorkOrderNo { get; set; } = "";
    public long ActivityId { get; set; }
    public string ActivityName { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class Bill
{
    public long Id { get; set; }
    public long WorkOrderId { get; set; }
    public string BillNo { get; set; } = "";
    public string BillType { get; set; } = "RA bill";
    public DateTime BillDate { get; set; } = DateTime.Today;
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public string Status { get; set; } = BillStatus.Draft;
    public decimal GstPct { get; set; } = 18m;
    public decimal RetentionPct { get; set; }
    public decimal TdsPct { get; set; }
    public decimal OtherDeduction { get; set; }
    public decimal PaidAmount { get; set; }
    public DateTime? PaidOn { get; set; }
    public string Remarks { get; set; } = "";
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public sealed class BillLine
{
    public long Id { get; set; }
    public long BillId { get; set; }
    public int LineNo { get; set; }
    /// <summary>Set for a BOM line (billed in tonnes against a drawing or item); null for a free line.</summary>
    public long? BomItemId { get; set; }
    /// <summary>Set for a BOQ line (a contract item of the work order, billed in tonnes against its R1 scope).</summary>
    public long? BoqItemId { get; set; }
    public string Description { get; set; } = "";
    public string Remarks { get; set; } = "";
    public long? ActivityId { get; set; }
    public string Unit { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
    /// <summary>Values of user-added fields, as JSON: {"Field name": "value"}.</summary>
    public string Extra { get; set; } = "{}";
    public decimal Amount => BillMath.LineAmount(Qty, Rate);
    public bool IsBom => BomItemId.HasValue;
    public bool IsBoq => BoqItemId.HasValue;
    public bool IsFree => !IsBom && !IsBoq;

    public BillLine Clone() => (BillLine)MemberwiseClone();
}

public sealed record BillTotals(
    decimal Gross, decimal Gst, decimal Retention, decimal Tds, decimal Other,
    decimal Net, decimal Paid, decimal Due);

/// <summary>One row of the Bills list.</summary>
public sealed class BillSummary
{
    public long Id { get; set; }
    public string BillNo { get; set; } = "";
    public DateTime BillDate { get; set; }
    public long ContractorId { get; set; }
    public string Contractor { get; set; } = "";
    public long WorkOrderId { get; set; }
    public string WorkOrder { get; set; } = "";
    public string BillType { get; set; } = "";
    public int Lines { get; set; }
    public decimal Gross { get; set; }
    public decimal Net { get; set; }
    public decimal Paid { get; set; }
    public decimal Due { get; set; }
    public string Status { get; set; } = "";
    public string Remarks { get; set; } = "";
    /// <summary>Number of lines that take the cumulative billing of a BOM item above its scope.</summary>
    public int OverScopeLines { get; set; }
}

public sealed class BillFilter
{
    public string? Search { get; set; }
    public string? Status { get; set; }
    public long? ContractorId { get; set; }
    public long? WorkOrderId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public bool OnlyOverScope { get; set; }
}

/// <summary>Scope check of one BOM line inside a bill being edited.</summary>
public sealed record ScopeInfo(decimal Scope, decimal Earlier, decimal This)
{
    public decimal Cumulative => Earlier + This;
    public decimal Percent => Scope > 0 ? Cumulative * 100m / Scope : (Cumulative > 0 ? 100m : 0m);
    public bool Over => Cumulative > Scope + 0.0005m;
}

public enum BomLevel { Systems, Drawings, Items }

public sealed class BomProgressRow
{
    public string System { get; set; } = "";
    public string Drawing { get; set; } = "";
    public string Item { get; set; } = "";
    public decimal BomTonnes { get; set; }
    /// <summary>Billed tonnes per BOM activity id.</summary>
    public Dictionary<long, decimal> Billed { get; } = new();
    public List<long> BomItemIds { get; } = new();
    public bool Over { get; set; }
}

public sealed class ActivityProgress
{
    public long ActivityId { get; set; }
    public string Activity { get; set; } = "";
    public decimal Tonnes { get; set; }
    public decimal Percent { get; set; }
}

public sealed class ContractorSummary
{
    public string Contractor { get; set; } = "";
    public decimal WoValue { get; set; }
    public int Bills { get; set; }
    public decimal Gross { get; set; }
    public decimal PercentOfWo => WoValue > 0 ? Gross * 100m / WoValue : 0m;
    public decimal Net { get; set; }
    public decimal Paid { get; set; }
    public decimal Outstanding { get; set; }
}

public sealed class WorkOrderUse
{
    public long WorkOrderId { get; set; }
    public string WoNo { get; set; } = "";
    public string Contractor { get; set; } = "";
    public string Service { get; set; } = "";
    public decimal Value { get; set; }
    public decimal Gross { get; set; }
    public decimal Balance => Value - Gross;
    public decimal? UsedPercent => Value > 0 ? Gross * 100m / Value : null;
}

public sealed class Dashboard
{
    public int BillCount { get; set; }
    public decimal Gross { get; set; }
    public decimal Net { get; set; }
    public decimal Paid { get; set; }
    public decimal Outstanding { get; set; }
    public int OverScopeLines { get; set; }
    public int DraftBills { get; set; }
    public int BomDrawings { get; set; }
    public decimal BomTonnes { get; set; }
    public List<ActivityProgress> Progress { get; set; } = new();
    public List<ContractorSummary> ByContractor { get; set; } = new();
    public List<WorkOrderUse> WorkOrders { get; set; } = new();
}

/// <summary>A bill line with its bill, for "lines behind this BOM row" and reports.</summary>
public sealed class BillLineDetail
{
    public long BillId { get; set; }
    public string BillNo { get; set; } = "";
    public DateTime BillDate { get; set; }
    public string Status { get; set; } = "";
    public string Contractor { get; set; } = "";
    public string WorkOrder { get; set; } = "";
    public int LineNo { get; set; }
    public string Item { get; set; } = "";
    public string Activity { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Qty { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    public string Remarks { get; set; } = "";
}

/// <summary>A field the user added (from an Excel import or in Masters) to contractors, work orders, BOM rows or bill lines.</summary>
public sealed class CustomField
{
    public long Id { get; set; }
    /// <summary>Contractor, WorkOrder, BomItem or BillLine.</summary>
    public string Entity { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public override string ToString() => Name;
}

public static class Entities
{
    public const string Contractor = "Contractor", WorkOrder = "WorkOrder", BomItem = "BomItem", BillLine = "BillLine";
    public static readonly string[] All = { Contractor, WorkOrder, BomItem, BillLine };
    public static string Label(string entity) => entity switch
    {
        Contractor => "Contractors", WorkOrder => "Work orders", BomItem => "BOM", BillLine => "Bill lines", _ => entity,
    };
}

/// <summary>Reads and writes the JSON of user-added field values.</summary>
public static class Extras
{
    public static Dictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return d == null ? new(StringComparer.OrdinalIgnoreCase) : new(d, StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Text.Json.JsonException) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public static string Write(IDictionary<string, string> values) =>
        System.Text.Json.JsonSerializer.Serialize(values.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim()));

    /// <summary>Adds or replaces the given values; blank new values leave old ones alone.</summary>
    public static string Merge(string? oldJson, IDictionary<string, string> newValues)
    {
        var d = Parse(oldJson);
        foreach (var (k, v) in newValues) if (!string.IsNullOrWhiteSpace(v)) d[k] = v.Trim();
        return Write(d);
    }

    public static string Get(string? json, string field) => Parse(json).GetValueOrDefault(field, "");
}

/// <summary>A contract (BOQ) item: scope in tonnes, the client's rate (external) and the agency's rate (internal).</summary>
public sealed class BoqItem
{
    public long Id { get; set; }
    public long? WorkOrderId { get; set; }
    public string WorkOrderNo { get; set; } = "";
    public string Source { get; set; } = "";
    public int SourceRow { get; set; }
    public int SortOrder { get; set; }
    public string SpecItem { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal? Qty { get; set; }
    public decimal? UnitWeightKg { get; set; }
    public decimal? ScopeTonnes { get; set; }
    public decimal? ExternalRate { get; set; }
    public decimal? InternalRate { get; set; }
    public string Activity { get; set; } = "";
    public string AgencyRef { get; set; } = "";
    public string DrawingQty { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string Extra { get; set; } = "{}";
    public string Label => (SpecItem.Length > 0 ? SpecItem + " · " : "") + Description;
    public override string ToString() => Label;
}

/// <summary>One line of a drawing's bill of materials (part or assembly), kept for reference under its BOM row.</summary>
public sealed class BomPart
{
    public long Id { get; set; }
    public long BomItemId { get; set; }
    public string SourceSheet { get; set; } = "";
    public int SourceRow { get; set; }
    public string RowType { get; set; } = "PART";
    /// <summary>False for detail rows already included in their assembly's weight.</summary>
    public bool Counted { get; set; } = true;
    public string AssemblyMark { get; set; } = "";
    public string PartMark { get; set; } = "";
    public string ItemNo { get; set; } = "";
    public string Description { get; set; } = "";
    public string Section { get; set; } = "";
    public string SectionStd { get; set; } = "";
    public decimal? LengthMm { get; set; }
    public decimal? WidthMm { get; set; }
    public decimal? ThicknessMm { get; set; }
    public decimal? Qty { get; set; }
    public string QtyText { get; set; } = "";
    public decimal? UnitWeightKg { get; set; }
    public decimal? TotalWeightKg { get; set; }
    public string Grade { get; set; } = "";
    public string GradeRaw { get; set; } = "";
    public string Remarks { get; set; } = "";
    public string Flags { get; set; } = "";
}
