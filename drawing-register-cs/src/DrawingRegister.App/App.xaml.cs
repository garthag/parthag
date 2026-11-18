using System.IO;
using System.Windows;
using System.Windows.Threading;
using DrawingRegister.Core;

namespace DrawingRegister.App;

public partial class App : Application
{
    private AppPaths? _paths;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var smokeIndex = Array.IndexOf(args, "--smoke-test");
        string? smokeReport = smokeIndex >= 0 && smokeIndex + 1 < args.Length ? args[smokeIndex + 1] : null;
        if (smokeIndex >= 0)
        {
            Environment.SetEnvironmentVariable("DRAWING_REGISTER_HOME",
                Path.Combine(Path.GetTempPath(), "drawing-register-smoke", Guid.NewGuid().ToString("N")));
        }

        DispatcherUnhandledException += OnUnhandled;
        try
        {
            _paths = AppPaths.Default();
            var db = new Database(_paths.DatabaseFile);
            db.Migrate();
            var window = new MainWindow(_paths, db);
            MainWindow = window;
            window.Show();
            if (smokeIndex >= 0)
            {
                _ = SmokeTest.RunAsync(window, smokeReport);
            }
        }
        catch (Exception ex)
        {
            Log(ex);
            if (smokeIndex >= 0)
            {
                SmokeTest.Report(smokeReport, $"FAIL startup: {ex}");
                Shutdown(1);
                return;
            }

            MessageBox.Show($"Drawing Register could not start:\n\n{ex.Message}", "Drawing Register",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        if (SmokeTest.ReportPath is not null)
        {
            SmokeTest.Report(SmokeTest.ReportPath, $"FAIL\n[FAIL] unhandled: {e.Exception}");
            e.Handled = true;
            Shutdown(1);
            return;
        }

        var message = e.Exception is ValidationException ? e.Exception.Message : $"Unexpected error: {e.Exception.Message}";
        MessageBox.Show(message, "Drawing Register", MessageBoxButton.OK,
            e.Exception is ValidationException ? MessageBoxImage.Warning : MessageBoxImage.Error);
        e.Handled = true;
    }

    private void Log(Exception ex)
    {
        try
        {
            var dir = _paths?.LogsDir ?? Path.GetTempPath();
            File.AppendAllText(Path.Combine(dir, "drawing-register.log"), $"{DateTime.Now:O} {ex}\n\n");
        }
        catch (IOException)
        {
        }
    }
}
