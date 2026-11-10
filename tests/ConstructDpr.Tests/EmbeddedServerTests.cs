using ConstructDpr.Client;
using ConstructDpr.Server;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ConstructDpr.Tests;

/// <summary>"Let phones connect to this PC": the Windows app hosts the sync server on its own database.</summary>
public class EmbeddedServerTests
{
    [Fact]
    public async Task A_phone_reaches_the_projects_of_the_single_pc()
    {
        using var t = new DemoDb(); // the PC's own SQLite file with the demo project
        var app = SyncServerHost.Create([], new Dictionary<string, string?>
        {
            ["Urls"] = "http://127.0.0.1:0",
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Default"] = $"Data Source={t.Path}",
            ["Features:AdminPortal"] = "false",
        });
        await app.StartAsync();
        try
        {
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            var session = new AuthSession(new MemorySessionStore());
            await session.SetServerAsync(url);
            await session.RegisterAsync("site@phone.test", "phone-pass-1", "Site engineer");
            await session.SignInAsync("site@phone.test", "phone-pass-1");
            var phone = new RemoteApi(session);
            Assert.Empty(await phone.ProjectsAsync());

            // The PC owner adds the phone user to the project (Projects & people).
            await t.Api().AddMemberAsync(t.Project.Id, "site@phone.test", ProjectRole.SiteEngineer);
            var p = Assert.Single(await phone.ProjectsAsync());
            var d = await phone.CreateDprAsync(p.Id, t.Today, Shift.Night);
            DemoDb.SetQty(d, t.ActivityId("A2010"), 3);
            await phone.SubmitDprAsync(p.Id, d);
            // ...and the PC sees the phone's report straight away (same database).
            Assert.Contains(await t.Api().DprsAsync(t.Project.Id), x => x.Id == d.Id && x.Status == DprStatus.Submitted);
        }
        finally { await app.StopAsync(); }
    }
}
