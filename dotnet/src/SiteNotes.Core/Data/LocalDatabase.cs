using System.Linq.Expressions;
using SiteNotes.Core.Models;
using SQLite;

namespace SiteNotes.Core.Data;

/// <summary>
/// The on-device SQLite database. Works fully offline; <see cref="Sync.SyncClient"/>
/// pushes rows marked dirty to the server and merges rows from other devices.
/// Deletes are soft (IsDeleted) so they can sync too.
/// </summary>
public sealed class LocalDatabase
{
    private readonly SQLiteAsyncConnection _db;
    private bool _ready;

    /// <summary>Raised after any local write, with the entity type name. Used for UI refresh and auto-sync.</summary>
    public event Action<string>? Changed;

    /// <summary>Raised after rows from the server were merged.</summary>
    public event Action? RemoteMerged;

    public string MediaFolder { get; }
    public string UserName { get; set; } = "";

    public LocalDatabase(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        MediaFolder = Path.Combine(dataFolder, "media");
        Directory.CreateDirectory(MediaFolder);
        // No FullMutex / SharedCache: then sqlite-net serialises every read and write on the connection itself.
        // (With FullMutex it skips that lock, and a background sync writing while a screen reads could make the
        // read return a row twice – found by the heavy demo.)
        _db = new SQLiteAsyncConnection(Path.Combine(dataFolder, "sitenotes.db3"),
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create);
    }

    public SQLiteAsyncConnection Connection => _db;

    public string MediaPath(string fileName) => Path.Combine(MediaFolder, fileName);

    public async Task InitAsync()
    {
        if (_ready) return;
        await _db.EnableWriteAheadLoggingAsync();
        await _db.CreateTableAsync<LocalSetting>();
        foreach (var t in EntityRegistry.Types.Values)
            await _db.CreateTablesAsync(CreateFlags.None, t);
        _ready = true;
        UserName = await GetSettingAsync("UserName") ?? "";
    }

    // ---------- settings ----------

    public async Task<string?> GetSettingAsync(string key) =>
        (await _db.FindAsync<LocalSetting>(key))?.Value;

    public Task SetSettingAsync(string key, string value) =>
        _db.InsertOrReplaceAsync(new LocalSetting { Key = key, Value = value });

    // ---------- generic CRUD ----------

    public Task<T?> GetAsync<T>(string id) where T : SyncEntity, new() =>
        _db.FindAsync<T>(id)!;

    public async Task<List<T>> ListAsync<T>(Expression<Func<T, bool>>? where = null) where T : SyncEntity, new()
    {
        var q = _db.Table<T>().Where(x => !x.IsDeleted);
        if (where != null) q = q.Where(where);
        return await q.ToListAsync();
    }

    public async Task<List<T>> ListForProjectAsync<T>(string projectId, Expression<Func<T, bool>>? where = null)
        where T : SyncEntity, new()
    {
        var q = _db.Table<T>().Where(x => !x.IsDeleted && x.ProjectId == projectId);
        if (where != null) q = q.Where(where);
        return await q.ToListAsync();
    }

    private void Stamp(SyncEntity e)
    {
        e.UpdatedAtUtc = DateTime.UtcNow;
        e.UpdatedBy = UserName;
        e.IsDirty = true;
        if (e is Project p) p.ProjectId = p.Id;
    }

    public async Task SaveAsync<T>(T entity) where T : SyncEntity
    {
        Stamp(entity);
        await _db.InsertOrReplaceAsync(entity);
        Changed?.Invoke(typeof(T).Name);
    }

    public async Task SaveAllAsync<T>(IEnumerable<T> entities) where T : SyncEntity
    {
        var list = entities.ToList();
        if (list.Count == 0) return;
        foreach (var e in list) Stamp(e);
        await _db.RunInTransactionAsync(c => { foreach (var e in list) c.InsertOrReplace(e); });
        Changed?.Invoke(typeof(T).Name);
    }

