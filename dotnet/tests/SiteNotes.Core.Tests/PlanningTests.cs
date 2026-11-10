using ClosedXML.Excel;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;

namespace SiteNotes.Core.Tests;

public class PlanningTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sitenotes-plan-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDatabase _db;

    public PlanningTests()
    {
        _db = new LocalDatabase(_dir);
        _db.InitAsync().GetAwaiter().GetResult();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Calendar_counts_working_days_and_holidays()
    {
        var cal = new WorkCalendar { WorkingDays = "12345", Holidays = "2026-11-11" }; // Mon–Fri
        // Mon 9 Nov – Sun 15 Nov 2026: 5 weekdays minus 1 holiday
        Assert.Equal(4, cal.WorkingDaysBetween(new DateTime(2026, 11, 9), new DateTime(2026, 11, 15)));
        Assert.Equal(new DateTime(2026, 11, 13), cal.AddWorkingDays(new DateTime(2026, 11, 9), 4));
    }

    [Fact]
    public void Costs_are_exact_with_overtime_and_day_rates()
    {
        var cal = new WorkCalendar { HoursPerDay = 8, OvertimeFactor = 2m };
        // 10 masons, 10 h, ₹900/day: 8 normal + 2×2 OT = 12 paid h → 10 × 900 × 12/8 = 13,500
        Assert.Equal(13500.00m, CostCalculator.Cost(new ReportManpower { Count = 10, Hours = 10, Rate = 900, Basis = RateBasis.PerDay }, cal));
        // hired JCB 6 h work + 2 h idle at ₹1,250.50/h = 10,004.00
        Assert.Equal(10004.00m, CostCalculator.Cost(new ReportMachine { Count = 1, WorkingHours = 6, IdleHours = 2, Ownership = MachineOwnership.Hired, Rate = 1250.50m, Basis = RateBasis.PerHour }, cal));
        // own machine: idle hours not charged
        Assert.Equal(7503.00m, CostCalculator.Cost(new ReportMachine { Count = 1, WorkingHours = 6, IdleHours = 2, Ownership = MachineOwnership.Own, Rate = 1250.50m, Basis = RateBasis.PerHour }, cal));
        // 45.5 m³ at ₹5,432.10 = 247,160.55
        Assert.Equal(247160.55m, CostCalculator.Cost(new ReportMaterial { Consumed = 45.5, Rate = 5432.10m, Basis = RateBasis.PerUnit }));
    }

    [Fact]
    public async Task Progress_sums_daily_quantities_to_100_percent_with_costs()
    {
        var p = new Project { Name = "Plant X" };
        await _db.SaveAsync(p);
        await _db.SaveAsync(new WorkCalendar { ProjectId = p.Id, HoursPerDay = 8, WorkingDays = "0123456" });
        var plant = new WbsNode { ProjectId = p.Id, Name = "Boiler plant", Level = WbsLevel.Plant, Code = "P1" };
        var unit = new WbsNode { ProjectId = p.Id, Name = "Unit 1", Level = WbsLevel.Unit, Code = "U1", ParentId = plant.Id };
        await _db.SaveAllAsync([plant, unit]);
        var sub = new Subcontractor { ProjectId = p.Id, Name = "Alpha Civil" };
        await _db.SaveAsync(sub);
        var dept = new Department { ProjectId = p.Id, Name = "Civil" };
        await _db.SaveAsync(dept);
        var act = new Activity
        {
            ProjectId = p.Id, Code = "C-101", Name = "Raft concrete", Unit = "m³", PlannedQty = 100, WbsId = unit.Id,
            SubcontractorId = sub.Id, DepartmentId = dept.Id, Method = "Pump", PlannedStart = new DateTime(2026, 11, 1), PlannedFinish = new DateTime(2026, 11, 4),
        };
        await _db.SaveAsync(act);
        await _db.SaveAsync(new ActivityResource { ProjectId = p.Id, ActivityId = act.Id, Type = ResourceType.Material, PlannedQty = 100, Rate = 5000, Basis = RateBasis.PerUnit });

        async Task Day(int day, double qty, int masons)
        {
            var b = await _db.NewReportAsync(p.Id, new DateTime(2026, 11, day), carryForwardOverride: true);
            var row = b.Activities.SingleOrDefault(a => a.ActivityId == act.Id);
            if (row == null) { row = new ReportActivity { ActivityId = act.Id }; b.Activities.Add(row); }
            row.TodayQty = qty;
            b.Manpower.Clear();
            b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = sub.Id, PartyName = sub.Name, Count = masons, Hours = 8, Rate = 800, ActivityId = act.Id, ActivityName = act.Name });
            b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Department, PartyName = "Civil", Count = 2, Hours = 8, Rate = 1500 }); // overhead
            b.Materials.Add(new ReportMaterial { Material = "RMC", Consumed = qty, Rate = 5000, Basis = RateBasis.PerUnit, ActivityId = act.Id });
            await _db.SaveReportAsync(b);
        }
        await Day(1, 30, 10);
        await Day(2, 40, 10);
        await Day(3, 45, 12); // overshoots: capped at 100 %

        var reports = await _db.ListForProjectAsync<DailyReport>(p.Id);
        var day2 = await _db.LoadReportAsync(reports.Single(r => r.Date.Day == 2).Id);
        Assert.Equal(70, day2.Activities.Single().CumulativeQty);            // auto-summed
        Assert.True(day2.Report.TotalCost > 0);

        var pp = await new ProgressService(_db).ComputeAsync(p.Id, asOf: new DateTime(2026, 11, 3));
        var a = pp.Activities.Single();
        Assert.Equal(115, a.DoneQty);
        Assert.Equal(100, a.Percent);
        Assert.Equal(ActivityState.Completed, a.State);
        Assert.Equal("P1 Boiler plant › U1 Unit 1", a.WbsPath);
        Assert.Equal(3, a.DaysWorked);
        Assert.Equal(32 * 800m, a.Actual.Manpower);
        Assert.Equal(115 * 5000m, a.Actual.Material);
        Assert.Equal(500000m, a.PlannedCost);
        Assert.Equal(3 * 2 * 1500m, pp.Unallocated.Manpower);
        Assert.Equal(100, pp.OverallPercent);
        var s = pp.BySubcontractor.Single(x => x.Name == "Alpha Civil");
        Assert.Equal(32, s.ManDays);
        Assert.Contains("115", s.ActivitiesDone);
        Assert.Contains(pp.ByDepartment, d => d.Name == "Civil" && d.Activities == 1);

        using (var ms = new MemoryStream())
        {
            ProgressReports.Pdf(ms, p, pp, "Nov 2026");
            Assert.True(ms.Length > 1000);
        }
        using (var ms = new MemoryStream())
        {
            ProgressReports.Excel(ms, p, pp, "Nov 2026");
            ms.Position = 0;
            using var wb = new XLWorkbook(ms);
            Assert.Equal(115 * 5000.0 + 32 * 800, wb.Worksheet("Cost by activity").Cell(5, 8).GetValue<double>());
        }

        var outDir = Environment.GetEnvironmentVariable("SITENOTES_SAMPLE_DIR");
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            await using var fs = File.Create(Path.Combine(outDir, "progress.pdf"));
            ProgressReports.Pdf(fs, p, pp, "01-Nov-2026 → 03-Nov-2026");
            var d3 = await _db.LoadReportAsync(reports.Single(r => r.Date.Day == 3).Id);
            await using var fs2 = File.Create(Path.Combine(outDir, "dpr-cost.pdf"));
            PdfReports.DailyReport(fs2, d3, _db.MediaPath, await _db.CalendarAsync(p.Id));
        }
    }
}

