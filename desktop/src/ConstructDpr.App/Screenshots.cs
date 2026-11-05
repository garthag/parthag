using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ConstructDpr.App.Views;
using ConstructDpr.Core.Model;

namespace ConstructDpr.App;

/// <summary>
/// "--screenshots &lt;folder&gt;": opens every screen on a fresh demo database, saves a PNG of each and exits.
/// Used by the build pipeline to publish an up-to-date preview of the app.
/// </summary>
public static class Screenshots
{
    public static void Capture(MainWindow window, string folder)
    {
        Directory.CreateDirectory(folder);
        window.WindowState = WindowState.Normal;
        window.Width = 1440;
        window.Height = 900;
        var steps = new List<(string File, Action Show)>
        {
            ("01-dashboard", () => window.Navigate("dashboard")),
            ("02-activities", () => ((ActivitiesView)window.Navigate("activities")).Select("A2010")),
            ("03-schedule-gantt", () => window.Navigate("schedule")),
            ("04-dpr-review", () =>
            {
                var view = (DprView)window.Navigate("dpr");
                var pending = AppState.Db.Dprs.Where(d => d.Status == DprStatus.Submitted).Select(d => d.Id).FirstOrDefault();
                if (pending > 0) view.Open(pending);
            }),
            ("05-dpr-approved", () =>
            {
                var view = (DprView)window.Navigate("dpr");
                var approved = AppState.Db.Dprs.Where(d => d.Status == DprStatus.Approved).OrderByDescending(d => d.ReportDate).Select(d => d.Id).FirstOrDefault();
                if (approved > 0) view.Open(approved);
            }),
            ("06-reports-excel", () => window.Navigate("reports")),
            ("07-settings", () => window.Navigate("settings")),
        };
        var i = 0;
        void Next()
        {
            if (i >= steps.Count) { Application.Current.Shutdown(0); return; }
            var (file, show) = steps[i++];
            show();
            // Let layout and rendering settle, then capture.
            window.Dispatcher.BeginInvoke(() =>
            {
                Save(window, Path.Combine(folder, file + ".png"));
                window.Dispatcher.BeginInvoke(Next, DispatcherPriority.ApplicationIdle);
            }, DispatcherPriority.ApplicationIdle);
        }
        window.Dispatcher.BeginInvoke(Next, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        // Lay the page out at a fixed 1440×860 regardless of the runner's screen size.
        var root = (FrameworkElement)window.Content;
        var size = new Size(1440, 860);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
