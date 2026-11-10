using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using SiteNotes.Core;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Core.Sync;

namespace SiteNotes.Desktop.Services;

/// <summary>Desktop app state: database, sync, current project, change notifications.</summary>
public static class Desk
{
    public static LocalDatabase Db { get; private set; } = null!;
    public static SyncClient Sync { get; private set; } = null!;
    public static SyncSettings SyncSettings { get; private set; } = new();
    public static ReportQueryService Reports { get; private set; } = null!;
    public static Project? Project { get; private set; }
    public static string ProjectId => Project?.Id ?? "";

    public static event Action? ProjectChanged;
    /// <summary>Data changed locally or arrived from another device (UI thread, debounced).</summary>
    public static event Action? DataChanged;
    public static event Action? SyncStatusChanged;
    public static event Action<string>? Status;

    public static string DataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SiteNotes");
    public static string ReportsFolder
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SiteNotes Reports");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    private static Dispatcher UI => Application.Current.Dispatcher;
    private static DispatcherTimer? _debounce;

    private static Task? _start;

    /// <summary>Starts once; later callers wait for the same start.</summary>
    public static Task StartAsync() => _start ??= StartCoreAsync();

    private static async Task StartCoreAsync()
    {
        Db = new LocalDatabase(DataFolder);
        await Db.InitAsync();
        Reports = new ReportQueryService(Db);
        SyncSettings = new SyncSettings
        {
            ServerUrl = await Db.GetSettingAsync("Sync.Url") ?? "",
            ApiKey = await Db.GetSettingAsync("Sync.Key") ?? "",
            DeviceId = await Db.GetSettingAsync("Sync.DeviceId") ?? Guid.NewGuid().ToString("N"),
        };
        await Db.SetSettingAsync("Sync.DeviceId", SyncSettings.DeviceId);
        if (string.IsNullOrWhiteSpace(Db.UserName)) Db.UserName = Environment.UserName;

        var last = await Db.GetSettingAsync("CurrentProject");
        var projects = await Db.ListAsync<Project>();
        Project = projects.FirstOrDefault(p => p.Id == last) ?? projects.OrderBy(p => p.Name).FirstOrDefault();

        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            DataChanged?.Invoke();
        }, UI);
        _debounce.Stop();
        Db.Changed += _ => Changed();
        Db.RemoteMerged += () => UI.BeginInvoke(async () =>
        {
            if (Project == null && (await Db.ListAsync<Project>()).FirstOrDefault() is { } p) await SetProjectAsync(p);
            Changed();
        });
        StartSync();
    }

    private static void Changed() => UI.BeginInvoke(() => { _debounce!.Stop(); _debounce.Start(); });

    private static void StartSync()
    {
        Sync = new SyncClient(Db, SyncSettings);
        Sync.StatusChanged += () => UI.BeginInvoke(() => SyncStatusChanged?.Invoke());
        _ = Task.Run(Sync.StartAsync);
    }

    public static async Task StopAsync()
    {
        try { await Sync.DisposeAsync(); } catch { /* closing */ }
    }

    public static async Task ReconfigureSyncAsync(string url, string key, string user)
    {
        await Db.SetSettingAsync("Sync.Url", url.Trim());
        await Db.SetSettingAsync("Sync.Key", key.Trim());
        await Db.SetSettingAsync("UserName", user.Trim());
        Db.UserName = user.Trim();
        await Sync.DisposeAsync();
        SyncSettings = new SyncSettings { ServerUrl = url.Trim(), ApiKey = key.Trim(), DeviceId = SyncSettings.DeviceId };
        StartSync();
        SyncStatusChanged?.Invoke();
    }

    public static async Task SetProjectAsync(Project? p)
    {
        Project = p;
        await Db.SetSettingAsync("CurrentProject", p?.Id ?? "");
        ProjectChanged?.Invoke();
    }

    public static void Say(string message) => Status?.Invoke(message);

    public static string SyncText => Sync.State switch
    {
        SyncState.Online => "● Live sync",
        SyncState.Syncing => "⟳ Syncing",
        SyncState.Connecting => "⟳ Connecting",
        SyncState.Offline => "○ Offline (changes kept)",
        SyncState.Error => "⚠ Sync error",
        _ => "○ Local only",
    };

    // ------------------------------------------------------------------ report settings

    public static async Task<(ReportProfile, ReportOptions)> LoadReportOptionsAsync()
    {
        var p = (await Db.ListForProjectAsync<ReportProfile>(ProjectId)).OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault()
                ?? new ReportProfile { ProjectId = ProjectId };
        var o = ReportOptions.FromJson(p.OptionsJson);
        if (p.OptionsJson == "" && Project is { } proj)
        {
            o.Layout.CompanyName = proj.Contractor;
            o.Layout.HeaderLines = string.Join("\n", new[] { proj.Code != "" ? $"Project ref: {proj.Code}" : "", proj.Client != "" ? $"Client: {proj.Client}" : "" }.Where(x => x != ""));
        }
        return (p, o);
    }

    public static async Task<ReportOptions> ReportOptionsAsync() => (await LoadReportOptionsAsync()).Item2.Resolve(Db.MediaPath);

    public static async Task SaveReportOptionsAsync(ReportProfile p, ReportOptions o)
    {
        p.ProjectId = ProjectId;
        p.OptionsJson = o.ToJson();
        await Db.SaveAsync(p);
    }

    /// <summary>Copies a file into the media folder (so it syncs) and returns its new name.</summary>
    public static async Task<MediaItem> ImportMediaAsync(string path, MediaOwner owner, string ownerId, MediaKind kind, string caption = "", int pageNo = 0)
    {
        var name = Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant();
        await Task.Run(() => File.Copy(path, Db.MediaPath(name)));
        var m = new MediaItem
        {
            ProjectId = ProjectId, OwnerType = owner, OwnerId = ownerId, Kind = kind, FileName = name,
            Caption = caption == "" ? Path.GetFileNameWithoutExtension(path) : caption, PageNo = pageNo,
            CapturedAtUtc = File.GetLastWriteTimeUtc(path), FileSize = new FileInfo(path).Length,
        };
        await Db.SaveAsync(m);
        return m;
    }
}

