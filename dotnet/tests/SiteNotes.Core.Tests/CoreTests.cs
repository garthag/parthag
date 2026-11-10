using ClosedXML.Excel;
using SiteNotes.Core;
using SiteNotes.Core.Data;
using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SkiaSharp;

namespace SiteNotes.Core.Tests;

public class NoteDocumentTests
{
    [Fact]
    public void Numbering_restarts_after_other_blocks_and_respects_indent()
    {
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Numbered, Text = "a" },
            new() { Type = BlockType.Numbered, Text = "b" },
            new() { Type = BlockType.Bullet, Text = "sub", Indent = 1 },
            new() { Type = BlockType.Numbered, Text = "c" },
            new() { Type = BlockType.Paragraph, Text = "break" },
            new() { Type = BlockType.Numbered, Text = "again" },
        };
        Assert.Equal("1.", NoteDocument.Marker(blocks, 0));
        Assert.Equal("2.", NoteDocument.Marker(blocks, 1));
        Assert.Equal("3.", NoteDocument.Marker(blocks, 3));
        Assert.Equal("1.", NoteDocument.Marker(blocks, 5));
    }

    [Fact]
    public void Round_trips_json_and_builds_plain_text()
    {
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Heading1, Text = "Slab pour" },
            new() { Type = BlockType.Checklist, Text = "Shuttering checked", Checked = true },
            new() { Type = BlockType.Arrow, Text = "Call RMC", Style = new TextStyle { Bold = true, FontFamily = "Serif", FontSize = 18 } },
        };
        var back = NoteDocument.Parse(NoteDocument.Serialize(blocks));
        Assert.Equal(3, back.Count);
        Assert.True(back[1].Checked);
        Assert.Equal("Serif", back[2].Style.FontFamily);
        Assert.Contains("☑ Shuttering checked", NoteDocument.ToPlainText(back));
        Assert.Equal((1, 1), NoteDocument.ChecklistProgress(back));
    }

    [Fact]
    public void Plain_text_json_falls_back_to_a_paragraph() =>
        Assert.Equal("hello", NoteDocument.Parse("hello")[0].Text);
}

