using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ConstructDpr.App.Views;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.App;

/// <summary>Screens that reload their data when shown, when the project changes and on live changes.</summary>
public interface IRefreshable { Task RefreshAsync(); }

/// <summary>Screens that save with Ctrl+S.</summary>
public interface ISaveable { Task SaveShortcutAsync(); }

public sealed record ProjectChoice(int Id, string Title);

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Func<UserControl>> _factories = new()
    {
        ["dashboard"] = () => new DashboardView(),
        ["activities"] = () => new ActivitiesView(),
        ["schedule"] = () => new ScheduleView(),
        ["dpr"] = () => new DprView(),
        ["reports"] = () => new ReportsView(),
        ["projects"] = () => new ProjectsView(),
        ["settings"] = () => new SettingsView(),
    };
    private readonly Dictionary<string, UserControl> _views = new();
    private bool _updatingHeader;

    public MainWindow()
    {
        InitializeComponent();
        UpdateHeader();
        AppState.Changed += OnStateChanged;
        AppState.LiveChanged += c => { _ = RefreshCurrentAsync(); };
        if (Host.Content is null) _ = NavigateAsync("dashboard");
        PreviewKeyDown += OnKey;
    }

    /// <summary>Ctrl+S save · F5 reload · Ctrl+1…7 switch screens.</summary>
    private async void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        if (ctrl && e.Key == System.Windows.Input.Key.S && Host.Content is ISaveable s) { e.Handled = true; await s.SaveShortcutAsync(); }
        else if (e.Key == System.Windows.Input.Key.F5) { e.Handled = true; await RefreshCurrentAsync(); }
        else if (ctrl && e.Key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D7)
        {
            var keys = new[] { "dashboard", "dpr", "activities", "schedule", "reports", "projects", "settings" };
            e.Handled = true;
            await NavigateAsync(keys[e.Key - System.Windows.Input.Key.D1]);
        }
    }

    public string Current { get; private set; } = "";

    public async Task<UserControl> NavigateAsync(string key)
    {
        if (!_views.TryGetValue(key, out var view)) _views[key] = view = _factories[key]();
        Current = key;
        Host.Content = view;
        foreach (var rb in Nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == key && rb.IsChecked != true) rb.IsChecked = true;
        if (view is IRefreshable r) await Dialogs.RunAsync(r.RefreshAsync);
        return view;
    }

    private async Task RefreshCurrentAsync()
    {
        if (Host.Content is IRefreshable r) await Dialogs.RunAsync(r.RefreshAsync);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key } && key != Current && IsInitialized) _ = NavigateAsync(key);
    }

    private void OnStateChanged()
    {
        UpdateHeader();
        _ = RefreshCurrentAsync();
    }

    private async void OnProjectPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingHeader || ProjectPicker.SelectedValue is not int id || id == AppState.Pid) return;
        await AppState.OpenProjectAsync(id);
    }

    private void UpdateHeader()
    {
        _updatingHeader = true;
        ProjectPicker.ItemsSource = AppState.Projects.Select(p => new ProjectChoice(p.Id, $"{p.Code} — {p.Name}")).ToList();
        ProjectPicker.SelectedValue = AppState.Project?.Id;
        _updatingHeader = false;
        ProjectSub.Text = AppState.Project is null ? "No project yet: load the demo in Settings" : $"{Permissions.Label(AppState.Role)} · {AppState.Me.DisplayName}";
        DateText.Text = DateTime.Today.ToString("dddd, dd MMM yyyy");
        UserText.Text = AppState.Me.DisplayName;
        ModeText.Text = AppState.ModeLabel;
        var status = AppState.Live.Status;
        LiveText.Text = status switch { "live" => "Live", "office" => "Office · live", "local" => "This PC", "reconnecting" => "Reconnecting…", "connecting" => "Connecting…", _ => "Offline" };
        LiveDot.Fill = (Brush)FindResource(status is "live" or "local" or "office" ? "Good" : status is "reconnecting" or "connecting" ? "Warn" : "Muted");
    }
}
