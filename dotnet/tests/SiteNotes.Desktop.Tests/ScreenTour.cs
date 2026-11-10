using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Ribbon;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SiteNotes.Core;
using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Desktop;
using SiteNotes.Desktop.Services;
using SiteNotes.Desktop.UI;
using SiteNotes.Desktop.Views;
using SkiaSharp;

namespace SiteNotes.Desktop.Tests;

/// <summary>
/// Opens every screen of the real Windows app with a sample project and saves a picture of each,
/// plus one PDF with all of them. Runs in CI when SCREENSHOT_DIR is set.
/// </summary>
public class ScreenTour
{
    private static readonly List<(string File, string Title)> Shots = [];

    [Fact]
    public void Capture_every_screen() => Sta.Run(async () =>
    {
        // Also the "every screen opens" smoke test: always runs; pictures go to SCREENSHOT_DIR when set.
        var dir = Environment.GetEnvironmentVariable("SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Path.GetTempPath(), "SiteNotesScreens");
        Directory.CreateDirectory(dir);

        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Apply(app);
        await Desk.StartAsync();
        var p = await SampleData.CreateAsync(Desk.Db);
        await Desk.SetProjectAsync(p);
        await SeedExtrasAsync(p);

        var main = new MainWindow { Width = 1500, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
        app.MainWindow = main;
        main.Show();
        await main.FillProjectsAsync();
        await Settle();

        async Task Show(IView v, string file, string title, Func<Task>? after = null)
        {
            main.Open((UIElement)v);
            await v.RefreshAsync();
            await Settle();
            if (after != null) { await after(); await Settle(); }
            Capture(main, dir, file, title);
        }

        await Show(new DashboardView(), "01-dashboard", "Dashboard – key figures, 14-day manpower, needs attention, recent reports");
        await Show(new ReportsView(), "02-daily-reports", "Daily reports – filters, status chips, column sorting, multi-select and bulk actions");

        var report = (await Desk.Db.ListForProjectAsync<DailyReport>(p.Id)).OrderByDescending(r => r.Date).First();
        var editor = new DprEditorView(report.Id);
        main.Open(editor);
        await editor.RefreshAsync();
        await Settle();
        var tabs = Find<TabControl>(editor);
        string[] tabNames = ["Work progress (today's qty → to date, balance, % auto)", "Manpower – subcontractor & department, activity, cost", "Machinery – own / hired, hours, fuel, cost",
            "Materials & non-material – consumed, activity, cost", "Hindrances", "Requirements", "Safety, toolbox talk, plan for tomorrow, remarks", "Photos & videos"];
        for (var i = 0; i < tabs!.Items.Count; i++)
        {
            tabs.SelectedIndex = i;
            await Settle();
            Capture(main, dir, $"03-dpr-{i + 1}", $"Daily progress report – {tabNames[Math.Min(i, tabNames.Length - 1)]}");
        }

        await Show(new ActivitiesView(), "04-activities-progress", "Activities & progress – plan + calculated progress, forecast, cost per unit/day, budget; grouped by WBS",
            async () =>
            {
                var g = Find<DataGrid>(main.CurrentView as DependencyObject);
                if (g?.Items.Count > 0) g.SelectedIndex = 1;
                await Task.CompletedTask;
            });
        await Show(new WbsView(), "05-wbs", "WBS – Project › Plant › Unit › Area › System › Work package",
            async () => { var t = Find<TreeView>(main.CurrentView as DependencyObject); if (t?.Items.Count > 0 && t.Items[0] is TreeViewItem ti) ti.IsSelected = true; await Task.CompletedTask; });
        await Show(MasterViews.Resources(), "06-resources-rates", "Resources & rates – manpower, machinery, material, non-material; per hour / day / unit / lump sum");
        await Show(new CalendarView(), "07-work-calendar", "Work calendar – hours per day, working days, holidays, overtime factor, currency");
        await Show(MasterViews.Subcontractors(), "08-subcontractors", "Subcontractors");
        await Show(MasterViews.Departments(), "09-departments", "Departments");
        await Show(new ProjectsView(), "10-projects", "Projects – report prefix, first report number (asked once), copy-forward");
        await Show(RegisterViews.Hindrances(), "11-hindrance-register", "Hindrance register");
        await Show(RegisterViews.Requirements(), "12-requirement-register", "Requirement register");
        await Show(new NotesView(), "13-notes", "Notes – folders, list, rich editor (headings, bullets, numbering, arrows, checklists, fonts, colours, pictures)",
            async () =>
            {
                var list = FindAll<ListBox>(main.CurrentView as DependencyObject).FirstOrDefault(l => l.Items.Count > 0);
                if (list != null) list.SelectedIndex = 0;
                await Task.Delay(300);
            });
        var scan = (await Desk.Db.ListForProjectAsync<ScanDocument>(p.Id)).First();
        await Show(new ScannerView(scan.Id), "14-scan-to-pdf", "Scan to PDF – pages, clean-up filters, rotate, reorder, remarks per page");
        await Show(new ReportManagerView(), "15-report-manager", "Report manager – report type, days, sections & order, sorting, logos, header lines, footer, page numbers");
        await Show(new SettingsView(), "16-settings-sync", "Settings & real-time sync");

        var ribbon = Find<Ribbon>(main);
        string[] ribbonNames = ["Home", "Planning & cost", "Registers & masters", "Notes & scans", "Reports", "Settings"];
        for (var i = 1; ribbon != null && i < ribbon.Items.Count; i++)
        {
            ribbon.SelectedIndex = i;
            await Settle();
            Capture(main, dir, $"17-ribbon-{i}", $"Ribbon tab: {ribbonNames[i]}");
        }

        // a sample of the generated reports, rendered from the real PDF engine
        var bundle = await Desk.Db.LoadReportAsync(report.Id);
        var opt = await Desk.ReportOptionsAsync();
        await using (var fs = File.Create(Path.Combine(dir, "sample-daily-report.pdf")))
            PdfReports.DailyReport(fs, bundle, Desk.Db.MediaPath, await Desk.Db.CalendarAsync(p.Id), opt);
        var pp = await new ProgressService(Desk.Db).ComputeAsync(p.Id);
        await using (var fs = File.Create(Path.Combine(dir, "sample-progress-cost.pdf")))
            ProgressReports.Pdf(fs, p, pp, "Sample project", true, opt);

        // the editor still saves after all that navigation
        await editor.SaveAsync();
        Assert.False(editor.IsDirty);
        Assert.True(Shots.Count >= 20, $"only {Shots.Count} screens captured");

        BuildPdf(dir);
        main.Close();
    });

    /// <summary>A note, a scanned document, photos on a report and report settings with a logo, so screens are not empty.</summary>
    private static async Task SeedExtrasAsync(Project p)
    {
        string Pic(string name, SKColor a, SKColor b, string label)
        {
            var path = Path.Combine(Path.GetTempPath(), name);
            using var bmp = new SKBitmap(800, 600);
            using (var c = new SKCanvas(bmp))
            {
                using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(800, 600), [a, b], SKShaderTileMode.Clamp);
                using var paint = new SKPaint { Shader = shader };
                c.DrawRect(0, 0, 800, 600, paint);
                using var font = new SKFont(SKTypeface.Default, 48);
                using var ink = new SKPaint { Color = SKColors.White };
                c.DrawText(label, 40, 320, SKTextAlign.Left, font, ink);
            }
            ImageTools.SaveJpeg(bmp, path);
            return path;
        }
        var report = (await Desk.Db.ListForProjectAsync<DailyReport>(p.Id)).OrderByDescending(r => r.Date).First();
        await Desk.ImportMediaAsync(Pic("s1.jpg", SKColor.Parse("#1F4E79"), SKColor.Parse("#5B8DB8"), "Raft reinforcement"), MediaOwner.Report, report.Id, MediaKind.Photo, "Raft reinforcement – grid C/4");
        await Desk.ImportMediaAsync(Pic("s2.jpg", SKColor.Parse("#92400E"), SKColor.Parse("#F59E0B"), "Excavation"), MediaOwner.Report, report.Id, MediaKind.Photo, "Excavation – clarifier");

        var folder = new Folder { ProjectId = p.Id, Name = "Inspections" };
        await Desk.Db.SaveAsync(folder);
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Heading1, Text = "Raft pre-pour inspection" },
            new() { Type = BlockType.Heading2, Text = "Checklist" },
            new() { Type = BlockType.Checklist, Text = "Shuttering level & props checked", Checked = true },
            new() { Type = BlockType.Checklist, Text = "Cover blocks 50 mm placed", Checked = true },
            new() { Type = BlockType.Checklist, Text = "Pump & standby vibrators ready" },
            new() { Type = BlockType.Heading2, Text = "Observations" },
            new() { Type = BlockType.Bullet, Text = "Honeycomb at column C4 – repair with polymer mortar" },
            new() { Type = BlockType.Bullet, Text = "Lap length short at beam B12", Style = new TextStyle { Bold = true, Color = "#B91C1C" } },
            new() { Type = BlockType.Numbered, Text = "Inform QA/QC" },
            new() { Type = BlockType.Numbered, Text = "Re-inspect at 4 pm", Style = new TextStyle { Underline = true } },
            new() { Type = BlockType.Arrow, Text = "RMC M30 ordered for 7 am", Style = new TextStyle { FontFamily = "Serif", Italic = true } },
            new() { Type = BlockType.Quote, Text = "Client: proceed after QA sign-off." },
            new() { Type = BlockType.Paragraph, Text = "Highlighted note", Style = new TextStyle { Highlight = "#FEF08A" } },
        };
        await Desk.Db.SaveAsync(new Note { ProjectId = p.Id, FolderId = folder.Id, Title = "Raft pre-pour inspection", Tags = "QA, raft", ContentJson = NoteDocument.Serialize(blocks), PlainText = NoteDocument.ToPlainText(blocks), IsPinned = true });

