// Writes sample reports from the demo project: dotnet run --project tools/ConstructDpr.Preview -- <output folder>
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Model;
using ConstructDpr.Core.Services;
using ConstructDpr.Reports;

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "preview");
Directory.CreateDirectory(outDir);
var dbPath = Path.Combine(Path.GetTempPath(), $"cdpr-preview-{Guid.NewGuid():N}.db");
var today = DateOnly.FromDateTime(DateTime.Today);
using (var db = AppDbContext.OpenSqlite(dbPath))
{
    var p = DemoData.Create(db, today);
    var svc = new DprService(db, "planning.engineer");
    var acts = db.Activities.Where(a => a.ProjectId == p.Id).OrderBy(a => a.SortOrder).ToList();
    var deps = db.Dependencies.Where(d => d.ProjectId == p.Id).ToList();
    var approved = svc.Load(db.Dprs.Where(d => d.Status == DprStatus.Approved).OrderByDescending(d => d.ReportDate).First().Id);
    void Write(string name, byte[] bytes) { File.WriteAllBytes(Path.Combine(outDir, name), bytes); Console.WriteLine($"{name,-34} {bytes.Length / 1024,6} KB"); }
    Write("sample-dpr.pdf", DprPdfReport.Generate(approved, p));
    Write("sample-schedule.pdf", SchedulePdfReport.Generate(p, acts, deps, today));
    Write("sample-schedule.xlsx", ExcelService.ExportSchedule(p, acts));
    Write("sample-dpr.xlsx", ExcelService.ExportDpr(approved, p));
    Write("activity-import-template.xlsx", ExcelService.ActivityTemplate());
}
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
File.Delete(dbPath);
