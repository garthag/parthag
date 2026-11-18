using Bf3Billing.Core;
using Xunit;

namespace Bf3Billing.Tests;

public sealed class TempDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "bf3test-" + Guid.NewGuid().ToString("N"));
    public BillingDb Db { get; }
    public BillingRepository Repo { get; }

    public TempDb(bool demo = false)
    {
        Directory.CreateDirectory(Dir);
        Db = new BillingDb(Path.Combine(Dir, "billing.db"));
        Repo = new BillingRepository(Db);
        if (demo) Seed.LoadDemo(Repo);
    }

    public string File(string name) => Path.Combine(Dir, name);

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class FormatTests
{
    [Theory]
    [InlineData(2942730, "29,42,730.00")]
    [InlineData(123456789.5, "12,34,56,789.50")]
    [InlineData(999, "999.00")]
    public void Money_uses_lakh_grouping(decimal v, string expected) => Assert.Equal(expected, Fmt.Money(v));

    [Fact]
    public void Short_amounts_in_lakh_and_crore()
    {
        Assert.Equal("₹ 29.43 L", Fmt.Short(2942730m));
        Assert.Equal("₹ 7.55 Cr", Fmt.Short(75496930m));
        Assert.Equal("₹ 0", Fmt.Short(0m));
    }

    [Fact]
    public void Parse_accepts_group_separators_and_blank()
    {
        Assert.True(Fmt.TryParse("1,62,500.50", out var v)); Assert.Equal(162500.50m, v);
        Assert.True(Fmt.TryParse("", out v)); Assert.Equal(0m, v);
        Assert.False(Fmt.TryParse("abc", out _));
        Assert.True(Fmt.TryParseDate("09-03-2026", out var d)); Assert.Equal(new DateTime(2026, 3, 9), d);
    }
}

public class BillMathTests
{
    [Fact]
    public void Totals_match_the_design_example()
    {
        var bill = new Bill { GstPct = 18 };
        var lines = new[]
        {
            new BillLine { Qty = 25, Rate = 6500 }, new BillLine { Qty = 25, Rate = 6500 }, new BillLine { Qty = 1, Rate = 85000 },
        };
        var t = BillMath.Totals(bill, lines);
        Assert.Equal(410000m, t.Gross);
        Assert.Equal(73800m, t.Gst);
        Assert.Equal(483800m, t.Net);
        Assert.Equal(483800m, t.Due);
    }

    [Fact]
    public void Deductions_are_on_gross()
    {
        var bill = new Bill { GstPct = 18, RetentionPct = 5, TdsPct = 2, OtherDeduction = 1000, PaidAmount = 50000 };
        var t = BillMath.Totals(bill, new[] { new BillLine { Qty = 10, Rate = 10000 } });
        Assert.Equal(100000m, t.Gross);
        Assert.Equal(5000m, t.Retention);
        Assert.Equal(2000m, t.Tds);
        Assert.Equal(100000m + 18000m - 5000m - 2000m - 1000m, t.Net);
        Assert.Equal(t.Net - 50000m, t.Due);
    }
}

public class RepositoryTests
{
    [Fact]
    public void New_data_file_has_masters_and_no_bills()
    {
        using var t = new TempDb();
        var c = t.Repo.Counts();
        Assert.Equal(5, c.Contractors);
        Assert.Equal(14, c.WorkOrders);
        Assert.Equal(0, c.Bills);
        Assert.Equal(4, t.Repo.Activities().Count(a => a.IsBom));
        var d = t.Repo.GetDashboard();
        Assert.Equal(0, d.BillCount);
        Assert.Equal(754_96_930m, d.ByContractor.Single(x => x.Contractor == "G.R ENTERPRISES").WoValue);
    }

    [Fact]
    public void Opening_twice_does_not_duplicate_masters()
    {
        using var t = new TempDb();
        var again = new BillingRepository(new BillingDb(t.Db.FilePath));
        Assert.Equal(14, again.Counts().WorkOrders);
    }

