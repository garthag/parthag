using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Controls.Ribbon;
using System.Windows.Input;
using System.Windows.Media;
using SiteNotes.Core.Models;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;
using SiteNotes.Desktop.Views;

namespace SiteNotes.Desktop;

/// <summary>A screen shown in a document tab.</summary>
public interface IView
{
    string Title { get; }
    /// <summary>Same key = same tab (re-opening focuses the existing tab).</summary>
    string Key { get; }
    Task RefreshAsync();
}

/// <summary>Views that can save with Ctrl+S.</summary>
public interface ISaveable { Task SaveAsync(); bool IsDirty { get; } }

/// <summary>Views that can print a PDF with Ctrl+P.</summary>
public interface IPrintable { Task PdfAsync(); }

/// <summary>
/// Main window: Ribbon (title bar + grouped tools), document tabs (several reports open side by side),
/// status bar (project, user, sync, last action). Keyboard: Ctrl+N new DPR, Ctrl+S save, Ctrl+P PDF,
/// Ctrl+W close tab, F5 refresh.
/// </summary>
public class MainWindow : Window
{
    private readonly TabControl _tabs = new() { Padding = new Thickness(0), BorderThickness = new Thickness(0, 1, 0, 0) };
    private readonly ComboBox _project = new() { Width = 230, Margin = new Thickness(4) };
    private readonly TextBlock _statusProject = new();
    private readonly TextBlock _statusSync = new();
    private readonly TextBlock _statusUser = new();
    private readonly TextBlock _statusMessage = new() { Foreground = Theme.Muted };

    public static MainWindow Current => (MainWindow)Application.Current.MainWindow;

