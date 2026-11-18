using Microsoft.Data.Sqlite;

namespace DrawingRegister.Core;

public sealed class ValidationException(string message) : Exception(message);

public sealed class ProjectRepository(Database db)
{
    public IReadOnlyList<Project> List(string? search = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.*, (SELECT COUNT(*) FROM drawings d WHERE d.project_id = p.id) AS drawing_count
            FROM projects p
            WHERE $q IS NULL OR p.code LIKE $like OR p.name LIKE $like OR p.client LIKE $like
            ORDER BY p.code
            """;
        cmd.Parameters.AddWithValue("$q", Database.Db(string.IsNullOrWhiteSpace(search) ? null : search));
        cmd.Parameters.AddWithValue("$like", $"%{search?.Trim()}%");
        using var reader = cmd.ExecuteReader();
        var list = new List<Project>();
        while (reader.Read())
        {
            list.Add(Map(reader));
        }

        return list;
    }

    public Project? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.*, (SELECT COUNT(*) FROM drawings d WHERE d.project_id = p.id) AS drawing_count
            FROM projects p WHERE p.id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Insert or update. Throws <see cref="ValidationException"/> for invalid input or a duplicate code.</summary>
    public Project Save(Project project)
    {
        Validate(project);
        project.Code = project.Code.Trim().ToUpperInvariant();
        project.Name = project.Name.Trim();
        var now = DateTime.Now;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        if (project.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO projects (code, name, client, location, description, status, start_date, end_date, created_at, updated_at)
                VALUES ($code, $name, $client, $location, $description, $status, $start, $end, $now, $now)
                RETURNING id
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE projects SET code=$code, name=$name, client=$client, location=$location, description=$description,
                       status=$status, start_date=$start, end_date=$end, updated_at=$now
                WHERE id=$id
                RETURNING id
                """;
            cmd.Parameters.AddWithValue("$id", project.Id);
        }

        cmd.Parameters.AddWithValue("$code", project.Code);
        cmd.Parameters.AddWithValue("$name", project.Name);
        cmd.Parameters.AddWithValue("$client", Database.Db(Blank(project.Client)));
        cmd.Parameters.AddWithValue("$location", Database.Db(Blank(project.Location)));
        cmd.Parameters.AddWithValue("$description", Database.Db(Blank(project.Description)));
        cmd.Parameters.AddWithValue("$status", project.Status);
        cmd.Parameters.AddWithValue("$start", Database.Db(Database.ToDbDate(project.StartDate)));
        cmd.Parameters.AddWithValue("$end", Database.Db(Database.ToDbDate(project.EndDate)));
        cmd.Parameters.AddWithValue("$now", Database.ToDb(now));
        try
        {
            var id = cmd.ExecuteScalar() ?? throw new ValidationException("Project no longer exists.");
            project.Id = Convert.ToInt64(id);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT
        {
            throw new ValidationException($"A project with code {project.Code} already exists.");
        }

        return Get(project.Id)!;
    }

    /// <summary>Delete a project with all its drawings, revisions and file records. Returns stored paths now unused.</summary>
    public IReadOnlyList<string> Delete(long id)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var paths = DrawingRepository.StoredPathsOnlyReferencedBy(conn, tx, "d.project_id = $id", id);
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM projects WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
        tx.Commit();
        return paths;
    }

    private static void Validate(Project p)
    {
        if (string.IsNullOrWhiteSpace(p.Code) || p.Code.Trim().Length < 2)
        {
            throw new ValidationException("Project code must have at least 2 characters.");
        }

        if (p.Code.Trim().Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
        {
            throw new ValidationException("Project code may only contain letters, digits, '-' and '_'.");
        }

        if (string.IsNullOrWhiteSpace(p.Name))
        {
            throw new ValidationException("Project name is required.");
        }

        if (p.StartDate is { } s && p.EndDate is { } e && e < s)
        {
            throw new ValidationException("End date must be on or after the start date.");
        }

        if (!ProjectStatus.All.Contains(p.Status))
        {
            throw new ValidationException($"Unknown status '{p.Status}'.");
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Project Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Code = r.GetString(r.GetOrdinal("code")),
        Name = r.GetString(r.GetOrdinal("name")),
        Client = r["client"] as string,
        Location = r["location"] as string,
        Description = r["description"] as string,
        Status = r.GetString(r.GetOrdinal("status")),
        StartDate = Database.FromDbDate(r["start_date"]),
        EndDate = Database.FromDbDate(r["end_date"]),
        CreatedAt = Database.FromDb(r.GetString(r.GetOrdinal("created_at"))),
        UpdatedAt = Database.FromDb(r.GetString(r.GetOrdinal("updated_at"))),
        DrawingCount = r.GetInt32(r.GetOrdinal("drawing_count")),
    };
}
