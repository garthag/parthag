namespace DrawingRegister.Core;

public sealed class Project
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Client { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = ProjectStatus.Active;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Number of drawings (filled by list queries).</summary>
    public int DrawingCount { get; set; }

    public string Display => $"{Code} – {Name}";
}

public static class ProjectStatus
{
    public const string Active = "Active";
    public const string OnHold = "On hold";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public static readonly string[] All = [Active, OnHold, Completed, Cancelled];
}

public static class DrawingStatus
{
    public static readonly string[] All = ["PRELIM", "IFR", "IFA", "IFC", "AFC", "AS-BUILT", "SUPERSEDED", "VOID"];
}

public sealed class Drawing
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string DrawingNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Discipline { get; set; }
    public string? Area { get; set; }
    public string? DrawingType { get; set; }
    public string Status { get; set; } = "IFR";
    public string? CurrentRevision { get; set; }
    public string? FolderPath { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Filled by list queries.</summary>
    public int RevisionCount { get; set; }
}

public sealed class Revision
{
    public long Id { get; set; }
    public long DrawingId { get; set; }
    public string Code { get; set; } = "";
    public int Sequence { get; set; }
    public DateTime? RevisionDate { get; set; }
    public string? Status { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IsCurrent { get; set; }

    /// <summary>Latest file of this revision (may be null for manually added revisions).</summary>
    public DrawingFile? File { get; set; }
}

public sealed class DrawingFile
{
    public long Id { get; set; }
    public long DrawingId { get; set; }
    public long RevisionId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string? RelativePath { get; set; }
    /// <summary>Path relative to the storage root.</summary>
    public string StoredPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
}

public enum ImportStatus
{
    Created,
    NewRevision,
    Duplicate,
    Failed,
}

public sealed record ImportItem(
    string FileName,
    string? RelativePath,
    ImportStatus Status,
    string? DrawingNumber = null,
    string? Revision = null,
    string? Discipline = null,
    long? DrawingId = null,
    string? Error = null);

public sealed class ImportSummary
{
    public List<ImportItem> Items { get; } = [];
    public int Total => Items.Count;
    public int Created => Items.Count(i => i.Status == ImportStatus.Created);
    public int NewRevisions => Items.Count(i => i.Status == ImportStatus.NewRevision);
    public int Duplicates => Items.Count(i => i.Status == ImportStatus.Duplicate);
    public int Failed => Items.Count(i => i.Status == ImportStatus.Failed);
    public double SuccessRate => Total == 0 ? 0 : (Total - Failed) * 100.0 / Total;
}

/// <summary>A file chosen for import: absolute path plus the path relative to the chosen folder (if any).</summary>
public sealed record ImportSource(string FullPath, string? RelativePath = null);
