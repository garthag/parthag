using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;
using SiteNotes.Core.Sync;

namespace SiteNotes.Core.Tests;

/// <summary>Two devices syncing through the real server (in-memory host, SQLite database).</summary>
public class SyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sitenotes-sync-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public SyncTests()
    {
        Directory.CreateDirectory(_dir);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("SiteNotes:Provider", "Sqlite");
            b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(_dir, "server.db")}");
            b.UseSetting("SiteNotes:MediaFolder", Path.Combine(_dir, "server-media"));
            b.UseSetting("SiteNotes:ApiKey", "test-key");
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private async Task<(LocalDatabase, SyncClient)> DeviceAsync(string name)
    {
        var db = new LocalDatabase(Path.Combine(_dir, name)) { UserName = name };
        await db.InitAsync();
        var http = _factory.CreateClient();
        var client = new SyncClient(db, new SyncSettings { ServerUrl = http.BaseAddress!.ToString(), ApiKey = "test-key", DeviceId = name }, http);
        return (db, client);
    }

    [Fact]
    public async Task Rows_and_files_flow_between_devices_and_newest_edit_wins()
    {
        var (siteDb, site) = await DeviceAsync("site-phone");
        var (officeDb, office) = await DeviceAsync("office-pc");

        var (project, bundle) = await DatabaseAndReportTests.SeedAsync(siteDb, siteDb.MediaFolder);
        Assert.True(await site.SyncNowAsync());
        Assert.True(await office.SyncNowAsync());

        var reports = await officeDb.ListForProjectAsync<DailyReport>(project.Id);
        Assert.Single(reports);
        Assert.Equal(35, reports[0].TotalManpower);
        Assert.False(reports[0].IsDirty);
        var loaded = await officeDb.LoadReportAsync(bundle.Report.Id);
        Assert.Equal(3, loaded.Manpower.Count);
        Assert.True(File.Exists(officeDb.MediaPath("photo1.jpg")), "photo should download to the office PC");

        // office edits later -> site receives it
        loaded.Report.Status = ReportStatus.Approved;
        await officeDb.SaveReportAsync(loaded);
        Assert.True(await office.SyncNowAsync());
        Assert.True(await site.SyncNowAsync());
        Assert.Equal(ReportStatus.Approved, (await siteDb.GetAsync<DailyReport>(bundle.Report.Id))!.Status);

        // deletes travel too
        var hindrance = (await siteDb.ListAsync<Hindrance>()).Single();
        await siteDb.DeleteAsync(hindrance);
        await site.SyncNowAsync();
        await office.SyncNowAsync();
        Assert.Empty(await officeDb.ListAsync<Hindrance>());

        // a stale push is ignored by the server
        var stale = new SyncRecord(project.Id, nameof(Project), project.Id, "{}", DateTime.UtcNow.AddDays(-10), false);
        var res = await _factory.Services.GetRequiredService<SiteNotes.Server.SyncStore>().PushAsync(new PushRequest("old", [stale]));
        Assert.Equal(1, res.Ignored);
    }

    [Fact]
    public async Task Wrong_key_is_rejected()
    {
        var http = _factory.CreateClient();
        var res = await http.GetAsync("api/sync/pull?since=0");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
