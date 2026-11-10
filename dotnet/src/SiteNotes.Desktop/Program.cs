using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;

namespace SiteNotes.Desktop;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application();
        Theme.Apply(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "SiteNotes", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        var splash = new Window
        {
            Width = 360, Height = 160, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Theme.Primary,
            Content = new TextBlock
            {
                Text = "SiteNotes DPR\nopening…", Foreground = Brushes.White, FontSize = 22, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center,
            },
        };
        splash.Loaded += async (_, _) =>
        {
            await Desk.StartAsync();
            var main = new MainWindow();
            app.MainWindow = main;
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
            splash.Close();
        };
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Exit += async (_, _) => await Desk.StopAsync();
        splash.Show();
        app.Run();
    }
}