/// <summary>Message boxes, prompts, file dialogs and opening files.</summary>
public static class Dialogs
{
    /// <summary>Automated runs (tests, screenshot tour): never block on a dialog. Messages and errors are recorded instead.</summary>
    public static bool Headless { get; set; }
    public static List<string> Messages { get; } = [];
    public static List<string> Errors { get; } = [];

    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public static void Info(string text, string title = "SiteNotes")
    {
        if (Headless) { Messages.Add($"{title}: {text}"); return; }
        MessageBox.Show(Owner!, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static bool Confirm(string text, string title = "Please confirm")
    {
        if (Headless) { Messages.Add($"{title}: {text} → Yes"); return true; }
        return MessageBox.Show(Owner!, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    public static void Error(Exception ex)
    {
        if (Headless) { Errors.Add(ex.ToString()); return; }
        MessageBox.Show(Owner!, ex.Message, "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static async Task Guard(Func<Task> action)
    {
        try
        {
            Mouse(true);
            await action();
        }
        catch (Exception ex) { Error(ex); }
        finally { Mouse(false); }
    }

    private static void Mouse(bool busy) => System.Windows.Input.Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.AppStarting : null;

    /// <summary>Small input box. Returns null when cancelled.</summary>
    public static string? Prompt(string title, string label, string initial = "")
    {
        if (Headless) { Messages.Add($"{title}: {label} → {initial}"); return initial; }
        var box = new System.Windows.Controls.TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10), MinWidth = 320 };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = Owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => w.DialogResult = true;
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }

    /// <summary>Picks one choice from a list (radio-style list box). Returns the index or -1.</summary>
    public static int Choose(string title, string label, IList<string> options)
    {
        if (Headless) { Messages.Add($"{title}: {label} → {options.FirstOrDefault()}"); return options.Count > 0 ? 0 : -1; }
        var list = new System.Windows.Controls.ListBox { ItemsSource = options, MinWidth = 380, MaxHeight = 420, Margin = new Thickness(0, 6, 0, 10), SelectedIndex = 0 };
        var ok = new System.Windows.Controls.Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var w = new Window { Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = Owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        ok.Click += (_, _) => w.DialogResult = true;
        list.MouseDoubleClick += (_, _) => w.DialogResult = true;
        var buttons = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label });
        panel.Children.Add(list);
        panel.Children.Add(buttons);
        w.Content = panel;
        return w.ShowDialog() == true ? list.SelectedIndex : -1;
    }

    public static string[] OpenImages(bool multi = true, string title = "Choose photos")
    {
        var d = new OpenFileDialog { Title = title, Multiselect = multi, Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.heic|All files|*.*" };
        return d.ShowDialog() == true ? d.FileNames : [];
    }

    public static string[] OpenMedia()
    {
        var d = new OpenFileDialog { Title = "Add photos or videos", Multiselect = true, Filter = "Photos and videos|*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.mp4;*.mov;*.avi;*.wmv|All files|*.*" };
        return d.ShowDialog() == true ? d.FileNames : [];
    }

    /// <summary>Writes a report in the background and opens it. The file goes to Documents\SiteNotes Reports.</summary>
    public static async Task<string> CreateAndOpenAsync(string fileName, Action<Stream> write, bool askWhere = false)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '-');
        var path = Path.Combine(Desk.ReportsFolder, fileName.Replace(' ', '_'));
        if (askWhere)
        {
            var ext = Path.GetExtension(fileName);
            var d = new SaveFileDialog { FileName = Path.GetFileName(path), InitialDirectory = Desk.ReportsFolder, Filter = $"{ext.TrimStart('.').ToUpperInvariant()}|*{ext}" };
            if (d.ShowDialog() != true) return "";
            path = d.FileName;
        }
        Desk.Say($"Creating {Path.GetFileName(path)}…");
        await Task.Run(() =>
        {
            using var fs = File.Create(path);
            write(fs);
        });
        Desk.Say($"Saved {path}");
        Open(path);
        return path;
    }

    public static void Open(string path)
    {
        if (Headless) { Messages.Add("Open " + path); return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Info($"Saved to {path}\n\n(could not open it: {ex.Message})"); }
    }

    public static void ShowInFolder(string path)
    {
        if (Headless) return;
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { /* ignore */ }
    }
}
