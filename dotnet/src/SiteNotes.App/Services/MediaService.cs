using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;

namespace SiteNotes.App.Services;

/// <summary>Camera, gallery and file picking. Everything is copied into the app's media folder
/// with a unique name, so deleting it from the phone gallery never breaks a report.</summary>
public static class MediaService
{
    private static readonly MediaPickerOptions PhotoOptions = new() { CompressionQuality = 85, MaximumWidth = 2400, MaximumHeight = 2400, RotateImage = true };

    public static async Task<MediaItem?> CapturePhotoAsync(MediaOwner owner, string ownerId)
    {
        if (!await EnsureCameraAsync()) return null;
        try
        {
            if (!MediaPicker.Default.IsCaptureSupported) return (await PickPhotosAsync(owner, ownerId, 1)).FirstOrDefault();
            var file = await MediaPicker.Default.CapturePhotoAsync(PhotoOptions);
            return file == null ? null : await ImportAsync(file, owner, ownerId, MediaKind.Photo);
        }
        catch (FeatureNotSupportedException)
        {
            return (await PickPhotosAsync(owner, ownerId, 1)).FirstOrDefault();
        }
    }

    public static async Task<List<MediaItem>> PickPhotosAsync(MediaOwner owner, string ownerId, int limit = 20)
    {
        var result = new List<MediaItem>();
        var files = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = limit, CompressionQuality = 85, MaximumWidth = 2400, MaximumHeight = 2400 });
        foreach (var f in files ?? [])
        {
            if (f == null) continue;
            var m = await ImportAsync(f, owner, ownerId, MediaKind.Photo);
            if (m != null) result.Add(m);
        }
        return result;
    }

    public static async Task<MediaItem?> CaptureVideoAsync(MediaOwner owner, string ownerId)
    {
        if (!await EnsureCameraAsync()) return null;
        await Permissions.RequestAsync<Permissions.Microphone>();
        try
        {
            var file = MediaPicker.Default.IsCaptureSupported
                ? await MediaPicker.Default.CaptureVideoAsync(new MediaPickerOptions())
                : await PickOneVideoAsync();
            return file == null ? null : await ImportAsync(file, owner, ownerId, MediaKind.Video);
        }
        catch (FeatureNotSupportedException)
        {
            var file = await PickOneVideoAsync();
            return file == null ? null : await ImportAsync(file, owner, ownerId, MediaKind.Video);
        }
    }

    public static async Task<MediaItem?> PickVideoAsync(MediaOwner owner, string ownerId)
    {
        var file = await PickOneVideoAsync();
        return file == null ? null : await ImportAsync(file, owner, ownerId, MediaKind.Video);
    }

    /// <summary>Takes a photo for the scanner and returns the raw file path (not yet saved as media).</summary>
    public static async Task<string?> CaptureRawAsync()
    {
        if (!await EnsureCameraAsync()) return null;
        FileResult? file;
        try
        {
            file = MediaPicker.Default.IsCaptureSupported
                ? await MediaPicker.Default.CapturePhotoAsync(PhotoOptions)
                : await PickOnePhotoAsync();
        }
        catch (FeatureNotSupportedException)
        {
            file = await PickOnePhotoAsync();
        }
        return file == null ? null : await CopyToCacheAsync(file);
    }

    public static async Task<List<string>> PickRawPhotosAsync()
    {
        var files = await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 50 });
        var list = new List<string>();
        foreach (var f in files ?? []) if (f != null) list.Add(await CopyToCacheAsync(f));
        return list;
    }

    private static async Task<FileResult?> PickOneVideoAsync() =>
        (await MediaPicker.Default.PickVideosAsync(new MediaPickerOptions { SelectionLimit = 1 }))?.FirstOrDefault();

    private static async Task<FileResult?> PickOnePhotoAsync() =>
        (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { SelectionLimit = 1, MaximumWidth = 2400, MaximumHeight = 2400 }))?.FirstOrDefault();

    private static async Task<string> CopyToCacheAsync(FileResult file)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, Guid.NewGuid().ToString("N") + Ext(file.FileName, ".jpg"));
        await using var src = await file.OpenReadAsync();
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst);
        return path;
    }

    private static string Ext(string name, string fallback)
    {
        var e = Path.GetExtension(name);
        return string.IsNullOrEmpty(e) ? fallback : e.ToLowerInvariant();
    }

    public static async Task<MediaItem?> ImportAsync(FileResult file, MediaOwner owner, string ownerId, MediaKind kind)
    {
        var name = Guid.NewGuid().ToString("N") + Ext(file.FileName, kind == MediaKind.Video ? ".mp4" : ".jpg");
        var dest = AppServices.Db.MediaPath(name);
        await using (var src = await file.OpenReadAsync())
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst);
        return await RegisterAsync(dest, name, owner, ownerId, kind);
    }

    /// <summary>Saves a media row for a file that already sits in the media folder.</summary>
    public static async Task<MediaItem> RegisterAsync(string path, string name, MediaOwner owner, string ownerId, MediaKind kind,
        string caption = "", int pageNo = 0)
    {
        var item = new MediaItem
        {
            ProjectId = AppServices.ProjectId,
            OwnerType = owner,
            OwnerId = ownerId,
            Kind = kind,
            FileName = name,
            Caption = caption,
            PageNo = pageNo,
            CapturedAtUtc = DateTime.UtcNow,
            FileSize = new FileInfo(path).Length,
        };
        await TryGeoTagAsync(item);
        await AppServices.Db.SaveAsync(item);
        return item;
    }

    private static async Task TryGeoTagAsync(MediaItem item)
    {
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted) status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted) return;
            var loc = await Geolocation.Default.GetLastKnownLocationAsync()
                      ?? await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(4)));
            if (loc == null) return;
            item.Latitude = Math.Round(loc.Latitude, 6);
            item.Longitude = Math.Round(loc.Longitude, 6);
        }
        catch
        {
            // Location is a nice-to-have; never block a photo for it.
        }
    }

    private static async Task<bool> EnsureCameraAsync()
    {
        var s = await Permissions.CheckStatusAsync<Permissions.Camera>();
        if (s != PermissionStatus.Granted) s = await Permissions.RequestAsync<Permissions.Camera>();
        return s == PermissionStatus.Granted;
    }

    /// <summary>Thumbnail path (created on demand) for lists and note blocks.</summary>
    public static string? Thumbnail(MediaItem m)
    {
        if (m.Kind == MediaKind.Video) return null;
        var src = AppServices.Db.MediaPath(m.FileName);
        if (!File.Exists(src)) return null;
        var thumbDir = Path.Combine(FileSystem.CacheDirectory, "thumbs");
        Directory.CreateDirectory(thumbDir);
        var thumb = Path.Combine(thumbDir, Path.GetFileNameWithoutExtension(m.FileName) + ".jpg");
        if (!File.Exists(thumb) && !ImageTools.MakeThumbnail(src, thumb, 480)) return src;
        return thumb;
    }

    public static Task OpenAsync(MediaItem m) => FileExport.OpenAsync(AppServices.Db.MediaPath(m.FileName));
}