    [Fact]
    public void Demo_figures_match_the_design()
    {
        using var t = new TempDb(demo: true);
        var bills = t.Repo.BillSummaries();
        Assert.Equal(6, bills.Count);
        Assert.Equal(29_42_730m, bills.Sum(b => b.Gross));
        Assert.Equal(34_72_421.40m, bills.Sum(b => b.Net));
        Assert.Equal(17_21_301.40m, bills.Sum(b => b.Paid));
        Assert.Equal(17_51_120.00m, bills.Sum(b => b.Due));

        var draft = bills.Single(b => b.Status == BillStatus.Draft);
        Assert.Equal(1, draft.OverScopeLines);
        Assert.Equal(483800m, draft.Net);
        Assert.Equal(0, bills.Where(b => b != draft).Sum(b => b.OverScopeLines));

        var d = t.Repo.GetDashboard();
        Assert.Equal(1, d.OverScopeLines);
        Assert.Equal(1, d.DraftBills);
        Assert.Equal(248.420m, d.Progress.Single(p => p.Activity == "Erection").Tonnes);
        Assert.Equal(60m, d.Progress.Single(p => p.Activity == "Supply / transport").Tonnes);
        var gre = d.ByContractor.Single(x => x.Contractor == "G.R ENTERPRISES");
        Assert.Equal(3, gre.Bills);
        Assert.Equal(18_68_730m, gre.Gross);
        Assert.Equal(4_83_800m, gre.Outstanding);
        Assert.Equal(3_25_000m, d.WorkOrders.Single(w => w.WoNo == "MCK/WO-MECH/BSL/2024-25/013").Gross);
    }

    [Fact]
    public void Scope_check_in_editor_counts_other_bills_only()
    {
        using var t = new TempDb(demo: true);
        var draftId = t.Repo.BillSummaries().Single(b => b.Status == BillStatus.Draft).Id;
        var (_, lines) = t.Repo.GetBill(draftId)!.Value;
        var keys = lines.Where(l => l.IsBom).Select(l => (l.BomItemId!.Value, l.ActivityId!.Value)).ToList();
        var scope = t.Repo.ScopeFor(draftId, keys);
        var sht3 = scope[keys[0]];
        var sht4 = scope[keys[1]];
        Assert.Equal(309.714m, sht3.Scope);
        Assert.Equal(120m, sht3.Earlier);
        Assert.Equal(58.2m, sht4.Scope);
        Assert.Equal(40m, sht4.Earlier);
        var info = new ScopeInfo(sht4.Scope, sht4.Earlier, 25m);
        Assert.True(info.Over);
        Assert.Equal(111.7m, Math.Round(info.Percent, 1));
    }

    [Fact]
    public void Save_load_and_edit_bill_round_trips()
    {
        using var t = new TempDb();
        var wo = t.Repo.WorkOrders().First(w => w.Value > 0);
        var b = t.Repo.NewBill(wo.Id);
        Assert.Equal("RA-1", b.BillNo);
        b.BillDate = new DateTime(2026, 4, 1);
        b.PeriodFrom = new DateTime(2026, 3, 1);
        b.PeriodTo = new DateTime(2026, 3, 31);
        var act = t.Repo.Activities().First(a => a.Name == "Utility");
        var id = t.Repo.SaveBill(b, new[] { new BillLine { Description = "Scaffolding", ActivityId = act.Id, Unit = "LS", Qty = 1.5m, Rate = 1234.56m } });

        var (loaded, lines) = t.Repo.GetBill(id)!.Value;
        Assert.Equal(new DateTime(2026, 4, 1), loaded.BillDate);
        Assert.Equal(new DateTime(2026, 3, 31), loaded.PeriodTo);
        Assert.Equal(18m, loaded.GstPct);
        Assert.Single(lines);
        Assert.Equal(1.5m, lines[0].Qty);
        Assert.Equal(1234.56m, lines[0].Rate);
        Assert.Equal(1851.84m, lines[0].Amount);

        loaded.Status = BillStatus.Paid;
        loaded.PaidAmount = 100;
        t.Repo.SaveBill(loaded, lines.Append(new BillLine { Description = "Extra", Qty = 2, Rate = 10 }));
        var again = t.Repo.GetBill(id)!.Value;
        Assert.Equal(2, again.Lines.Count);
        Assert.Equal(BillStatus.Paid, again.Bill.Status);
        Assert.Equal("RA-2", t.Repo.NextBillNo(wo.Id));
    }

    [Fact]
    public void Duplicate_bill_number_is_refused_and_duplicate_makes_next_number()
    {
        using var t = new TempDb();
        var wo = t.Repo.WorkOrders().First();
        var id = t.Repo.SaveBill(new Bill { WorkOrderId = wo.Id, BillNo = "RA-1" }, new[] { new BillLine { Description = "x", Qty = 1, Rate = 1 } });
        Assert.Throws<BillingException>(() => t.Repo.SaveBill(new Bill { WorkOrderId = wo.Id, BillNo = "ra-1" }, Array.Empty<BillLine>()));
        var copy = t.Repo.DuplicateBill(id);
        Assert.Equal("RA-2", t.Repo.GetBill(copy)!.Value.Bill.BillNo);
        Assert.Single(t.Repo.GetBill(copy)!.Value.Lines);
    }

