using System.Text.RegularExpressions;

namespace DrawingRegister.Core;

/// <summary>
/// The folder that holds the drawing files, laid out the same way as the
/// register so it is useful even without the app:
/// <code>
/// Library\Plant A\Unit 100\03 ST - Structural\RF-100-ST-GA-0001_R2.pdf
/// </code>
/// It can be a local folder or a shared network folder (\\server\drawings).
/// </summary>
public sealed class DrawingLibrary(string root)
{
    public string Root { get; } = root;

    static string Safe(string s, string fallback)
    {
        var t = Regex.Replace(s.Replace(" / ", " - ").Replace('/', '-'), @"[<>:""\\|?*\x00-\x1F]", "_").Trim().TrimEnd('.');
        return t.Length == 0 ? fallback : t.Length > 80 ? t[..80] : t;
    }

    public static string TypeFolder(DrawingType t) => $"{t.Order + 1:00} {t.Code} - {Safe(t.Name, t.Code)}";

    public static string RelativeFolder(Drawing d) => Path.Combine(
        Safe(Register.PlantLabel(d.Plant), "Plant"),
        Safe(Register.UnitLabel(d.Unit), "Unit"),
        TypeFolder(d.Type));

    public string FullPath(Drawing d) => Path.Combine(Root, d.StoredPath);

    public bool Exists(Drawing d) => d.StoredPath.Length > 0 && File.Exists(FullPath(d));

    string UniqueRelative(string folder, string fileName)
    {
        var name = Safe(fileName, "drawing");
        var rel = Path.Combine(folder, name);
        var n = 2;
        while (File.Exists(Path.Combine(Root, rel)))
            rel = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n++}){Path.GetExtension(name)}");
        return rel;
    }

    /// <summary>Copies <paramref name="content"/> into the library and sets StoredPath and SizeBytes.</summary>
    public void Store(Drawing d, Stream content)
    {
        var rel = UniqueRelative(RelativeFolder(d), d.FileName);
        var full = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var f = File.Create(full)) content.CopyTo(f);
        d.StoredPath = rel;
        d.SizeBytes = new FileInfo(full).Length;
    }

    /// <summary>Moves the file after plant, unit or type was edited so the folders stay in register order.</summary>
    public void Relocate(Drawing d)
    {
        if (!Exists(d)) return;
        var folder = RelativeFolder(d);
        if (string.Equals(Path.GetDirectoryName(d.StoredPath), folder, StringComparison.OrdinalIgnoreCase)) return;
        var old = FullPath(d);
        var rel = UniqueRelative(folder, Path.GetFileName(d.StoredPath));
        Directory.CreateDirectory(Path.Combine(Root, folder));
        File.Move(old, Path.Combine(Root, rel));
        d.StoredPath = rel;
        PruneEmpty(Path.GetDirectoryName(old));
    }

    public void Delete(Drawing d)
    {
        if (!Exists(d)) return;
        var full = FullPath(d);
        File.Delete(full);
        PruneEmpty(Path.GetDirectoryName(full));
    }

    void PruneEmpty(string? dir)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar);
        while (dir != null && Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar).Length > root.Length
               && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Directory.Delete(dir);
            dir = Path.GetDirectoryName(dir);
        }
    }
}
