using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.AspNetCore.Identity;

namespace ConstructDpr.Server;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record ProfileRequest(string DisplayName);
public sealed record NewDprRequest(DateOnly Date, Shift Shift);
public sealed record ReasonRequest(string Reason);
public sealed record NameRequest(string Name);
public sealed record MemberRequest(string Email, ProjectRole Role);
public sealed record ApproveRequest(Dictionary<int, decimal>? Accepted);
public sealed record ImportRequest(byte[] Content);

public static class Endpoints
{
    /// <summary>Accounts: sign-up with a display name, then the standard Identity endpoints (login, refresh, password reset).</summary>
    public static void MapAccount(this WebApplication app)
    {
        var account = app.MapGroup("/api/account");
        account.MapGet("/features", (AppFeatures f) => f).AllowAnonymous();

        account.MapPost("/register", async (RegisterRequest r, UserManager<AppUser> users, AppFeatures f) =>
        {
            if (!f.Registration) throw new ForbiddenException("New accounts are switched off on this server. Ask your administrator.");
            if (string.IsNullOrWhiteSpace(r.DisplayName)) throw new WorkflowException("Enter your name.");
            if (!new EmailAddressAttribute().IsValid(r.Email)) throw new WorkflowException("Enter a valid e-mail address.");
            var user = new AppUser { UserName = r.Email.Trim(), Email = r.Email.Trim(), DisplayName = r.DisplayName.Trim() };
            var result = await users.CreateAsync(user, r.Password ?? "");
            if (!result.Succeeded) throw new WorkflowException(string.Join(" ", result.Errors.Select(e => e.Description)));
            return Results.Ok(new UserInfo(user.Id, user.Email, user.DisplayName));
        }).RequireRateLimiting("signin");

        account.MapPut("/profile", async (ProfileRequest r, ClaimsPrincipal me, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(me) ?? throw new UnauthorizedAccessException();
            if (string.IsNullOrWhiteSpace(r.DisplayName)) throw new WorkflowException("Enter your name.");
            user.DisplayName = r.DisplayName.Trim();
            await users.UpdateAsync(user);
            return Results.Ok(new UserInfo(user.Id, user.Email ?? "", user.DisplayName));
        }).RequireAuthorization();

        // /api/identity/login, /refresh, /forgotPassword, /resetPassword, /manage/info … (ASP.NET Core Identity).
        // Its own /register is replaced by /api/account/register above (which also stores the person's name).
        var identity = app.MapGroup("/api/identity");
        identity.AddEndpointFilter(async (ctx, next) =>
            ctx.HttpContext.Request.Path.Value?.EndsWith("/register", StringComparison.OrdinalIgnoreCase) == true
                ? Results.NotFound()
                : await next(ctx));
        identity.MapIdentityApi<AppUser>();
    }

    /// <summary>The REST API: one endpoint per IConstructDprApi method. All need a signed-in user.</summary>
    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        static async Task<IResult> Ok<T>(ApiFactory f, Func<IConstructDprApi, Task<T>> call) => Results.Ok(await call(await f.ForCurrentUserAsync()));
        static async Task<IResult> NoContent(ApiFactory f, Func<IConstructDprApi, Task> call) { await call(await f.ForCurrentUserAsync()); return Results.NoContent(); }

        api.MapGet("/me", (ApiFactory f) => Ok(f, a => a.MeAsync()));
        api.MapGet("/projects", (ApiFactory f) => Ok(f, a => a.ProjectsAsync()));
        api.MapPost("/projects", (Project p, ApiFactory f) => Ok(f, a => a.CreateProjectAsync(p)));
        api.MapPost("/projects/demo", (ApiFactory f) => Ok(f, a => a.CreateDemoProjectAsync()));

        var project = api.MapGroup("/projects/{projectId:int}");
        project.MapGet("", (int projectId, ApiFactory f) => Ok(f, a => a.ProjectAsync(projectId)));
        project.MapPut("", (int projectId, Project p, ApiFactory f) => { p.Id = projectId; return Ok(f, a => a.SaveProjectAsync(p)); });

