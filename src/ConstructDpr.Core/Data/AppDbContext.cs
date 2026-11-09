using ConstructDpr.Shared.Model;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace ConstructDpr.Core.Data;

/// <summary>A person who signs in to the server (ASP.NET Core Identity).</summary>
public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
}

public enum DbProvider { Sqlite, PostgreSql }

/// <summary>
/// The one database of CONSTRUCT DPR. PostgreSQL for the shared server (tables in schema "cdpr", so an existing
/// database such as Supabase's can be reused safely), SQLite for a single PC or a small site-office server.
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<AppUser>(options), IDataProtectionKeyContext
{
    public const string Schema = "cdpr";

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> Members => Set<ProjectMember>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<Dependency> Dependencies => Set<Dependency>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<Baseline> Baselines => Set<Baseline>();
    public DbSet<Dpr> Dprs => Set<Dpr>();
    public DbSet<DprActivityLine> DprActivityLines => Set<DprActivityLine>();
    public DbSet<ManpowerLine> ManpowerLines => Set<ManpowerLine>();
    public DbSet<EquipmentLine> EquipmentLines => Set<EquipmentLine>();
    public DbSet<MaterialLine> MaterialLines => Set<MaterialLine>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    /// <summary>Server sign-in keys, kept in the database so tokens survive restarts and redeploys.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    private bool IsSqlite => Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        if (!IsSqlite) b.HasDefaultSchema(Schema);
        b.Entity<Project>().HasIndex(p => p.Code).IsUnique();
        b.Entity<ProjectMember>().HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique();
        b.Entity<Activity>().HasIndex(a => new { a.ProjectId, a.Code }).IsUnique();
        b.Entity<Activity>().Ignore(a => a.ProgressPct);
        b.Entity<Dependency>().HasIndex(d => new { d.PredecessorId, d.SuccessorId }).IsUnique();
        b.Entity<Dpr>().HasIndex(d => new { d.ProjectId, d.Number, d.Revision }).IsUnique();
        b.Entity<Dpr>().Ignore(d => d.IsEditable);
        b.Entity<ManpowerLine>().Ignore(m => m.Headcount).Ignore(m => m.ManHours);
        b.Entity<EquipmentLine>().Ignore(e => e.UtilisationPct);
        b.Entity<MaterialLine>().Ignore(m => m.Closing);
        b.Entity<AuditEntry>().HasIndex(a => new { a.Entity, a.EntityId });
        foreach (var t in b.Model.GetEntityTypes())
            foreach (var p in t.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                p.SetColumnType(IsSqlite ? "TEXT" : "numeric(20,6)"); // exact decimals, never binary floating point
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void Stamp()
    {
        foreach (var e in ChangeTracker.Entries<Entity>().Where(e => e.State == EntityState.Modified))
        {
            e.Entity.UpdatedAt = DateTime.UtcNow;
            e.Entity.Version++;
        }
    }

    public static DbContextOptions<AppDbContext> Options(DbProvider provider, string connectionString)
    {
        var b = new DbContextOptionsBuilder<AppDbContext>();
        if (provider == DbProvider.Sqlite) b.UseSqlite(connectionString);
        else b.UseNpgsql(connectionString);
        b.ReplaceService<IModelCacheKeyFactory, ProviderModelCacheKey>();
        return b.Options;
    }

    /// <summary>SQLite file for one PC (offline Windows app, tests).</summary>
    public static AppDbContext OpenSqlite(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new AppDbContext(Options(DbProvider.Sqlite, $"Data Source={path}"));
        db.EnsureSchema();
        return db;
    }

    /// <summary>
    /// Creates the tables on first start. Only looks at CONSTRUCT DPR's own tables, so it works in a database that already
    /// holds other tables (EnsureCreated would skip it). Schema version 1.
    /// </summary>
    public void EnsureSchema()
    {
        var creator = (RelationalDatabaseCreator)Database.GetService<IDatabaseCreator>();
        if (!creator.Exists()) creator.Create();
        if (IsSqlite)
        {
            if (!TableExists("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Projects'")) creator.CreateTables();
        }
        else
        {
            if (!TableExists($"SELECT count(*) FROM information_schema.tables WHERE table_schema='{Schema}' AND table_name='Projects'"))
            {
                Database.ExecuteSqlRaw($"CREATE SCHEMA IF NOT EXISTS {Schema}");
                creator.CreateTables();
            }
        }
    }

    private bool TableExists(string sql)
    {
        var conn = Database.GetDbConnection();
        var close = conn.State != System.Data.ConnectionState.Open;
        if (close) conn.Open();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        finally { if (close) conn.Close(); }
    }
}

/// <summary>The model differs per provider (schema, column types), so cache one model per provider.</summary>
internal sealed class ProviderModelCacheKey : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => (context.GetType(), context.Database.ProviderName, designTime);
}
