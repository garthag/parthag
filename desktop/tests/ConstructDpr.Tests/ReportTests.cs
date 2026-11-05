using System.Text;
using ClosedXML.Excel;
using ConstructDpr.Core.Model;
using ConstructDpr.Core.Services;
using ConstructDpr.Reports;

namespace ConstructDpr.Tests;

public class ReportTests
{
    private static bool IsPdf(byte[] b) => b.Length > 1000 && Encoding.ASCII.GetString(b, 0, 5) == "%PDF-";

    [Fact]
    public void Dpr_and_schedule_pdfs_are_generated()
    {
        using var t = new DemoDb();
        var svc = new DprService(t.Db, "x");
        foreach (var d in t.Db.Dprs.ToList().Select(d => svc.Load(d.Id)))
            Assert.True(IsPdf(DprPdfReport.Generate(d, t.Project)), d.Number);
        Assert.True(IsPdf(SchedulePdfReport.Generate(t.Project, t.Db.Activities.ToList(), t.Db.Dependencies.ToList(), t.Today)));
    }

    [Fact]
    public void Pdf_does_not_fail_on_text_the_font_lacks()
    {
        using var t = new DemoDb();
        t.Db.Activities.First().Name = "Footing ◆ नींव ₹ 足場";
        t.Db.SaveChanges();
        Assert.True(IsPdf(SchedulePdfReport.Generate(t.Project, t.Db.Activities.ToList(), t.Db.Dependencies.ToList(), t.Today)));
    }

    [Fact]
    public void Schedule_workbook_contains_activities_and_formulas()
    {
        using var t = new DemoDb();
        var acts = t.Db.Activities.ToList();
        using var wb = new XLWorkbook(new MemoryStream(ExcelService.ExportSchedule(t.Project, acts)));
        var ws = wb.Worksheets.First();
        var codes = ws.Column(1).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.All(acts, a => Assert.Contains(a.Code, codes));
        Assert.Contains(wb.Worksheets, s => s.CellsUsed().Any(c => c.HasFormula));
    }

    [Fact]
    public void Dpr_workbook_is_readable()
    {
        using var t = new DemoDb();
        var d = new DprService(t.Db, "x").Load(t.Db.Dprs.First().Id);
        using var wb = new XLWorkbook(new MemoryStream(ExcelService.ExportDpr(d, t.Project)));
        Assert.Contains(wb.Worksheets.SelectMany(s => s.CellsUsed()), c => c.GetString().Contains(d.Number));
    }

    [Fact]
    public void Template_round_trip_import_updates_and_adds()
    {
        using var t = new DemoDb();
        var before = t.Db.Activities.Count();
        using var wb = new XLWorkbook(new MemoryStream(ExcelService.ActivityTemplate()));
        var ws = wb.Worksheets.First();
        ws.Cell(2, 1).Value = "A1000"; ws.Cell(2, 2).Value = "Excavation (renamed)"; ws.Cell(2, 6).Value = 5000;
        ws.Cell(3, 1).Value = "A9000"; ws.Cell(3, 2).Value = "Painting"; ws.Cell(3, 5).Value = "m²"; ws.Cell(3, 6).Value = 800; ws.Cell(3, 9).Value = 10;
        var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
        var result = ExcelService.ImportActivities(t.Db, t.Project.Id, ms);
        Assert.Empty(result.Errors);
        Assert.Equal((1, 1), (result.Created, result.Updated));
        Assert.Equal(before + 1, t.Db.Activities.Count());
        Assert.Equal("Excavation (renamed)", t.Db.Activities.Single(a => a.Code == "A1000").Name);
    }

    [Fact]
    public void Import_with_errors_writes_nothing()
    {
        using var t = new DemoDb();
        using var wb = new XLWorkbook(new MemoryStream(ExcelService.ActivityTemplate()));
        var ws = wb.Worksheets.First();
        ws.Cell(2, 1).Value = "A1000"; ws.Cell(2, 2).Value = "Excavation"; ws.Cell(2, 6).Value = 10; // below approved qty
        ws.Cell(3, 1).Value = "A9100"; ws.Cell(3, 2).Value = "Bad"; ws.Cell(3, 6).Value = "lots";
        var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
        var result = ExcelService.ImportActivities(t.Db, t.Project.Id, ms);
        Assert.Equal(2, result.Errors.Count);
        Assert.False(t.Db.Activities.Any(a => a.Code == "A9100"));
    }
}