public class SampleDataTests
{
    [Fact]
    public async Task Sample_project_builds_and_reports()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sitenotes-sample-" + Guid.NewGuid().ToString("N"));
        var db = new LocalDatabase(dir);
        await db.InitAsync();
        var p = await SampleData.CreateAsync(db, new DateTime(2026, 11, 10));
        var reports = await db.ListForProjectAsync<DailyReport>(p.Id);
        Assert.True(reports.Count >= 4);
        Assert.All(reports, r => Assert.True(r.TotalCost > 0));
        var pp = await new ProgressService(db).ComputeAsync(p.Id, asOf: new DateTime(2026, 11, 10));
        Assert.True(pp.OverallPercent > 0);
        Assert.Contains(pp.BySubcontractor, s => s.Name == "Om Rebar Works" && s.ManDays > 0);
        Assert.True(pp.Activities.Single(a => a.Activity.Code == "C-100").DoneQty > 0);
        // The next report automatically lists running activities.
        var next = await db.NewReportAsync(p.Id, new DateTime(2026, 11, 10), carryForwardOverride: true);
        Assert.Contains(next.Activities, a => a.ActivityName.Contains("Excavation"));
        using var ms = new MemoryStream();
        PdfReports.ReportRegister(ms, p, await new ReportQueryService(db).LoadBundlesAsync(new ReportFilter { ProjectId = p.Id }), "All", true, db.MediaPath, await db.CalendarAsync(p.Id));
        Assert.True(ms.Length > 5000);
    }
}

