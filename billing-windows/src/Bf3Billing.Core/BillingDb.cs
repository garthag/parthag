using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bf3Billing.Core;

/// <summary>The SQLite data file (billing.db) and its schema.</summary>
public sealed class BillingDb
{
    public const int SchemaVersion = 2;
    public string FilePath { get; }

    static BillingDb()
    {
        SqlMapper.AddTypeHandler(new DateHandler());
        SqlMapper.AddTypeHandler(new DecimalHandler());
    }

    public BillingDb(string filePath, bool seedMasters = true)
    {
        FilePath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var c = Open();
        EnsureSchema(c);
        if (seedMasters) Seed.Masters(c);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bf3Billing", "billing.db");

    public SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        return c;
    }

    /// <summary>Copies the whole database to another file (safe while the app is open).</summary>
    public void BackupTo(string targetPath)
    {
        if (File.Exists(targetPath)) File.Delete(targetPath);
        using var src = Open();
        using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = targetPath, Pooling = false }.ToString());
        dst.Open();
        src.BackupDatabase(dst);
    }

    /// <summary>Checks that a file is a billing database before it is restored or opened.</summary>
    public static bool LooksLikeBillingDb(string path)
    {
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            c.Open();
            return c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Bill','BillLine','WorkOrder','BomItem')") == 4;
        }
        catch (SqliteException) { return false; }
    }

    static void EnsureSchema(IDbConnection c)
    {
        c.Execute(Schema);
        // Version 2: user-added fields are kept as JSON in an Extra column.
        foreach (var table in new[] { "Contractor", "WorkOrder", "BomItem", "BillLine" })
            if (!c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").Contains("Extra", StringComparer.OrdinalIgnoreCase))
                c.Execute($"ALTER TABLE {table} ADD COLUMN Extra TEXT NOT NULL DEFAULT '{{}}'");
        var v = c.ExecuteScalar<string?>("SELECT Value FROM Setting WHERE Key='SchemaVersion'");
        if (v == null)
            c.Execute("INSERT INTO Setting(Key, Value) VALUES ('SchemaVersion', @v)", new { v = SchemaVersion.ToString(CultureInfo.InvariantCulture) });
    }

    const string Schema = """
        CREATE TABLE IF NOT EXISTS Setting(
            Key   TEXT PRIMARY KEY,
            Value TEXT);

        CREATE TABLE IF NOT EXISTS Contractor(
            Id      INTEGER PRIMARY KEY,
            Code    TEXT NOT NULL DEFAULT '',
            Name    TEXT NOT NULL UNIQUE COLLATE NOCASE,
            Gstin   TEXT NOT NULL DEFAULT '',
            Pan     TEXT NOT NULL DEFAULT '',
            Phone   TEXT NOT NULL DEFAULT '',
            Address TEXT NOT NULL DEFAULT '',
            Remarks TEXT NOT NULL DEFAULT '');

        CREATE TABLE IF NOT EXISTS WorkOrder(
            Id           INTEGER PRIMARY KEY,
            WoNo         TEXT NOT NULL UNIQUE COLLATE NOCASE,
            ContractorId INTEGER NOT NULL REFERENCES Contractor(Id),
            Service      TEXT NOT NULL DEFAULT '',
            Value        REAL NOT NULL DEFAULT 0,
            WoDate       TEXT,
            Status       TEXT NOT NULL DEFAULT 'Open',
            Remarks      TEXT NOT NULL DEFAULT '');

        CREATE TABLE IF NOT EXISTS Activity(
            Id          INTEGER PRIMARY KEY,
            Name        TEXT NOT NULL UNIQUE COLLATE NOCASE,
            IsBom       INTEGER NOT NULL DEFAULT 0,
            DefaultUnit TEXT NOT NULL DEFAULT 't',
            SortOrder   INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE IF NOT EXISTS BomItem(
            Id        INTEGER PRIMARY KEY,
            System    TEXT NOT NULL,
            DrawingNo TEXT NOT NULL,
            Sheet     TEXT NOT NULL DEFAULT '',
            Rev       TEXT NOT NULL DEFAULT '',
            Item      TEXT NOT NULL DEFAULT '',
            Tonnes    REAL NOT NULL DEFAULT 0,
            UNIQUE(System, DrawingNo, Sheet, Rev, Item));

        CREATE TABLE IF NOT EXISTS Rate(
            Id          INTEGER PRIMARY KEY,
            WorkOrderId INTEGER NOT NULL DEFAULT 0,
            ActivityId  INTEGER NOT NULL REFERENCES Activity(Id) ON DELETE CASCADE,
            Unit        TEXT NOT NULL DEFAULT '',
            Amount      REAL NOT NULL DEFAULT 0,
            UNIQUE(WorkOrderId, ActivityId));

        CREATE TABLE IF NOT EXISTS Bill(
            Id             INTEGER PRIMARY KEY,
            WorkOrderId    INTEGER NOT NULL REFERENCES WorkOrder(Id),
            BillNo         TEXT NOT NULL,
            BillType       TEXT NOT NULL DEFAULT 'RA bill',
            BillDate       TEXT NOT NULL,
            PeriodFrom     TEXT,
            PeriodTo       TEXT,
            Status         TEXT NOT NULL DEFAULT 'Draft',
            GstPct         REAL NOT NULL DEFAULT 18,
            RetentionPct   REAL NOT NULL DEFAULT 0,
            TdsPct         REAL NOT NULL DEFAULT 0,
            OtherDeduction REAL NOT NULL DEFAULT 0,
            PaidAmount     REAL NOT NULL DEFAULT 0,
            PaidOn         TEXT,
            Remarks        TEXT NOT NULL DEFAULT '',
            CreatedAt      TEXT,
            UpdatedAt      TEXT,
            UNIQUE(WorkOrderId, BillNo COLLATE NOCASE));

        CREATE TABLE IF NOT EXISTS BillLine(
            Id          INTEGER PRIMARY KEY,
            BillId      INTEGER NOT NULL REFERENCES Bill(Id) ON DELETE CASCADE,
            LineNo      INTEGER NOT NULL,
            BomItemId   INTEGER REFERENCES BomItem(Id),
            Description TEXT NOT NULL DEFAULT '',
            Remarks     TEXT NOT NULL DEFAULT '',
            ActivityId  INTEGER REFERENCES Activity(Id),
            Unit        TEXT NOT NULL DEFAULT '',
            Qty         REAL NOT NULL DEFAULT 0,
            Rate        REAL NOT NULL DEFAULT 0);

        CREATE TABLE IF NOT EXISTS CustomField(
            Id        INTEGER PRIMARY KEY,
            Entity    TEXT NOT NULL,
            Name      TEXT NOT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            UNIQUE(Entity, Name COLLATE NOCASE));

        CREATE INDEX IF NOT EXISTS IX_BillLine_Bill ON BillLine(BillId);
        CREATE INDEX IF NOT EXISTS IX_BillLine_Bom ON BillLine(BomItemId, ActivityId);
        CREATE INDEX IF NOT EXISTS IX_Bill_WorkOrder ON Bill(WorkOrderId);
        """;

    /// <summary>Dates are stored as ISO text: 2026-03-09 (or 2026-03-09 14:05:00 for time stamps).</summary>
    sealed class DateHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.String;
            parameter.Value = value.TimeOfDay == TimeSpan.Zero
                ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        public override DateTime Parse(object value) => value switch
        {
            DateTime d => d,
            string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.None),
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Amounts and quantities are stored as REAL and rounded back to 4 decimals when read.</summary>
    sealed class DecimalHandler : SqlMapper.TypeHandler<decimal>
    {
        public override void SetValue(IDbDataParameter parameter, decimal value)
        {
            parameter.DbType = DbType.Double;
            parameter.Value = (double)value;
        }

        public override decimal Parse(object value) => value switch
        {
            double d => Math.Round((decimal)d, 4),
            long l => l,
            string s => decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => Math.Round(Convert.ToDecimal(value, CultureInfo.InvariantCulture), 4),
        };
    }
}
