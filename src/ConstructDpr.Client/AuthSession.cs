using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ConstructDpr.Shared.Api;

namespace ConstructDpr.Client;

/// <summary>Where an app keeps the server address and sign-in tokens (browser storage, Android secure storage, a protected file on Windows).</summary>
public interface ISessionStore
{
    Task<string?> LoadAsync();
    Task SaveAsync(string? value);
}

/// <summary>Keeps nothing; for tests and one-off tools.</summary>
public sealed class MemorySessionStore : ISessionStore
{
    private string? _value;
    public Task<string?> LoadAsync() => Task.FromResult(_value);
    public Task SaveAsync(string? value) { _value = value; return Task.CompletedTask; }
}

public sealed class SessionData
{
    public string ServerUrl { get; set; } = "";
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? Email { get; set; }
    public int? ProjectId { get; set; }
}

/// <summary>
/// Signs in to the server and keeps the tokens fresh. One instance per app; RemoteApi and LiveClient ask it for the token.
/// </summary>
public sealed class AuthSession(ISessionStore store, Func<Uri, HttpMessageHandler>? handlerFactory = null)
{
    private SessionData _data = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HttpClient? _http;

    public event Action? Changed;

    public string ServerUrl => _data.ServerUrl;
    public string? Email => _data.Email;
    public bool IsSignedIn => !string.IsNullOrEmpty(_data.RefreshToken);
    public int? ProjectId => _data.ProjectId;

    /// <summary>The HttpClient for the current server (shared by RemoteApi).</summary>
    public HttpClient Http => _http ??= CreateClient(_data.ServerUrl);

    public HttpMessageHandler? CreateHandler(Uri server) => handlerFactory?.Invoke(server);

    private HttpClient CreateClient(string url)
    {
        var baseUri = new Uri(Normalise(url));
        var handler = handlerFactory?.Invoke(baseUri);
        return new HttpClient(handler ?? new HttpClientHandler()) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
    }

    public static string Normalise(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) throw new ArgumentException("Enter the server address, for example https://yourcompany-dpr.onrender.com");
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new ArgumentException("That is not a valid server address.");
        return u.GetLeftPart(UriPartial.Authority) + "/";
    }

    public async Task LoadAsync()
    {
        var raw = await store.LoadAsync();
        if (!string.IsNullOrEmpty(raw))
        {
            try { _data = JsonSerializer.Deserialize<SessionData>(raw, Json.Options) ?? new(); }
            catch (JsonException) { _data = new(); }
        }
        _http = null;
    }

    private Task PersistAsync()
    {
        Changed?.Invoke();
        return store.SaveAsync(JsonSerializer.Serialize(_data, Json.Options));
    }

    public async Task SetServerAsync(string url)
    {
        var n = Normalise(url);
        if (n != _data.ServerUrl) _data = new SessionData { ServerUrl = n };
        _http = null;
        await PersistAsync();
    }

    public async Task<AppFeatures> FeaturesAsync()
    {
        try { return await Http.GetFromJsonAsync<AppFeatures>("api/account/features", Json.Options) ?? throw new HttpRequestException("Empty answer"); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
        {
            throw new HttpRequestException($"Cannot reach the CONSTRUCT DPR server at {ServerUrl}. Check the address and your internet connection. ({ex.Message})", ex);
        }
    }

    public async Task RegisterAsync(string email, string password, string displayName)
    {
        var res = await Http.PostAsJsonAsync("api/account/register", new { email, password, displayName }, Json.Options);
        await RemoteApi.EnsureSuccessAsync(res);
    }

    public async Task SignInAsync(string email, string password)
    {
        var res = await Http.PostAsJsonAsync("api/identity/login", new { email, password }, Json.Options);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Wrong e-mail or password.");
        if (res.StatusCode == HttpStatusCode.TooManyRequests) throw new UnauthorizedAccessException("Too many attempts. Wait a minute and try again.");
        await RemoteApi.EnsureSuccessAsync(res);
        await StoreTokensAsync(res);
        _data.Email = email.Trim();
        await PersistAsync();
    }

    public async Task SignOutAsync()
    {
        _data = new SessionData { ServerUrl = _data.ServerUrl };
        await PersistAsync();
    }

    public async Task SetProjectAsync(int? projectId)
    {
        _data.ProjectId = projectId;
        await PersistAsync();
    }

    /// <summary>A valid access token, refreshed when it is about to expire. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false)
    {
        if (!IsSignedIn) return null;
        if (!forceRefresh && _data.AccessToken is not null && _data.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return _data.AccessToken;
        await _refreshLock.WaitAsync();
        try
        {
            if (!forceRefresh && _data.AccessToken is not null && _data.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return _data.AccessToken;
            var res = await Http.PostAsJsonAsync("api/identity/refresh", new { refreshToken = _data.RefreshToken }, Json.Options);
            if (!res.IsSuccessStatusCode)
            {
                if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) await SignOutAsync();
                return null;
            }
            await StoreTokensAsync(res);
            await PersistAsync();
            return _data.AccessToken;
        }
        finally { _refreshLock.Release(); }
    }

    private async Task StoreTokensAsync(HttpResponseMessage res)
    {
        var t = await res.Content.ReadFromJsonAsync<TokenResponse>(Json.Options) ?? throw new HttpRequestException("The server sent no sign-in token.");
        _data.AccessToken = t.AccessToken;
        _data.RefreshToken = t.RefreshToken;
        _data.ExpiresAt = DateTime.UtcNow.AddSeconds(t.ExpiresIn);
    }

    private sealed record TokenResponse(string TokenType, string AccessToken, long ExpiresIn, string RefreshToken);
}