public class DatabaseAndReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sitenotes-test-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDatabase _db;

    public DatabaseAndReportTests()
    {
        _db = new LocalDatabase(_dir) { UserName = "Tester" };
        _db.InitAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* sqlite may still hold the file on some OSes */ }
    }

    internal static async Task<(Project, DprBundle)> SeedAsync(LocalDatabase db, string mediaDir)
    {
        var p = new Project { Name = "G+12 Residential Tower", Code = "RT-01", Client = "ABC Developers", Location = "Pune" };
        await db.SaveAsync(p);
        var sub = new Subcontractor { ProjectId = p.Id, Name = "Shree Formwork" };
        await db.SaveAsync(sub);

        var b = await db.NewReportAsync(p.Id, new DateTime(2026, 11, 9), carryForwardOverride: true);
        b.Activities.Add(new ReportActivity { ActivityName = "Slab casting L5", Unit = "m³", PlannedQty = 120, TodayQty = 45, CumulativeQty = 45, SubcontractorId = sub.Id, SubcontractorName = sub.Name });
        b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = sub.Id, PartyName = sub.Name, Trade = "Carpenter", Count = 12 });
        b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = sub.Id, PartyName = sub.Name, Trade = "Helper", Count = 20 });
        b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Department, PartyName = "Civil", Trade = "Engineer", Count = 3, Hours = 10 });
        b.Machines.Add(new ReportMachine { Name = "Tower crane", Count = 1, WorkingHours = 9 });
        b.Materials.Add(new ReportMaterial { Material = "RMC M30", Unit = "m³", Received = 46, Consumed = 45 });
        b.Hindrances.Add(new Hindrance { Category = "Weather", Description = "Rain stopped pour for 2 hrs", HoursLost = 2 });
        b.Requirements.Add(new Requirement { Item = "Cover blocks 40mm", Quantity = 500, RequiredBy = DateTime.Today.AddDays(-1) });
        b.Report.Remarks = "Pour completed by 6 pm";

        // a real photo on disk
        var photo = Path.Combine(mediaDir, "photo1.jpg");
        using (var bmp = new SKBitmap(400, 300))
        {
            using var c = new SKCanvas(bmp);
            c.Clear(SKColors.SteelBlue);
            ImageTools.SaveJpeg(bmp, photo);
        }
        b.Media.Add(new MediaItem { OwnerType = MediaOwner.Report, OwnerId = b.Report.Id, FileName = "photo1.jpg", Caption = "Slab L5" });
        await db.SaveReportAsync(b);
        await db.SaveAllAsync(b.Media);
        return (p, b);
    }

    [Fact]
    public async Task Saves_report_with_totals_and_carries_forward()
    {
        var (p, b) = await SeedAsync(_db, _db.MediaFolder);
        var loaded = await _db.LoadReportAsync(b.Report.Id);
        Assert.Equal(1, loaded.Report.ReportNo);
        Assert.Equal(35, loaded.Report.TotalManpower);
        Assert.Equal(12 * 8 + 20 * 8 + 3 * 10, loaded.Report.TotalManHours);
        Assert.Equal(1, loaded.Hindrances[0].SerialNo);
        Assert.Single(loaded.Media);

        var next = await _db.NewReportAsync(p.Id, new DateTime(2026, 11, 10), carryForwardOverride: true);
        Assert.Equal(2, next.Report.ReportNo);
        Assert.Equal(3, next.Manpower.Count);
        Assert.Equal(45, next.Activities[0].CumulativeQty);
        Assert.Equal(0, next.Activities[0].TodayQty);

        // removing a row soft-deletes it
        loaded.Manpower.RemoveAt(2);
        await _db.SaveReportAsync(loaded);
        Assert.Equal(32, (await _db.LoadReportAsync(b.Report.Id)).Report.TotalManpower);
    }

    [Fact]
    public async Task Filter_and_sort()
    {
        var (p, _) = await SeedAsync(_db, _db.MediaFolder);
        var b2 = await _db.NewReportAsync(p.Id, new DateTime(2026, 11, 10), carryForwardOverride: false);
        b2.Report.Status = ReportStatus.Approved;
        b2.Manpower.Add(new ReportManpower { PartyName = "X", Count = 99 });
        await _db.SaveReportAsync(b2);

        var q = new ReportQueryService(_db);
        Assert.Equal(2, (await q.FindAsync(new ReportFilter { ProjectId = p.Id })).Count);
        Assert.Equal(99, (await q.FindAsync(new ReportFilter { ProjectId = p.Id, Sort = ReportSort.ManpowerHigh }))[0].TotalManpower);
        Assert.Single(await q.FindAsync(new ReportFilter { ProjectId = p.Id, Status = ReportStatus.Approved }));
        Assert.Single(await q.FindAsync(new ReportFilter { ProjectId = p.Id, Search = "slab" }));
        Assert.Single(await q.FindAsync(new ReportFilter { ProjectId = p.Id, To = new DateTime(2026, 11, 9) }));
    }

    [Fact]
    public async Task Generates_pdfs_and_excel()
    {
        var (p, b) = await SeedAsync(_db, _db.MediaFolder);
        var bundle = await _db.LoadReportAsync(b.Report.Id);

        using (var ms = new MemoryStream())
        {
            PdfReports.DailyReport(ms, bundle, _db.MediaPath);
            AssertPdf(ms);
        }
        using (var ms = new MemoryStream())
        {
            PdfReports.ReportRegister(ms, p, [bundle, bundle], "Nov 2026", includeFullReports: true, _db.MediaPath);
            AssertPdf(ms);
        }
        using (var ms = new MemoryStream())
        {
            PdfReports.HindranceRegister(ms, p, bundle.Hindrances, "All");
            AssertPdf(ms);
        }
        using (var ms = new MemoryStream())
        {
            var blocks = new List<NoteBlock>
            {
                new() { Type = BlockType.Heading1, Text = "Site visit" },
                new() { Type = BlockType.Bullet, Text = "Column C4 honeycomb – repair" },
                new() { Type = BlockType.Checklist, Text = "Inform QA", Checked = true },
                new() { Type = BlockType.Divider },
                new() { Type = BlockType.Image, MediaId = bundle.Media[0].Id },
            };
            PdfReports.Note(ms, new Note { Title = "Visit" }, blocks, "Inspections", id => bundle.Media.FirstOrDefault(m => m.Id == id), _db.MediaPath);
            AssertPdf(ms);
        }

        using (var ms = new MemoryStream())
        {
            ExcelReports.ReportWorkbook(ms, p, [bundle], "All reports");
            ms.Position = 0;
            using var wb = new XLWorkbook(ms);
            Assert.True(wb.Worksheets.Contains("DPR Summary"));
            Assert.True(wb.Worksheets.Contains("Manpower Grid"));
            Assert.Equal(35, wb.Worksheet("DPR Summary").Cell(5, 12).GetValue<int>());
            Assert.Equal("Shree Formwork", wb.Worksheet("Manpower").Cell(5, 4).GetString());
        }
    }

    [Fact]
    public void Scanner_filters_produce_pages_and_pdf()
    {
        var src = Path.Combine(_dir, "raw.jpg");
        using (var bmp = new SKBitmap(600, 800))
        {
            using var c = new SKCanvas(bmp);
            c.Clear(new SKColor(200, 190, 170));
            using var ink = new SKPaint { Color = new SKColor(40, 40, 40) };
            c.DrawRect(50, 100, 500, 20, ink);
            ImageTools.SaveJpeg(bmp, src);
        }
        var pages = new List<string>();
        foreach (var f in Enum.GetValues<ScanFilter>())
        {
            var dest = Path.Combine(_dir, $"page-{f}.jpg");
            Assert.True(ScanProcessor.Process(src, dest, f, f == ScanFilter.BlackWhite ? 90 : 0));
            pages.Add(dest);
        }
        using var bw = ImageTools.LoadUpright(pages[^1])!;
        Assert.Equal(800, bw.Width); // rotated 90°

        using var ms = new MemoryStream();
        PdfReports.ScanPages(ms, "Delivery challan", pages);
        AssertPdf(ms);
    }

    private static void AssertPdf(MemoryStream ms)
    {
        var bytes = ms.ToArray();
        Assert.True(bytes.Length > 1000, "PDF too small");
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }
}

