using System.Security.Claims;
using ConstructDpr.Core;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Server;

/// <summary>Builds the LocalApi for the signed-in person of the current request.</summary>
public sealed class ApiFactory(IHttpContextAccessor http, DbContextOptions<AppDbContext> options, AppFeatures features,
    SiteClock clock, IHubContext<LiveHub> hub, ILogger<ApiFactory> log)
{
    public Task<LocalApi> ForCurrentUserAsync() => ForUserAsync(http.HttpContext?.User ?? throw new UnauthorizedAccessException());

    public async Task<LocalApi> ForUserAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();
        await using var db = new AppDbContext(options);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id) ?? throw new UnauthorizedAccessException();
        var caller = new Caller(user.Id, user.Email ?? "", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName);
        return new LocalApi(() => new AppDbContext(options), caller, features, Publish, () => clock.Today);
    }

    private void Publish(LiveChange change) =>
        hub.Clients.Group(LiveHub.Group(change.ProjectId)).SendAsync("changed", change)
            .ContinueWith(t => log.LogWarning(t.Exception, "Live update not sent"), TaskContinuationOptions.OnlyOnFaulted);
}
