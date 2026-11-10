using System.Data.Common;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using SiteNotes.Core.Sync;

namespace SiteNotes.Server;

/// <summary>
/// Stores every synced row in one SQL table (SyncRecords) with a server-wide version counter.
/// Devices pull "everything after version N". Plain ADO.NET + SQL, works on SQL Server and SQLite.
/// </summary>
public sealed class SyncStore
{
    private readonly string _provider;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SyncStore(IConfiguration config)
    {
        _provider = config["SiteNotes:Provider"] ?? "Sqlite";
        _connectionString = config.GetConnectionString(_provider)
                            ?? throw new InvalidOperationException($"ConnectionStrings:{_provider} is missing");
    }

    public bool IsSqlServer => _provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase);

    private DbConnection Create() => IsSqlServer ? new SqlConnection(_connectionString) : new SqliteConnection(_connectionString);

    private static DbCommand Cmd(DbConnection c, string sql, DbTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (n, v) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = n;
            p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    public async Task InitializeAsync()
    {
        var script = IsSqlServer ? "sqlserver.sql" : "sqlite.sql";
        await using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SiteNotes.Server.Sql.{script}")
                            ?? throw new InvalidOperationException("Schema script not embedded");
        var sql = await new StreamReader(s).ReadToEndAsync();
        await using var c = Create();
        await c.OpenAsync();
        foreach (var batch in System.Text.RegularExpressions.Regex.Split(sql, @"^\s*GO\s*$",
                     System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(StripComments(batch))) continue;
            await using var cmd = Cmd(c, batch);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string StripComments(string sql) =>
        string.Join("\n", sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--")));

    /// <summary>Applies pushed rows. A row older than what the server has is ignored (newest edit wins).</summary>
    public async Task<PushResponse> PushAsync(PushRequest req)
    {
        await _writeLock.WaitAsync();
        try
        {
            await using var c = Create();
            await c.OpenAsync();
            await using var tx = await c.BeginTransactionAsync();
            int accepted = 0, ignored = 0;
            long version = 0;
            foreach (var r in req.Records)
            {
                await using var find = Cmd(c, "SELECT UpdatedAtUtc FROM SyncRecords WHERE EntityType = @t AND Id = @id", tx,
                    ("@t", r.EntityType), ("@id", r.Id));
                var existing = await find.ExecuteScalarAsync();
                if (existing is not null and not DBNull && Convert.ToDateTime(existing) >= r.UpdatedAtUtc.ToUniversalTime())
                {
                    ignored++;
                    continue;
                }

                await using (var bump = Cmd(c, "UPDATE SyncCounter SET Value = Value + 1 WHERE Id = 1", tx))
                    await bump.ExecuteNonQueryAsync();
                await using (var read = Cmd(c, "SELECT Value FROM SyncCounter WHERE Id = 1", tx))
                    version = Convert.ToInt64(await read.ExecuteScalarAsync());

                var sql = existing is null or DBNull
                    ? "INSERT INTO SyncRecords (Id, EntityType, ProjectId, Json, UpdatedAtUtc, IsDeleted, Version, DeviceId) " +
                      "VALUES (@id, @t, @p, @j, @u, @d, @v, @dev)"
                    : "UPDATE SyncRecords SET ProjectId = @p, Json = @j, UpdatedAtUtc = @u, IsDeleted = @d, Version = @v, DeviceId = @dev " +
                      "WHERE EntityType = @t AND Id = @id";
                await using var write = Cmd(c, sql, tx, ("@id", r.Id), ("@t", r.EntityType), ("@p", r.ProjectId), ("@j", r.Json),
                    ("@u", r.UpdatedAtUtc.ToUniversalTime()), ("@d", r.IsDeleted), ("@v", version), ("@dev", req.DeviceId));
                await write.ExecuteNonQueryAsync();
                accepted++;
            }
            await tx.CommitAsync();
            return new PushResponse(version == 0 ? await CurrentVersionAsync() : version, accepted, ignored);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<long> CurrentVersionAsync()
    {
        await using var c = Create();
        await c.OpenAsync();
        await using var cmd = Cmd(c, "SELECT Value FROM SyncCounter WHERE Id = 1");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<PullResponse> PullAsync(long since, int take)
    {
        take = Math.Clamp(take, 1, 2000);
        await using var c = Create();
        await c.OpenAsync();
        var sql = IsSqlServer
            ? $"SELECT TOP ({take + 1}) Id, EntityType, ProjectId, Json, UpdatedAtUtc, IsDeleted, Version FROM SyncRecords WHERE Version > @s ORDER BY Version"
            : $"SELECT Id, EntityType, ProjectId, Json, UpdatedAtUtc, IsDeleted, Version FROM SyncRecords WHERE Version > @s ORDER BY Version LIMIT {take + 1}";
        await using var cmd = Cmd(c, sql, null, ("@s", since));
        await using var rd = await cmd.ExecuteReaderAsync();
        var list = new List<SyncRecord>();
        while (await rd.ReadAsync())
        {
            list.Add(new SyncRecord(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3),
                DateTime.SpecifyKind(Convert.ToDateTime(rd.GetValue(4)), DateTimeKind.Utc),
                Convert.ToBoolean(rd.GetValue(5)), Convert.ToInt64(rd.GetValue(6))));
        }
        var hasMore = list.Count > take;
        if (hasMore) list.RemoveAt(list.Count - 1);
        var version = list.Count > 0 ? list[^1].Version : Math.Max(since, await CurrentVersionAsync());
        return new PullResponse(list, version, hasMore);
    }

    public async Task<object> StatsAsync()
    {
        await using var c = Create();
        await c.OpenAsync();
        await using var cmd = Cmd(c, "SELECT EntityType, COUNT(*) FROM SyncRecords WHERE IsDeleted = 0 GROUP BY EntityType");
        await using var rd = await cmd.ExecuteReaderAsync();
        var d = new Dictionary<string, long>();
        while (await rd.ReadAsync()) d[rd.GetString(0)] = Convert.ToInt64(rd.GetValue(1));
        return new { provider = _provider, version = await CurrentVersionAsync(), rows = d };
    }
}
