using System.Data;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace BSL.Core.Data;

/// <summary>Thin ADO.NET helper around SQL Server. One instance per connection string.</summary>
public sealed class Database
{
    public string ConnectionString { get; }

    public Database(string connectionString) => ConnectionString = connectionString;

    public SqlConnection Open()
    {
        var cn = new SqlConnection(ConnectionString);
        cn.Open();
        return cn;
    }

    /// <summary>Creates the database if missing and runs the embedded schema/view/seed scripts.</summary>
    public void EnsureCreated()
    {
        var b = new SqlConnectionStringBuilder(ConnectionString);
        var dbName = b.InitialCatalog;
        if (!string.IsNullOrWhiteSpace(dbName))
        {
            b.InitialCatalog = "master";
            using var master = new SqlConnection(b.ConnectionString);
            master.Open();
            using var cmd = master.CreateCommand();
            cmd.CommandText = $"IF DB_ID(@n) IS NULL EXEC(N'CREATE DATABASE [{dbName.Replace("]", "]]")}]')";
            cmd.Parameters.AddWithValue("@n", dbName);
            cmd.ExecuteNonQuery();
        }

        using var cn = Open();
        foreach (var script in EmbeddedScripts())
            foreach (var batch in SplitBatches(script.Sql))
            {
                using var cmd = cn.CreateCommand();
                cmd.CommandText = batch;
                cmd.CommandTimeout = 120;
                try { cmd.ExecuteNonQuery(); }
                catch (SqlException ex) { throw new InvalidOperationException($"{script.Name}: {ex.Message}\n---\n{batch}", ex); }
            }
    }

    public static IEnumerable<(string Name, string Sql)> EmbeddedScripts()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).OrderBy(n => n))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            yield return (name, r.ReadToEnd());
        }
    }

    public static IEnumerable<string> SplitBatches(string sql) =>
        Regex.Split(sql, @"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
             .Select(b => b.Trim()).Where(b => b.Length > 0 && !IsOnlyComments(b));

    static bool IsOnlyComments(string b) =>
        Regex.Replace(Regex.Replace(b, @"/\*.*?\*/", "", RegexOptions.Singleline), @"--.*$", "", RegexOptions.Multiline).Trim().Length == 0;

    public DataTable Query(string sql, params (string Name, object? Value)[] ps)
    {
        using var cn = Open();
        return Query(cn, null, sql, ps);
    }

    public static DataTable Query(SqlConnection cn, SqlTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = new SqlCommand(sql, cn, tx) { CommandTimeout = 300 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        using var da = new SqlDataAdapter(cmd);
        var dt = new DataTable();
        da.Fill(dt);
        return dt;
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] ps)
    {
        using var cn = Open();
        return Scalar(cn, null, sql, ps);
    }

    public static object? Scalar(SqlConnection cn, SqlTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = new SqlCommand(sql, cn, tx);
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var r = cmd.ExecuteScalar();
        return r is DBNull ? null : r;
    }

    public int Execute(string sql, params (string Name, object? Value)[] ps)
    {
        using var cn = Open();
        return Execute(cn, null, sql, ps);
    }

    public static int Execute(SqlConnection cn, SqlTransaction? tx, string sql, params (string Name, object? Value)[] ps)
    {
        using var cmd = new SqlCommand(sql, cn, tx) { CommandTimeout = 300 };
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public void Audit(string entity, int? recordId, string action, string? detail = null) =>
        Execute("INSERT dbo.AuditLog(EntityName, RecordId, Action, Detail) VALUES(@e,@r,@a,@d)",
            ("@e", entity), ("@r", recordId), ("@a", action), ("@d", detail));
}
