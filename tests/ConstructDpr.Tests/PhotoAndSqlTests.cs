using ConstructDpr.Core;
using ConstructDpr.Core.Data;
using ConstructDpr.Core.Services;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Tests;

public class PhotoAndSqlTests
{
    /// <summary>A real 1×1 PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [Fact]
    public async Task Photos_are_stored_in_the_database_and_locked_after_approval()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var pending = (await api.DprsAsync(t.Project.Id)).Single(d => d.Status == DprStatus.Submitted);
        // Submitted reports cannot take photos; drafts can.
        await Assert.ThrowsAsync<WorkflowException>(() => api.AddPhotoAsync(t.Project.Id, pending.Id, "x.png", Png, null));
        var draft = await api.CreateDprAsync(t.Project.Id, t.Today, Shift.Night);
        await Assert.ThrowsAsync<WorkflowException>(() => api.AddPhotoAsync(t.Project.Id, draft.Id, "fake.jpg", "not an image at all"u8.ToArray(), null));
        var info = await api.AddPhotoAsync(t.Project.Id, draft.Id, @"C:\phone\IMG_0001.png", Png, "Truss T3 lifted");
        Assert.Equal(("IMG_0001.png", "image/png", "Truss T3 lifted"), (info.FileName, info.ContentType, info.Caption));
        Assert.Equal(Png, (await api.PhotoAsync(t.Project.Id, info.Id)).Content);

        var d = await api.DprAsync(t.Project.Id, draft.Id);
        d.Activities.Add(new DprActivityLine { ActivityId = t.ActivityId("A2000"), QtyToday = 2 });
        await api.SubmitDprAsync(t.Project.Id, d);
        await api.ApproveDprAsync(t.Project.Id, draft.Id, new());
        Assert.True(await api.VerifyDprAsync(t.Project.Id, draft.Id));
        await Assert.ThrowsAsync<WorkflowException>(() => api.DeletePhotoAsync(t.Project.Id, info.Id));

