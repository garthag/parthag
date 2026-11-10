using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core.Sync;

public enum SyncState { Off, Connecting, Online, Syncing, Offline, Error }

public class SyncSettings
{
    public string ServerUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled => !string.IsNullOrWhiteSpace(ServerUrl);
}

/// <summary>
/// Real-time sync with the SiteNotes server.
/// <list type="bullet">
/// <item>Local edits are pushed a moment after they happen (debounced).</item>
/// <item>The server broadcasts a SignalR "Changed" message; every other device pulls immediately.</item>
/// <item>Photos, videos and scans are uploaded/downloaded as files.</item>
/// <item>Offline is normal: changes queue up (IsDirty) and go out when the connection returns.</item>
/// </list>
/// Conflicts are resolved per row: the most recent edit wins.
/// </summary>
public sealed class SyncClient : IAsyncDisposable
{
    private readonly LocalDatabase _db;
    private readonly SyncSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private CancellationTokenSource? _debounce;
    private Timer? _fallbackTimer;
    private bool _pendingAgain;

    public SyncState State { get; private set; } = SyncState.Off;
    public string LastMessage { get; private set; } = "Sync is off";
    public DateTime? LastSyncLocal { get; private set; }
    public event Action? StatusChanged;

    public SyncClient(LocalDatabase db, SyncSettings settings, HttpClient? http = null)
    {
        _db = db;
        _settings = settings;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (_settings.Enabled) _http.BaseAddress = new Uri(_settings.ServerUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Remove(SyncJson.ApiKeyHeader);
        _http.DefaultRequestHeaders.Add(SyncJson.ApiKeyHeader, _settings.ApiKey);
        _db.Changed += OnLocalChange;
    }

    private void SetState(SyncState s, string msg)
    {
        State = s;
        LastMessage = msg;
        StatusChanged?.Invoke();
    }

    /// <summary>Connects the real-time channel and does a first full sync.</summary>
    public async Task StartAsync()
    {
        if (!_settings.Enabled) { SetState(SyncState.Off, "Sync is off – add a server in Settings"); return; }
        SetState(SyncState.Connecting, "Connecting…");

        _hub = new HubConnectionBuilder()
            .WithUrl(_settings.ServerUrl.TrimEnd('/') + SyncJson.HubPath, o =>
            {
                o.Headers[SyncJson.ApiKeyHeader] = _settings.ApiKey;
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
            .Build();

        _hub.On<long, string>(SyncJson.ChangedMethod, (_version, fromDevice) =>
        {
            if (fromDevice != _settings.DeviceId) FireAndForget();
        });
        _hub.Reconnecting += _ => { SetState(SyncState.Offline, "Connection lost – changes are saved on this device"); return Task.CompletedTask; };
        _hub.Reconnected += _ => SyncAsync(waitIfBusy: false);
        _hub.Closed += _ => { SetState(SyncState.Offline, "Offline – will retry"); return Task.CompletedTask; };

        // Safety net: retry every 2 minutes (also reconnects after long outages).
        _fallbackTimer = new Timer(_ => _ = TickAsync(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));

        await ConnectHubAsync();
        await SyncAsync(waitIfBusy: false);
    }

    private async Task ConnectHubAsync()
    {
        if (_hub == null || _hub.State != HubConnectionState.Disconnected) return;
        try { await _hub.StartAsync(); }
        catch (Exception ex) { SetState(SyncState.Offline, "Server not reachable: " + ex.Message); }
    }

    private async Task TickAsync()
    {
        await ConnectHubAsync();
        await SyncAsync(waitIfBusy: false);
    }

    private void FireAndForget() => _ = SyncAsync(waitIfBusy: false);

    private void OnLocalChange(string entityType)
    {
        if (!_settings.Enabled) return;
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        Task.Delay(1500, cts.Token).ContinueWith(t => { if (!t.IsCanceled) FireAndForget(); }, TaskScheduler.Default);
    }

    /// <summary>Push local changes, upload files, pull remote changes, download files.</summary>
    public async Task<bool> SyncNowAsync(CancellationToken ct = default) => await SyncAsync(waitIfBusy: true, ct);

    /// <summary>Automatic syncs (after a local change or a server ping) never queue up behind each other:
    /// if one is running it simply runs once more at the end. A manual "Sync now" waits its turn.</summary>
    private async Task<bool> SyncAsync(bool waitIfBusy, CancellationToken ct = default)
    {
        if (!_settings.Enabled) return false;
        if (!await _gate.WaitAsync(0, ct))
        {
            _pendingAgain = true;
            if (!waitIfBusy) return false;
            await _gate.WaitAsync(ct);
        }
        try
        {
            do
            {
                _pendingAgain = false;
                SetState(SyncState.Syncing, "Syncing…");
                var pushed = await PushAsync(ct);
                var uploaded = await UploadMediaAsync(ct);
                var pulled = await PullAsync(ct);
                var downloaded = await DownloadMissingMediaAsync(ct);
                LastSyncLocal = DateTime.Now;
                SetState(_hub?.State == HubConnectionState.Connected ? SyncState.Online : SyncState.Offline,
                    $"Synced {LastSyncLocal:HH:mm:ss} · ↑{pushed} ↓{pulled} · files ↑{uploaded} ↓{downloaded}");
            } while (_pendingAgain);
            return true;
        }
        catch (HttpRequestException ex)
        {
            SetState(SyncState.Offline, "Offline – " + ex.Message);
            return false;
        }
        catch (TaskCanceledException)
        {
            SetState(SyncState.Offline, "Offline – request timed out");
            return false;
        }
        catch (Exception ex)
        {
            SetState(SyncState.Error, "Sync error: " + ex.Message);
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task<int> PushAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var (name, type) in EntityRegistry.Types)
        {
            var rows = (await _db.DirtyRowsAsync(type)).Cast<SyncEntity>().ToList();
            foreach (var chunk in rows.Chunk(200))
            {
                var records = chunk.Select(e => new SyncRecord(e.Id, name, e.ProjectId,
                    JsonSerializer.Serialize(e, type, SyncJson.Options), e.UpdatedAtUtc, e.IsDeleted)).ToList();
                var res = await _http.PostAsJsonAsync("api/sync/push", new PushRequest(_settings.DeviceId, records), SyncJson.Options, ct);
                res.EnsureSuccessStatusCode();
                await _db.MarkCleanAsync(type, chunk);
                total += chunk.Length;
            }
        }
        return total;
    }

    private async Task<int> PullAsync(CancellationToken ct)
    {
        var since = long.TryParse(await _db.GetSettingAsync("Sync.LastVersion"), out var v) ? v : 0;
        var total = 0;
        while (true)
        {
            var res = await _http.GetFromJsonAsync<PullResponse>($"api/sync/pull?since={since}&take=500", SyncJson.Options, ct)
                      ?? new PullResponse([], since, false);
            var rows = new List<(Type, SyncEntity)>();
            foreach (var rec in res.Records)
            {
                if (!EntityRegistry.Types.TryGetValue(rec.EntityType, out var t)) continue; // newer app version's table
                if (JsonSerializer.Deserialize(rec.Json, t, SyncJson.Options) is SyncEntity e) rows.Add((t, e));
            }
            total += await _db.MergeRemoteAsync(rows);
            since = res.ServerVersion;
            await _db.SetSettingAsync("Sync.LastVersion", since.ToString());
            if (!res.HasMore) break;
        }
        return total;
    }

    private async Task<int> UploadMediaAsync(CancellationToken ct)
    {
        var n = 0;
        foreach (var m in await _db.MediaToUploadAsync())
        {
            var path = _db.MediaPath(m.FileName);
            if (!File.Exists(path)) continue;
            await using var fs = File.OpenRead(path);
            using var content = new StreamContent(fs);
            var res = await _http.PutAsync($"api/media/{Uri.EscapeDataString(m.FileName)}", content, ct);
            res.EnsureSuccessStatusCode();
            await _db.SetUploadedAsync(m);
            n++;
        }
        // Scan PDFs live next to the page images.
        foreach (var s in await _db.ListAsync<ScanDocument>(x => x.PdfFileName != ""))
        {
            var path = _db.MediaPath(s.PdfFileName);
            var marker = path + ".up";
            if (!File.Exists(path) || (File.Exists(marker) && File.GetLastWriteTimeUtc(marker) >= File.GetLastWriteTimeUtc(path))) continue;
            await using var fs = File.OpenRead(path);
            using var content = new StreamContent(fs);
            (await _http.PutAsync($"api/media/{Uri.EscapeDataString(s.PdfFileName)}", content, ct)).EnsureSuccessStatusCode();
            await File.WriteAllTextAsync(marker, "", ct);
            n++;
        }
        return n;
    }

    private async Task<int> DownloadMissingMediaAsync(CancellationToken ct)
    {
        var n = 0;
        var names = (await _db.ListAsync<MediaItem>()).Select(m => m.FileName)
            .Concat((await _db.ListAsync<ScanDocument>()).Select(s => s.PdfFileName))
            .Where(f => !string.IsNullOrWhiteSpace(f)).Distinct();
        foreach (var name in names)
        {
            var path = _db.MediaPath(name);
            if (File.Exists(path)) continue;
            using var res = await _http.GetAsync($"api/media/{Uri.EscapeDataString(name)}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) continue; // not uploaded yet by the other device
            var tmp = path + ".part";
            await using (var fs = File.Create(tmp)) await res.Content.CopyToAsync(fs, ct);
            File.Move(tmp, path, true);
            File.WriteAllText(path + ".up", "");
            n++;
        }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        _db.Changed -= OnLocalChange;
        _fallbackTimer?.Dispose();
        if (_hub != null) await _hub.DisposeAsync();
    }
}
