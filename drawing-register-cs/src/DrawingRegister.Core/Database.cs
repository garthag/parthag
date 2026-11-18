using Microsoft.Data.Sqlite;

namespace DrawingRegister.Core;

/// <summary>SQLite database with forward-only schema migrations tracked in PRAGMA user_version.</summary>
public sealed class Database
{
    private static readonly string[] Migrations =
    [
        // 1: initial schema
        """
        CREATE TABLE projects (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            code         TEXT    NOT NULL COLLATE NOCASE UNIQUE,
            name         TEXT    NOT NULL,
            client       TEXT,
            location     TEXT,
            description  TEXT,
            status       TEXT    NOT NULL DEFAULT 'Active',
            start_date   TEXT,
            end_date     TEXT,
            created_at   TEXT    NOT NULL,
            updated_at   TEXT    NOT NULL
        );

        CREATE TABLE drawings (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id        INTEGER NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            drawing_number    TEXT    NOT NULL COLLATE NOCASE,
            title             TEXT    NOT NULL DEFAULT '',
            discipline        TEXT,
            area              TEXT,
            drawing_type      TEXT,
            status            TEXT    NOT NULL DEFAULT 'IFR',
            current_revision  TEXT,
            folder_path       TEXT,
            remarks           TEXT,
            created_at        TEXT    NOT NULL,
            updated_at        TEXT    NOT NULL,
            UNIQUE (project_id, drawing_number)
        );
        CREATE INDEX ix_drawings_project ON drawings(project_id);
        CREATE INDEX ix_drawings_title ON drawings(title);
        CREATE INDEX ix_drawings_discipline ON drawings(discipline);

        CREATE TABLE revisions (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            drawing_id     INTEGER NOT NULL REFERENCES drawings(id) ON DELETE CASCADE,
            code           TEXT    NOT NULL COLLATE NOCASE,
            sequence       INTEGER NOT NULL,
            revision_date  TEXT,
            status         TEXT,
            description    TEXT,
            created_at     TEXT    NOT NULL,
            UNIQUE (drawing_id, code)
        );
        CREATE INDEX ix_revisions_drawing ON revisions(drawing_id);

        CREATE TABLE drawing_files (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            drawing_id         INTEGER NOT NULL REFERENCES drawings(id) ON DELETE CASCADE,
            revision_id        INTEGER NOT NULL REFERENCES revisions(id) ON DELETE CASCADE,
            original_filename  TEXT    NOT NULL,
            relative_path      TEXT,
            stored_path        TEXT    NOT NULL,
            sha256             TEXT    NOT NULL,
            size_bytes         INTEGER NOT NULL,
            uploaded_at        TEXT    NOT NULL
        );
        CREATE INDEX ix_files_drawing ON drawing_files(drawing_id);
        CREATE INDEX ix_files_revision ON drawing_files(revision_id);
        CREATE INDEX ix_files_sha ON drawing_files(sha256);
        """,
    ];

    public Database(string databaseFile)
    {
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Cache = SqliteCacheMode.Private,
        }.ToString();
    }

    public string ConnectionString { get; }

    public static int SchemaVersion => Migrations.Length;

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Apply any migrations newer than the database's user_version.</summary>
    public void Migrate()
    {
        using var connection = Open();
        using var read = connection.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(read.ExecuteScalar());
        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    internal static string ToDb(DateTime value) => value.ToUniversalTime().ToString("O");

    internal static string? ToDbDate(DateTime? value) => value?.ToString("yyyy-MM-dd");

    internal static DateTime FromDb(string value) =>
        DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();

    internal static DateTime? FromDbDate(object value) =>
        value is string s && !string.IsNullOrEmpty(s) ? DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : null;

    internal static object Db(object? value) => value ?? DBNull.Value;
}
