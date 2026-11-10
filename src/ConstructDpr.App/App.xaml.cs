using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ConstructDpr.App;

public partial class App : Application
{
    private static string? _shotDir;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUnhandled;

        var args = e.Args;
        var shotIndex = Array.IndexOf(args, "--screenshots");
        _shotDir = shotIndex >= 0 && shotIndex + 1 < args.Length ? Path.GetFullPath(args[shotIndex + 1]) : null;
        if (_shotDir is not null) Directory.CreateDirectory(_shotDir);
        var dataIndex = Array.IndexOf(args, "--data");
        var dataFolder = dataIndex >= 0 && dataIndex + 1 < args.Length
            ? Path.GetFullPath(args[dataIndex + 1])
            : _shotDir is not null
                ? Path.Combine(Path.GetTempPath(), "ConstructDpr-screenshots-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConstructDpr");

        try
        {
            AppState.LoadSettings(dataFolder);
            if (_shotDir is not null) AppState.Settings.Mode = DataMode.Local; // screenshots always use a fresh offline demo
            var ready = await AppState.StartAsync();
            while (!ready)
            {
                // Office or server mode without a valid sign-in.
                Window signIn = AppState.Mode == DataMode.Office ? new OfficeConnectWindow() : new SignInWindow(AppState.Session);
                if (signIn.ShowDialog() == true) { ready = await AppState.StartAsync(); continue; }
                if (MessageBox.Show("Use CONSTRUCT DPR on this PC only (offline, no account) instead?", "CONSTRUCT DPR", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    Shutdown(0);
                    return;
                }
                await AppState.SwitchModeAsync(DataMode.Local);
                ready = true;
            }
        }
        catch (Exception ex) when (_shotDir is not null)
        {
            File.WriteAllText(Path.Combine(_shotDir, "error.txt"), ex.ToString());
            Shutdown(3);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show("CONSTRUCT DPR could not start:\n\n" + ex.GetBaseException().Message + "\n\nData folder: " + dataFolder,
                "CONSTRUCT DPR", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        if (_shotDir is not null) Screenshots.Capture(window, _shotDir);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (_shotDir is not null)
        {
            // Unattended screenshot run: never block on a dialog; record the error and fail the run.
            File.WriteAllText(Path.Combine(_shotDir, "error.txt"), e.Exception.ToString());
            Shutdown(2);
            return;
        }
        Dialogs.Error(e.Exception.GetBaseException().Message);
    }
}
