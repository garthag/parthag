using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using Npgsql;

namespace ConstructDpr.Server;

/// <summary>Reads the server's settings from appsettings.json or environment variables.</summary>
public static class ServerSettings
{
    public static string Version => typeof(ServerSettings).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// DATABASE_URL (postgres://user:password@host:5432/db, as Render, Supabase or Neon show it), or
    /// ConnectionStrings:Default with Database:Provider = PostgreSql | Sqlite. Default: SQLite file next to the server.
    /// </summary>
    public static (DbProvider Provider, string Connection) Database(IConfiguration cfg, string contentRoot)
    {
        var url = cfg["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(url)) return (DbProvider.PostgreSql, FromUrl(url));
        var provider = Enum.TryParse<DbProvider>(cfg["Database:Provider"], true, out var p) ? p : DbProvider.Sqlite;
        var conn = cfg.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(conn))
        {
            if (provider == DbProvider.PostgreSql) throw new InvalidOperationException("Set DATABASE_URL or ConnectionStrings:Default for PostgreSQL.");
            var dir = Path.Combine(contentRoot, "data");
            Directory.CreateDirectory(dir);
            conn = $"Data Source={Path.Combine(dir, "construct-dpr.db")}";
        }
        return (provider, conn);
    }

    public static string FromUrl(string url)
    {
        var u = new Uri(url.Trim());
        var userInfo = u.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = u.Host,
            Port = u.IsDefaultPort || u.Port <= 0 ? 5432 : u.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            Database = u.AbsolutePath.Trim('/') is { Length: > 0 } db ? Uri.UnescapeDataString(db) : "postgres",
            SslMode = u.Host is "localhost" or "127.0.0.1" or "db" ? SslMode.Prefer : SslMode.Require,
        };
        foreach (var kv in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = kv.Split('=', 2);
            if (parts[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase) && parts.Length == 2 && Enum.TryParse<SslMode>(parts[1], true, out var m)) b.SslMode = m;
        }
        return b.ConnectionString;
    }

    public static AppFeatures Features(IConfiguration cfg) =>
        new(cfg.GetValue("Features:AdminPortal", false), cfg.GetValue("Features:Registration", true), Version);
}

/// <summary>"Today" on site (the server itself usually runs in UTC).</summary>
public sealed class SiteClock(string timeZone)
{
    private readonly TimeZoneInfo _zone = Find(timeZone);

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _zone));

    private static TimeZoneInfo Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}