        var pages = new List<ScanPageInput>
        {
            new() { RawPath = Pic("p1.jpg", SKColor.Parse("#E5E7EB"), SKColor.Parse("#D1D5DB"), "Delivery challan p1"), Remarks = "RMC 46 m³" },
            new() { RawPath = Pic("p2.jpg", SKColor.Parse("#E5E7EB"), SKColor.Parse("#CBD5E1"), "Delivery challan p2"), Remarks = "Signed by store" },
        };
        await new ScanService(Desk.Db, Path.Combine(Desk.DataFolder, "scanraw")).SaveAsync(new ScanDocument { ProjectId = p.Id, Title = "RMC delivery challan" }, pages, ScanFilter.Enhanced);

        var (profile, o) = await Desk.LoadReportOptionsAsync();
        var logo = await Desk.ImportMediaAsync(Pic("logo.jpg", SKColor.Parse("#F59E0B"), SKColor.Parse("#B45309"), "BuildRight"), MediaOwner.Note, "logo", MediaKind.Document, "Logo");
        o.LogoFile = logo.FileName;
        o.Layout.CompanyName = "BuildRight Infra Pvt Ltd";
        o.Layout.HeaderLines = "Project ref: WTP/2026/014\nContract no: 45/2026";
        o.Layout.FooterText = "Confidential – for client review";
        await Desk.SaveReportOptionsAsync(profile, o);
    }

    private static async Task Settle()
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Task.Delay(400);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static void Capture(Window w, string dir, string file, string title)
    {
        w.UpdateLayout();
        var content = (FrameworkElement)w.Content;
        var width = (int)Math.Max(1, content.ActualWidth);
        var height = (int)Math.Max(1, content.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Theme.Page, null, new Rect(0, 0, width, height));
        rtb.Render(bg);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(Path.Combine(dir, file + ".png"))) enc.Save(fs);
        Shots.Add((file + ".png", title));
    }

    private static void BuildPdf(string dir)
    {
        using var fs = File.Create(Path.Combine(dir, "SiteNotes-Windows-Screens.pdf"));
        PdfDoc.Render(fs, "SiteNotes DPR – Windows app", "All screens", true, new PdfLayout { PageNumbers = PageNumberStyle.PageNofTotal, FooterText = "Screens captured from the real app with the sample project" }, pdf =>
        {
            pdf.NewPage();
            pdf.Text("SiteNotes DPR – Windows (WPF) screens", 22, bold: true, color: PdfDoc.Accent, spacingAfter: 8);
            pdf.Text("Every screen below was captured from the real Windows app, running the built-in sample project.", 11, color: PdfDoc.Muted, spacingAfter: 12);
            foreach (var (f, t) in Shots) pdf.Text("• " + t, 10, spacingAfter: 2);
            foreach (var (f, t) in Shots)
            {
                pdf.NewPage();
                pdf.Text(t, 12, bold: true, color: PdfDoc.Accent, spacingAfter: 6);
                pdf.Image(Path.Combine(dir, f), 470);
            }
        });
    }

    private static T? Find<T>(DependencyObject? root) where T : DependencyObject => FindAll<T>(root).FirstOrDefault();

    private static IEnumerable<T> FindAll<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root == null) yield break;
        if (root is T t) yield return t;
        var n = root is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(root) : 0;
        for (var i = 0; i < n; i++)
            foreach (var x in FindAll<T>(VisualTreeHelper.GetChild(root, i))) yield return x;
    }
}