    public MainWindow()
    {
        Title = "SiteNotes DPR";
        Width = 1400;
        Height = 860;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/sitenotes.ico")); } catch { /* icon optional */ }

        var dock = new DockPanel();
        var ribbon = BuildRibbon();
        DockPanel.SetDock(ribbon, Dock.Top);
        dock.Children.Add(ribbon);
        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        dock.Children.Add(status);
        dock.Children.Add(_tabs);
        Content = dock;

        InputBindings.Add(Shortcut(Key.N, ModifierKeys.Control, () => DprFlow.NewAsync()));
        InputBindings.Add(Shortcut(Key.S, ModifierKeys.Control, () => CurrentView is ISaveable s ? s.SaveAsync() : Task.CompletedTask));
        InputBindings.Add(Shortcut(Key.P, ModifierKeys.Control, () => CurrentView is IPrintable p ? p.PdfAsync() : Task.CompletedTask));
        InputBindings.Add(Shortcut(Key.W, ModifierKeys.Control, () => { CloseTab(_tabs.SelectedItem as TabItem); return Task.CompletedTask; }));
        InputBindings.Add(Shortcut(Key.F5, ModifierKeys.None, () => CurrentView?.RefreshAsync() ?? Task.CompletedTask));

        Desk.ProjectChanged += OnProjectChanged;
        Desk.SyncStatusChanged += UpdateStatus;
        Desk.Status += m => _statusMessage.Text = m;
        Desk.DataChanged += async () => { if (CurrentView is { } v and not ISaveable) await v.RefreshAsync(); };
        Closing += async (_, e) =>
        {
            if (_closingConfirmed) return;
            var dirty = _tabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ISaveable>().Where(s => s.IsDirty).ToList();
            if (dirty.Count == 0) return;
            e.Cancel = true;
            var r = Dialogs.Headless ? MessageBoxResult.Yes : MessageBox.Show(this, $"{dirty.Count} open item(s) have unsaved changes. Save them?", "SiteNotes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) foreach (var s in dirty) await Dialogs.Guard(s.SaveAsync);
            _closingConfirmed = true;
            Close();
        };
        Loaded += async (_, _) =>
        {
            await FillProjectsAsync();
            UpdateStatus();
            Open(new DashboardView());
        };
    }

    private static KeyBinding Shortcut(Key key, ModifierKeys mods, Func<Task> action) =>
        new(new RelayCommand(() => _ = Dialogs.Guard(action)), key, mods);

    private bool _closingConfirmed;

    public IView? CurrentView => (_tabs.SelectedItem as TabItem)?.Content as IView;

    // ------------------------------------------------------------------ ribbon

    private Ribbon BuildRibbon()
    {
        var r = new Ribbon { ApplicationMenu = new RibbonApplicationMenu { Visibility = Visibility.Collapsed } };
        var home = new RibbonTab { Header = "Home" };
        var planning = new RibbonTab { Header = "Planning & cost" };
        var registers = new RibbonTab { Header = "Registers & masters" };
        var notes = new RibbonTab { Header = "Notes & scans" };
        var reports = new RibbonTab { Header = "Reports" };
        var tools = new RibbonTab { Header = "Settings" };

        var projGroup = new RibbonGroup { Header = "Project" };
        _project.SelectionChanged += async (_, _) => { if (_project.SelectedItem is Project p && p.Id != Desk.ProjectId) await Desk.SetProjectAsync(p); };
        projGroup.Items.Add(_project);
        projGroup.Items.Add(Small("New project", Glyph.Add, () => Open(new ProjectsView(), "new")));
        projGroup.Items.Add(Small("Edit projects", Glyph.Edit, () => Open(new ProjectsView())));
        home.Items.Add(projGroup);
        home.Items.Add(Group("Daily report",
            Large("New DPR", Glyph.Add, () => DprFlow.NewAsync()),
            Large("Today's DPR", Glyph.Today, () => DprFlow.TodayAsync()),
            Large("All reports", Glyph.Table, () => Open(new ReportsView()))));
        home.Items.Add(Group("Site",
            Large("Dashboard", Glyph.Home, () => Open(new DashboardView())),
            Large("Progress & cost", Glyph.Chart, () => Open(new ActivitiesView())),
            Large("Hindrances", Glyph.Warning, () => Open(RegisterViews.Hindrances())),
            Large("Requirements", Glyph.Package, () => Open(RegisterViews.Requirements()))));
        home.Items.Add(Group("Notes", Large("Notes", Glyph.Note, () => Open(new NotesView())), Large("Scan to PDF", Glyph.Scan, () => Open(new ScannerView(null)))));
        home.Items.Add(Group("Output", Large("Report manager", Glyph.Print, () => Open(new ReportManagerView())), Large("Sync now", Glyph.Sync, SyncNowAsync)));

        planning.Items.Add(Group("Plan",
            Large("Activities & progress", Glyph.Chart, () => Open(new ActivitiesView())),
            Large("WBS (plant / unit)", Glyph.Hierarchy, () => Open(new WbsView())),
            Large("Resources & rates", Glyph.Calculator, () => Open(MasterViews.Resources())),
            Large("Work calendar", Glyph.Calendar, () => Open(new CalendarView()))));

        registers.Items.Add(Group("Registers", Large("Hindrances", Glyph.Warning, () => Open(RegisterViews.Hindrances())), Large("Requirements", Glyph.Package, () => Open(RegisterViews.Requirements()))));
        registers.Items.Add(Group("Masters",
            Large("Subcontractors", Glyph.People, () => Open(MasterViews.Subcontractors())),
            Large("Departments", Glyph.Org, () => Open(MasterViews.Departments())),
            Large("Projects", Glyph.Folder, () => Open(new ProjectsView()))));

        notes.Items.Add(Group("Notes", Large("Notes", Glyph.Note, () => Open(new NotesView())), Large("Scan to PDF", Glyph.Scan, () => Open(new ScannerView(null)))));

        reports.Items.Add(Group("Reports",
            Large("Report manager", Glyph.Print, () => Open(new ReportManagerView())),
            Large("All reports", Glyph.Table, () => Open(new ReportsView())),
            Large("Open reports folder", Glyph.Folder, () => Dialogs.Open(Desk.ReportsFolder))));

        tools.Items.Add(Group("Settings", Large("Settings & sync", Glyph.Settings, () => Open(new SettingsView())), Large("Sync now", Glyph.Sync, SyncNowAsync)));

        foreach (var t in new[] { home, planning, registers, notes, reports, tools }) r.Items.Add(t);
        return r;
    }

    private static RibbonGroup Group(string header, params object[] items)
    {
        var g = new RibbonGroup { Header = header };
        foreach (var i in items) g.Items.Add(i);
        return g;
    }

    private static RibbonButton Large(string label, string glyph, Action action) => Large(label, glyph, () => { action(); return Task.CompletedTask; });

    private static RibbonButton Large(string label, string glyph, Func<Task> action)
    {
        var b = new RibbonButton { Label = label, LargeImageSource = Theme.Icon(glyph, 32), SmallImageSource = Theme.Icon(glyph, 16), ToolTipTitle = label };
        b.Click += async (_, _) => await Dialogs.Guard(action);
        return b;
    }

    private static RibbonButton Small(string label, string glyph, Action action)
    {
        var b = new RibbonButton { Label = label, SmallImageSource = Theme.Icon(glyph, 16) };
        b.Click += (_, _) => action();
        return b;
    }

    // ------------------------------------------------------------------ status bar

    private StatusBar BuildStatusBar()
    {
        var s = new StatusBar { Background = Theme.PrimaryDark, Foreground = Brushes.White, Padding = new Thickness(6, 2, 6, 2) };
        foreach (var t in new[] { _statusProject, _statusUser, _statusSync }) t.Foreground = Brushes.White;
        _statusMessage.Foreground = new SolidColorBrush(Color.FromRgb(0xC7, 0xD7, 0xEA));
        s.Items.Add(new StatusBarItem { Content = _statusProject });
        s.Items.Add(new Separator());
        s.Items.Add(new StatusBarItem { Content = _statusUser });
        s.Items.Add(new Separator());
        s.Items.Add(new StatusBarItem { Content = _statusSync });
        s.Items.Add(new Separator());
        s.Items.Add(new StatusBarItem { Content = _statusMessage });
        s.Items.Add(new StatusBarItem { HorizontalAlignment = HorizontalAlignment.Right, Content = new TextBlock { Text = "Ctrl+N new DPR · Ctrl+S save · Ctrl+P PDF · Ctrl+W close", Foreground = Brushes.LightGray } });
        return s;
    }

    private void UpdateStatus()
    {
        _statusProject.Text = Desk.Project == null ? "No project" : $"Project: {Desk.Project}";
        _statusUser.Text = $"User: {Desk.Db.UserName}";
        _statusSync.Text = Desk.SyncText + (Desk.Sync.LastSyncLocal is { } t ? $" · {t:HH:mm}" : "");
        _statusSync.ToolTip = Desk.Sync.LastMessage;
        Title = Desk.Project == null ? "SiteNotes DPR" : $"SiteNotes DPR – {Desk.Project.Name}";
    }

    public async Task FillProjectsAsync()
    {
        var list = (await Desk.Db.ListAsync<Project>()).OrderBy(p => p.Name).ToList();
        _project.ItemsSource = list;
        _project.SelectedItem = list.FirstOrDefault(p => p.Id == Desk.ProjectId);
    }

    private async void OnProjectChanged()
    {
        UpdateStatus();
        await FillProjectsAsync();
        // project-specific tabs are closed so nothing shows another project's data
        foreach (var t in _tabs.Items.OfType<TabItem>().Where(t => t.Content is not (DashboardView or SettingsView or ProjectsView)).ToList()) CloseTab(t, force: true);
        foreach (var t in _tabs.Items.OfType<TabItem>()) if (t.Content is IView v) await v.RefreshAsync();
    }

    private static async Task SyncNowAsync()
    {
        if (!Desk.SyncSettings.Enabled) { Dialogs.Info("Sync is off. Add your server in Settings & sync to share data with phones and other PCs in real time."); return; }
        await Desk.Sync.SyncNowAsync();
        Desk.Say(Desk.Sync.LastMessage);
    }

    // ------------------------------------------------------------------ tabs

    public void Open(UIElement view, string? arg = null)
    {
        if (view is not IView v) return;
        if (Desk.Project == null && view is not (ProjectsView or SettingsView or DashboardView))
        {
            if (Dialogs.Confirm("Create a project first. Every report, note and register belongs to a project.\n\nCreate one now?"))
                Open(new ProjectsView(), "new");
            return;
        }
        var existing = _tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content is IView o && o.Key == v.Key);
        if (existing != null)
        {
            _tabs.SelectedItem = existing;
            if (arg == "new" && existing.Content is ProjectsView pv) pv.AddNew();
            return;
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = v.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Content = "✕", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 11 };
        header.Children.Add(title);
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view, ToolTip = v.Title };
        close.Click += (_, _) => CloseTab(tab);
        header.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
        if (arg == "new" && view is ProjectsView p) p.AddNew();
    }

    /// <summary>Updates a tab's caption (e.g. after a new report is saved and numbered).</summary>
    public void Retitle(IView v)
    {
        var tab = _tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content == v);
        if (tab?.Header is StackPanel { Children: [TextBlock t, ..] }) t.Text = v.Title;
    }

    public void CloseTab(TabItem? tab, bool force = false)
    {
        if (tab == null) return;
        if (!force && tab.Content is ISaveable { IsDirty: true } s)
        {
            var r = Dialogs.Headless ? MessageBoxResult.Yes : MessageBox.Show(this, "Save changes before closing?", "SiteNotes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) { _ = SaveThenCloseAsync(tab, s); return; }
        }
        _tabs.Items.Remove(tab);
    }

    private async Task SaveThenCloseAsync(TabItem tab, ISaveable s)
    {
        await Dialogs.Guard(s.SaveAsync);
        if (!s.IsDirty) _tabs.Items.Remove(tab);
    }

    public void CloseView(IView v) => CloseTab(_tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content == v), force: true);
}

public sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