/// <summary>Writes sample reports to $SITENOTES_SAMPLE_DIR for a visual check (skipped otherwise).</summary>
public class SampleOutput
{
    [Fact]
    public async Task Write_samples_when_requested()
    {
        var outDir = Environment.GetEnvironmentVariable("SITENOTES_SAMPLE_DIR");
        if (string.IsNullOrEmpty(outDir)) return;
        Directory.CreateDirectory(outDir);
        var db = new LocalDatabase(Path.Combine(outDir, "data")) { UserName = "R. Patil" };
        await db.InitAsync();
        var (p, b) = await DatabaseAndReportTests.SeedAsync(db, db.MediaFolder);
        var bundle = await db.LoadReportAsync(b.Report.Id);
        await using (var fs = File.Create(Path.Combine(outDir, "dpr.pdf"))) PdfReports.DailyReport(fs, bundle, db.MediaPath);
        await using (var fs = File.Create(Path.Combine(outDir, "register.pdf"))) PdfReports.ReportRegister(fs, p, [bundle], "01-Nov-2026 → 10-Nov-2026", false, db.MediaPath);
        await using (var fs = File.Create(Path.Combine(outDir, "dpr.xlsx"))) ExcelReports.ReportWorkbook(fs, p, [bundle], "All");
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Heading1, Text = "Slab L5 pre-pour inspection" },
            new() { Type = BlockType.Heading2, Text = "Checklist" },
            new() { Type = BlockType.Checklist, Text = "Shuttering level & props checked", Checked = true },
            new() { Type = BlockType.Checklist, Text = "Cover blocks placed" },
            new() { Type = BlockType.Heading3, Text = "Observations" },
            new() { Type = BlockType.Bullet, Text = "Honeycomb at column C4 – repair with polymer mortar" },
            new() { Type = BlockType.Bullet, Text = "Lap length short at beam B12", Indent = 1, Style = new TextStyle { Color = "#B91C1C", Bold = true } },
            new() { Type = BlockType.Numbered, Text = "Inform QA" },
            new() { Type = BlockType.Numbered, Text = "Re-inspect at 4 pm", Style = new TextStyle { Underline = true } },
            new() { Type = BlockType.Arrow, Text = "RMC ordered for 7 am", Style = new TextStyle { FontFamily = "Serif", Italic = true } },
            new() { Type = BlockType.Quote, Text = "Client: proceed after QA sign-off." },
            new() { Type = BlockType.Divider },
            new() { Type = BlockType.Image, MediaId = bundle.Media[0].Id, Text = "Column C4" },
        };
        await using (var fs = File.Create(Path.Combine(outDir, "note.pdf")))
            PdfReports.Note(fs, new Note { Title = "Inspection notes", Tags = "QA, slab" }, blocks, "Inspections", id => bundle.Media.FirstOrDefault(m => m.Id == id), db.MediaPath);
    }
}