        // Swapping the stored image is detected by the approval snapshot.
        t.Db.Database.ExecuteSql($"UPDATE Photos SET Sha256 = 'tampered' WHERE Id = {info.Id}");
        Assert.False(await api.VerifyDprAsync(t.Project.Id, draft.Id));
    }

    [Fact]
    public async Task Pdf_contains_the_photos_and_revisions_keep_them()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var draft = await api.CreateDprAsync(t.Project.Id, t.Today, Shift.Night);
        var without = (await api.ReportAsync(t.Project.Id, ReportKind.DprPdf, draft.Id)).Content.Length;
        await api.AddPhotoAsync(t.Project.Id, draft.Id, "a.png", Png, "Footing F6");
        var with = await api.ReportAsync(t.Project.Id, ReportKind.DprPdf, draft.Id);
        Assert.True(with.Content.Length > without);

        var approved = (await api.DprsAsync(t.Project.Id)).First(x => x.Status == DprStatus.Approved);
        var d = await api.DprAsync(t.Project.Id, approved.Id);
        t.Db.Photos.Add(new DprPhoto { ProjectId = t.Project.Id, DprId = approved.Id, FileName = "old.png", Content = Png, Sha256 = "x", ContentType = "image/png" });
        t.Db.SaveChanges();
        var rev = await api.ReviseDprAsync(t.Project.Id, approved.Id, "Add missing photo caption");
        Assert.Single(await api.PhotosAsync(t.Project.Id, rev.Id));
    }

    [Fact]
    public async Task Database_from_an_earlier_version_gains_the_new_tables()
    {
        using var t = new DemoDb();
        t.Db.Database.ExecuteSqlRaw("DROP TABLE Photos");
        using (var again = new AppDbContext(AppDbContext.Options(DbProvider.Sqlite, $"Data Source={t.Path}"))) again.EnsureSchema();
        var draft = await t.Api().CreateDprAsync(t.Project.Id, t.Today, Shift.Night);
        await t.Api().AddPhotoAsync(t.Project.Id, draft.Id, "p.png", Png, null);
        Assert.Equal(1, t.Db.Photos.Count());
        Assert.Equal(8, t.Db.Dprs.Count(d => d.Status != DprStatus.Draft)); // existing data untouched
    }

    [Fact]
    public async Task Office_accounts_register_sign_in_and_lock_out()
    {
        using var t = new DemoDb();
        var accounts = new DirectAccounts(() => new AppDbContext(AppDbContext.Options(DbProvider.Sqlite, $"Data Source={t.Path}")));
        var me = accounts.Register("ravi@site.test", "office-pass-1", "Ravi Kumar");
        Assert.Throws<WorkflowException>(() => accounts.Register("RAVI@site.test", "office-pass-1", "Again"));
        Assert.Equal(me.UserId, accounts.SignIn("Ravi@Site.test", "office-pass-1").UserId);
        Assert.Throws<UnauthorizedAccessException>(() => accounts.SignIn("ravi@site.test", "wrong"));

        // The account works with the rules engine: create a project, add a colleague.
        var api = t.Api(me);
        var p = await api.CreateProjectAsync(new Project { Code = "OFFICE1", Name = "Office test", StartDate = t.Today });
        accounts.Register("sita@site.test", "office-pass-2", "Sita");
        await api.AddMemberAsync(p.Id, "sita@site.test", ProjectRole.SiteEngineer);
        Assert.Single(await t.Api(accounts.SignIn("sita@site.test", "office-pass-2")).ProjectsAsync());

        for (var i = 0; i < 8; i++) Assert.Throws<UnauthorizedAccessException>(() => accounts.SignIn("ravi@site.test", "bad"));
        var locked = Assert.Throws<UnauthorizedAccessException>(() => accounts.SignIn("ravi@site.test", "office-pass-1"));
        Assert.Contains("Too many", locked.Message);
    }

    [Fact]
    public async Task Change_stamp_moves_when_anyone_changes_the_project()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var before = await api.ChangeStampAsync(t.Project.Id);
        await api.CreateDprAsync(t.Project.Id, t.Today, Shift.Night);
        Assert.True(await api.ChangeStampAsync(t.Project.Id) > before);
    }

    /// <summary>
    /// Runs only where a SQL Server is available (the build pipeline starts one):
    /// CDPR_SQLSERVER="Server=localhost;User Id=sa;Password=…;TrustServerCertificate=true".
    /// </summary>
    [Fact]
    public async Task Sql_server_end_to_end()
    {
        var conn = Environment.GetEnvironmentVariable("CDPR_SQLSERVER");
        if (string.IsNullOrWhiteSpace(conn)) return;
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(conn) { InitialCatalog = $"cdpr_test_{Guid.NewGuid():N}" };
        var options = AppDbContext.Options(DbProvider.SqlServer, b.ConnectionString);
        try
        {
            using (var db = new AppDbContext(options)) db.EnsureSchema();
            using (var db = new AppDbContext(options)) db.EnsureSchema(); // second start: nothing to do
            var accounts = new DirectAccounts(() => new AppDbContext(options));
            var pm = accounts.Register("pm@site.test", "sqlserver-1", "PM");
            var api = new LocalApi(() => new AppDbContext(options), pm, new AppFeatures(true, true, "test"));
            var p = await api.CreateDemoProjectAsync();
            var pending = (await api.DprsAsync(p.Id)).Single(d => d.Status == DprStatus.Submitted);
            await api.ApproveDprAsync(p.Id, pending.Id, new());
            Assert.True(await api.VerifyDprAsync(p.Id, pending.Id));
            var draft = await api.CreateDprAsync(p.Id, DateOnly.FromDateTime(DateTime.Today), Shift.Night);
            await api.AddPhotoAsync(p.Id, draft.Id, "p.png", Png, "SQL Server photo");
            Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await api.ReportAsync(p.Id, ReportKind.DprPdf, draft.Id)).Content, 0, 4));
            var stats = await api.DashboardAsync(p.Id);
            Assert.True(stats.ActualPct > 0);
            var a = (await api.ActivitiesAsync(p.Id)).First();
            a.Rate += 1; // decimals exact
            await api.SaveActivitiesAsync(p.Id, new ActivityChanges { Upserts = [a] });
            Assert.Equal(a.Rate, (await api.ActivitiesAsync(p.Id)).First(x => x.Id == a.Id).Rate);
        }
        finally
        {
            using var db = new AppDbContext(options);
            db.Database.EnsureDeleted();
        }
    }
}