public class ReportOptionsTests
{
    [Fact]
    public async Task Auto_numbering_dates_bulk_load_and_report_options()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sitenotes-opt-" + Guid.NewGuid().ToString("N"));
        var db = new LocalDatabase(dir);
        await db.InitAsync();
        var p = await SampleData.CreateAsync(db, DateTime.Today);

        // numbering: first number asked once, then automatic
        var fresh = new Project { Name = "Fresh" };
        await db.SaveAsync(fresh);
        Assert.True(await db.NeedsFirstReportNoAsync(fresh.Id));
        fresh.FirstReportNo = 120;
        await db.SaveAsync(fresh);
        Assert.False(await db.NeedsFirstReportNoAsync(fresh.Id));
        Assert.Equal(120, await db.NextReportNoAsync(fresh.Id));
        var b = await db.NewReportAsync(fresh.Id, DateTime.Today);
        await db.SaveReportAsync(b);
        Assert.Equal(121, await db.NextReportNoAsync(fresh.Id));
        Assert.Equal(DateTime.Today, await db.NextReportDateAsync(fresh.Id));

        // auto date for the sample: day after the latest report, not later than today
        var next = await db.NextReportDateAsync(p.Id);
        Assert.True(next <= DateTime.Today);
        var latest = (await db.ListForProjectAsync<DailyReport>(p.Id)).Max(r => r.Date);
        Assert.True(next > latest);

        // bulk loader gives the same cumulative figures as the single loader
        var q = new ReportQueryService(db);
        var bulk = await q.LoadBundlesAsync(new ReportFilter { ProjectId = p.Id });
        foreach (var bb in bulk)
        {
            var single = await db.LoadReportAsync(bb.Report.Id);
            foreach (var a in bb.Activities)
                Assert.Equal(single.Activities.Single(x => x.Id == a.Id).CumulativeQty, a.CumulativeQty, 3);
            Assert.Equal(single.Manpower.Count, bb.Manpower.Count);
        }

        // options round-trip and render with logo, header lines, page X of Y, chosen sections
        var logo = Path.Combine(dir, "logo.jpg");
        using (var bmp = new SkiaSharp.SKBitmap(200, 80))
        {
            using var c = new SkiaSharp.SKCanvas(bmp);
            c.Clear(SkiaSharp.SKColors.OrangeRed);
            Imaging.ImageTools.SaveJpeg(bmp, logo);
        }
        var opt = new ReportOptions
        {
            Sections = [DprSection.Summary, DprSection.Manpower, DprSection.Activities, DprSection.Signatures],
            ActivitySort = ActivityRowSort.ProgressHigh,
            ExtraRemarks = "Issued for client review",
            Layout = new PdfLayout { LogoPath = logo, CompanyName = "BuildRight Infra", HeaderLines = "Project ref: WTP/2026/014\nContract: 45/2026", HeaderAlign = TextAlign.Center, FooterText = "Confidential", PageNumbers = PageNumberStyle.PageNofTotal },
        };
        var back = ReportOptions.FromJson(opt.ToJson());
        Assert.Equal(4, back.Sections.Count);
        Assert.Equal(DprSection.Manpower, back.Sections[1]);
        Assert.Equal("BuildRight Infra", back.Layout.CompanyName);

        using var ms = new MemoryStream();
        PdfReports.ReportRegister(ms, p, bulk, "All", true, db.MediaPath, await db.CalendarAsync(p.Id), opt);
        Assert.True(ms.Length > 5000);
        var outDir = Environment.GetEnvironmentVariable("SITENOTES_SAMPLE_DIR");
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            await using var fs = File.Create(Path.Combine(outDir, "dpr-options.pdf"));
            opt.Sections = [.. Enum.GetValues<DprSection>()];
            PdfReports.DailyReport(fs, bulk[0], db.MediaPath, await db.CalendarAsync(p.Id), opt);
        }
    }
}
