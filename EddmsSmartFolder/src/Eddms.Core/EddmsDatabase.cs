using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Eddms.Core;

/// <summary>SQL Server storage for folders, documents, revisions and the drawing registers.</summary>
public sealed class EddmsDatabase
{
    public const string DefaultConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=EddmsSmartFolder;Trusted_Connection=True;TrustServerCertificate=True";

    public string ConnectionString { get; }

    public EddmsDatabase(string connectionString) => ConnectionString = connectionString;

    private SqlConnection Open()
    {
        var c = new SqlConnection(ConnectionString);
        c.Open();
        return c;
    }

    // ------------------------------------------------------------- schema

    /// <summary>Creates the database (if missing) and runs the schema script (idempotent).</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        var dbName = builder.InitialCatalog;
        if (!string.IsNullOrWhiteSpace(dbName))
        {
            var master = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
            await using var mc = new SqlConnection(master.ConnectionString);
            await mc.OpenAsync(ct);
            await using var cmd = mc.CreateCommand();
            cmd.CommandText = "IF DB_ID(@n) IS NULL EXEC('CREATE DATABASE ' + @q)";
            cmd.Parameters.AddWithValue("@n", dbName);
            cmd.Parameters.AddWithValue("@q", "[" + dbName.Replace("]", "]]") + "]");
            await cmd.ExecuteNonQueryAsync(ct);
        }

