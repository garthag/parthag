#pragma warning disable EF1002 // fixed SQL text built from constants in this test file
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Model;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Tests;

/// <summary>The hand-written SQL: reporting views and the script files in database/.</summary>
public class SqlViewTests
{
    private sealed record Row(string Code, decimal Qty);

    /// <summary>The views must agree with what the apps show. Runs on SQLite here and on SQL Server in the build.</summary>
    public static void CheckViews(AppDbContext db, string prefix)
    {
        T One<T>(string sql) => db.Database.SqlQueryRaw<T>(sql).AsEnumerable().First();
        Assert.Equal(SqlViews.Version, One<int>($"SELECT \"ViewsVersion\" AS \"Value\" FROM {prefix}\"vw_SchemaInfo\""));

        // Approved quantity per activity = sum of the daily quantities = what the app stores on the activity.
        var acts = db.Activities.AsNoTracking().ToList();
        var daily = db.Database.SqlQueryRaw<Row>(
            $"SELECT \"ActivityCode\" AS \"Code\", CAST(SUM(\"AcceptedQty\") AS decimal(20,6)) AS \"Qty\" FROM {prefix}\"vw_DailyQuantities\" GROUP BY \"ActivityCode\"").ToList();
        Assert.NotEmpty(daily);
        foreach (var d in daily) Assert.Equal(acts.Single(a => a.Code == d.Code).AcceptedQty, d.Qty);
        var progress = db.Database.SqlQueryRaw<Row>($"SELECT \"ActivityCode\" AS \"Code\", \"ApprovedQty\" AS \"Qty\" FROM {prefix}\"vw_ActivityProgress\"").ToList();
        Assert.Equal(acts.Count, progress.Count);
        foreach (var r in progress) Assert.Equal(acts.Single(a => a.Code == r.Code).AcceptedQty, r.Qty);

        var approved = db.Dprs.Count(d => d.Status == DprStatus.Approved);
        Assert.Equal(approved, One<int>($"SELECT COUNT(*) AS \"Value\" FROM {prefix}\"vw_ApprovedReports\""));
        Assert.Equal(db.Dprs.Count(), One<int>($"SELECT COUNT(*) AS \"Value\" FROM {prefix}\"vw_ReportRegister\""));
        Assert.True(One<int>($"SELECT CAST(SUM(\"Headcount\") AS int) AS \"Value\" FROM {prefix}\"vw_DailyManpower\"") > 0);
        Assert.True(One<int>($"SELECT COUNT(*) AS \"Value\" FROM {prefix}\"vw_DailyEquipment\"") > 0);
    }

    [Fact]
    public void Views_agree_with_the_app_on_sqlite()
    {
        using var t = new DemoDb();
        CheckViews(t.Db, "");
    }

    [Fact]
    public void A_superseded_revision_is_not_counted_twice()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var first = t.Db.Dprs.Where(d => d.Status == DprStatus.Approved).OrderBy(d => d.ReportDate).First();
        var rev = api.ReviseDprAsync(t.Project.Id, first.Id, "Correct quantity").Result;
        rev.Activities[0].QtyToday += 1;
        api.SubmitDprAsync(t.Project.Id, rev).Wait();
        api.ApproveDprAsync(t.Project.Id, rev.Id, new()).Wait();
        t.Db.ChangeTracker.Clear();
        var numbers = t.Db.Dprs.Where(d => d.Status == DprStatus.Approved).Select(d => d.Number).Distinct().Count();
        Assert.Equal(numbers, t.Db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM \"vw_ApprovedReports\"").AsEnumerable().First());
        CheckViewsSums(t.Db);
    }

    private static void CheckViewsSums(AppDbContext db)
    {
        foreach (var a in db.Activities.AsNoTracking().ToList().Where(a => a.AcceptedQty > 0))
        {
            var sum = db.Database.SqlQueryRaw<decimal>(
                "SELECT CAST(SUM(\"AcceptedQty\") AS decimal(20,6)) AS \"Value\" FROM \"vw_DailyQuantities\" WHERE \"ActivityCode\" = {0}", a.Code).AsEnumerable().First();
            Assert.Equal(a.AcceptedQty, sum);
        }
    }

    [Fact]
    public void Views_are_recreated_when_missing_or_outdated()
    {
        using var t = new DemoDb();
        t.Db.Database.ExecuteSqlRaw("DROP VIEW \"vw_SchemaInfo\"");
        t.Db.Database.ExecuteSqlRaw("DROP VIEW \"vw_DailyManpower\"");
        using (var again = new AppDbContext(AppDbContext.Options(DbProvider.Sqlite, $"Data Source={t.Path}"))) again.EnsureSchema();
        CheckViews(t.Db, "");
    }

    [Fact]
    public void Sql_files_in_the_repository_are_up_to_date()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ConstructDpr.sln"))) root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository root not found");
        foreach (var (name, expected) in SqlScripts.Files())
        {
            var path = Path.Combine(root, "database", name);
            Assert.True(File.Exists(path), $"{name} is missing. Run: dotnet run --project tools/ConstructDpr.Preview -- --sql database");
            Assert.True(expected == File.ReadAllText(path).ReplaceLineEndings("\n"),
                $"database/{name} is out of date. Run: dotnet run --project tools/ConstructDpr.Preview -- --sql database");
        }
    }
}