    public async Task DeleteAsync<T>(T entity) where T : SyncEntity
    {
        entity.IsDeleted = true;
        await SaveAsync(entity);
    }

    // ---------- daily reports ----------

    /// <summary>Next report number: last number + 1, or the project's first number (asked once) for the first report.</summary>
    public async Task<int> NextReportNoAsync(string projectId)
    {
        var last = await _db.Table<DailyReport>()
            .Where(r => r.ProjectId == projectId && !r.IsDeleted)
            .OrderByDescending(r => r.ReportNo).FirstOrDefaultAsync();
        if (last != null) return last.ReportNo + 1;
        var p = await _db.FindAsync<Project>(projectId);
        return p is { FirstReportNo: > 0 } ? p.FirstReportNo : 1;
    }

    /// <summary>True when the project has no reports and no starting number yet: the app asks once.</summary>
    public async Task<bool> NeedsFirstReportNoAsync(string projectId)
    {
        var p = await _db.FindAsync<Project>(projectId);
        if (p == null || p.FirstReportNo > 0) return false;
        return await _db.Table<DailyReport>().Where(r => r.ProjectId == projectId && !r.IsDeleted).CountAsync() == 0;
    }

    /// <summary>Date for the next report: the working day after the latest report, but never later than today.</summary>
    public async Task<DateTime> NextReportDateAsync(string projectId)
    {
        var today = DateTime.Today;
        var last = await _db.Table<DailyReport>()
            .Where(r => r.ProjectId == projectId && !r.IsDeleted)
            .OrderByDescending(r => r.Date).FirstOrDefaultAsync();
        if (last == null || last.Date.Date >= today) return today;
        var cal = await CalendarAsync(projectId);
        var hol = cal.HolidaySet;
        var d = last.Date.Date.AddDays(1);
        while (d < today && !cal.IsWorkingDay(d, hol)) d = d.AddDays(1);
        return d;
    }

    /// <summary>Loads many reports with all rows using one query per table (fast for registers and Excel).</summary>
    public async Task<List<DprBundle>> LoadReportsAsync(string projectId, IReadOnlyList<DailyReport> reports)
    {
        var ids = reports.Select(r => r.Id).ToHashSet();
        var project = await _db.FindAsync<Project>(projectId);
        async Task<ILookup<string, T>> Rows<T>(Func<T, string> key) where T : SyncEntity, new() =>
            (await ListForProjectAsync<T>(projectId)).Where(x => ids.Contains(key(x))).ToLookup(key);
        var allActs = await ListForProjectAsync<ReportActivity>(projectId);
        var acts = allActs.Where(a => ids.Contains(a.ReportId)).ToLookup(a => a.ReportId);
        var mp = await Rows<ReportManpower>(x => x.ReportId);
        var mc = await Rows<ReportMachine>(x => x.ReportId);
        var mt = await Rows<ReportMaterial>(x => x.ReportId);
        var hi = await Rows<Hindrance>(x => x.ReportId);
        var rq = await Rows<Requirement>(x => x.ReportId);
        var md = await Rows<MediaItem>(x => x.OwnerId);
        var master = (await ListForProjectAsync<Activity>(projectId)).ToDictionary(a => a.Id);

        // Cumulative quantities: running sum over every report in date order.
        var order = (await ListForProjectAsync<DailyReport>(projectId))
            .OrderBy(r => r.Date.Date).ThenBy(r => r.ReportNo).Select(r => r.Id).ToList();
        var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var running = new Dictionary<string, double>();
        var cumulative = new Dictionary<string, double>(); // row id -> cumulative
        foreach (var row in allActs.Where(a => a.ProgressKey != "" && rank.ContainsKey(a.ReportId))
                     .GroupBy(a => a.ReportId).OrderBy(g => rank[g.Key]).SelectMany(g => g))
        {
            running[row.ProgressKey] = running.GetValueOrDefault(row.ProgressKey) + row.TodayQty;
            cumulative[row.Id] = running[row.ProgressKey];
        }
        var drawingPlan = await DrawingPlanAsync(projectId);

        var list = new List<DprBundle>(reports.Count);
        foreach (var r in reports)
        {
            var b = new DprBundle
            {
                Report = r, Project = project,
                Activities = acts[r.Id].OrderBy(x => x.SortOrder).ToList(),
                Manpower = mp[r.Id].OrderBy(x => x.SortOrder).ToList(),
                Machines = mc[r.Id].OrderBy(x => x.SortOrder).ToList(),
                Materials = mt[r.Id].OrderBy(x => x.SortOrder).ToList(),
                Hindrances = hi[r.Id].ToList(),
                Requirements = rq[r.Id].ToList(),
                Media = md[r.Id].OrderBy(x => x.CapturedAtUtc).ToList(),
            };
            // all rows of the same activity / drawing item+agency in one report share the report's final cumulative
            foreach (var a in b.Activities.Where(a => a.ProgressKey != ""))
            {
                a.CumulativeQty = Math.Round(b.Activities.Where(x => x.ProgressKey == a.ProgressKey).Select(x => cumulative.GetValueOrDefault(x.Id)).Max(), 4);
                if (a.DrawingItemId != "" && drawingPlan.TryGetValue(a.DrawingItemId, out var item))
                {
                    a.PlannedQty = item.Allocations.GetValueOrDefault(a.SubcontractorId, item.Qty);
                    a.Unit = item.Unit;
                }
                else if (master.TryGetValue(a.ActivityId, out var m)) { a.PlannedQty = m.PlannedQty; a.Unit = m.Unit; }
            }
            list.Add(b);
        }
        return list;
    }

