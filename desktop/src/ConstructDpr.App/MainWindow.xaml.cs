using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ConstructDpr.App.Views;

namespace ConstructDpr.App;

/// <summary>Screens that reload their data when shown or when other screens change data.</summary>
public interface IRefreshable { void Refresh(); }

public partial class MainWindow : Window
{
    private readonly Dictionary<string, Func<UserControl>> _factories = new()
    {
        ["dashboard"] = () => new DashboardView(),
        ["activities"] = () => new ActivitiesView(),
        ["schedule"] = () => new ScheduleView(),
        ["dpr"] = () => new DprView(),
        ["reports"] = () => new ReportsView(),
        ["settings"] = () => new SettingsView(),
    };
    private readonly Dictionary<string, UserControl> _views = new();

    public MainWindow()
    {
        InitializeComponent();
        UpdateHeader();
        AppState.Changed += UpdateHeader;
        if (Host.Content is null) Navigate("dashboard");
    }

    public string Current { get; private set; } = "";

    public UserControl Navigate(string key)
    {
        if (!_views.TryGetValue(key, out var view)) _views[key] = view = _factories[key]();
        Current = key;
        Host.Content = view;
        if (view is IRefreshable r) r.Refresh();
        foreach (var rb in Nav.Children.OfType<RadioButton>())
            if ((string)rb.Tag == key && rb.IsChecked != true) rb.IsChecked = true;
        return view;
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string key } && key != Current && IsInitialized) Navigate(key);
    }

    private void UpdateHeader()
    {
        var p = AppState.Project;
        ProjectText.Text = $"{p.Code} — {p.Name}";
        ProjectSub.Text = string.Join(" · ", new[] { p.Client, p.Contractor, p.WorkOrderNo is null ? null : "WO " + p.WorkOrderNo }.Where(s => !string.IsNullOrWhiteSpace(s)));
        DateText.Text = DateTime.Today.ToString("dddd, dd MMM yyyy");
        UserText.Text = AppState.Settings.UserName;
    }
}
