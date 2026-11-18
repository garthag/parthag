using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace DrawingRegister.Core;

/// <summary>
/// Registers PDF drawings: validates, hashes, copies them into the storage folder (content-addressed,
/// so identical files are stored once) and creates/updates drawings and revisions.
/// </summary>
public sealed class ImportService(Database db, AppPaths paths)
{
    public const long MaxFileBytes = 500L * 1024 * 1024;
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    /// <summary>All PDFs under <paramref name="folder"/>; relative paths include the folder's own name.</summary>
    public static IReadOnlyList<ImportSource> CollectFolder(string folder)
    {
        var root = Path.GetFullPath(folder);
        var parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? root;
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new ImportSource(f, Path.GetRelativePath(parent, f).Replace('\\', '/')))
            .ToList();
    }

    public ImportSummary Import(long projectId, IEnumerable<ImportSource> sources, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = sources.ToList();
        var summary = new ImportSummary();
        var projectCode = ProjectCode(projectId);
        for (var i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            summary.Items.Add(ImportOne(projectId, projectCode, list[i]));
            progress?.Report((i + 1, list.Count));
        }

        return summary;
    }

    private string ProjectCode(long projectId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT code FROM projects WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", projectId);
        return cmd.ExecuteScalar() as string ?? throw new ValidationException("Project not found.");
    }

    private ImportItem ImportOne(long projectId, string projectCode, ImportSource source)
    {
        var fileName = Path.GetFileName(source.FullPath);
        try
        {
            if (!fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return Failed("Only PDF files are supported.");
            }

            var info = new FileInfo(source.FullPath);
            if (!info.Exists)
            {
                return Failed("File not found.");
            }

            if (info.Length > MaxFileBytes)
            {
                return Failed($"File is larger than {MaxFileBytes / 1024 / 1024} MB.");
            }

            string sha;
            using (var stream = File.OpenRead(source.FullPath))
            {
                var head = new byte[PdfMagic.Length];
                if (stream.Read(head, 0, head.Length) != head.Length || !head.AsSpan().SequenceEqual(PdfMagic))
                {
                    return Failed("Not a valid PDF file.");
                }

                stream.Position = 0;
                sha = Convert.ToHexStringLower(SHA256.HashData(stream));
            }

            var parsed = FileNameParser.Parse(fileName, source.RelativePath);
            using var conn = db.Open();
            using var tx = conn.BeginTransaction();

            var duplicateOf = Scalar<long?>(conn, tx,
                "SELECT f.drawing_id FROM drawing_files f JOIN drawings d ON d.id = f.drawing_id WHERE d.project_id = $p AND f.sha256 = $sha LIMIT 1",
                ("$p", projectId), ("$sha", sha));
            if (duplicateOf is not null)
            {
                return new ImportItem(fileName, source.RelativePath, ImportStatus.Duplicate, parsed.DrawingNumber,
                    parsed.Revision, parsed.Discipline, duplicateOf, "Identical file already registered.");
            }

            var storedPath = Store(source.FullPath, projectCode, sha);
            var now = Database.ToDb(DateTime.Now);
            var drawingId = Scalar<long?>(conn, tx, "SELECT id FROM drawings WHERE project_id = $p AND drawing_number = $n",
                ("$p", projectId), ("$n", parsed.DrawingNumber));
            var created = drawingId is null;
            if (created)
            {
                drawingId = Scalar<long>(conn, tx, """
                    INSERT INTO drawings (project_id, drawing_number, title, discipline, area, drawing_type, status, folder_path, created_at, updated_at)
                    VALUES ($p, $n, '', $disc, $area, $type, 'IFR', $folder, $now, $now) RETURNING id
                    """,
                    ("$p", projectId), ("$n", parsed.DrawingNumber), ("$disc", parsed.Discipline), ("$area", parsed.Area),
                    ("$type", parsed.DrawingType), ("$folder", parsed.FolderPath), ("$now", now));
            }
            else
            {
                Execute(conn, tx, """
                    UPDATE drawings SET discipline = COALESCE(discipline, $disc), area = COALESCE(area, $area),
                           drawing_type = COALESCE(drawing_type, $type), folder_path = COALESCE(folder_path, $folder)
                    WHERE id = $id
                    """,
                    ("$id", drawingId), ("$disc", parsed.Discipline), ("$area", parsed.Area), ("$type", parsed.DrawingType),
                    ("$folder", parsed.FolderPath));
            }

            var revisionCode = parsed.Revision;
            var sequence = RevisionOrder.Sequence(revisionCode);
            if (revisionCode is null)
            {
                // No revision in the file name: first upload is "0", later uploads get placeholder codes
                // (U2, U3, ...) ranked after the newest existing revision so the new file becomes current.
                var count = Scalar<long>(conn, tx, "SELECT COUNT(*) FROM revisions WHERE drawing_id = $id", ("$id", drawingId));
                revisionCode = count == 0 ? "0" : $"U{count + 1}";
                sequence = count == 0
                    ? RevisionOrder.Sequence("0")
                    : Scalar<int>(conn, tx, "SELECT MAX(sequence) FROM revisions WHERE drawing_id = $id", ("$id", drawingId)) + 1;
            }

            var revisionId = Scalar<long?>(conn, tx, "SELECT id FROM revisions WHERE drawing_id = $id AND code = $c",
                ("$id", drawingId), ("$c", revisionCode));
            revisionId ??= Scalar<long>(conn, tx, """
                INSERT INTO revisions (drawing_id, code, sequence, revision_date, status, created_at)
                VALUES ($id, $c, $seq, $date, NULL, $now) RETURNING id
                """,
                ("$id", drawingId), ("$c", revisionCode), ("$seq", sequence),
                ("$date", DateTime.Today.ToString("yyyy-MM-dd")), ("$now", now));

            Execute(conn, tx, """
                INSERT INTO drawing_files (drawing_id, revision_id, original_filename, relative_path, stored_path, sha256, size_bytes, uploaded_at)
                VALUES ($d, $r, $name, $rel, $stored, $sha, $size, $now)
                """,
                ("$d", drawingId), ("$r", revisionId), ("$name", fileName), ("$rel", source.RelativePath),
                ("$stored", storedPath), ("$sha", sha), ("$size", info.Length), ("$now", now));
            DrawingRepository.RefreshCurrentRevision(conn, tx, drawingId!.Value);
            tx.Commit();
            return new ImportItem(fileName, source.RelativePath, created ? ImportStatus.Created : ImportStatus.NewRevision,
                parsed.DrawingNumber, revisionCode, parsed.Discipline, drawingId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or ValidationException)
        {
            return Failed(ex.Message);
        }

        ImportItem Failed(string error) => new(fileName, source.RelativePath, ImportStatus.Failed, Error: error);
    }

    private string Store(string sourcePath, string projectCode, string sha)
    {
        var safeProject = new string(projectCode.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        var relative = Path.Combine(safeProject.Length > 0 ? safeProject : "default", sha[..2], sha + ".pdf");
        var destination = paths.AbsoluteStoredPath(relative);
        if (!File.Exists(destination))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".part";
            File.Copy(sourcePath, temp, overwrite: true);
            File.Move(temp, destination, overwrite: true);
        }

        return relative.Replace('\\', '/');
    }

    /// <summary>Remove stored files that are no longer referenced (after deleting drawings or projects).</summary>
    public void DeleteStoredFiles(IEnumerable<string> storedPaths)
    {
        foreach (var stored in storedPaths)
        {
            try
            {
                var full = paths.AbsoluteStoredPath(stored);
                if (File.Exists(full))
                {
                    File.Delete(full);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // a locked file (e.g. open in a PDF viewer) stays behind; it is harmless
            }
        }
    }

    private static SqliteCommand Command(SqliteConnection conn, SqliteTransaction tx, string sql, (string, object?)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    private static T Scalar<T>(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(conn, tx, sql, args);
        var value = cmd.ExecuteScalar();
        if (value is null or DBNull)
        {
            return default!;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target);
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(conn, tx, sql, args);
        cmd.ExecuteNonQuery();
    }
}
