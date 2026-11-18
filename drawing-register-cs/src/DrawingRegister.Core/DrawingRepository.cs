using Microsoft.Data.Sqlite;

namespace DrawingRegister.Core;

public sealed record DrawingQuery(long ProjectId, string? Search = null, string? Discipline = null, string? Status = null);

public sealed class DrawingRepository(Database db)
{
    public IReadOnlyList<Drawing> List(DrawingQuery query)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT d.*, (SELECT COUNT(*) FROM revisions r WHERE r.drawing_id = d.id) AS revision_count
            FROM drawings d
            WHERE d.project_id = $project
              AND ($search IS NULL OR d.drawing_number LIKE $like OR d.title LIKE $like
                   OR d.discipline LIKE $like OR d.area LIKE $like OR d.folder_path LIKE $like)
              AND ($discipline IS NULL OR d.discipline = $discipline)
              AND ($status IS NULL OR d.status = $status)
            ORDER BY d.drawing_number
            """;
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        cmd.Parameters.AddWithValue("$project", query.ProjectId);
        cmd.Parameters.AddWithValue("$search", Database.Db(search));
        cmd.Parameters.AddWithValue("$like", $"%{search}%");
        cmd.Parameters.AddWithValue("$discipline", Database.Db(string.IsNullOrWhiteSpace(query.Discipline) ? null : query.Discipline));
        cmd.Parameters.AddWithValue("$status", Database.Db(string.IsNullOrWhiteSpace(query.Status) ? null : query.Status));
        using var reader = cmd.ExecuteReader();
        var list = new List<Drawing>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public Drawing? Get(long id)
    {
        using var conn = db.Open();
        return Get(conn, null, id);
    }

    internal static Drawing? Get(SqliteConnection conn, SqliteTransaction? tx, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT d.*, (SELECT COUNT(*) FROM revisions r WHERE r.drawing_id = d.id) AS revision_count
            FROM drawings d WHERE d.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Revisions newest first, each with its latest file.</summary>
    public IReadOnlyList<Revision> Revisions(long drawingId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.*, f.id AS file_id, f.original_filename, f.relative_path, f.stored_path, f.sha256, f.size_bytes, f.uploaded_at
            FROM revisions r
            LEFT JOIN drawing_files f ON f.id = (SELECT MAX(id) FROM drawing_files WHERE revision_id = r.id)
            WHERE r.drawing_id = $id
            ORDER BY r.sequence DESC, r.id DESC
            """;
        cmd.Parameters.AddWithValue("$id", drawingId);
        using var reader = cmd.ExecuteReader();
        var list = new List<Revision>();
        while (reader.Read())
        {
            var rev = new Revision
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                DrawingId = drawingId,
                Code = reader.GetString(reader.GetOrdinal("code")),
                Sequence = reader.GetInt32(reader.GetOrdinal("sequence")),
                RevisionDate = Database.FromDbDate(reader["revision_date"]),
                Status = reader["status"] as string,
                Description = reader["description"] as string,
                CreatedAt = Database.FromDb(reader.GetString(reader.GetOrdinal("created_at"))),
                IsCurrent = list.Count == 0,
            };
            if (reader["file_id"] is long fileId)
            {
                rev.File = new DrawingFile
                {
                    Id = fileId,
                    DrawingId = drawingId,
                    RevisionId = rev.Id,
                    OriginalFileName = reader.GetString(reader.GetOrdinal("original_filename")),
                    RelativePath = reader["relative_path"] as string,
                    StoredPath = reader.GetString(reader.GetOrdinal("stored_path")),
                    Sha256 = reader.GetString(reader.GetOrdinal("sha256")),
                    SizeBytes = reader.GetInt64(reader.GetOrdinal("size_bytes")),
                    UploadedAt = Database.FromDb(reader.GetString(reader.GetOrdinal("uploaded_at"))),
                };
            }

