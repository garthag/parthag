namespace ConstructDpr.UI.Services;

/// <summary>Saves a downloaded report: a browser download on the web, the share sheet on Android.</summary>
public interface IFileSaver
{
    Task SaveAsync(string fileName, string contentType, byte[] content);
}

/// <summary>Small local key-value storage (browser localStorage, Android preferences): unsaved drafts, last screen.</summary>
public interface IKeyValueStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value);
}

/// <summary>What differs between the hosts.</summary>
public sealed class HostOptions
{
    /// <summary>The web app is served by the server itself, so the address is known; the Android app asks for it once.</summary>
    public string? FixedServerUrl { get; set; }
    public string Platform { get; set; } = "web";
}
