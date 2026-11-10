using SiteNotes.App.Pages;
using SiteNotes.Core;
using SiteNotes.Core.Models;

namespace SiteNotes.App.Services;

/// <summary>
/// Screenshot tour (started with the launch extra "tour"): loads the sample project and shows every
/// screen for a few seconds. CI takes a screenshot whenever "SITENOTES_TOUR READY n name" is logged.
/// </summary>
public static class Tour
{
    public static bool Enabled { get; set; }

    private static void Log(string text)
    {
        Console.WriteLine("SITENOTES_TOUR " + text);
#if ANDROID
        Android.Util.Log.Info("SiteNotesTour", "SITENOTES_TOUR " + text);
#endif
    }

    public static async Task RunAsync()
    {
        Log("START 00-start");
        try
        {
            await Task.Delay(1500);
            var p = await SampleData.CreateAsync(AppServices.Db);
            await AppServices.SetProjectAsync(p);
            var (noteId, scanId) = await SeedAsync(p);
            var report = (await AppServices.Db.ListForProjectAsync<DailyReport>(p.Id)).OrderByDescending(r => r.Date).First();
            var activity = (await AppServices.Db.ListForProjectAsync<Activity>(p.Id)).OrderBy(a => a.Code).Skip(1).First();
            var n = 0;

            async Task Go(string route, string name)
            {
                await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("//" + route));
                await Ready(++n, name);
            }
            async Task Push(Func<Page> page, string name)
            {
                await MainThread.InvokeOnMainThreadAsync(() => AppShell.Push(page()));
                await Ready(++n, name);
                await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.Navigation.PopAsync());
            }

            await Go("dashboard", "dashboard");
            await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.FlyoutIsPresented = true);
            await Ready(++n, "menu");
            await MainThread.InvokeOnMainThreadAsync(() => Shell.Current.FlyoutIsPresented = false);
            await Go("reports", "daily-reports");
            await Push(() => new DprEditorPage(report.Id), "dpr-editor");
            await Go("progress", "progress-cost");
            await Go("activities", "activities");
            await Push(() => EntityForms.ActivityForm(activity), "activity-form");
            await Go("wbs", "wbs");
            await Go("resources", "resources-rates");
            await Go("calendar", "work-calendar");
            await Go("hindrances", "hindrance-register");
            await Go("requirements", "requirement-register");
            await Go("notes", "notes");
            await Push(() => new NoteEditorPage(noteId, ""), "note-editor");
            await Push(() => new ScannerPage(scanId, ""), "scanner");
            await Go("reportcentre", "report-manager");
            await Go("subcontractors", "subcontractors");
            await Go("projects", "projects");
            await Go("settings", "settings");
            Log("DONE 0 end");
        }
        catch (Exception ex)
        {
            Log("DONE 0 error " + ex.Message.Replace('\n', ' '));
        }
    }

    private static async Task Ready(int n, string name)
    {
        await Task.Delay(3500);              // let the page load and render
        Log($"READY {n:00}-{name}");
        await Task.Delay(3000);              // time for the screenshot
    }

    private static async Task<(string NoteId, string ScanId)> SeedAsync(Project p)
    {
        var db = AppServices.Db;
        var folder = new Folder { ProjectId = p.Id, Name = "Inspections" };
        await db.SaveAsync(folder);
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Heading1, Text = "Raft pre-pour inspection" },
            new() { Type = BlockType.Checklist, Text = "Shuttering level & props checked", Checked = true },
            new() { Type = BlockType.Checklist, Text = "Pump & standby vibrators ready" },
            new() { Type = BlockType.Heading2, Text = "Observations" },
            new() { Type = BlockType.Bullet, Text = "Honeycomb at column C4 – repair" },
            new() { Type = BlockType.Bullet, Text = "Lap length short at beam B12", Style = new TextStyle { Bold = true, Color = "#B91C1C" } },
            new() { Type = BlockType.Numbered, Text = "Inform QA/QC" },
            new() { Type = BlockType.Numbered, Text = "Re-inspect at 4 pm", Style = new TextStyle { Underline = true } },
            new() { Type = BlockType.Arrow, Text = "RMC M30 ordered for 7 am" },
            new() { Type = BlockType.Quote, Text = "Client: proceed after QA sign-off." },
        };
        var note = new Note { ProjectId = p.Id, FolderId = folder.Id, Title = "Raft pre-pour inspection", Tags = "QA", ContentJson = NoteDocument.Serialize(blocks), PlainText = NoteDocument.ToPlainText(blocks), IsPinned = true };
        await db.SaveAsync(note);

        var pages = new List<ScanPageInput>();
        for (var i = 1; i <= 2; i++)
        {
            var path = Path.Combine(FileSystem.CacheDirectory, $"tourpage{i}.jpg");
            using var bmp = new SkiaSharp.SKBitmap(600, 800);
            using (var c = new SkiaSharp.SKCanvas(bmp))
            {
                c.Clear(new SkiaSharp.SKColor(235, 232, 225));
                using var ink = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(60, 60, 60) };
                for (var y = 120; y < 700; y += 40) c.DrawRect(60, y, 480, 6, ink);
            }
            Core.Imaging.ImageTools.SaveJpeg(bmp, path);
            pages.Add(new ScanPageInput { RawPath = path, Remarks = i == 1 ? "RMC 46 m³" : "Signed by store" });
        }
        var doc = await new ScanService(db, Path.Combine(FileSystem.AppDataDirectory, "scanraw"))
            .SaveAsync(new ScanDocument { ProjectId = p.Id, Title = "RMC delivery challan" }, pages, ScanFilter.Enhanced);
        return (note.Id, doc.Id);
    }
}