        string script;
        await using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("EddmsSchema.sql")
                             ?? throw new InvalidOperationException("Schema script is missing from the build."))
        using (var reader = new StreamReader(s))
            script = await reader.ReadToEndAsync(ct);

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync(ct);
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // ------------------------------------------------------------- sync runs

    public int StartSyncRun(string loginName)
    {
        using var c = Open();
        using var cmd = new SqlCommand(
            "INSERT dbo.SyncRun (StartedOn, LoginName, Status) OUTPUT INSERTED.SyncRunId VALUES (SYSDATETIME(), @l, N'Running')", c);
        cmd.Parameters.AddWithValue("@l", loginName);
        return (int)cmd.ExecuteScalar()!;
    }

    public void FinishSyncRun(int id, bool ok, int folders, int documents, string? message)
    {
        using var c = Open();
        using var cmd = new SqlCommand(
            "UPDATE dbo.SyncRun SET FinishedOn = SYSDATETIME(), Status = @s, FolderCount = @f, DocumentCount = @d, Message = @m WHERE SyncRunId = @id", c);
        cmd.Parameters.AddWithValue("@s", ok ? "Succeeded" : "Failed");
        cmd.Parameters.AddWithValue("@f", folders);
        cmd.Parameters.AddWithValue("@d", documents);
        cmd.Parameters.AddWithValue("@m", (object?)message ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // ------------------------------------------------------------- writes

    /// <summary>Saves the folder tree; folders no longer on the site are marked inactive.</summary>
    public void SaveFolderTree(IEnumerable<SmartFolderInfo> allFolders, DateTime syncedOn)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using (var off = new SqlCommand("UPDATE dbo.SmartFolder SET IsActive = 0", c, tx)) off.ExecuteNonQuery();
        foreach (var f in allFolders)
        {
            using var cmd = Proc(c, tx, "dbo.usp_UpsertSmartFolder");
            cmd.Parameters.AddWithValue("@FolderId", f.FolderId);
            cmd.Parameters.AddWithValue("@ParentFolderId", (object?)f.ParentFolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@FolderName", f.FolderName);
            cmd.Parameters.AddWithValue("@FolderType", f.FolderType);
            cmd.Parameters.AddWithValue("@FolderCriteriaId", f.FolderCriteriaId);
            cmd.Parameters.AddWithValue("@FolderSubType", f.FolderSubType);
            cmd.Parameters.AddWithValue("@HasChild", f.HasChild);
            cmd.Parameters.AddWithValue("@IsPublished", f.IsPublished);
            cmd.Parameters.AddWithValue("@Depth", f.Depth);
            cmd.Parameters.AddWithValue("@SortOrder", f.SortOrder);
            cmd.Parameters.AddWithValue("@FullPath", f.FullPath);
            cmd.Parameters.AddWithValue("@SyncedOn", syncedOn);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Saves one folder's documents, its column layout, and a revision record per document.</summary>
    public void SaveFolderContents(SmartFolderInfo folder, WrenchClient.FolderContents contents, DateTime syncedOn)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        Exec(c, tx, "DELETE dbo.FolderColumn WHERE FolderId = @f", ("@f", folder.FolderId));
        for (int i = 0; i < contents.Columns.Count; i++)
            Exec(c, tx, "INSERT dbo.FolderColumn (FolderId, Ordinal, Field, Title) VALUES (@f, @o, @fd, @t)",
                ("@f", folder.FolderId), ("@o", i), ("@fd", contents.Columns[i].Field), ("@t", contents.Columns[i].Title));

        foreach (var d in contents.Documents)
        {
            using var cmd = Proc(c, tx, "dbo.usp_UpsertDocument");
            var p = cmd.Parameters;
            p.AddWithValue("@DocId", d.DocId);
            Add(p, "@DocNumber", d.DocNumber); Add(p, "@VendorDocNumber", d.VendorDocNumber);
            Add(p, "@DocDescription", d.DocDescription); Add(p, "@RevisionNumber", d.RevisionNumber);
            Add(p, "@InternalRevisionNumber", d.InternalRevisionNumber); Add(p, "@VersionNumber", d.VersionNumber);
            Add(p, "@SheetNumber", d.SheetNumber); Add(p, "@DocumentType", d.DocumentType);
            Add(p, "@Discipline", d.Discipline); Add(p, "@Category", d.Category); Add(p, "@Package", d.Package);
            Add(p, "@PackageVendorName", d.PackageVendorName); Add(p, "@DeptDwgCategory", d.DeptDwgCategory);
            Add(p, "@LifeCycleStatus", d.LifeCycleStatus);
            Add(p, "@InternalApprovalStatusCode", d.InternalApprovalStatusCode);
            Add(p, "@InternalApprovalStatusDescription", d.InternalApprovalStatusDescription);
            Add(p, "@CurrentStage", d.CurrentStage); Add(p, "@CurrentStageUser", d.CurrentStageUser);
            Add(p, "@DocStatus", d.DocStatus); Add(p, "@WorkflowTeam", d.WorkflowTeam); Add(p, "@FileName", d.FileName);
            p.AddWithValue("@HasAttachment", d.HasAttachment);
            Add(p, "@Billable", d.Billable); Add(p, "@ProjectNumber", d.ProjectNumber);
            Add(p, "@ScheduledSubmissionDate", d.ScheduledSubmissionDate); Add(p, "@VendorSubmissionDate", d.VendorSubmissionDate);
            Add(p, "@RespondedDate", d.RespondedDate); Add(p, "@CreatedOn", d.CreatedOn); Add(p, "@LastEditedOn", d.LastEditedOn);
            Add(p, "@LatestTransmittalReleasedOn", d.LatestTransmittalReleasedOn); Add(p, "@FinalReleasedOn", d.FinalReleasedOn);
            p.AddWithValue("@RawJson", d.RawJson);
            p.AddWithValue("@SyncedOn", syncedOn);
            cmd.ExecuteNonQuery();

            using var rev = Proc(c, tx, "dbo.usp_UpsertDocumentRevision");
            var r = rev.Parameters;
            r.AddWithValue("@DocId", d.DocId);
            r.AddWithValue("@InternalRevisionNumber", d.InternalRevisionNumber ?? 0);
            r.AddWithValue("@VersionNumber", d.VersionNumber ?? 0);
            Add(r, "@RevisionNumber", d.RevisionNumber); Add(r, "@DocNumber", d.DocNumber); Add(r, "@SheetNumber", d.SheetNumber);
            Add(r, "@FileName", d.FileName); Add(r, "@LifeCycleStatus", d.LifeCycleStatus); Add(r, "@DocStatus", d.DocStatus);
            Add(r, "@InternalApprovalStatusCode", d.InternalApprovalStatusCode);
            Add(r, "@InternalApprovalStatusDescription", d.InternalApprovalStatusDescription);
            Add(r, "@CurrentStage", d.CurrentStage); Add(r, "@VendorSubmissionDate", d.VendorSubmissionDate);
            Add(r, "@RespondedDate", d.RespondedDate); Add(r, "@FinalReleasedOn", d.FinalReleasedOn);
            r.AddWithValue("@SyncedOn", syncedOn);
            rev.ExecuteNonQuery();
        }

        Exec(c, tx, "DELETE dbo.FolderDocument WHERE FolderId = @f", ("@f", folder.FolderId));
        int order = 0;
        foreach (var d in contents.Documents.DistinctBy(x => x.DocId))
            Exec(c, tx, "INSERT dbo.FolderDocument (FolderId, DocId, RowOrder) VALUES (@f, @d, @o)",
                ("@f", folder.FolderId), ("@d", d.DocId), ("@o", order++));
        Exec(c, tx, "UPDATE dbo.SmartFolder SET DocumentCount = @n WHERE FolderId = @f",
            ("@n", contents.Documents.Count), ("@f", folder.FolderId));

        tx.Commit();
    }

    /// <summary>Returns the local path already saved for this revision, if the file still exists.</summary>
    public string? GetDownloadedPath(long docId, int internalRevision, int version)
    {
        using var c = Open();
        using var cmd = new SqlCommand(
            "SELECT LocalFilePath FROM dbo.DocumentRevision WHERE DocId = @d AND InternalRevisionNumber = @r AND VersionNumber = @v", c);
        cmd.Parameters.AddWithValue("@d", docId);
        cmd.Parameters.AddWithValue("@r", internalRevision);
        cmd.Parameters.AddWithValue("@v", version);
        var path = cmd.ExecuteScalar() as string;
        return path != null && File.Exists(path) ? path : null;
    }

    public void SetDownloadedPath(long docId, int internalRevision, int version, string path)
    {
        using var c = Open();
        Exec(c, null,
            "UPDATE dbo.DocumentRevision SET LocalFilePath = @p, DownloadedOn = SYSDATETIME() WHERE DocId = @d AND InternalRevisionNumber = @r AND VersionNumber = @v",
            ("@p", path), ("@d", docId), ("@r", internalRevision), ("@v", version));
    }

    // ------------------------------------------------------------- reads

    public sealed record FolderRow(long FolderId, long? ParentFolderId, string FolderName, int SortOrder, int DocumentCount, string FullPath);

    public List<FolderRow> LoadFolders()
    {
        using var c = Open();
        using var cmd = new SqlCommand(
            "SELECT FolderId, ParentFolderId, FolderName, SortOrder, DocumentCount, FullPath FROM dbo.SmartFolder WHERE IsActive = 1 ORDER BY Depth, SortOrder", c);
        using var r = cmd.ExecuteReader();
        var list = new List<FolderRow>();
        while (r.Read())
            list.Add(new FolderRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5)));
        return list;
    }

    /// <summary>Documents in a folder (and optionally its sub-folders), filtered by free text.</summary>
    public DataTable LoadDocuments(long? folderId, bool includeSubFolders, string? search)
    {
        const string sql = """
            WITH Folders AS (
                SELECT FolderId FROM dbo.SmartFolder WHERE FolderId = @folder
                UNION ALL
                SELECT s.FolderId FROM dbo.SmartFolder s JOIN Folders f ON s.ParentFolderId = f.FolderId WHERE @sub = 1
            )
            SELECT d.DocNumber AS [Document Number], d.SheetNumber AS [Sheet], d.RevisionNumber AS [Rev],
                   d.DocDescription AS [Description], d.DocumentType AS [Type], d.Discipline, d.Package,
                   d.LifeCycleStatus AS [Issue Purpose], d.InternalApprovalStatusCode AS [Approval Code],
                   d.InternalApprovalStatusDescription AS [Approval Description], d.DocStatus AS [Status],
                   d.CurrentStage AS [Current Stage], d.CurrentStageUser AS [Stage User],
                   d.ScheduledSubmissionDate AS [Scheduled Submission], d.VendorSubmissionDate AS [Vendor Submission],
                   d.RespondedDate AS [Responded], d.FinalReleasedOn AS [Final Released],
                   d.CreatedOn AS [Created], d.LastEditedOn AS [Last Edited], d.VendorDocNumber AS [Vendor Doc No],
                   d.WorkflowTeam AS [Workflow Team], d.FileName AS [File Name], r.LocalFilePath AS [Local File], d.DocId
              FROM dbo.Document d
              LEFT JOIN dbo.DocumentRevision r
                     ON r.DocId = d.DocId AND r.InternalRevisionNumber = d.InternalRevisionNumber AND r.VersionNumber = d.VersionNumber
             WHERE (@folder IS NULL OR EXISTS (SELECT 1 FROM dbo.FolderDocument fd JOIN Folders f ON f.FolderId = fd.FolderId WHERE fd.DocId = d.DocId))
               AND (@q IS NULL OR d.DocNumber LIKE @q OR d.DocDescription LIKE @q OR d.VendorDocNumber LIKE @q
                    OR d.FileName LIKE @q OR d.CurrentStage LIKE @q OR d.InternalApprovalStatusCode LIKE @q)
             ORDER BY d.Discipline, d.DocNumber, d.SheetNumber
             OPTION (MAXRECURSION 100);
            """;
        return Query(sql, ("@folder", folderId), ("@sub", includeSubFolders), ("@q", LikeOrNull(search)));
    }

    public enum Register { Submission, Revision, Published }

    public DataTable LoadRegister(Register register, string? search)
    {
        var sql = register switch
        {
            Register.Submission => """
                SELECT Discipline, Package, DocNumber AS [Document Number], SheetNumber AS [Sheet], RevisionNumber AS [Rev],
                       DocDescription AS [Description], DocumentType AS [Type], PackageVendorName AS [Vendor],
                       ScheduledSubmissionDate AS [Scheduled], VendorSubmissionDate AS [Submitted], RespondedDate AS [Responded],
                       DaysToRespond AS [Days To Respond], DaysLate AS [Days Late], ReplyCode AS [Reply Code],
                       ReplyDescription AS [Reply], CurrentStage AS [Current Stage], SubmissionStatus AS [Submission Status],
                       FileName AS [File Name], LocalFilePath AS [Local File], DocId
                  FROM dbo.vw_SubmissionRegister
                 WHERE (@q IS NULL OR DocNumber LIKE @q OR DocDescription LIKE @q OR ReplyCode LIKE @q OR Discipline LIKE @q)
                 ORDER BY Discipline, DocNumber, SheetNumber, InternalRevisionNumber, VersionNumber
                """,
            Register.Revision => """
                SELECT Discipline, DocNumber AS [Document Number], SheetNumber AS [Sheet], RevisionNumber AS [Rev],
                       VersionNumber AS [Version], RevisionState AS [State], DocDescription AS [Description],
                       LifeCycleStatus AS [Issue Purpose], DocStatus AS [Status], ApprovalCode AS [Approval Code],
                       ApprovalDescription AS [Approval], VendorSubmissionDate AS [Submitted], RespondedDate AS [Responded],
                       FinalReleasedOn AS [Released], FirstSeenOn AS [First Seen], LastSeenOn AS [Last Seen],
                       FileName AS [File Name], LocalFilePath AS [Local File], DownloadedOn AS [Downloaded], DocId
                  FROM dbo.vw_RevisionRegister
                 WHERE (@q IS NULL OR DocNumber LIKE @q OR DocDescription LIKE @q OR ApprovalCode LIKE @q OR Discipline LIKE @q)
                 ORDER BY Discipline, DocNumber, SheetNumber, InternalRevisionNumber DESC, VersionNumber DESC
                """,
            _ => """
                SELECT Discipline, Package, DocNumber AS [Document Number], SheetNumber AS [Sheet], RevisionNumber AS [Rev],
                       DocDescription AS [Description], DocumentType AS [Type], IssuePurpose AS [Issue Purpose],
                       ApprovalCode AS [Approval Code], ApprovalDescription AS [Approval], PublishedOn AS [Published On],
                       FileName AS [File Name], LocalFilePath AS [Local File], DocId
                  FROM dbo.vw_PublishedRegister
                 WHERE (@q IS NULL OR DocNumber LIKE @q OR DocDescription LIKE @q OR ApprovalCode LIKE @q OR Discipline LIKE @q)
                 ORDER BY Discipline, DocNumber, SheetNumber
                """,
        };
        return Query(sql, ("@q", LikeOrNull(search)));
    }

    public DataTable LoadSyncHistory() =>
        Query("SELECT TOP 50 SyncRunId AS [Run], StartedOn AS [Started], FinishedOn AS [Finished], LoginName AS [Login], Status, FolderCount AS [Folders], DocumentCount AS [Documents], Message FROM dbo.SyncRun ORDER BY SyncRunId DESC");

    // ------------------------------------------------------------- helpers

    private static string? LikeOrNull(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : "%" + s.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

    private DataTable Query(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var table = new DataTable();
        using (var r = cmd.ExecuteReader()) table.Load(r);
        return table;
    }

    private static SqlCommand Proc(SqlConnection c, SqlTransaction tx, string name) =>
        new(name, c, tx) { CommandType = CommandType.StoredProcedure };

    private static void Exec(SqlConnection c, SqlTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = new SqlCommand(sql, c, tx);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static void Add(SqlParameterCollection p, string name, object? value) =>
        p.AddWithValue(name, value ?? DBNull.Value);
}
