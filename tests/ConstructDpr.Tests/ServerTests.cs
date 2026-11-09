using System.Net;
using System.Net.Http.Json;
using ConstructDpr.Client;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace ConstructDpr.Tests;

/// <summary>The real server in memory (SQLite), driven through the real client library the apps use.</summary>
public sealed class TestServer : WebApplicationFactory<Program>
{
    public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"cdpr-server-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={DbPath}");
        builder.UseSetting("Features:AdminPortal", "true");
    }

    public async Task<(AuthSession Session, RemoteApi Api)> SignUpAsync(string email, string name)
    {
        var session = new AuthSession(new MemorySessionStore(), _ => Server.CreateHandler());
        await session.SetServerAsync("http://localhost");
        await session.RegisterAsync(email, "site-pass-123", name);
        await session.SignInAsync(email, "site-pass-123");
        return (session, new RemoteApi(session));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try { File.Delete(DbPath); } catch (IOException) { }
    }
}

public class ServerTests
{
    [Fact]
    public async Task Sign_up_sign_in_and_load_the_demo_project()
    {
        using var server = new TestServer();
        var (session, api) = await server.SignUpAsync("pm@site.test", "Priya");
        Assert.True(session.IsSignedIn);
        Assert.Equal("Priya", (await api.MeAsync()).DisplayName);
        var p = await api.CreateDemoProjectAsync();
        Assert.Equal(ProjectRole.ProjectAdmin, Assert.Single(await api.ProjectsAsync()).MyRole);
        var stats = await api.DashboardAsync(p.Id);
        Assert.Equal(1, stats.PendingApprovals);
        var dprs = await api.DprsAsync(p.Id);
        var full = await api.DprAsync(p.Id, dprs[0].Id);
        Assert.NotEmpty(full.Activities);
        Assert.NotNull(full.Activities[0].Activity); // activity names travel with the report
        var pdf = await api.ReportAsync(p.Id, ReportKind.DprPdf, dprs[0].Id);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.EndsWith(".pdf", pdf.FileName);
    }

    [Fact]
    public async Task Rules_and_roles_arrive_as_the_same_errors_on_the_client()
    {
        using var server = new TestServer();
        var (_, pm) = await server.SignUpAsync("pm@site.test", "Priya");
        var (_, sup) = await server.SignUpAsync("sup@site.test", "Suresh");
        var p = await pm.CreateDemoProjectAsync();
        await Assert.ThrowsAsync<NotFoundException>(() => sup.DprsAsync(p.Id));
        await pm.AddMemberAsync(p.Id, "sup@site.test", ProjectRole.Supervisor);

        var d = await sup.CreateDprAsync(p.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)), Shift.Night);
        var a = (await sup.ActivitiesAsync(p.Id)).First(x => x.Code == "A1000");
        d.Activities.Add(new DprActivityLine { ActivityId = a.Id, QtyToday = 99_999 });
        var overrun = await Assert.ThrowsAsync<WorkflowException>(() => sup.SubmitDprAsync(p.Id, d));
        Assert.Contains("exceeds", overrun.Message);
        await Assert.ThrowsAsync<ForbiddenException>(() => sup.RunScheduleAsync(p.Id));

        var copy = await sup.DprAsync(p.Id, d.Id);
        await sup.SaveDprAsync(p.Id, copy);
        await Assert.ThrowsAsync<ConflictException>(() => sup.SaveDprAsync(p.Id, copy)); // stale version
        await Assert.ThrowsAsync<WorkflowException>(() => server.SignUpAsync("pm@site.test", "Again")); // e-mail taken
    }

    [Fact]
    public async Task Approval_on_one_device_reaches_another_live()
    {
        using var server = new TestServer();
        var (session, api) = await server.SignUpAsync("pm@site.test", "Priya");
        var p = await api.CreateDemoProjectAsync();
        var (_, phone) = (session, api);

        var live = new LiveClient(session);
        var got = new TaskCompletionSource<LiveChange>();
        live.Changed += c => got.TrySetResult(c);
        await live.WatchProjectAsync(p.Id);
        Assert.Equal("live", live.Status);

        var pending = (await phone.DprsAsync(p.Id)).Single(x => x.Status == DprStatus.Submitted);
        await phone.ApproveDprAsync(p.Id, pending.Id, new());
        var change = await got.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(("dpr", "approve", pending.Id), (change.Entity, change.Action, change.Id!.Value));
        await live.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_requests_are_refused_and_unknown_api_paths_are_404()
    {
        using var server = new TestServer();
        var http = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsJsonAsync("/api/identity/register", new { email = "a@b.c", password = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        var features = await http.GetFromJsonAsync<AppFeatures>("/api/account/features", Json.Options);
        Assert.True(features!.Registration);
    }

    [Theory]
    [InlineData("postgres://user:p%40ss@db.example.com:6543/app?sslmode=require", "db.example.com", 6543, "user", "p@ss", "app")]
    [InlineData("postgresql://postgres.abc:secret@aws-0-ap-south-1.pooler.supabase.com:5432/postgres", "aws-0-ap-south-1.pooler.supabase.com", 5432, "postgres.abc", "secret", "postgres")]
    public void Database_url_is_understood(string url, string host, int port, string user, string pass, string db)
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(ConstructDpr.Server.ServerSettings.FromUrl(url));
        Assert.Equal((host, port, user, pass, db), (b.Host, b.Port, b.Username, b.Password, b.Database));
        Assert.Equal(Npgsql.SslMode.Require, b.SslMode);
    }
}
