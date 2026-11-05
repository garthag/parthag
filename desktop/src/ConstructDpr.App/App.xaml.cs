using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ConstructDpr.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        var args = e.Args;
        var shotIndex = Array.IndexOf(args, "--screenshots");
        var shotDir = _shotDir = shotIndex >= 0 && shotIndex + 1 < args.Length ? Path.GetFullPath(args[shotIndex + 1]) : null;
        if (shotDir is not null) Directory.CreateDirectory(shotDir);
        var dataIndex = Array.IndexOf(args, "--data");
        var dataFolder = dataIndex >= 0 && dataIndex + 1 < args.Length
            ? Path.GetFullPath(args[dataIndex + 1])
            : shotDir is not null
                ? Path.Combine(Path.GetTempPath(), "ConstructDpr-screenshots-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConstructDpr");

        try { AppState.Initialise(dataFolder); }
        catch (Exception ex) when (shotDir is not null)
        {
            File.WriteAllText(Path.Combine(shotDir, "error.txt"), ex.ToString());
            Shutdown(3);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show("The database could not be opened:\n\n" + ex.Message + "\n\nFolder: " + dataFolder,
                "CONSTRUCT DPR", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (shotDir is not null) Screenshots.Capture(window, shotDir);
    }

    private static string? _shotDir;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_shotDir is not null)
        {
            // Unattended screenshot run: never block on a dialog; record the error and fail the run.
            File.WriteAllText(Path.Combine(_shotDir, "error.txt"), e.Exception.ToString());
            e.Handled = true;
            Shutdown(2);
            return;
        }
        Dialogs.Error(e.Exception.GetBaseException().Message);
        e.Handled = true;
    }
}
