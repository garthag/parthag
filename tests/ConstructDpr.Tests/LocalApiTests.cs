using ConstructDpr.Core;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Tests;

/// <summary>The rules every app goes through: roles, version conflicts, live notifications.</summary>
public class LocalApiTests
{
    private static Caller AddUser(DemoDb t, string email, string name)
    {
        var u = new AppUser { Id = Guid.NewGuid().ToString(), Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email, NormalizedUserName = email.ToUpperInvariant(), DisplayName = name };
        t.Db.Users.Add(u);
        t.Db.SaveChanges();
        return new Caller(u.Id, email, name);
    }

    private static async Task<(DemoDb T, int Pid, Caller Admin, Caller Supervisor)> Team()
    {
        var t = new DemoDb();
        var admin = AddUser(t, "pm@site.test", "Priya PM");
        var sup = AddUser(t, "sup@site.test", "Suresh Supervisor");
        var p = await t.Api(admin).CreateDemoProjectAsync();
        await t.Api(admin).AddMemberAsync(p.Id, "SUP@site.test", ProjectRole.Supervisor);
        return (t, p.Id, admin, sup);
    }

    [Fact]
    public async Task Outsiders_cannot_see_a_project()
    {
        var (t, pid, _, _) = await Team();
        using var _t = t;
        var stranger = AddUser(t, "x@other.test", "Stranger");
        Assert.Empty(await t.Api(stranger).ProjectsAsync());
        await Assert.ThrowsAsync<NotFoundException>(() => t.Api(stranger).DprsAsync(pid));
    }

    [Fact]
    public async Task Supervisor_reports_but_cannot_approve_or_plan()
    {
        var (t, pid, admin, sup) = await Team();
        using var _t = t;
        var api = t.Api(sup);
        Assert.Equal(ProjectRole.Supervisor, (await api.ProjectsAsync()).Single().MyRole);
        var d = await api.CreateDprAsync(pid, t.Today, Shift.Night);
        Assert.NotEmpty(d.Manpower); // crews copied from the previous report
        var act = (await api.ActivitiesAsync(pid)).First(a => a.Code == "A2000");
        DemoDb.SetQty(d, act.Id, 4);
        d = await api.SubmitDprAsync(pid, d);
        Assert.Equal(DprStatus.Submitted, d.Status);
        Assert.Equal("sup@site.test", d.PreparedBy);
        await Assert.ThrowsAsync<ForbiddenException>(() => api.ApproveDprAsync(pid, d.Id, new()));
        await Assert.ThrowsAsync<ForbiddenException>(() => api.RunScheduleAsync(pid));
        await Assert.ThrowsAsync<ForbiddenException>(() => api.AddMemberAsync(pid, "pm@site.test", ProjectRole.Auditor));

        var approved = await t.Api(admin).ApproveDprAsync(pid, d.Id, new() { [act.Id] = 3.5m });
        Assert.Equal(DprStatus.Approved, approved.Status);
        Assert.Equal(3.5m, approved.Activities.Single(l => l.ActivityId == act.Id).QtyAccepted);
        Assert.True(await api.VerifyDprAsync(pid, d.Id));
    }

    [Fact]
    public async Task Stale_copy_from_another_device_is_refused()
    {
        var (t, pid, admin, _) = await Team();
        using var _t = t;
        var api = t.Api(admin);
        var created = await api.CreateDprAsync(pid, t.Today, Shift.Night);
        var phone = await api.DprAsync(pid, created.Id);
        var laptop = await api.DprAsync(pid, created.Id);
        phone.Weather = "Rain";
        await api.SaveDprAsync(pid, phone);
        laptop.Weather = "Clear";
        await Assert.ThrowsAsync<ConflictException>(() => api.SaveDprAsync(pid, laptop));
        Assert.Equal("Rain", (await api.DprAsync(pid, created.Id)).Weather);
    }

    [Fact]
    public async Task Changes_are_published_for_live_sync()
    {
        var live = new List<LiveChange>();
        using var t = new DemoDb();
        var api = t.Api(live: live);
        var pending = (await api.DprsAsync(t.Project.Id)).Single(d => d.Status == DprStatus.Submitted);
        await api.ApproveDprAsync(t.Project.Id, pending.Id, new());
        var change = Assert.Single(live);
        Assert.Equal(("dpr", "approve", pending.Id), (change.Entity, change.Action, change.Id!.Value));
    }

