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
            ("06-dpr-photos", async () =>
            {
                // A draft for tonight with three demo pictures (drawn by the app: illustrations, not real site photos).
                var d = await AppState.Api.CreateDprAsync(AppState.Pid, AppState.Today, Shift.Night);
                string[] captions = ["Demo image: truss T3 lifted into place", "Demo image: footing F6 after pour", "Demo image: crane CR-01 at grid C"];
                for (var i = 0; i < captions.Length; i++)
                    await AppState.Api.AddPhotoAsync(AppState.Pid, d.Id, $"demo-{i + 1}.jpg", DemoPicture(i), captions[i]);
                var view = (DprView)await window.NavigateAsync("dpr");
                await view.OpenAsync(d.Id);
                view.ShowTab("Photos");
                await Task.Delay(800);
            }),
            ("07-reports-excel", () => window.NavigateAsync("reports")),
            ("08-projects-people", () => window.NavigateAsync("projects")),
            ("09-settings", () => window.NavigateAsync("settings")),
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

    /// <summary>A simple drawn site scene used as a demo picture (sky, ground, a steel truss, a crane).</summary>
    public static byte[] DemoPicture(int variant)
    {
        const int w = 1200, h = 800;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            var sky = new LinearGradientBrush(Color.FromRgb(0x9C, 0xC9, 0xEA), Color.FromRgb(0xE8, 0xF2, 0xF8), 90);
            dc.DrawRectangle(sky, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8D, 0x7B, 0x63)), null, new Rect(0, h * 0.72, w, h * 0.28));
            var steel = new Pen(new SolidColorBrush(Color.FromRgb(0xC7, 0x5B, 0x12)), 10);
            var y0 = 300.0 + variant * 40;
            for (var i = 0; i < 6; i++)
            {
                var x = 150 + i * 160;
                dc.DrawLine(steel, new Point(x, y0), new Point(x + 160, y0));
                dc.DrawLine(steel, new Point(x, y0 + 120), new Point(x + 160, y0 + 120));
                dc.DrawLine(steel, new Point(x, y0), new Point(x + 160, y0 + 120));
                dc.DrawLine(steel, new Point(x, y0), new Point(x, y0 + 120));
            }
            dc.DrawLine(steel, new Point(1110, y0), new Point(1110, y0 + 120));
            var crane = new Pen(new SolidColorBrush(Color.FromRgb(0xF5, 0xA5, 0x24)), 14);
            dc.DrawLine(crane, new Point(90, h * 0.72), new Point(90, 90));
            dc.DrawLine(crane, new Point(90, 100), new Point(760 - variant * 120, 140));
            dc.DrawLine(new Pen(Brushes.Black, 3), new Point(700 - variant * 120, 138), new Point(700 - variant * 120, y0));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)), null, new Rect(300, h * 0.62, 600, 50 + variant * 10));
            var label = new FormattedText($"DEMO IMAGE {variant + 1}", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 36, Brushes.White, 1.0);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), null, new Rect(20, h - 70, label.Width + 30, 56));
            dc.DrawText(label, new Point(35, h - 62));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
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
