using ConstructDpr.Core.Model;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Core.Data;

/// <summary>SQLite database (one file, e.g. %LOCALAPPDATA%\ConstructDpr\construct-dpr.db).</summary>
public class AppDbContext : DbContext
{
    private readonly string _connectionString;

    public AppDbContext(string databasePath) => _connectionString = $"Data Source={databasePath}";

    public DbSet<Project> Projects => Set<Project>();
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

    protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(_connectionString);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Project>().HasIndex(p => p.Code).IsUnique();
        b.Entity<Activity>().HasIndex(a => new { a.ProjectId, a.Code }).IsUnique();
        b.Entity<Activity>().Ignore(a => a.ProgressPct);
        b.Entity<Dependency>().HasIndex(d => new { d.PredecessorId, d.SuccessorId }).IsUnique();
        b.Entity<Dpr>().HasIndex(d => new { d.ProjectId, d.Number, d.Revision }).IsUnique();
        b.Entity<Dpr>().Ignore(d => d.IsEditable);
        b.Entity<ManpowerLine>().Ignore(m => m.Headcount).Ignore(m => m.ManHours);
        b.Entity<EquipmentLine>().Ignore(e => e.UtilisationPct);
        b.Entity<MaterialLine>().Ignore(m => m.Closing);
        foreach (var t in b.Model.GetEntityTypes())
            foreach (var p in t.GetProperties().Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                p.SetColumnType("TEXT"); // exact decimals in SQLite (stored as text, no binary rounding)
    }

    public override int SaveChanges()
    {
        foreach (var e in ChangeTracker.Entries<Entity>().Where(e => e.State == EntityState.Modified))
            e.Entity.UpdatedAt = DateTime.UtcNow;
        return base.SaveChanges();
    }

    /// <summary>Creates the database file and tables on first run.</summary>
    public static AppDbContext Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var db = new AppDbContext(path);
        db.Database.EnsureCreated();
        return db;
    }
}
