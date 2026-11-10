using SiteNotes.App.Pages;
using SiteNotes.App.Services;
using SiteNotes.App.UI;

namespace SiteNotes.App;

public class App : Application
{
    public App()
    {
        var theme = Preferences.Default.Get("Theme", "System");
        UserAppTheme = theme switch { "Light" => AppTheme.Light, "Dark" => AppTheme.Dark, _ => AppTheme.Unspecified };
    }

    protected override Window CreateWindow(Microsoft.Maui.IActivationState? activationState)
    {
        var window = new Window(new LoadingPage()) { Title = "SiteNotes DPR" };
        if (DeviceInfo.Platform == DevicePlatform.WinUI)
        {
            window.MinimumWidth = 900;
            window.MinimumHeight = 600;
            window.TitleBar = BuildTitleBar();
        }
        return window;
    }

    /// <summary>Windows title bar: app name, current project and live sync status.</summary>
    private static TitleBar BuildTitleBar()
    {
        var status = new Label { TextColor = Colors.White, FontSize = 12, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(0, 0, 150, 0) };
        var bar = new TitleBar
        {
            Title = "SiteNotes DPR",
            BackgroundColor = Ui.PrimaryDark,
            ForegroundColor = Colors.White,
            HeightRequest = 40,
            TrailingContent = status,
        };
        void Refresh() => MainThread.BeginInvokeOnMainThread(() =>
        {
            bar.Subtitle = AppServices.CurrentProject?.ToString() ?? "";
            status.Text = AppServices.Sync == null ? "" : SyncBadge.Text(AppServices.Sync.State) + "  " + AppServices.Sync.LastMessage;
        });
        AppServices.ProjectChanged += Refresh;
        SyncBadge.Changed += Refresh;
        return bar;
    }
}

/// <summary>Shown for a moment while the database opens.</summary>
public class LoadingPage : ContentPage
{
    public LoadingPage()
    {
        BackgroundColor = Ui.Primary;
        Content = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center, Spacing = 12,
            Children =
            {
                new Label { Text = "SiteNotes DPR", TextColor = Colors.White, FontSize = 28, FontAttributes = FontAttributes.Bold, HorizontalOptions = LayoutOptions.Center },
                new ActivityIndicator { IsRunning = true, Color = Colors.White },
            }
        };
    }

    private bool _opened;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_opened) return;      // Android can show this page twice while starting
        _opened = true;
        try
        {
            await AppServices.StartAsync();
            SyncBadge.Attach();
            if (Window != null) Window.Page = new AppShell();
            if (Tour.Enabled) _ = Task.Run(Tour.RunAsync);
        }
        catch (Exception ex)
        {
            Content = new Label { Text = "SiteNotes could not start:\n" + ex.Message, TextColor = Colors.White, Margin = 24, VerticalOptions = LayoutOptions.Center };
        }
    }
}
