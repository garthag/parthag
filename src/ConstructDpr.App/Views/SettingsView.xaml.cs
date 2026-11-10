using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace ConstructDpr.App.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    private Project? _project;
    private bool _loading;

    public SettingsView() => InitializeComponent();

    public async Task RefreshAsync()
    {
        _loading = true;
        LocalMode.IsChecked = AppState.Mode == DataMode.Local;
        OfficeMode.IsChecked = AppState.Mode == DataMode.Office;
        ServerMode.IsChecked = AppState.Mode == DataMode.Server;
        _loading = false;
        var signedIn = AppState.Mode switch { DataMode.Server => AppState.Session.IsSignedIn, DataMode.Office => AppState.OfficeAccounts is not null && AppState.Me.Id != "local", _ => false };
        ConnectionText.Text = AppState.Mode switch
        {
            DataMode.Server => signedIn ? $"Signed in to {AppState.Session.ServerUrl} as {AppState.Me.Email}. Changes appear on every device within seconds." : "Not signed in.",
            DataMode.Office => $"SQL Server {AppState.Settings.OfficeServer}, database {AppState.Settings.OfficeDatabase} · signed in as {AppState.Me.Email}. Colleagues' changes appear within a few seconds.",
            _ => "Data: " + AppState.DbPath,
        };
        SignInBtn.Visibility = AppState.HasAccounts && !signedIn ? Visibility.Visible : Visibility.Collapsed;
        SignOutBtn.Visibility = AppState.HasAccounts && signedIn ? Visibility.Visible : Visibility.Collapsed;
        PasswordBtn.Visibility = AppState.Mode == DataMode.Office && signedIn ? Visibility.Visible : Visibility.Collapsed;
        DataCard.Visibility = AppState.Mode == DataMode.Local ? Visibility.Visible : Visibility.Collapsed;
        UserBox.Text = AppState.Me.DisplayName;
        PathText.Text = "Database: " + AppState.DbPath + "\nEverything is stored in this one SQLite file. Back it up regularly (for example to OneDrive or a USB drive).";

        _project = AppState.Pid == 0 ? null : await AppState.Api.ProjectAsync(AppState.Pid);
        ProjectForm.DataContext = _project;
        ProjectForm.IsEnabled = _project is not null && AppState.Can(Module.Project, Act.Edit);
        StartPick.SelectedDate = _project?.StartDate.ToDateTime(TimeOnly.MinValue);
        EndPick.SelectedDate = _project?.CompletionDate?.ToDateTime(TimeOnly.MinValue);
    }

    private async void OnMode(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = OfficeMode.IsChecked == true ? DataMode.Office : ServerMode.IsChecked == true ? DataMode.Server : DataMode.Local;
        if (mode == AppState.Settings.Mode) return;
        var previous = AppState.Settings.Mode;
        if (mode == DataMode.Office)
        {
            if (new OfficeConnectWindow { Owner = Window.GetWindow(this) }.ShowDialog() != true) { await RefreshAsync(); return; }
        }
        else if (mode == DataMode.Server)
        {
            await AppState.Session.LoadAsync();
            if (!AppState.Session.IsSignedIn && new SignInWindow(AppState.Session) { Owner = Window.GetWindow(this) }.ShowDialog() != true)
            {
                await RefreshAsync(); // stay where we were
                return;
            }
        }
        var ok = false;
        await Dialogs.RunAsync(async () => ok = await AppState.SwitchModeAsync(mode));
        if (!ok) await Dialogs.RunAsync(() => AppState.SwitchModeAsync(previous));
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        Window w = AppState.Mode == DataMode.Office ? new OfficeConnectWindow() : new SignInWindow(AppState.Session);
        w.Owner = Window.GetWindow(this);
        if (w.ShowDialog() == true) await Dialogs.RunAsync(AppState.StartAsync);
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Sign out on this PC?")) return;
        await AppState.SignOutAsync();
        await RefreshAsync();
    }

    private async void OnPassword(object sender, RoutedEventArgs e)
    {
        var current = Dialogs.Prompt("Change password", "Current password:", password: true);
        if (current is null) return;
        var next = Dialogs.Prompt("Change password", "New password (at least 8 characters):", password: true);
        if (next is null) return;
        await Dialogs.RunAsync(() => Task.Run(() => AppState.OfficeAccounts!.ChangePassword(AppState.Me.Id, current, next)), "Password changed.");
    }

    private async void OnDemo(object sender, RoutedEventArgs e)
    {
        ConstructDpr.Shared.Api.ProjectSummary? p = null;
        if (await Dialogs.RunAsync(async () => p = await AppState.Api.CreateDemoProjectAsync()))
        {
            await AppState.RefreshProjectsAsync();
            await AppState.OpenProjectAsync(p!.Id);
        }
    }

    private async void OnSaveUser(object sender, RoutedEventArgs e)
    {
        var name = UserBox.Text.Trim();
        if (name.Length < 2) { Dialogs.Error("Enter your name."); return; }
        if (AppState.Mode == DataMode.Server)
        {
            if (!await Dialogs.RunAsync(() => AppState.Session.UpdateProfileAsync(name), "Saved.")) return;
        }
        else if (AppState.Mode == DataMode.Office)
        {
            if (!await Dialogs.RunAsync(() => Task.Run(() => AppState.OfficeAccounts!.Rename(AppState.Me.Id, name)), "Saved.")) return;
        }
        else
        {
            AppState.Settings.UserName = name;
            AppState.SaveSettings();
        }
        await Dialogs.RunAsync(AppState.StartAsync);
    }

    private async void OnSaveProject(object sender, RoutedEventArgs e)
    {
        if (_project is null) return;
        if (StartPick.SelectedDate is null) { Dialogs.Error("Enter the project start date."); return; }
        _project.StartDate = DateOnly.FromDateTime(StartPick.SelectedDate.Value);
        _project.CompletionDate = EndPick.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
        if (await Dialogs.RunAsync(async () => _project = await AppState.Api.SaveProjectAsync(_project), "Project saved and schedule recalculated."))
            await AppState.RefreshProjectsAsync();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppState.DataFolder) { UseShellExecute = true });

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = $"construct-dpr-backup-{DateTime.Now:yyyyMMdd-HHmm}.db", Filter = "SQLite database (*.db)|*.db" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
        using (var source = new SqliteConnection($"Data Source={AppState.DbPath}"))
        using (var target = new SqliteConnection($"Data Source={dlg.FileName}"))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target); // consistent copy even while the app is open
        }
        SqliteConnection.ClearAllPools();
        Dialogs.Info("Backup saved to " + dlg.FileName + "\n\nTo restore: close the app and replace construct-dpr.db in the data folder with the backup (renamed).");
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("This deletes ALL data in this app on this PC and loads the demo project again.\n\nBack up first if you need the data. Continue?")) return;
        await Dialogs.RunAsync(AppState.ResetLocalToDemoAsync);
    }
}