    public async Task<DprBundle> LoadReportAsync(string reportId)
    {
        var r = await _db.FindAsync<DailyReport>(reportId) ?? throw new KeyNotFoundException(reportId);
        var b = new DprBundle
        {
            Report = r,
            Project = await _db.FindAsync<Project>(r.ProjectId),
            Activities = (await ListAsync<ReportActivity>(x => x.ReportId == reportId)).OrderBy(x => x.SortOrder).ToList(),
            Manpower = (await ListAsync<ReportManpower>(x => x.ReportId == reportId)).OrderBy(x => x.SortOrder).ToList(),
            Machines = (await ListAsync<ReportMachine>(x => x.ReportId == reportId)).OrderBy(x => x.SortOrder).ToList(),
            Materials = (await ListAsync<ReportMaterial>(x => x.ReportId == reportId)).OrderBy(x => x.SortOrder).ToList(),
            Hindrances = await ListAsync<Hindrance>(x => x.ReportId == reportId),
            Requirements = await ListAsync<Requirement>(x => x.ReportId == reportId),
            Media = (await ListAsync<MediaItem>(x => x.OwnerId == reportId)).OrderBy(x => x.CapturedAtUtc).ToList(),
        };
        await RefreshCumulativeAsync(b);
        return b;
    }

    /// <summary>Saves a report with all its rows in one transaction and refreshes the cached totals.
    /// Rows that were removed in the editor are soft-deleted.</summary>
    public async Task SaveReportAsync(DprBundle b)
    {
        var r = b.Report;
        if (r.ReportNo == 0) r.ReportNo = await NextReportNoAsync(r.ProjectId);
        await RefreshCumulativeAsync(b);
        DprCalculator.UpdateTotals(b);
        r.TotalCost = CostCalculator.ForReport(b, await CalendarAsync(r.ProjectId)).Total;

        var existing = new List<SyncEntity>();
        existing.AddRange(await ListAsync<ReportActivity>(x => x.ReportId == r.Id));
        existing.AddRange(await ListAsync<ReportManpower>(x => x.ReportId == r.Id));
        existing.AddRange(await ListAsync<ReportMachine>(x => x.ReportId == r.Id));
        existing.AddRange(await ListAsync<ReportMaterial>(x => x.ReportId == r.Id));
        existing.AddRange(await ListAsync<Hindrance>(x => x.ReportId == r.Id));
        existing.AddRange(await ListAsync<Requirement>(x => x.ReportId == r.Id));

        var rows = new List<SyncEntity> { r };
        void Add<T>(List<T> list) where T : SyncEntity
        {
            for (var i = 0; i < list.Count; i++)
            {
                var e = list[i];
                e.ProjectId = r.ProjectId;
                switch (e)
                {
                    case ReportActivity a: a.ReportId = r.Id; a.SortOrder = i; break;
                    case ReportManpower m: m.ReportId = r.Id; m.SortOrder = i; break;
                    case ReportMachine m: m.ReportId = r.Id; m.SortOrder = i; break;
                    case ReportMaterial m: m.ReportId = r.Id; m.SortOrder = i; break;
                    case Hindrance h: h.ReportId = r.Id; break;
                    case Requirement q: q.ReportId = r.Id; break;
                }
                rows.Add(e);
            }
        }
        Add(b.Activities); Add(b.Manpower); Add(b.Machines); Add(b.Materials);
        await AssignSerialsAsync(r.ProjectId, b.Hindrances, b.Requirements);
        Add(b.Hindrances); Add(b.Requirements);

        var keep = rows.Select(x => x.Id).ToHashSet();
        foreach (var gone in existing.Where(e => !keep.Contains(e.Id)))
        {
            gone.IsDeleted = true;
            rows.Add(gone);
        }

        foreach (var e in rows) Stamp(e);
        await _db.RunInTransactionAsync(c => { foreach (var e in rows) c.InsertOrReplace(e); });
        Changed?.Invoke(nameof(DailyReport));
    }