    [Fact]
    public void Validation_rules()
    {
        using var t = new TempDb();
        var wo = t.Repo.WorkOrders().First();
        Assert.Throws<BillingException>(() => t.Repo.SaveBill(new Bill { WorkOrderId = wo.Id, BillNo = "A" }, new[] { new BillLine { Description = "", Qty = 1 } }));
        Assert.Throws<BillingException>(() => t.Repo.SaveBill(new Bill { WorkOrderId = wo.Id, BillNo = "A", GstPct = 120 }, Array.Empty<BillLine>()));
        Assert.Throws<BillingException>(() => t.Repo.SaveBill(new Bill { WorkOrderId = wo.Id, BillNo = "A", PeriodFrom = DateTime.Today, PeriodTo = DateTime.Today.AddDays(-1) }, Array.Empty<BillLine>()));
        Assert.Throws<BillingException>(() => t.Repo.DeleteContractor(wo.ContractorId));
    }

    [Fact]
    public void Cancelled_bills_are_left_out_of_totals()
    {
        using var t = new TempDb(demo: true);
        var draftId = t.Repo.BillSummaries().Single(b => b.Status == BillStatus.Draft).Id;
        var (b, lines) = t.Repo.GetBill(draftId)!.Value;
        b.Status = BillStatus.Cancelled;
        t.Repo.SaveBill(b, lines);
        var d = t.Repo.GetDashboard();
        Assert.Equal(5, d.BillCount);
        Assert.Equal(0, d.OverScopeLines);
        Assert.Equal(198.42m, d.Progress.Single(p => p.Activity == "Erection").Tonnes);
    }

    [Fact]
    public void Bom_progress_levels_and_over_flag()
    {
        using var t = new TempDb(demo: true);
        var drawings = t.Repo.BomProgress(BomLevel.Drawings);
        Assert.Equal(10, drawings.Count);
        var erection = t.Repo.Activities().Single(a => a.Name == "Erection").Id;
        var sht4 = drawings.Single(r => r.Drawing == "16193442 · Sht 4/6 · Rev A");
        Assert.True(sht4.Over);
        Assert.Equal(65m, sht4.Billed[erection]);
        var systems = t.Repo.BomProgress(BomLevel.Systems);
        Assert.Equal(3, systems.Count);
        Assert.True(systems.Single(s => s.System == "1 STOVE").Over);
        Assert.Equal(3, t.Repo.BomProgress(BomLevel.Drawings, onlyBilled: true).Count);
        Assert.Equal(2, t.Repo.LineDetails(sht4.BomItemIds).Count);
    }

    [Fact]
    public void Rates_prefer_work_order_rate_over_default()
    {
        using var t = new TempDb();
        var wo = t.Repo.WorkOrders().First();
        var act = t.Repo.Activities().First();
        t.Repo.SaveRate(new Rate { WorkOrderId = 0, ActivityId = act.Id, Unit = "t", Amount = 6000 });
        Assert.Equal(6000m, t.Repo.FindRate(wo.Id, act.Id)!.Amount);
        t.Repo.SaveRate(new Rate { WorkOrderId = wo.Id, ActivityId = act.Id, Unit = "t", Amount = 6500 });
        Assert.Equal(6500m, t.Repo.FindRate(wo.Id, act.Id)!.Amount);
        Assert.Equal(2, t.Repo.Rates().Count);
    }

    [Fact]
    public void Backup_makes_a_valid_copy()
    {
        using var t = new TempDb(demo: true);
        var target = t.File("backup.db");
        t.Db.BackupTo(target);
        Assert.True(BillingDb.LooksLikeBillingDb(target));
        Assert.Equal(6, new BillingRepository(new BillingDb(target)).Counts().Bills);
        System.IO.File.WriteAllText(t.File("junk.db"), "not a database");
        Assert.False(BillingDb.LooksLikeBillingDb(t.File("junk.db")));
    }

    [Fact]
    public void All_reports_run()
    {
        using var t = new TempDb(demo: true);
        foreach (var name in Reports.Names)
        {
            var table = Reports.Run(t.Repo, name);
            Assert.True(table.Columns.Count > 0, name);
        }
        var over = Reports.Run(t.Repo, "Lines above BOM scope");
        Assert.Equal(1, over.Rows.Count);
        var abs = Reports.Run(t.Repo, "Contractor-wise abstract");
        Assert.Equal(29_42_730m, (decimal)abs.Rows[abs.Rows.Count - 1]["Gross ₹"]);
    }
}

