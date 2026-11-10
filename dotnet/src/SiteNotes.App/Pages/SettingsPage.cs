using SiteNotes.App.Services;
using SiteNotes.App.UI;
using SiteNotes.Core;

namespace SiteNotes.App.Pages;

/// <summary>User name, real-time sync server, theme and storage info.</summary>
public class SettingsPage : ContentPage
{
    private readonly Label _status = Ui.Label("", 13);

    public SettingsPage()
    {
        Title = "Settings & sync";
        this.Page();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var user = Ui.Entry(AppServices.Db.UserName, "Your name (shown on reports)");
        var url = Ui.Entry(AppServices.SyncSettings.ServerUrl, "http://192.168.1.10:5080", Keyboard.Url);
        var key = new Entry { Text = AppServices.SyncSettings.ApiKey, Placeholder = "Site key from the server's appsettings.json", IsPassword = true };
        var theme = new Picker { ItemsSource = new[] { "System", "Light", "Dark" }, SelectedItem = Preferences.Default.Get("Theme", "System") };
        theme.SelectedIndexChanged += (_, _) =>
        {
            var t = theme.SelectedItem as string ?? "System";
            Preferences.Default.Set("Theme", t);
            if (Application.Current != null)
                Application.Current.UserAppTheme = t switch { "Light" => AppTheme.Light, "Dark" => AppTheme.Dark, _ => AppTheme.Unspecified };
        };
        SyncBadge.Changed += UpdateStatus;
        UpdateStatus();

        Content = Ui.Scroll(new VerticalStackLayout
        {
            Spacing = 10, MaximumWidthRequest = 800,
            Children =
            {
                Ui.SectionHeader("You"),
                Ui.Field("Name", user),
                Ui.SectionHeader("Real-time sync (optional)"),
                Ui.Caption("Run SiteNotes.Server on an office PC or a cloud VM (SQL Server or SQLite). Every phone and PC with the same server and key sees changes within seconds. Without a server the app works fully offline."),
                Ui.Field("Server address", url),
                Ui.Field("Site key", key),
                Ui.Columns(3,
                    Ui.Button("💾 Save & connect", async () =>
                    {
                        await AppServices.ReconfigureSyncAsync(url.Text ?? "", key.Text ?? "", user.Text ?? "");
                        await DisplayAlertAsync("Saved", AppServices.SyncSettings.Enabled ? "Connecting to the server…" : "Saved. Sync is off.", "OK");
                    }),
                    Ui.Button("⟳ Sync now", async () => { await AppServices.Sync.SyncNowAsync(); UpdateStatus(); }, false),
                    Ui.Button("Test connection", TestAsync, false)),
                Ui.Card(_status),
                Ui.SectionHeader("Planning set-up"),
                Ui.Columns(3,
                    Ui.Button("🗓 Work calendar", () => AppShell.Push(new CalendarPage()), false),
                    Ui.Button("💰 Resources & rates", () => AppShell.Push(new ResourceListPage()), false),
                    Ui.Button("🧱 WBS (plant / unit)", () => AppShell.Push(new WbsListPage()), false)),
                Ui.SectionHeader("Appearance"),
                Ui.Field("Theme", theme),
                Ui.SectionHeader("Storage"),
                Ui.Caption($"Data: {FileSystem.AppDataDirectory}"),
                Ui.Caption($"Reports: {FileExport.ReportsFolder}"),
                Ui.Caption($"Device id: {AppServices.SyncSettings.DeviceId}"),
                Ui.Button("Load sample project", async () =>
                {
                    var p = await SampleData.CreateAsync(AppServices.Db);
                    await AppServices.SetProjectAsync(p);
                    await DisplayAlertAsync("Sample loaded", $"“{p.Name}” is now the current project.", "OK");
                }, false),
                Ui.Caption("SiteNotes DPR 1.0 · Android & Windows · C# / .NET MAUI · SQLite on device, SQL Server on the sync server."),
            }
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SyncBadge.Changed -= UpdateStatus;
    }

    private void UpdateStatus()
    {
        var s = AppServices.Sync;
        _status.Text = $"{SyncBadge.Text(s.State)}\n{s.LastMessage}" + (s.LastSyncLocal is { } t ? $"\nLast sync {t:dd-MMM HH:mm:ss}" : "");
    }

    private async Task TestAsync()
    {
        var url = AppServices.SyncSettings.ServerUrl;
        if (string.IsNullOrWhiteSpace(url)) { await DisplayAlertAsync("No server", "Enter the server address and tap Save first.", "OK"); return; }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            var res = await http.GetAsync(url.TrimEnd('/') + "/api/health");
            await DisplayAlertAsync("Connection", res.IsSuccessStatusCode ? "✔ Server reachable." : $"Server answered {(int)res.StatusCode}.", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Connection failed", ex.Message + "\n\nCheck the address, that the server is running, and that the phone is on the same network (or the server is public).", "OK");
        }
    }
}

/// <summary>Full-screen photo with caption / remarks, share, open and delete. Videos open in the system player.</summary>
public class MediaViewerPage : ContentPage
{
    public MediaViewerPage(Core.Models.MediaItem m, Func<Task> onChanged)
    {
        Title = m.Kind == Core.Models.MediaKind.Video ? "Video" : "Photo";
        this.Page();
        var path = AppServices.Db.MediaPath(m.FileName);
        View preview = m.Kind == Core.Models.MediaKind.Video
            ? Ui.Button("▶ Play video", () => FileExport.OpenAsync(path))
            : new Image { Source = File.Exists(path) ? ImageSource.FromFile(path) : null, Aspect = Aspect.AspectFit, HeightRequest = 420 };
        ToolbarItems.Add(new ToolbarItem("Share", null, () => _ = Ui.Guard(() => FileExport.ShareAsync(path))));
        ToolbarItems.Add(new ToolbarItem("Open", null, () => _ = Ui.Guard(() => FileExport.OpenAsync(path))));
        ToolbarItems.Add(new ToolbarItem("🗑 Delete", null, () => _ = Ui.Guard(async () =>
        {
            if (!await DisplayAlertAsync("Delete?", "Remove this photo/video from the record?", "Delete", "Cancel")) return;
            await AppServices.Db.DeleteAsync(m);
            await onChanged();
            await Navigation.PopAsync();
        })) { Order = ToolbarItemOrder.Secondary });
        Content = Ui.Scroll(new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                preview,
                Ui.Caption($"{m.CapturedAtUtc.ToLocalTime():dd-MMM-yyyy HH:mm}" + (m.Latitude is { } la ? $" · GPS {la:0.00000}, {m.Longitude:0.00000}" : "") +
                           (File.Exists(path) ? "" : " · file not on this device yet (syncing)")),
                Ui.Field("Caption", Ui.Entry(m.Caption, "What does it show?", changed: v => m.Caption = v)),
                Ui.Field("Remarks", Ui.Memo(m.Remarks, "", v => m.Remarks = v, 60)),
                Ui.Button("💾 Save", async () => { await AppServices.Db.SaveAsync(m); await onChanged(); await Navigation.PopAsync(); }),
            }
        });
    }
}