            list.Add(rev);
        }

        return list;
    }

    public Drawing Update(Drawing drawing)
    {
        if (string.IsNullOrWhiteSpace(drawing.DrawingNumber))
        {
            throw new ValidationException("Drawing number is required.");
        }

        if (!DrawingStatus.All.Contains(drawing.Status))
        {
            throw new ValidationException($"Unknown status '{drawing.Status}'.");
        }

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE drawings SET drawing_number=$number, title=$title, discipline=$discipline, area=$area,
                   drawing_type=$type, status=$status, remarks=$remarks, updated_at=$now
            WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", drawing.Id);
        cmd.Parameters.AddWithValue("$number", drawing.DrawingNumber.Trim().ToUpperInvariant());
        cmd.Parameters.AddWithValue("$title", drawing.Title?.Trim() ?? "");
        cmd.Parameters.AddWithValue("$discipline", Database.Db(Blank(drawing.Discipline)));
        cmd.Parameters.AddWithValue("$area", Database.Db(Blank(drawing.Area)));
        cmd.Parameters.AddWithValue("$type", Database.Db(Blank(drawing.DrawingType)));
        cmd.Parameters.AddWithValue("$status", drawing.Status);
        cmd.Parameters.AddWithValue("$remarks", Database.Db(Blank(drawing.Remarks)));
        cmd.Parameters.AddWithValue("$now", Database.ToDb(DateTime.Now));
        try
        {
            if (cmd.ExecuteNonQuery() == 0)
            {
                throw new ValidationException("Drawing no longer exists.");
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ValidationException($"Drawing number {drawing.DrawingNumber} already exists in this project.");
        }

        return Get(drawing.Id)!;
    }

    /// <summary>Delete a drawing and its revisions; returns stored file paths no other drawing uses.</summary>
    public IReadOnlyList<string> Delete(long drawingId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var paths = StoredPathsOnlyReferencedBy(conn, tx, "d.id = $id", drawingId);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM drawings WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", drawingId);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return paths;
    }

    internal static IReadOnlyList<string> StoredPathsOnlyReferencedBy(SqliteConnection conn, SqliteTransaction tx, string scope, long id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT DISTINCT f.stored_path FROM drawing_files f JOIN drawings d ON d.id = f.drawing_id
            WHERE {scope}
              AND NOT EXISTS (SELECT 1 FROM drawing_files o JOIN drawings od ON od.id = o.drawing_id
                              WHERE o.stored_path = f.stored_path AND NOT ({scope.Replace("d.", "od.")}))
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    internal static void RefreshCurrentRevision(SqliteConnection conn, SqliteTransaction tx, long drawingId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE drawings SET
                current_revision = (SELECT code FROM revisions WHERE drawing_id = $id ORDER BY sequence DESC, id DESC LIMIT 1),
                status = COALESCE((SELECT status FROM revisions WHERE drawing_id = $id AND status IS NOT NULL
                                   ORDER BY sequence DESC, id DESC LIMIT 1), status),
                updated_at = $now
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", drawingId);
        cmd.Parameters.AddWithValue("$now", Database.ToDb(DateTime.Now));
        cmd.ExecuteNonQuery();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Drawing Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        ProjectId = r.GetInt64(r.GetOrdinal("project_id")),
        DrawingNumber = r.GetString(r.GetOrdinal("drawing_number")),
        Title = r.GetString(r.GetOrdinal("title")),
        Discipline = r["discipline"] as string,
        Area = r["area"] as string,
        DrawingType = r["drawing_type"] as string,
        Status = r.GetString(r.GetOrdinal("status")),
        CurrentRevision = r["current_revision"] as string,
        FolderPath = r["folder_path"] as string,
        Remarks = r["remarks"] as string,
        CreatedAt = Database.FromDb(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt = Database.FromDb(r.GetString(r.GetOrdinal("updated_at"))),
        RevisionCount = r.GetInt32(r.GetOrdinal("revision_count")),
    };
}