        project.MapGet("/members", (int projectId, ApiFactory f) => Ok(f, a => a.MembersAsync(projectId)));
        project.MapPost("/members", (int projectId, MemberRequest r, ApiFactory f) => Ok(f, a => a.AddMemberAsync(projectId, r.Email, r.Role)));
        project.MapDelete("/members/{memberId:int}", (int projectId, int memberId, ApiFactory f) => NoContent(f, a => a.RemoveMemberAsync(projectId, memberId)));

        project.MapGet("/activities", (int projectId, ApiFactory f) => Ok(f, a => a.ActivitiesAsync(projectId)));
        project.MapGet("/dependencies", (int projectId, ApiFactory f) => Ok(f, a => a.DependenciesAsync(projectId)));
        project.MapPost("/activities/save", (int projectId, ActivityChanges c, ApiFactory f) => NoContent(f, a => a.SaveActivitiesAsync(projectId, c)));
        project.MapPost("/activities/import", (int projectId, ImportRequest r, ApiFactory f) => Ok(f, a => a.ImportActivitiesAsync(projectId, r.Content)));
        project.MapPost("/schedule/run", (int projectId, ApiFactory f) => Ok(f, a => a.RunScheduleAsync(projectId)));
        project.MapPost("/baselines", (int projectId, NameRequest r, ApiFactory f) => Ok(f, a => a.FreezeBaselineAsync(projectId, r.Name)));
        project.MapGet("/dashboard", (int projectId, ApiFactory f) => Ok(f, a => a.DashboardAsync(projectId)));

        project.MapGet("/dprs", (int projectId, ApiFactory f) => Ok(f, a => a.DprsAsync(projectId)));
        project.MapPost("/dprs", (int projectId, NewDprRequest r, ApiFactory f) => Ok(f, a => a.CreateDprAsync(projectId, r.Date, r.Shift)));
        project.MapPost("/dprs/recalculate", (int projectId, Dpr d, ApiFactory f) => Ok(f, a => a.RecalculateDprAsync(projectId, d)));
        project.MapGet("/dprs/{dprId:int}", (int projectId, int dprId, ApiFactory f) => Ok(f, a => a.DprAsync(projectId, dprId)));
        project.MapPut("/dprs/{dprId:int}", (int projectId, int dprId, Dpr d, ApiFactory f) => { d.Id = dprId; return Ok(f, a => a.SaveDprAsync(projectId, d)); });
        project.MapPost("/dprs/{dprId:int}/submit", (int projectId, int dprId, Dpr d, ApiFactory f) => { d.Id = dprId; return Ok(f, a => a.SubmitDprAsync(projectId, d)); });
        project.MapPost("/dprs/{dprId:int}/approve", (int projectId, int dprId, ApproveRequest r, ApiFactory f) => Ok(f, a => a.ApproveDprAsync(projectId, dprId, r.Accepted ?? new())));
        project.MapPost("/dprs/{dprId:int}/reject", (int projectId, int dprId, ReasonRequest r, ApiFactory f) => Ok(f, a => a.RejectDprAsync(projectId, dprId, r.Reason)));
        project.MapPost("/dprs/{dprId:int}/revise", (int projectId, int dprId, ReasonRequest r, ApiFactory f) => Ok(f, a => a.ReviseDprAsync(projectId, dprId, r.Reason)));
        project.MapGet("/dprs/{dprId:int}/verify", (int projectId, int dprId, ApiFactory f) => Ok(f, a => a.VerifyDprAsync(projectId, dprId)));

        project.MapGet("/audit", (int projectId, int? take, ApiFactory f) => Ok(f, a => a.AuditAsync(projectId, take ?? 50)));
        project.MapGet("/reports/{kind}", async (int projectId, ReportKind kind, int? dprId, ApiFactory f) =>
        {
            var file = await (await f.ForCurrentUserAsync()).ReportAsync(projectId, kind, dprId);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
    }

    /// <summary>The Blazor web app (published into wwwroot with the server) answers every other path.</summary>
    public static void MapWebApp(this WebApplication app)
    {
        app.Map("/api/{**rest}", () => Results.NotFound()); // unknown API paths stay 404, never the web page
        app.MapFallbackToFile("index.html");
    }
}
