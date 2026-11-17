using Bf3Billing.Core;
using Bf3Billing.Core.Bsl;
using Xunit;

namespace Bf3Billing.Tests;

public class BslImportTests
{
    [Fact]
    public void Reads_both_layouts_and_reconciles_with_sheet_totals()
    {
        using var t = new TempDb();
        var (bom, con) = BslSample.Write(t.File("bsl"));
        var plan = BslImport.Analyze(bom, con);
        var rec = plan.Reconciliation();
        var stove = rec.Rows.Cast<System.Data.DataRow>().Single(r => (string)r["Sheet"] == "STOVE");
        Assert.Equal(3, (int)stove["Sets"]);
        Assert.Equal(3.021m, (decimal)stove["Per set t"]);
        Assert.Equal("Matches", stove["Result"]);
        Assert.Equal(9.063m, (decimal)stove["All sets t"]);
        Assert.Contains(plan.Issues, i => i.Code == "SHEET_AS_DATE");
        Assert.Contains(plan.Issues, i => i.Code == "GRADE_CORRECTED");
    }

    [Fact]
    public void Import_builds_linked_records_and_is_safe_to_repeat()
    {
        using var t = new TempDb();
        var (bom, con) = BslSample.Write(t.File("bsl"));
        var r1 = BslImport.Apply(t.Repo, BslImport.Analyze(bom, con));

        // BOM: drawings with sheet/rev split out of the number, parts kept under each drawing.
        var items = t.Repo.BomItems();
        Assert.Contains(items, i => i.DrawingNo == "ERC_SMS_DSWG_F-0016" && i.Sheet == "1/1" && i.Rev == "R0");
        var sht2 = items.Single(i => i.DrawingNo == "16193442" && i.Sheet == "2/6");
        Assert.Equal(3, sht2.Sets);
        Assert.Equal(Math.Round((1655m + 1177.6m) / 1000m, 3) * 3, sht2.Tonnes);
        Assert.Equal(2, t.Repo.BomParts(sht2.Id).Count);
        Assert.Contains(items, i => i.DrawingNo == "16193442" && i.Sheet == "3/6"); // sheet stored as a date (3 June), turned back

        // Contractors and work orders: existing ones updated, new ones added, extra fields kept.
        var gre = t.Repo.Contractors().Single(c => c.Name == "G.R ENTERPRISES");
        Assert.Equal("R.K. Singh", Extras.Get(gre.Extra, "Contact person"));
        Assert.Equal("20AABFG8971H1ZH", gre.Gstin);
        Assert.Contains(t.Repo.Contractors(), c => c.Name == "NEW AGENCY");
        var wo6 = t.Repo.WorkOrders().Single(w => w.WoNo == "MCK/WO-MECH/BSL/2024-25/006");
        Assert.Equal("BM05", Extras.Get(wo6.Extra, "Ref no"));
        Assert.Equal(new DateTime(2024, 4, 17), wo6.WoDate);

        // BOQ: both "Hot blast valves" rows kept; scope and extra items imported.
        var boq = t.Repo.BoqItems();
        Assert.Equal(2, boq.Count(q => q.Description == "Hot blast valves"));
        Assert.Contains(boq, q => q.Source == "DRAWING sheet" && q.ScopeTonnes == 2.488m);
        Assert.Contains(boq, q => q.Source == "REMARKS sheet" && q.ScopeTonnes == 6.3m && q.AgencyRef == "MDE");
        Assert.Equal("BSL", boq.Single(q => q.Description == "Anchor bolts").AgencyRef);

        // RA bills: one per work order and RA column, priced at the agency rate, per-bill quantities.
        var bills = t.Repo.BillSummaries();
        Assert.Equal(4, bills.Count); // BM05: RA-1, RA-2; BM07: RA-3, RA-4
        var ra2 = bills.Single(b => b.BillNo == "RA-2" && b.WorkOrder == wo6.WoNo);
        Assert.Equal(35m * 8000m + 81.98m * 9000m, ra2.Gross);
        Assert.Equal(1, ra2.OverScopeLines); // 81.98 t against 60 t R1 scope
        var ra3 = t.Repo.GetBill(bills.Single(b => b.BillNo == "RA-3").Id)!.Value;
        Assert.Equal(0m, ra3.Lines.Single().Rate);
        Assert.Contains("no agency rate", ra3.Bill.Remarks);
        Assert.Equal("20000", Extras.Get(ra3.Lines.Single().Extra, "Client rate"));

        // Importing again changes nothing.
        var r2 = BslImport.Apply(t.Repo, BslImport.Analyze(bom, con));
        Assert.Equal(0, r2.Bills);
        Assert.Equal(4, r2.BillsSkipped);
        Assert.Equal(boq.Count, t.Repo.BoqItems().Count);
        Assert.Equal(items.Count, t.Repo.BomItems().Count);
        Assert.Equal(r1.BomParts, r2.BomParts);
    }

    [Fact]
    public void Boq_lines_have_their_own_scope_check()
    {
        using var t = new TempDb();
        var (bom, con) = BslSample.Write(t.File("bsl"));
        BslImport.Apply(t.Repo, BslImport.Analyze(bom, con));
        var dome = t.Repo.BoqItems().Single(q => q.Description.StartsWith("Dome"));
        var s = t.Repo.BoqScopeFor(0, new[] { dome.Id })[dome.Id];
        Assert.Equal(100m, s.Scope);
        Assert.Equal(75m, s.Earlier);
        Assert.Equal(75m, t.Repo.BoqBilled()[dome.Id].Tonnes);
        Assert.Equal(2, t.Repo.BoqLineDetails(dome.Id).Count);
        Assert.Single(Reports.Run(t.Repo, "Lines above BOM scope").Rows.Cast<System.Data.DataRow>());
    }
}
