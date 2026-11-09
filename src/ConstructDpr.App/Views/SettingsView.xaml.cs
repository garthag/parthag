using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ConstructDpr.Shared.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace ConstructDpr.App.Views;

public partial class SettingsView : UserControl, IRefreshable
{
    public SettingsView() => InitializeComponent();

    public void Refresh()
    {
        UserBox.Text = AppState.Settings.UserName;
        var p = AppState.Project;
        ProjectForm.DataContext = p;
        StartPick.SelectedDate = p.StartDate.ToDateTime(TimeOnly.MinValue);
        EndPick.SelectedDate = p.CompletionDate?.ToDateTime(TimeOnly.MinValue);
        PathText.Text = "Database: " + AppState.DbPath + "\nEverything is stored in this one SQLite file. Back it up regularly (for example to OneDrive or a USB drive).";
    }

    private void OnSaveUser(object sender, RoutedEventArgs e)
    {
        var name = UserBox.Text.Trim();
        if (name.Length < 2) { Dialogs.Error("Enter your name."); return; }
        AppState.Settings.UserName = name;
        AppState.SaveSettings();
        AppState.NotifyChanged();
        Dialogs.Info("Saved.");
    }

    private void OnSaveProject(object sender, RoutedEventArgs e)
    {
        var p = AppState.Project;
        if (StartPick.SelectedDate is null) { Dialogs.Error("Enter the project start date."); return; }
        if (string.IsNullOrWhiteSpace(p.Code) || string.IsNullOrWhiteSpace(p.Name)) { Dialogs.Error("Code and name are required."); return; }
        if (!WorkCalendar.ParseWorkDays(p.WorkDays).Any()) { Dialogs.Error("Enter at least one working day, e.g. 1,2,3,4,5,6"); return; }
        p.StartDate = DateOnly.FromDateTime(StartPick.SelectedDate.Value);
        p.CompletionDate = EndPick.SelectedDate is { } d ? DateOnly.FromDateTime(d) : null;
        AppState.Db.SaveChanges();
        AppState.Schedule.Run(p.Id, AppState.Today);
        AppState.NotifyChanged();
        Dialogs.Info("Project saved and schedule recalculated.");
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppState.DataFolder) { UseShellExecute = true });

    private void OnBackup(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = $"construct-dpr-backup-{DateTime.Now:yyyyMMdd-HHmm}.db", Filter = "SQLite database (*.db)|*.db" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
        AppState.Db.Database.ExecuteSql($"VACUUM INTO {dlg.FileName}"); // consistent copy even while the app is open
        Dialogs.Info("Backup saved to " + dlg.FileName + "\n\nTo restore: close the app and replace construct-dpr.db in the data folder with the backup (renamed).");
    }

    private void OnReset(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("This deletes ALL data in this app on this PC and loads the demo project again.\n\nBack up first if you need the data. Continue?")) return;
        AppState.ResetToDemo();
        Refresh();
    }
}