    public async Task<WorkCalendar> CalendarAsync(string projectId) =>
        (await ListForProjectAsync<WorkCalendar>(projectId)).OrderByDescending(c => c.UpdatedAtUtc).FirstOrDefault()
        ?? new WorkCalendar { ProjectId = projectId };

    /// <summary>Cumulative = sum of today's quantity in every earlier report (by date) + this report.
    /// Planned quantity and unit come from the activity master, so the report always matches it.</summary>
    public async Task RefreshCumulativeAsync(DprBundle b)
    {
        var linked = b.Activities.Where(a => a.ProgressKey != "").ToList();
        if (linked.Count == 0) return;
        var r = b.Report;
        var earlier = (await ListForProjectAsync<DailyReport>(r.ProjectId))
            .Where(x => x.Id != r.Id && (x.Date.Date < r.Date.Date || (x.Date.Date == r.Date.Date && x.ReportNo < r.ReportNo)))
            .Select(x => x.Id).ToHashSet();
        var keys = linked.Select(a => a.ProgressKey).ToHashSet();
        var before = (await ListForProjectAsync<ReportActivity>(r.ProjectId))
            .Where(x => earlier.Contains(x.ReportId) && keys.Contains(x.ProgressKey))
            .GroupBy(x => x.ProgressKey).ToDictionary(g => g.Key, g => g.Sum(x => x.TodayQty));
        var master = (await ListForProjectAsync<Activity>(r.ProjectId)).ToDictionary(a => a.Id);
        var plan = await DrawingPlanAsync(r.ProjectId);
        foreach (var a in linked)
        {
            a.CumulativeQty = Math.Round(before.GetValueOrDefault(a.ProgressKey) + b.Activities.Where(x => x.ProgressKey == a.ProgressKey).Sum(x => x.TodayQty), 4);
            if (a.DrawingItemId != "" && plan.TryGetValue(a.DrawingItemId, out var item))
            {
                // planned for this agency = its allocation (or the whole item if nothing is allocated to it)
                a.PlannedQty = item.Allocations.GetValueOrDefault(a.SubcontractorId, item.Qty);
                a.Unit = item.Unit;
            }
            else if (master.TryGetValue(a.ActivityId, out var m))
            {
                a.PlannedQty = m.PlannedQty;
                a.Unit = m.Unit;
                if (string.IsNullOrWhiteSpace(a.ActivityName)) a.ActivityName = m.ToString();
            }
        }
    }