    [Fact]
    public async Task Circular_logic_is_refused_and_nothing_is_saved()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var acts = await api.ActivitiesAsync(t.Project.Id);
        int Id(string c) => acts.Single(a => a.Code == c).Id;
        var before = (await api.DependenciesAsync(t.Project.Id)).Count;
        var changes = new ActivityChanges { LinksFor = [Id("A1000")], Links = [new Dependency { PredecessorId = Id("M9000"), SuccessorId = Id("A1000") }] };
        await Assert.ThrowsAsync<ScheduleException>(() => api.SaveActivitiesAsync(t.Project.Id, changes));
        Assert.Equal(before, (await api.DependenciesAsync(t.Project.Id)).Count);
    }

    [Fact]
    public async Task Activities_are_added_edited_and_versioned()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var a = (await api.ActivitiesAsync(t.Project.Id)).Single(x => x.Code == "A3000");
        a.Name = "Vibrating screens, feeders & chutes";
        await api.SaveActivitiesAsync(t.Project.Id, new ActivityChanges
        {
            Upserts = [a, new Activity { Code = "A6000", Name = "Painting", Unit = "m²", TotalQty = 500, Duration = 10, Weight = 2 }],
        });
        var after = await api.ActivitiesAsync(t.Project.Id);
        Assert.Equal("Vibrating screens, feeders & chutes", after.Single(x => x.Code == "A3000").Name);
        Assert.NotNull(after.Single(x => x.Code == "A6000").EarlyStart); // scheduled on save
        a.Name = "stale";
        await Assert.ThrowsAsync<ConflictException>(() => api.SaveActivitiesAsync(t.Project.Id, new ActivityChanges { Upserts = [a] }));
        var used = after.Single(x => x.Code == "A1000");
        await Assert.ThrowsAsync<WorkflowException>(() => api.SaveActivitiesAsync(t.Project.Id, new ActivityChanges { Deletes = [used.Id] }));
    }

    [Fact]
    public async Task Project_creation_follows_the_admin_portal_switch()
    {
        using var t = new DemoDb();
        var user = AddUser(t, "new@site.test", "New");
        var p = new Project { Code = "bf3", Name = "Blast Furnace 3", StartDate = t.Today };
        await Assert.ThrowsAsync<ForbiddenException>(() => t.Api(user, adminPortal: false).CreateProjectAsync(p));
        var created = await t.Api(user, adminPortal: true).CreateProjectAsync(p);
        Assert.Equal(("BF3", ProjectRole.ProjectAdmin), (created.Code, created.MyRole));
        await Assert.ThrowsAsync<WorkflowException>(() => t.Api(user).CreateProjectAsync(p)); // duplicate code
    }

    [Fact]
    public async Task Reports_download_as_files()
    {
        using var t = new DemoDb();
        var api = t.Api();
        var dpr = (await api.DprsAsync(t.Project.Id)).First();
        var pdf = await api.ReportAsync(t.Project.Id, ReportKind.DprPdf, dpr.Id);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 4));
        Assert.EndsWith(".xlsx", (await api.ReportAsync(t.Project.Id, ReportKind.ScheduleExcel)).FileName);
    }

    [Fact]
    public void Permission_matrix_matches_the_role_table()
    {
        Assert.True(Permissions.Can(ProjectRole.ProjectAdmin, Module.Members, Act.Delete));
        Assert.True(Permissions.Can(ProjectRole.ClientReviewer, Module.Dpr, Act.Approve));
        Assert.False(Permissions.Can(ProjectRole.ClientReviewer, Module.Dpr, Act.Create));
        Assert.False(Permissions.Can(ProjectRole.Integration, Module.Dpr, Act.Submit));
        Assert.True(Permissions.Can(ProjectRole.Supervisor, Module.Dpr, Act.Create | Act.Submit));
        Assert.False(Permissions.Can(ProjectRole.Auditor, Module.Masters, Act.Edit));
    }
}
