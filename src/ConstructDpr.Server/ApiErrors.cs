using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Server;

/// <summary>Turns rule violations into clear HTTP answers the apps show as-is (and maps back to the same exception).</summary>
public sealed class ApiErrors(RequestDelegate next, ILogger<ApiErrors> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (Exception ex) when (!ctx.Response.HasStarted && Map(ex) is { } m)
        {
            ctx.Response.StatusCode = m.Status;
            await ctx.Response.WriteAsJsonAsync(new ApiError(m.Kind, m.Message), Json.Options);
        }
    }

    private (int Status, string Kind, string Message)? Map(Exception ex)
    {
        switch (ex)
        {
            case WorkflowException: return (422, "workflow", ex.Message);
            case ScheduleException: return (422, "schedule", ex.Message);
            case ConflictException: return (409, "conflict", ex.Message);
            case ForbiddenException: return (403, "forbidden", ex.Message);
            case NotFoundException: return (404, "notfound", ex.Message);
            case UnauthorizedAccessException: return (401, "unauthorized", "Please sign in again.");
            case BadHttpRequestException b: return (400, "badrequest", b.Message);
            case DbUpdateException d:
                log.LogWarning(d, "Database update refused");
                return (409, "conflict", "The change clashes with existing data (for example a duplicate code or report). Reload and try again.");
            default: return null;
        }
    }
}