    /// <summary>Drawing item quantity, unit and allocated quantity per agency.</summary>
    public async Task<Dictionary<string, (double Qty, string Unit, Dictionary<string, double> Allocations)>> DrawingPlanAsync(string projectId)
    {
        var items = await ListForProjectAsync<DrawingItem>(projectId);
        if (items.Count == 0) return [];
        var allocs = (await ListForProjectAsync<DrawingAllocation>(projectId)).ToLookup(x => x.DrawingItemId);
        return items.ToDictionary(i => i.Id, i => (i.Qty, i.Unit,
            allocs[i.Id].GroupBy(x => x.SubcontractorId).ToDictionary(g => g.Key, g => g.Sum(x => x.AllocatedQty))));
    }

    private async Task AssignSerialsAsync(string projectId, List<Hindrance> hs, List<Requirement> rs)
    {
        if (hs.Any(h => h.SerialNo == 0))
        {
            var max = (await ListForProjectAsync<Hindrance>(projectId)).Select(h => h.SerialNo).DefaultIfEmpty(0).Max();
            foreach (var h in hs.Where(h => h.SerialNo == 0)) h.SerialNo = ++max;
        }
        if (rs.Any(r => r.SerialNo == 0))
        {
            var max = (await ListForProjectAsync<Requirement>(projectId)).Select(r => r.SerialNo).DefaultIfEmpty(0).Max();
            foreach (var r in rs.Where(r => r.SerialNo == 0)) r.SerialNo = ++max;
        }
    }

