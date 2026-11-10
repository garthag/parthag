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
        _db = new SQLiteAsyncConnection(Path.Combine(dataFolder, "sitenotes.db3"),
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
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

    public async Task<int> NextReportNoAsync(string projectId)
    {
        var last = await _db.Table<DailyReport>()
            .Where(r => r.ProjectId == projectId && !r.IsDeleted)
            .OrderByDescending(r => r.ReportNo).FirstOrDefaultAsync();
        return (last?.ReportNo ?? 0) + 1;
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
        return b;
    }

    /// <summary>Saves a report with all its rows in one transaction and refreshes the cached totals.
    /// Rows that were removed in the editor are soft-deleted.</summary>
    public async Task SaveReportAsync(DprBundle b)
    {
        var r = b.Report;
        if (r.ReportNo == 0) r.ReportNo = await NextReportNoAsync(r.ProjectId);
        DprCalculator.UpdateTotals(b);

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
    public async Task<DprBundle> NewReportAsync(string projectId, DateTime date, bool carryForward)
    {
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

        var last = await _db.Table<DailyReport>()
            .Where(r => r.ProjectId == projectId && !r.IsDeleted && r.Date < date.Date)
            .OrderByDescending(r => r.Date).FirstOrDefaultAsync();
        if (last == null) return b;

        var prev = await LoadReportAsync(last.Id);
        b.Report.Shift = prev.Report.Shift;
        b.Report.WorkingHours = prev.Report.WorkingHours;
        b.Activities = prev.Activities.Select(a => new ReportActivity
        {
            ActivityId = a.ActivityId, ActivityName = a.ActivityName, Location = a.Location, Unit = a.Unit,
            PlannedQty = a.PlannedQty, CumulativeQty = a.CumulativeQty, SubcontractorId = a.SubcontractorId,
            SubcontractorName = a.SubcontractorName,
        }).Where(a => a.PlannedQty <= 0 || a.CumulativeQty < a.PlannedQty).ToList();
        b.Manpower = prev.Manpower.Select(m => new ReportManpower
        {
            Source = m.Source, PartyId = m.PartyId, PartyName = m.PartyName, Trade = m.Trade, Count = m.Count, Hours = m.Hours,
        }).ToList();
        b.Machines = prev.Machines.Select(m => new ReportMachine
        {
            Name = m.Name, Ownership = m.Ownership, Count = m.Count, WorkingHours = m.WorkingHours,
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
