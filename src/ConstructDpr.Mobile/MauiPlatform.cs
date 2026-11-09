using ConstructDpr.Client;
using ConstructDpr.UI.Services;

namespace ConstructDpr.Mobile;

/// <summary>Android implementations: reports open in the share sheet, sign-in tokens in Android secure storage.</summary>
public sealed class MauiPlatform : IFileSaver, IKeyValueStore, ISessionStore
{
    public async Task SaveAsync(string fileName, string contentType, byte[] content)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, fileName);
        await File.WriteAllBytesAsync(path, content);
        await MainThread.InvokeOnMainThreadAsync(() => Share.Default.RequestAsync(new ShareFileRequest
        {
            Title = fileName,
            File = new ShareFile(path, contentType),
        }));
    }

    public Task<string?> GetAsync(string key) => Task.FromResult(Preferences.Default.Get<string?>(key, null));

    public Task SetAsync(string key, string? value)
    {
        if (value is null) Preferences.Default.Remove(key); else Preferences.Default.Set(key, value);
        return Task.CompletedTask;
    }

    public async Task<string?> LoadAsync()
    {
        try { return await SecureStorage.Default.GetAsync("cdpr.session"); }
        catch (Exception) { SecureStorage.Default.Remove("cdpr.session"); return null; } // keystore reset (e.g. after restoring a backup)
    }

    async Task ISessionStore.SaveAsync(string? value)
    {
        if (value is null) SecureStorage.Default.Remove("cdpr.session");
        else await SecureStorage.Default.SetAsync("cdpr.session", value);
    }
}
