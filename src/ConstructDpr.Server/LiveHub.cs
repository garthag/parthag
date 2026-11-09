using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ConstructDpr.Server;

/// <summary>
/// Live updates: each app joins the group of the project it shows and receives "changed" (a LiveChange)
/// whenever anyone commits a change there. Joining checks project membership.
/// </summary>
[Authorize]
public sealed class LiveHub(ApiFactory factory) : Hub
{
    public static string Group(int projectId) => $"project-{projectId}";

    public async Task WatchProject(int projectId)
    {
        var api = await factory.ForUserAsync(Context.User ?? throw new UnauthorizedAccessException());
        await api.ProjectAsync(projectId); // throws for non-members
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(projectId));
    }

    public Task UnwatchProject(int projectId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(projectId));
}
