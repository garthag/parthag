using ClosedXML.Excel;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core.Tests;

public class ExchangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sitenotes-xchg-" + Guid.NewGuid().ToString("N"));
    private readonly LocalDatabase _db;
    private readonly Project _p = new() { Name = "P" };

    public ExchangeTests()
    {
        _db = new LocalDatabase(_dir);
        _db.InitAsync().GetAwaiter().GetResult();
        _db.SaveAsync(_p).GetAwaiter().GetResult();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static MemoryStream Edit(MemoryStream exported, Action<IXLWorksheet> edit)
    {
        exported.Position = 0;
        using var wb = new XLWorkbook(exported);
        edit(wb.Worksheets.First());
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Activities_round_trip_edit_add_and_keep_everything_else()
    {
        var sub = new Subcontractor { ProjectId = _p.Id, Name = "Alpha Constructions" };
        var wbs = new WbsNode { ProjectId = _p.Id, Code = "U1", Name = "Unit 1" };
        var a1 = new Activity { ProjectId = _p.Id, Code = "A-01", Name = "Excavation", Unit = "m³", PlannedQty = 1200.5, PlannedStart = new DateTime(2026, 1, 5), SubcontractorId = sub.Id, WbsId = wbs.Id, Weightage = 10 };
        var a2 = new Activity { ProjectId = _p.Id, Code = "A-02", Name = "PCC", Unit = "m³", PlannedQty = 80 };
        await _db.SaveAllAsync<SyncEntity>([sub, wbs]);
        await _db.SaveAllAsync([a1, a2]);
        var x = new ExchangeSheets(_db);
        var sheet = await x.ActivitiesAsync(_p.Id);

        var ms = new MemoryStream();
        DataExchange.Export(ms, sheet, [a2, a1]); // any order
        // unchanged file → nothing to do
        ms.Position = 0;
        var same = DataExchange.Import(ms, "a.xlsx", sheet, await _db.ListForProjectAsync<Activity>(_p.Id));
        Assert.Equal((0, 0, 2), (same.Added.Count, same.Updated.Count, same.Unchanged));
        Assert.Empty(same.Warnings);

        var edited = Edit(ms, ws =>
        {
            // rows: 2 = PCC, 3 = Excavation. Columns: 1 Code, 2 Activity, 6 Planned qty, 7 start, 9 Subcontractor, 15 ID
            ws.Cell(2, 6).Value = 95.25;
            ws.Cell(2, 9).Value = "alpha constructions";      // lookup by name, any case
            ws.Cell(2, 7).Value = new DateTime(2026, 2, 1);
            ws.Cell(3, 9).Value = "Nobody Ltd";                // unknown → warning, value kept
            ws.Cell(4, 1).Value = "A-03"; ws.Cell(4, 2).Value = "Footing"; ws.Cell(4, 5).Value = "m³"; ws.Cell(4, 6).Value = "1,250.75"; ws.Cell(4, 7).Value = "15-03-2026";
            ws.Cell(5, 2).Value = "";                           // blank row ignored
            ws.Cell(6, 1).Value = "A-04"; ws.Cell(6, 6).Value = 5; // no name → skipped
        });
        var existing = await _db.ListForProjectAsync<Activity>(_p.Id);
        var res = DataExchange.Import(edited, "a.xlsx", sheet, existing);
        Assert.Single(res.Added);
        Assert.Single(res.Updated);
        Assert.Equal(1, res.Unchanged);
        Assert.Equal(2, res.Warnings.Count);
        await x.SaveAsync(sheet, res, _p.Id);

        var all = (await _db.ListForProjectAsync<Activity>(_p.Id)).ToDictionary(a => a.Code);
        Assert.Equal(3, all.Count);
        Assert.Equal(95.25, all["A-02"].PlannedQty);
        Assert.Equal(sub.Id, all["A-02"].SubcontractorId);
        Assert.Equal(new DateTime(2026, 2, 1), all["A-02"].PlannedStart);
        Assert.Equal(sub.Id, all["A-01"].SubcontractorId); // unknown agency did not wipe the old one
        Assert.Equal(wbs.Id, all["A-01"].WbsId);
        Assert.Equal(10, all["A-01"].Weightage);
        Assert.Equal(1250.75, all["A-03"].PlannedQty);
        Assert.Equal(new DateTime(2026, 3, 15), all["A-03"].PlannedStart);
        Assert.Equal(_p.Id, all["A-03"].ProjectId);
    }

    [Fact]
    public async Task Rows_without_id_match_by_code_and_csv_works()
    {
        var a1 = new Activity { ProjectId = _p.Id, Code = "A-01", Name = "Excavation", PlannedQty = 10 };
        await _db.SaveAsync(a1);
        var sheet = await new ExchangeSheets(_db).ActivitiesAsync(_p.Id);
        var csv = "Planned qty;Activity;Code\n25,5;Excavation rock;a-01\n7;Backfilling;A-09\n";
        var res = DataExchange.Import(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv.Replace("25,5", "25.5"))), "a.csv", sheet, [a1]);
        Assert.Single(res.Updated);
        Assert.Single(res.Added);
        Assert.Same(a1, res.Updated[0]);
        Assert.Equal(25.5, a1.PlannedQty);
        Assert.Equal("Excavation rock", a1.Name);
        Assert.Equal("Backfilling", res.Added[0].Name);
    }

    [Fact]
    public async Task Allocations_import_resolves_drawing_item_and_agency_and_rejects_bad_rows()
    {
        var sub = new Subcontractor { ProjectId = _p.Id, Name = "Steel Fab" };
        var d = new Drawing { ProjectId = _p.Id, Number = "DRG-1" };
        var item = new DrawingItem { ProjectId = _p.Id, DrawingId = d.Id, ItemNo = "1457", Description = "Steel", Unit = "MT", Qty = 100, Rate = 85000 };
        await _db.SaveAllAsync<SyncEntity>([sub, d, item]);
        var x = new ExchangeSheets(_db);
        var sheet = await x.AllocationsAsync(_p.Id);
        var ms = new MemoryStream();
        DataExchange.Export(ms, sheet, []);
        var edited = Edit(ms, ws =>
        {
            // 1 Drawing, 2 Item, 5 Agency, 6 WO, 7 Qty, 8 Unit price
            ws.Cell(2, 1).Value = "DRG-1"; ws.Cell(2, 2).Value = "1457"; ws.Cell(2, 5).Value = "Steel Fab"; ws.Cell(2, 6).Value = "WO/77"; ws.Cell(2, 7).Value = 60; ws.Cell(2, 8).Value = 12500.5;
            ws.Cell(3, 1).Value = "DRG-1"; ws.Cell(3, 2).Value = "9999"; ws.Cell(3, 5).Value = "Steel Fab"; ws.Cell(3, 7).Value = 10;
        });
        var res = DataExchange.Import(edited, "x.xlsx", sheet, []);
        Assert.Single(res.Added);
        Assert.Contains(res.Warnings, w => w.Contains("row skipped"));
        await x.SaveAsync(sheet, res, _p.Id);
        var a = Assert.Single(await _db.ListForProjectAsync<DrawingAllocation>(_p.Id));
        Assert.Equal((item.Id, d.Id, sub.Id, "Steel Fab", 60d, 12500.5m, "WO/77"), (a.DrawingItemId, a.DrawingId, a.SubcontractorId, a.SubcontractorName, a.AllocatedQty, a.Rate, a.WorkOrderNo));

        var reg = await new DrawingService(_db).ComputeAsync(_p.Id);
        var ip = reg.AllItems.Single();
        Assert.Equal(40, ip.Unallocated);
        Assert.Equal(60 * 12500.5m, ip.AllocatedValue);
        Assert.Equal(40 * 85000m, ip.UnallocatedValue);
    }

    [Fact]
    public async Task Hindrances_get_serial_numbers_and_a_failed_row_leaves_the_record_untouched()
    {
        var h = new Hindrance { ProjectId = _p.Id, SerialNo = 1, FromDate = new DateTime(2026, 3, 1), Description = "No drawings", Status = HindranceStatus.Open };
        await _db.SaveAsync(h);
        var x = new ExchangeSheets(_db);
        var sheet = await x.HindrancesAsync();
        var ms = new MemoryStream();
        DataExchange.Export(ms, sheet, [h]);
        var edited = Edit(ms, ws =>
        {
            // 2 From, 3 Resolved on, 5 Description, 9 Status
            ws.Cell(2, 3).Value = new DateTime(2026, 2, 1); ws.Cell(2, 9).Value = "Resolved"; // resolved before from → rejected
            ws.Cell(3, 2).Value = "05/03/2026"; ws.Cell(3, 5).Value = "Rain"; ws.Cell(3, 9).Value = "open"; ws.Cell(3, 8).Value = "abc";
        });
        var res = DataExchange.Import(edited, "h.xlsx", sheet, [h]);
        Assert.Empty(res.Updated);
        Assert.Equal(HindranceStatus.Open, h.Status);
        Assert.Null(h.ToDate);
        var added = Assert.Single(res.Added);
        Assert.Equal(new DateTime(2026, 3, 5), added.FromDate);
        Assert.Contains(res.Warnings, w => w.Contains("not a number"));
        await x.SaveAsync(sheet, res, _p.Id);
        Assert.Equal(2, (await _db.ListForProjectAsync<Hindrance>(_p.Id)).Single(q => q.Description == "Rain").SerialNo);
    }

    [Fact]
    public async Task Boq_template_imports_and_activities_are_created_once()
    {
        var d = new Drawing { ProjectId = _p.Id, Number = "DRG-9", Revision = "R1" };
        await _db.SaveAsync(d);
        var ms = new MemoryStream();
        BoqService.Template(ms);
        ms.Position = 0;
        var imp = BoqService.Import(ms, "boq.xlsx");
        Assert.Equal(2, imp.Items.Count);
        Assert.Equal((100d, 85000m, DrawingItemKind.BoqItem), (imp.Items[0].Qty, imp.Items[0].Rate, imp.Items[0].Kind));
        Assert.Equal(DrawingItemKind.Equipment, imp.Items[1].Kind);
        var boq = new BoqService(_db);
        Assert.Equal((2, 0), await boq.SaveImportAsync(d, imp.Items));
        ms.Position = 0;
        Assert.Equal((0, 2), await boq.SaveImportAsync(d, BoqService.Import(ms, "boq.xlsx").Items));
        Assert.Equal(2, await boq.CreateActivitiesAsync(d));
        Assert.Equal(0, await boq.CreateActivitiesAsync(d));
        var acts = await _db.ListForProjectAsync<Activity>(_p.Id);
        Assert.Contains(acts, a => a.Code == "DRG-9/1457" && a.PlannedQty == 100 && a.Unit == "MT");
    }
}
