using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eddms.Core;

/// <summary>A Smart Folder node as returned by the Wrench EDMS site.</summary>
public sealed class SmartFolderInfo
{
    public long FolderId { get; init; }
    public long? ParentFolderId { get; set; }
    public string FolderName { get; init; } = "";
    public int FolderType { get; init; }
    public int FolderCriteriaId { get; init; }
    public int FolderSubType { get; init; }
    public int DisplayCount { get; init; }
    public bool HasChild { get; init; }
    public bool IsPublished { get; init; }
    public int Depth { get; set; }
    public int SortOrder { get; set; }
    public string FullPath { get; set; } = "";
    public List<SmartFolderInfo> Children { get; } = new();

    public IEnumerable<SmartFolderInfo> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }
}

/// <summary>A grid column the site shows for a folder (field name + caption).</summary>
public sealed record FolderColumn(string Field, string Title);

/// <summary>One document row from a Smart Folder, mapped to well-known properties.</summary>
public sealed class WrenchDocument
{
    public long DocId { get; set; }
    public string? DocNumber { get; set; }
    public string? VendorDocNumber { get; set; }
    public string? DocDescription { get; set; }
    public string? RevisionNumber { get; set; }
    public int? InternalRevisionNumber { get; set; }
    public int? VersionNumber { get; set; }
    public string? SheetNumber { get; set; }
    public string? DocumentType { get; set; }
    public string? Discipline { get; set; }
    public string? Category { get; set; }
    public string? Package { get; set; }
    public string? PackageVendorName { get; set; }
    public string? DeptDwgCategory { get; set; }
    public string? LifeCycleStatus { get; set; }
    public string? InternalApprovalStatusCode { get; set; }
    public string? InternalApprovalStatusDescription { get; set; }
    public string? CurrentStage { get; set; }
    public string? CurrentStageUser { get; set; }
    public string? DocStatus { get; set; }
    public string? WorkflowTeam { get; set; }
    public string? FileName { get; set; }
    public bool HasAttachment { get; set; }
    public string? Billable { get; set; }
    public string? ProjectNumber { get; set; }
    public DateTime? ScheduledSubmissionDate { get; set; }
    public DateTime? VendorSubmissionDate { get; set; }
    public DateTime? RespondedDate { get; set; }
    public DateTime? CreatedOn { get; set; }
    public DateTime? LastEditedOn { get; set; }
    public DateTime? LatestTransmittalReleasedOn { get; set; }
    public DateTime? FinalReleasedOn { get; set; }
    public string RawJson { get; set; } = "{}";

    /// <summary>
    /// Builds a document from a grid row. Fixed fields are read by name; project-specific
    /// custom attributes (CHAR_xxx_000) are found through the column captions.
    /// </summary>
    public static WrenchDocument FromRow(JsonElement row, IReadOnlyList<FolderColumn> columns)
    {
        string? Field(string name) =>
            row.TryGetProperty(name, out var v) ? JsonValueToString(v) : null;

        string? ByTitle(params string[] titles)
        {
            foreach (var t in titles)
            {
                var col = columns.FirstOrDefault(c => string.Equals(c.Title.Trim(), t, StringComparison.OrdinalIgnoreCase));
                if (col != null)
                {
                    var v = Field(col.Field);
                    if (v != null) return v;
                }
            }
            return null;
        }

        int? Int(string name) => int.TryParse(Field(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

        var attachments = Field("Attachments");

        return new WrenchDocument
        {
            DocId = ReadId(row) ?? 0,
            DocNumber = Field("DocNumber"),
            VendorDocNumber = Blank(Field("VendorDocNumber")),
            DocDescription = Field("DocDescription")?.Replace("\r", "").Replace("\n", " ").Trim(),
            RevisionNumber = Field("UserRevisionNumber") ?? Field("RevisionNumber"),
            InternalRevisionNumber = Int("DocInternalRevisionNumber"),
            VersionNumber = Int("DocVersionNumber"),
            SheetNumber = Field("SheetNo"),
            DocumentType = ByTitle("Document Type"),
            Discipline = ByTitle("Discipline"),
            Category = ByTitle("CATEGORY", "Category"),
            Package = ByTitle("Package"),
            PackageVendorName = ByTitle("Package Vendor Name", "Vendor Name"),
            DeptDwgCategory = ByTitle("Dept Dwg category"),
            LifeCycleStatus = Field("LifeCycleStatus"),
            InternalApprovalStatusCode = Field("InternalApprovalStatusCode"),
            InternalApprovalStatusDescription = Field("InternalApprovalStatusDescripion") ?? Field("InternalApprovalStatusDescription"),
            CurrentStage = Field("CurrentStage"),
            CurrentStageUser = Field("CurrentStageUser"),
            DocStatus = Field("DocStatus"),
            WorkflowTeam = Field("WorkflowTeam"),
            FileName = Field("FileName"),
            HasAttachment = !string.IsNullOrWhiteSpace(attachments),
            Billable = ByTitle("Billable"),
            ProjectNumber = Field("ProjectNumber"),
            ScheduledSubmissionDate = WrenchDate.Parse(ByTitle("Scheduled Date of Submission")),
            VendorSubmissionDate = WrenchDate.Parse(ByTitle("Vendor Submission date", "Vendor Submission Date")),
            RespondedDate = WrenchDate.Parse(ByTitle("Responded Date")),
            CreatedOn = WrenchDate.Parse(Field("CreatedOn")),
            LastEditedOn = WrenchDate.Parse(Field("LastEditedOn")),
            LatestTransmittalReleasedOn = WrenchDate.Parse(Field("LatestReleasedTransmittalReleasedOn")),
            FinalReleasedOn = WrenchDate.Parse(Field("FinalReleaseOn")),
            RawJson = row.GetRawText(),
        };
    }

    /// <summary>Document id: "DocID" in normal folders, "ObjectId" in Sent Items style folders.</summary>
    public static long? ReadId(JsonElement row)
    {
        foreach (var name in new[] { "DocID", "ObjectId" })
            if (row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt64() > 0)
                return v.GetInt64();
        return null;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string? JsonValueToString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => v.GetRawText(),
    };
}

/// <summary>Parses ASP.NET JSON dates such as "/Date(1751000519000)/".</summary>
public static class WrenchDate
{
    private static readonly Regex AspNetDate = new(@"^/Date\((-?\d+)([+-]\d{4})?\)/$", RegexOptions.Compiled);

    public static DateTime? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = AspNetDate.Match(value.Trim());
        if (m.Success)
            return DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).LocalDateTime;
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }
}

/// <summary>Progress message for the UI / console.</summary>
public sealed record SyncProgress(string Message, int Done = 0, int Total = 0);

public sealed class WrenchLoginException(string message) : Exception(message);