/// <summary>Builds the Android screens PDF from the emulator screenshots (CI, when ANDROID_SHOTS_DIR is set).</summary>
public class AndroidScreensPdf
{
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["dashboard"] = "Dashboard – project, quick actions, key figures, 14-day manpower",
        ["menu"] = "Side menu – every module",
        ["daily-reports"] = "Daily reports – search, period & status chips, sort, select for bulk actions",
        ["dpr-editor"] = "Daily progress report – live totals, sections, today's quantity → progress auto",
        ["progress-cost"] = "Progress & cost – overall %, activity cards with forecast and cost",
        ["drawing-register"] = "Drawing register – quantity, allocated %, completed %, status per drawing",
        ["activities"] = "Activities",
        ["activity-form"] = "Activity – WBS, method, agency, schedule, calculated progress panel, resources",
        ["wbs"] = "WBS – plant / unit / area",
        ["resources-rates"] = "Resources & rates",
        ["work-calendar"] = "Work calendar",
        ["hindrance-register"] = "Hindrance register",
        ["requirement-register"] = "Requirement register",
        ["notes"] = "Notes & scans – folders, pinned notes, scans",
        ["note-editor"] = "Note editor – formatting toolbox, checklist, bullets, numbering, arrows",
        ["scanner"] = "Scan to PDF – pages, filter, rotate, reorder, remarks",
        ["report-manager"] = "Report manager – report, days, sections & order, header/footer, page numbers",
        ["subcontractors"] = "Subcontractors",
        ["projects"] = "Projects",
        ["settings"] = "Settings & real-time sync",
    };

    [Fact]
    public void Build_when_requested()
    {
        var dir = Environment.GetEnvironmentVariable("ANDROID_SHOTS_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        var shots = Directory.GetFiles(dir, "*.png").OrderBy(f => f).ToList();
        Assert.NotEmpty(shots);
        using var fs = File.Create(Path.Combine(dir, "SiteNotes-Android-Screens.pdf"));
        PdfDoc.Render(fs, "SiteNotes DPR – Android app", "All screens", false, new PdfLayout { PageNumbers = PageNumberStyle.PageNofTotal, FooterText = "Captured on an Android emulator from the real app with the sample project" }, pdf =>
        {
            pdf.NewPage();
            pdf.Text("SiteNotes DPR – Android screens", 20, bold: true, color: PdfDoc.Accent, spacingAfter: 8);
            pdf.Text("Captured from the real Android app (emulator) with the built-in sample project.", 10, color: PdfDoc.Muted, spacingAfter: 10);
            for (var i = 0; i < shots.Count; i += 2)
            {
                pdf.NewPage();
                var pair = shots.Skip(i).Take(2).Select(f =>
                {
                    var key = string.Join('-', Path.GetFileNameWithoutExtension(f).Split('-').Skip(1));
                    return (f, Titles.GetValueOrDefault(key, key));
                });
                pdf.ImageGrid(pair, 2, 640);
            }
        });
    }
}