public class ExcelTests
{
    [Fact]
    public void Bom_import_reads_tonnes_kg_and_sheet_dates()
    {
        using var t = new TempDb();
        var path = t.File("bom.xlsx");
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.Worksheets.Add("HOT BLAST");
            ws.Cell(1, 1).Value = "BOM of hot blast main";
            ws.Cell(3, 1).Value = "DRG NO"; ws.Cell(3, 2).Value = "Sheet"; ws.Cell(3, 3).Value = "Rev"; ws.Cell(3, 4).Value = "Total weight (kg)";
            ws.Cell(4, 1).Value = "16282205"; ws.Cell(4, 2).Value = new DateTime(2026, 3, 2); ws.Cell(4, 3).Value = "0"; ws.Cell(4, 4).Value = 1500;
            ws.Cell(5, 1).Value = "16282205"; ws.Cell(5, 2).Value = new DateTime(2026, 3, 2); ws.Cell(5, 3).Value = "0"; ws.Cell(5, 4).Value = 500;
            ws.Cell(6, 1).Value = "TOTAL"; ws.Cell(6, 4).Value = 2000;
            var ws2 = wb.Worksheets.Add("Other");
            ws2.Cell(1, 1).Value = "System"; ws2.Cell(1, 2).Value = "Drawing no"; ws2.Cell(1, 3).Value = "Tonnes";
            ws2.Cell(2, 1).Value = "1 STOVE"; ws2.Cell(2, 2).Value = "16193442"; ws2.Cell(2, 3).Value = 12.5;
            wb.SaveAs(path);
        }
        var r = ExcelIO.ImportBom(t.Repo, path);
        Assert.Equal(2, r.Added);
        var items = t.Repo.BomItems();
        var hb = items.Single(i => i.System == "HOT BLAST");
        Assert.Equal("2/3", hb.Sheet);
        Assert.Equal(2.000m, hb.Tonnes);
        Assert.Equal(12.5m, items.Single(i => i.System == "1 STOVE").Tonnes);

        var again = ExcelIO.ImportBom(t.Repo, path);
        Assert.Equal(0, again.Added);
        Assert.Equal(2, t.Repo.BomCount());
    }

    [Fact]
    public void Exported_bill_lines_import_into_a_new_data_file()
    {
        using var src = new TempDb(demo: true);
        var export = src.File("all.xlsx");
        ExcelIO.ExportAll(src.Repo, export);
        Assert.True(File.Exists(export));

        using var dst = new TempDb();
        var bomResult = ExcelIO.ImportBom(dst.Repo, export);
        Assert.Equal(10, bomResult.Added);
        var r = ExcelIO.ImportBills(dst.Repo, export);
        Assert.Equal(6, r.Added);
        var bills = dst.Repo.BillSummaries();
        Assert.Equal(29_42_730m, bills.Sum(b => b.Gross));
        Assert.Equal(17_51_120.00m, bills.Sum(b => b.Due));
        Assert.Equal(1, bills.Sum(b => b.OverScopeLines));

        var twice = ExcelIO.ImportBills(dst.Repo, export);
        Assert.Equal(0, twice.Added);
        Assert.Equal(6, dst.Repo.BillSummaries().Count);
    }

    [Fact]
    public void Templates_are_written_and_importable()
    {
        using var t = new TempDb();
        ExcelIO.WriteTemplates(t.File("bom.xlsx"), t.File("bills.xlsx"));
        Assert.Equal(1, ExcelIO.ImportBom(t.Repo, t.File("bom.xlsx")).Added);
        var r = ExcelIO.ImportBills(t.Repo, t.File("bills.xlsx"));
        Assert.Equal(1, r.Added);
        var b = t.Repo.BillSummaries().Single();
        Assert.Equal(2, b.Lines);
        Assert.Equal(25m * 6500m + 85000m, b.Gross);
    }
}

public class DataFileTests
{
    [Fact]
    public void File_made_by_another_program_is_refused_and_left_untouched()
    {
        using var t = new TempDb();
        var path = t.File("other.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE Bill(id INTEGER PRIMARY KEY, work_order_id INTEGER, amount REAL); INSERT INTO Bill(work_order_id, amount) VALUES (1, 100);";
            cmd.ExecuteNonQuery();
        }
        var before = File.ReadAllBytes(path);
        var ex = Assert.Throws<IncompatibleDataFileException>(() => new BillingDb(path));
        Assert.Contains("WorkOrderId", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Text_file_is_refused()
    {
        using var t = new TempDb();
        var path = t.File("notes.db");
        File.WriteAllText(path, "this is not a database, just some text that is long enough to have a header");
        Assert.Throws<IncompatibleDataFileException>(() => new BillingDb(path));
    }

    [Fact]
    public void Own_files_and_empty_files_open()
    {
        using var t = new TempDb(demo: true);
        Assert.Equal(6, new BillingRepository(new BillingDb(t.Db.FilePath)).Counts().Bills);
        var empty = t.File("empty.db");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.Equal(0, new BillingRepository(new BillingDb(empty)).Counts().Bills);
    }
}
