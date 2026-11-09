using ConstructDpr.Shared.Api;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace ConstructDpr.Client;

/// <summary>
/// Live updates from the server (SignalR). Reconnects by itself; after a reconnect it raises a "resync" change so every
/// screen reloads what it may have missed while offline.
/// </summary>
public sealed class LiveClient(AuthSession session) : ILiveUpdates, IAsyncDisposable
{
    private HubConnection? _hub;
    private int? _projectId;
    private string _status = "offline";

    public event Action<LiveChange>? Changed;
    public event Action<string>? StatusChanged;
    public string Status => _status;

    private void SetStatus(string s)
    {
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public async Task WatchProjectAsync(int projectId)
    {
        _projectId = projectId;
        try
        {
            await EnsureConnectedAsync();
            if (_hub?.State == HubConnectionState.Connected) await _hub.InvokeAsync("WatchProject", projectId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or HubException or TaskCanceledException or System.Net.WebSockets.WebSocketException)
        {
            SetStatus("offline");
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (_hub is null)
        {
            var url = new Uri(new Uri(session.ServerUrl), "hubs/live");
            _hub = new HubConnectionBuilder()
                .WithUrl(url, o =>
                {
                    o.AccessTokenProvider = () => session.GetAccessTokenAsync();
                    if (session.CreateHandler(url) is { } h)
                    {
                        o.HttpMessageHandlerFactory = _ => h; // in-memory test server: long polling only
                        o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                    }
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)])
                .AddJsonProtocol(o => Json.Configure(o.PayloadSerializerOptions))
                .Build();
            _hub.On<LiveChange>("changed", c => Changed?.Invoke(c));
            _hub.Reconnecting += _ => { SetStatus("reconnecting"); return Task.CompletedTask; };
            _hub.Reconnected += async _ =>
            {
                SetStatus("live");
                if (_projectId is { } p)
                {
                    await _hub.InvokeAsync("WatchProject", p);
                    Changed?.Invoke(new LiveChange(p, "all", null, "resync", "", DateTime.UtcNow));
                }
            };
            _hub.Closed += _ => { SetStatus("offline"); return Task.CompletedTask; };
        }
        if (_hub.State == HubConnectionState.Disconnected)
        {
            SetStatus("connecting");
            await _hub.StartAsync();
            SetStatus("live");
        }
    }

    public async Task StopAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        _hub = null;
        SetStatus("offline");
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
