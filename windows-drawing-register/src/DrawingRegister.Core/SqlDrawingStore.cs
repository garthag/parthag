using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DrawingRegister.Core;

/// <summary>Register rows in SQL Server (LocalDB, Express or a full server).</summary>
public sealed class SqlDrawingStore(string connectionString) : IDrawingStore
{
    public const string DefaultConnection =
        // LocalDB can take ~30 s to start the first time, so allow longer than the default 15 s.
        @"Server=(localdb)\MSSQLLocalDB;Database=DrawingRegister;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=60";

    public string ConnectionString { get; } = connectionString;

    SqlConnection Open()
    {
        var c = new SqlConnection(ConnectionString);
        c.Open();
        return c;
    }

    /// <summary>Creates the database if it is missing, then creates / upgrades the schema.</summary>
    public void EnsureDatabase()
    {
        var b = new SqlConnectionStringBuilder(ConnectionString);
        var name = b.InitialCatalog;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("The connection string has no Database name.");
        b.InitialCatalog = "master";
        using (var master = new SqlConnection(b.ConnectionString))
        {
            master.Open();
            using var cmd = master.CreateCommand();
            cmd.CommandText = $"IF DB_ID(@n) IS NULL CREATE DATABASE {Quote(name)}";
            cmd.Parameters.AddWithValue("@n", name);
            cmd.ExecuteNonQuery();
        }
        using var c = Open();
        foreach (var batch in SchemaBatches())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = batch;
            cmd.ExecuteNonQuery();
        }
    }

    static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static IEnumerable<string> SchemaBatches()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DrawingRegister.sql")
                      ?? throw new InvalidOperationException("Schema script is missing from the build.");
        using var r = new StreamReader(s);
        return Regex.Split(r.ReadToEnd(), @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b) && Regex.IsMatch(b, @"^\s*[^-\s]", RegexOptions.Multiline))
            .ToList();
    }

    const string Columns =
        "Id, Plant, Unit, TypeCode, DrawingNo, Title, Kind, Revision, Status, FileName, StoredPath, SourcePath, SizeBytes, Remarks, BatchId, ImportedOn, Sheet, Agency";

    public List<Drawing> All()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {Columns} FROM dbo.Drawing";
        using var r = cmd.ExecuteReader();
        var list = new List<Drawing>();
        while (r.Read())
        {
            list.Add(new Drawing
            {
                Id = r.GetInt32(0), Plant = r.GetString(1), Unit = r.GetString(2), TypeCode = r.GetString(3),
                DrawingNo = r.GetString(4), Title = r.GetString(5), Kind = r.GetString(6), Revision = r.GetString(7),
                Status = DrawingStatusInfo.FromCode(r.GetString(8)), FileName = r.GetString(9), StoredPath = r.GetString(10),
                SourcePath = r.GetString(11), SizeBytes = r.GetInt64(12), Remarks = r.GetString(13),
                BatchId = r.IsDBNull(14) ? null : r.GetInt32(14), ImportedOn = r.GetDateTime(15),
                Sheet = r.GetString(16), Agency = r.GetString(17),
            });
        }
        return list;
    }

    static void Bind(SqlCommand cmd, Drawing d)
    {
        cmd.Parameters.AddWithValue("@Plant", d.Plant);
        cmd.Parameters.AddWithValue("@Unit", d.Unit);
        cmd.Parameters.AddWithValue("@TypeCode", d.TypeCode);
        cmd.Parameters.AddWithValue("@DrawingNo", d.DrawingNo);
        cmd.Parameters.AddWithValue("@Title", d.Title);
        cmd.Parameters.AddWithValue("@Kind", d.Kind);
        cmd.Parameters.AddWithValue("@Revision", d.Revision);
        cmd.Parameters.AddWithValue("@RevRank", d.RevRank);
        cmd.Parameters.AddWithValue("@Status", d.Status.Code());
        cmd.Parameters.AddWithValue("@FileName", d.FileName);
        cmd.Parameters.AddWithValue("@StoredPath", d.StoredPath);
        cmd.Parameters.AddWithValue("@SourcePath", d.SourcePath);
        cmd.Parameters.AddWithValue("@SizeBytes", d.SizeBytes);
        cmd.Parameters.AddWithValue("@Remarks", d.Remarks);
        cmd.Parameters.AddWithValue("@BatchId", (object?)d.BatchId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Sheet", d.Sheet);
        cmd.Parameters.AddWithValue("@Agency", d.Agency);
    }

    public void Add(Drawing d)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT dbo.Drawing (Plant, Unit, TypeCode, DrawingNo, Title, Kind, Revision, RevRank, Status, FileName, StoredPath, SourcePath, SizeBytes, Remarks, BatchId, Sheet, Agency) " +
            "OUTPUT INSERTED.Id, INSERTED.ImportedOn " +
            "VALUES (@Plant, @Unit, @TypeCode, @DrawingNo, @Title, @Kind, @Revision, @RevRank, @Status, @FileName, @StoredPath, @SourcePath, @SizeBytes, @Remarks, @BatchId, @Sheet, @Agency)";
        Bind(cmd, d);
        using var r = cmd.ExecuteReader();
        r.Read();
        d.Id = r.GetInt32(0);
        d.ImportedOn = r.GetDateTime(1);
    }

    public void Update(Drawing d)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE dbo.Drawing SET Plant=@Plant, Unit=@Unit, TypeCode=@TypeCode, DrawingNo=@DrawingNo, Title=@Title, Kind=@Kind, " +
            "Revision=@Revision, RevRank=@RevRank, Status=@Status, FileName=@FileName, StoredPath=@StoredPath, SourcePath=@SourcePath, " +
            "SizeBytes=@SizeBytes, Remarks=@Remarks, BatchId=@BatchId, Sheet=@Sheet, Agency=@Agency, ModifiedOn=SYSDATETIME(), ModifiedBy=SUSER_SNAME() WHERE Id=@Id";
        Bind(cmd, d);
        cmd.Parameters.AddWithValue("@Id", d.Id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(int id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE dbo.Drawing WHERE Id = @Id";
        cmd.Parameters.AddWithValue("@Id", id);
        cmd.ExecuteNonQuery();
    }

    public int StartBatch(string source, string defaultPlant)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT dbo.ImportBatch (Source, DefaultPlant) OUTPUT INSERTED.Id VALUES (@s, @p)";
        cmd.Parameters.AddWithValue("@s", source.Length > 1000 ? source[..1000] : source);
        cmd.Parameters.AddWithValue("@p", defaultPlant);
        return (int)cmd.ExecuteScalar();
    }

    public void FinishBatch(int batchId, int added, int duplicates, int errors)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE dbo.ImportBatch SET Added=@a, Duplicates=@d, Errors=@e WHERE Id=@id";
        cmd.Parameters.AddWithValue("@a", added);
        cmd.Parameters.AddWithValue("@d", duplicates);
        cmd.Parameters.AddWithValue("@e", errors);
        cmd.Parameters.AddWithValue("@id", batchId);
        cmd.ExecuteNonQuery();
    }
}
