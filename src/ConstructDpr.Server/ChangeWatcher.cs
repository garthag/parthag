using System.Collections.Concurrent;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Api;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Server;

/// <summary>
/// Windows PCs in the office write straight into the shared SQL Server database, past this server. Every few seconds
/// this watcher looks at the newest change number of each project a phone is watching and, when it moved, tells those
/// phones to reload. Changes made through this server are announced immediately by ApiFactory instead.
/// </summary>
public sealed class ChangeWatcher(DbContextOptions<AppDbContext> options, IHubContext<LiveHub> hub, ILogger<ChangeWatcher> log) : BackgroundService
{
    private readonly ConcurrentDictionary<int, int> _known = new();

    public void Watch(int projectId) => _known.TryAdd(projectId, Stamp(projectId));

    /// <summary>Called after this server announced its own change, so it is not announced twice.</summary>
    public void Seen(int projectId)
    {
        if (_known.ContainsKey(projectId)) _known[projectId] = Stamp(projectId);
    }

    private int Stamp(int projectId)
    {
        using var db = new AppDbContext(options);
        return db.Audit.Where(a => a.ProjectId == projectId).Select(a => (int?)a.Id).Max() ?? 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stop).ContinueWith(_ => { });
            if (_known.IsEmpty) continue;
            try
            {
                var ids = _known.Keys.ToList();
                await using var db = new AppDbContext(options);
                var now = await db.Audit.Where(a => a.ProjectId != null && ids.Contains(a.ProjectId.Value))
                    .GroupBy(a => a.ProjectId!.Value).Select(g => new { Id = g.Key, Max = g.Max(a => a.Id) }).ToListAsync(stop);
                foreach (var p in now.Where(p => _known.TryGetValue(p.Id, out var k) && k != p.Max))
                {
                    _known[p.Id] = p.Max;
                    await hub.Clients.Group(LiveHub.Group(p.Id)).SendAsync("changed", new LiveChange(p.Id, "all", null, "resync", "", DateTime.UtcNow), stop);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Change check failed");
            }
        }
    }
}
