namespace DrawingRegister.Core;

/// <summary>Where the application keeps its database and the uploaded drawing files.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(StorageRoot);
        Directory.CreateDirectory(LogsDir);
    }

    public string Root { get; }
    public string DatabaseFile => Path.Combine(Root, "drawing_register.db");
    public string StorageRoot => Path.Combine(Root, "drawings");
    public string LogsDir => Path.Combine(Root, "logs");
    public string WebViewDataDir => Path.Combine(Root, "webview2");

    /// <summary>%LOCALAPPDATA%\DrawingRegister, or DRAWING_REGISTER_HOME when set.</summary>
    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable("DRAWING_REGISTER_HOME");
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(overridden);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return new AppPaths(Path.Combine(local, "DrawingRegister"));
    }

    public string AbsoluteStoredPath(string storedPath)
    {
        var full = Path.GetFullPath(Path.Combine(StorageRoot, storedPath));
        var root = Path.GetFullPath(StorageRoot) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Stored path escapes the storage folder.");
        }

        return full;
    }
}
