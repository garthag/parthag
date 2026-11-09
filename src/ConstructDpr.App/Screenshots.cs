using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConstructDpr.App.Views;
using ConstructDpr.Shared.Model;

namespace ConstructDpr.App;

/// <summary>
/// "--screenshots &lt;folder&gt;": opens every screen on a fresh demo database, saves a PNG of each and exits.
/// Used by the build pipeline to publish an up-to-date preview of the app.
/// </summary>
public static class Screenshots
{
    public static async void Capture(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder);
        window.WindowState = WindowState.Normal;
        var dprs = await AppState.Api.DprsAsync(AppState.Pid);
        var steps = new List<(string File, Func<Task> Show)>
        {
            ("01-dashboard", () => window.NavigateAsync("dashboard")),
            ("02-activities", async () => ((ActivitiesView)await window.NavigateAsync("activities")).Select("A2010")),
            ("03-schedule-gantt", () => window.NavigateAsync("schedule")),
            ("04-dpr-review", async () =>
            {
                var view = (DprView)await window.NavigateAsync("dpr");
                if (dprs.FirstOrDefault(d => d.Status == DprStatus.Submitted) is { } p) await view.OpenAsync(p.Id);
            }),
            ("05-dpr-approved", async () =>
            {
                var view = (DprView)await window.NavigateAsync("dpr");
                if (dprs.FirstOrDefault(d => d.Status == DprStatus.Approved) is { } a) await view.OpenAsync(a.Id);
            }),
            ("06-reports-excel", () => window.NavigateAsync("reports")),
            ("07-settings", () => window.NavigateAsync("settings")),
        };
        foreach (var (file, show) in steps)
        {
            await show();
            await Task.Delay(700); // let async loads and layout settle
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Save(window, Path.Combine(folder, file + ".png"));
        }
        Application.Current.Shutdown(0);
    }

    private static void Save(Window window, string path)
    {
        // Lay the page out at a fixed 1440×860 regardless of the runner's screen size.
        var root = (FrameworkElement)window.Content;
        var size = new Size(1440, 860);
        if (root is System.Windows.Controls.Panel panel) panel.Background = window.Background; // page colour, not transparent
        // Measure/Arrange run synchronously; render straight away, before the window lays it out again.
        root.Measure(size);
        root.Arrange(new Rect(size));
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(size.Width * dpi.DpiScaleX), (int)(size.Height * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
