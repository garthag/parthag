using ConstructDpr.Client;
using Microsoft.JSInterop;

namespace ConstructDpr.UI.Services;

/// <summary>Browser implementations (web app): downloads, localStorage, and the sign-in session in localStorage.</summary>
public sealed class BrowserPlatform(IJSRuntime js) : IFileSaver, IKeyValueStore, ISessionStore
{
    public Task SaveAsync(string fileName, string contentType, byte[] content) =>
        js.InvokeVoidAsync("cdpr.download", fileName, contentType, Convert.ToBase64String(content)).AsTask();

    public async Task<string?> GetAsync(string key) => await js.InvokeAsync<string?>("cdpr.get", key);
    public Task SetAsync(string key, string? value) => js.InvokeVoidAsync("cdpr.set", key, value).AsTask();

    public Task<string?> LoadAsync() => GetAsync("cdpr.session");
    Task ISessionStore.SaveAsync(string? value) => SetAsync("cdpr.session", value);
}
