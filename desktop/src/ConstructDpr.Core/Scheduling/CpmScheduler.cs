using ConstructDpr.Core.Model;

namespace ConstructDpr.Core.Scheduling;

public sealed record CpmActivity(int Id, string Code, int Duration, bool IsMilestone = false,
    DateOnly? ActualStart = null, DateOnly? ActualFinish = null, int? RemainingDuration = null);

public sealed record CpmLink(int PredecessorId, int SuccessorId, DependencyType Type, int Lag);

public sealed record CpmResult(int Id, DateOnly EarlyStart, DateOnly EarlyFinish, DateOnly LateStart, DateOnly LateFinish,
    int TotalFloat, int FreeFloat, bool Critical);

public sealed record CpmOutput(IReadOnlyDictionary<int, CpmResult> Activities, DateOnly ProjectFinish, IReadOnlyList<int> CriticalPath);

public sealed class ScheduleException(string message) : Exception(message);

/// <summary>
/// Critical Path Method (forward and backward pass) on a working-day calendar.
/// An activity occupies days [S, F): it starts on day S and finishes at the end of day F-1.
///   FS: S(succ) ≥ F(pred)+lag   SS: S(succ) ≥ S(pred)+lag
///   FF: F(succ) ≥ F(pred)+lag   SF: F(succ) ≥ S(pred)+lag
/// Actual starts/finishes are fixed; unstarted work cannot start before the data date.
/// </summary>
public static class CpmScheduler
{
    public static CpmOutput Schedule(IReadOnlyList<CpmActivity> activities, IReadOnlyList<CpmLink> links,
        DateOnly projectStart, DateOnly? dataDate, WorkCalendar calendar)
    {
        var byId = activities.ToDictionary(a => a.Id);
        var preds = activities.ToDictionary(a => a.Id, _ => new List<CpmLink>());
        var succs = activities.ToDictionary(a => a.Id, _ => new List<CpmLink>());
        foreach (var l in links)
        {
            if (!byId.ContainsKey(l.PredecessorId) || !byId.ContainsKey(l.SuccessorId)) continue;
            if (l.PredecessorId == l.SuccessorId) throw new ScheduleException("An activity cannot depend on itself.");
            preds[l.SuccessorId].Add(l);
            succs[l.PredecessorId].Add(l);
        }

        // Topological order (Kahn); leftovers form a loop.
        var indeg = activities.ToDictionary(a => a.Id, a => preds[a.Id].Count);
        var queue = new Queue<int>(activities.Where(a => indeg[a.Id] == 0).Select(a => a.Id));
        var order = new List<int>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var l in succs[id])
                if (--indeg[l.SuccessorId] == 0) queue.Enqueue(l.SuccessorId);
        }
        if (order.Count != activities.Count)
            throw new ScheduleException("Circular dependency between: " +
                string.Join(", ", activities.Where(a => !order.Contains(a.Id)).Select(a => a.Code)));

        int Dur(CpmActivity a) => a.IsMilestone ? 0 : Math.Max(0, a.Duration);
        int? dataIdx = dataDate is { } dd ? calendar.IndexOf(dd) : null;
        var es = new Dictionary<int, int>();
        var ef = new Dictionary<int, int>();

        foreach (var id in order)
        {
            var a = byId[id];
            var d = Dur(a);
            if (a.ActualStart is { } ast)
            {
                var s = calendar.IndexOf(ast);
                es[id] = s;
                if (a.ActualFinish is { } afn)
                    ef[id] = Math.Max(s + (d == 0 ? 0 : 1), calendar.IndexOf(afn) + (d == 0 ? 0 : 1));
                else
                {
                    var elapsed = dataIdx is { } di ? Math.Max(0, di - s) : 0;
                    var remaining = a.RemainingDuration ?? Math.Max(d - elapsed, d == 0 ? 0 : 1);
                    ef[id] = Math.Max(s + (d == 0 ? 0 : 1), (dataIdx ?? s) + remaining);
                }
                continue;
            }
            var start = 0;
            foreach (var p in preds[id])
            {
                int ps = es[p.PredecessorId], pf = ef[p.PredecessorId];
                var req = p.Type switch
                {
                    DependencyType.FS => pf + p.Lag,
                    DependencyType.SS => ps + p.Lag,
                    DependencyType.FF => pf + p.Lag - d,
                    _ => ps + p.Lag - d,
                };
                start = Math.Max(start, req);
            }
            if (dataIdx is { } dix) start = Math.Max(start, dix);
            es[id] = start;
            ef[id] = start + d;
        }

        var finishIdx = Math.Max(0, activities.Count == 0 ? 0 : activities.Max(a => ef[a.Id]));
        var ls = new Dictionary<int, int>();
        var lf = new Dictionary<int, int>();
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var id = order[i];
            var cur = ef[id] - es[id];
            var late = finishIdx;
            foreach (var s in succs[id])
            {
                int sls = ls[s.SuccessorId], slf = lf[s.SuccessorId];
                var bound = s.Type switch
                {
                    DependencyType.FS => sls - s.Lag,
                    DependencyType.SS => sls - s.Lag + cur,
                    DependencyType.FF => slf - s.Lag,
                    _ => slf - s.Lag + cur,
                };
                late = Math.Min(late, bound);
            }
            lf[id] = late;
            ls[id] = late - cur;
        }

        var results = new Dictionary<int, CpmResult>();
        foreach (var a in activities)
        {
            int e = es[a.Id], f = ef[a.Id], l = ls[a.Id], lt = lf[a.Id];
            var free = finishIdx - f;
            foreach (var s in succs[a.Id])
            {
                int ses = es[s.SuccessorId], sef = ef[s.SuccessorId];
                var slack = s.Type switch
                {
                    DependencyType.FS => ses - (f + s.Lag),
                    DependencyType.SS => ses - (e + s.Lag),
                    DependencyType.FF => sef - (f + s.Lag),
                    _ => sef - (e + s.Lag),
                };
                free = Math.Min(free, slack);
            }
            var done = a.ActualFinish is not null;
            var tf = lt - f;
            (DateOnly s0, DateOnly f0) Dates(int st, int fi) => (calendar.DateAt(st), fi > st ? calendar.DateAt(fi - 1) : calendar.DateAt(st));
            var (es0, ef0) = Dates(e, f);
            var (ls0, lf0) = Dates(l, lt);
            results[a.Id] = new CpmResult(a.Id, es0, ef0, ls0, lf0, done ? 0 : tf, done ? 0 : Math.Max(0, free), !done && tf <= 0);
        }
        var critical = order.Where(id => results[id].Critical).ToList();
        var projectFinish = finishIdx > 0 ? calendar.DateAt(finishIdx - 1) : calendar.DateAt(0);
        return new CpmOutput(results, projectFinish, critical);
    }
}
