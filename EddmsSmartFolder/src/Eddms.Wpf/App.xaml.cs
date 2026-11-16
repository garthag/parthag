using System.Windows;

namespace Eddms.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "EDMS Drawing Register", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        var login = new LoginWindow();
        if (login.ShowDialog() != true) { Shutdown(); return; }

        var main = new MainWindow(login.Settings, login.Password, login.SyncNow);
        main.Closed += (_, _) => Shutdown();
        main.Show();
    }
}