    public async Task<int> NextSerialAsync<T>(string projectId) where T : SyncEntity, new()
    {
        var all = await ListForProjectAsync<T>(projectId);
        return all.Select(x => x switch { Hindrance h => h.SerialNo, Requirement r => r.SerialNo, _ => 0 })
                  .DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>Starts a new report pre-filled from the latest one: manpower, machinery and
    /// activities are copied (today's quantity cleared, cumulative carried forward).</summary>
    public async Task<DprBundle> NewReportAsync(string projectId, DateTime date, bool? carryForwardOverride = null)
    {
        var carryForward = carryForwardOverride ?? (await _db.FindAsync<Project>(projectId))?.CarryForward ?? true;
        var b = new DprBundle
        {
            Report = new DailyReport
            {
                ProjectId = projectId,
                Date = date.Date,
                ReportNo = await NextReportNoAsync(projectId),
                PreparedBy = UserName,
            },
            Project = await _db.FindAsync<Project>(projectId),
        };
        if (!carryForward) return b;

        // Activities planned for this date (or already started) and not yet 100 % are listed automatically.
        var progress = await new ProgressService(this).ComputeAsync(projectId, asOf: date.Date.AddDays(-1));
        var running = progress.Activities.Where(p => p.Percent < 100 && p.Activity.PlannedQty > 0 &&
            (p.DoneQty > 0 || (p.Activity.PlannedStart is { } st && st.Date <= date.Date))).ToList();

        var last = await _db.Table<DailyReport>()
            .Where(r => r.ProjectId == projectId && !r.IsDeleted && r.Date < date.Date)
            .OrderByDescending(r => r.Date).FirstOrDefaultAsync();
        void AddRunning()
        {
            foreach (var p in running.Where(p => b.Activities.All(a => a.ActivityId != p.Activity.Id)))
                b.Activities.Add(new ReportActivity
                {
                    ActivityId = p.Activity.Id, ActivityName = p.Activity.ToString(), Location = p.Activity.Location, Unit = p.Activity.Unit,
                    PlannedQty = p.Activity.PlannedQty, CumulativeQty = p.DoneQty, SubcontractorId = p.Activity.SubcontractorId, SubcontractorName = p.Agency,
                });
        }
        if (last == null) { AddRunning(); return b; }

        var prev = await LoadReportAsync(last.Id);
        b.Report.Shift = prev.Report.Shift;
        b.Report.WorkingHours = prev.Report.WorkingHours;
        b.Activities = prev.Activities.Select(a => new ReportActivity
        {
            ActivityId = a.ActivityId, ActivityName = a.ActivityName, Location = a.Location, Unit = a.Unit,
            PlannedQty = a.PlannedQty, CumulativeQty = a.CumulativeQty, SubcontractorId = a.SubcontractorId,
            SubcontractorName = a.SubcontractorName, DrawingItemId = a.DrawingItemId,
        }).Where(a => a.PlannedQty <= 0 || a.CumulativeQty < a.PlannedQty).ToList();
        AddRunning();
        var open = b.Activities.Select(a => a.ActivityId).ToHashSet();
        string Keep(string activityId) => activityId == "" || open.Contains(activityId) ? activityId : "";
        b.Manpower = prev.Manpower.Select(m => new ReportManpower
        {
            Source = m.Source, PartyId = m.PartyId, PartyName = m.PartyName, Trade = m.Trade, Count = m.Count, Hours = m.Hours,
            ActivityId = Keep(m.ActivityId), ActivityName = Keep(m.ActivityId) == "" ? "" : m.ActivityName,
            ResourceId = m.ResourceId, Rate = m.Rate, Basis = m.Basis,
        }).ToList();
        b.Machines = prev.Machines.Select(m => new ReportMachine
        {
            Name = m.Name, Ownership = m.Ownership, Count = m.Count, WorkingHours = m.WorkingHours,
            ActivityId = Keep(m.ActivityId), ActivityName = Keep(m.ActivityId) == "" ? "" : m.ActivityName,
            ResourceId = m.ResourceId, Rate = m.Rate, Basis = m.Basis,
        }).ToList();
        return b;
    }

    // ---------- sync support ----------

    internal Task<List<object>> DirtyRowsAsync(Type t)
    {
        var map = _db.GetConnection().GetMapping(t);
        return _db.QueryAsync(map, $"SELECT * FROM \"{map.TableName}\" WHERE IsDirty = 1");
    }

    /// <summary>Clears the dirty flag, unless the row was edited again while it was being pushed.</summary>
    internal Task MarkCleanAsync(Type t, IEnumerable<SyncEntity> pushed)
    {
        var map = _db.GetConnection().GetMapping(t);
        var list = pushed.Select(e => (e.Id, e.UpdatedAtUtc)).ToList();
        return _db.RunInTransactionAsync(c =>
        {
            foreach (var (id, stamp) in list)
                c.Execute($"UPDATE \"{map.TableName}\" SET IsDirty = 0 WHERE Id = ? AND UpdatedAtUtc = ?", id, stamp);
        });
    }

    /// <summary>Merges rows from the server. A local row that was edited later and not yet pushed wins.</summary>
    internal async Task<int> MergeRemoteAsync(IEnumerable<(Type Type, SyncEntity Row)> rows)
    {
        var list = rows.ToList();
        var applied = 0;
        await _db.RunInTransactionAsync(c =>
        {
            foreach (var (t, row) in list)
            {
                var map = c.GetMapping(t);
                var local = c.Find(row.Id, map) as SyncEntity;
                if (local is { IsDirty: true } && local.UpdatedAtUtc > row.UpdatedAtUtc) continue;
                row.IsDirty = false;
                if (row is MediaItem m)
                    m.IsUploaded = true;
                c.InsertOrReplace(row, t);
                applied++;
            }
        });
        if (applied > 0) RemoteMerged?.Invoke();
        return applied;
    }

    internal Task<List<MediaItem>> MediaToUploadAsync() =>
        _db.Table<MediaItem>().Where(m => !m.IsUploaded && !m.IsDeleted).ToListAsync();

    internal Task SetUploadedAsync(MediaItem m) =>
        _db.ExecuteAsync("UPDATE MediaItem SET IsUploaded = 1 WHERE Id = ?", m.Id);
}
