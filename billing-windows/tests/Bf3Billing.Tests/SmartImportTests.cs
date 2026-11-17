using Bf3Billing.Core;
using ClosedXML.Excel;
using Xunit;

namespace Bf3Billing.Tests;

public class SmartImportTests
{
    /// <summary>An untidy workbook: title rows, odd headers, an unknown column, repeats, a total row, merged title.</summary>
    static string MessyWorkbook(TempDb t)
    {
        var path = t.File("messy.xlsx");
        using var wb = new XLWorkbook();

        var k = wb.Worksheets.Add("Agencies");
        k.Cell(1, 1).Value = "LIST OF AGENCIES – BF#3";
        k.Range(1, 1, 1, 5).Merge();
        k.Cell(2, 1).Value = "as on 01.11.2026";
        k.Cell(4, 1).Value = "Sl"; k.Cell(4, 2).Value = "Agency Name"; k.Cell(4, 3).Value = "GST No."; k.Cell(4, 4).Value = "Mobile"; k.Cell(4, 5).Value = "Bank A/c";
        object[][] rows =
        {
            new object[] { 1, "G.R. Enterprises", "20ABCDE1234F1Z5", 9334240112d, "SBI 1234" },   // already saved as G.R ENTERPRISES
            new object[] { 2, "Shakti Fabricators", "20AAAAA1111A1Z1", "98350 12345", "PNB 999" },
            new object[] { 3, "Shakti  Fabricators", "20AAAAA1111A1Z1", "98350 12345", "PNB 999" },  // exact copy (extra space)
            new object[] { 4, "SHAKTI FABRICATORS", "", "70000 00000", "" },                        // same name, different details
            new object[] { 5, "", "", "", "" },
            new object[] { 6, "Om Sai Works", "", "", "HDFC 42" },
        };
        for (var i = 0; i < rows.Length; i++)
            for (var c = 0; c < rows[i].Length; c++)
            {
                var v = rows[i][c];
                k.Cell(5 + i, 1 + c).Value = v switch { int n => n, double d => d, string s => s, _ => "" };
            }

        var w = wb.Worksheets.Add("WO list");
        w.Cell(2, 1).Value = "W.O. No."; w.Cell(2, 2).Value = "Name of Contractor"; w.Cell(2, 3).Value = "Nature of work";
        w.Cell(2, 4).Value = "WO Value"; w.Cell(2, 5).Value = "WO Date"; w.Cell(2, 6).Value = "Site Engineer";
        w.Cell(3, 1).Value = "MCK/WO-MECH/BSL/2025-26/020"; w.Cell(3, 2).Value = "Shakti Fabricators"; w.Cell(3, 3).Value = "FABRICATION";
        w.Cell(3, 4).Value = "₹ 12,50,000/-"; w.Cell(3, 5).Value = "02.11.2025"; w.Cell(3, 6).Value = "R. Kumar";
        w.Cell(4, 1).Value = "MCK/WO-MECH/BSL/2024-25/012"; w.Cell(4, 2).Value = "G.R ENTERPRISES"; w.Cell(4, 3).Value = "ERECTION";
        w.Cell(4, 4).Value = 23952700; w.Cell(4, 5).Value = new DateTime(2024, 6, 1); w.Cell(4, 6).Value = "S. Das";
        w.Cell(5, 1).Value = "MCK/WO-MECH/BSL/2025-26/021"; w.Cell(5, 2).Value = "New Agency Pvt Ltd"; w.Cell(5, 4).Value = "abc";
        w.Cell(6, 1).Value = "TOTAL"; w.Cell(6, 4).Value = 99999999;

        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void Reads_sheets_finds_headers_and_suggests_targets()
    {
        using var t = new TempDb();
        var sheets = RawSheet.ReadWorkbook(MessyWorkbook(t));
        Assert.Equal(2, sheets.Count);
        var k = sheets[0];
        Assert.Equal(4, k.SuggestedHeaderRow);
        Assert.Equal(ImportTarget.Contractors, SmartImport.SuggestTarget(k, 4));
        var w = sheets[1];
        Assert.Equal(2, w.SuggestedHeaderRow);
        Assert.Equal(ImportTarget.WorkOrders, SmartImport.SuggestTarget(w, 2));
    }

    [Fact]
    public void Columns_fit_existing_fields_and_unknown_ones_need_a_decision()
    {
        using var t = new TempDb();
        var k = RawSheet.ReadWorkbook(MessyWorkbook(t))[0];
        var map = SmartImport.AutoMap(k, 4, ImportTarget.Contractors, t.Repo.CustomFields(Entities.Contractor));
        Assert.Equal("name", map.Single(m => m.Header == "Agency Name").FieldKey);
        Assert.Equal("gstin", map.Single(m => m.Header == "GST No.").FieldKey);
        Assert.Equal("phone", map.Single(m => m.Header == "Mobile").FieldKey);
        Assert.True(map.Single(m => m.Header == "Bank A/c").NeedsDecision);
        Assert.True(map.Single(m => m.Header == "Sl").NeedsDecision);
    }

    [Fact]
    public void Duplicates_are_grouped_and_existing_rows_flagged()
    {
        using var t = new TempDb();
        var k = RawSheet.ReadWorkbook(MessyWorkbook(t))[0];
        var map = SmartImport.AutoMap(k, 4, ImportTarget.Contractors, Array.Empty<CustomField>());
        var p = SmartImport.Analyze(t.Repo, k, 4, ImportTarget.Contractors, map);

        var gr = p.Rows.Single(r => r.SourceRow == 5);
        Assert.Equal(RowStatus.Exists, gr.Status);
        Assert.False(gr.Include);

        var shakti = p.Rows.Where(r => r.SourceRow is 6 or 7 or 8).ToList();
        Assert.All(shakti, r => Assert.Equal(shakti[0].Group, r.Group));
        Assert.NotNull(shakti[0].Group);
        Assert.Equal(RowStatus.New, shakti[0].Status);
        Assert.True(shakti[0].Include);
        Assert.Equal(RowStatus.Repeated, shakti[1].Status);
        Assert.Contains("Exact copy of row 6", shakti[1].Note);
        Assert.Equal(RowStatus.Repeated, shakti[2].Status);
        Assert.Contains("different details", shakti[2].Note);
        Assert.False(shakti[1].Include || shakti[2].Include);

        Assert.Equal(RowStatus.New, p.Rows.Single(r => r.SourceRow == 10).Status);
        Assert.Equal(1, p.Groups);
    }

    [Fact]
    public void Unmatched_column_becomes_a_new_field_with_its_data()
    {
        using var t = new TempDb();
        var k = RawSheet.ReadWorkbook(MessyWorkbook(t))[0];
        var map = SmartImport.AutoMap(k, 4, ImportTarget.Contractors, Array.Empty<CustomField>());
        var bank = map.Single(m => m.Header == "Bank A/c");
        bank.NeedsDecision = false; bank.CustomField = "Bank A/c"; bank.NewCustomField = true;
        var sl = map.Single(m => m.Header == "Sl");
        sl.NeedsDecision = false; // left out

        var p = SmartImport.Analyze(t.Repo, k, 4, ImportTarget.Contractors, map);
        var res = SmartImport.Apply(t.Repo, p, new ImportOptions());
        Assert.Equal(2, res.Added); // Shakti Fabricators and Om Sai Works

        Assert.Single(t.Repo.CustomFields(Entities.Contractor), f => f.Name == "Bank A/c");
        var shakti = t.Repo.Contractors().Single(c => c.Name == "Shakti Fabricators");
        Assert.Equal("PNB 999", Extras.Get(shakti.Extra, "Bank A/c"));
        Assert.Equal("20AAAAA1111A1Z1", shakti.Gstin);
        Assert.Equal("HDFC 42", Extras.Get(t.Repo.Contractors().Single(c => c.Name == "Om Sai Works").Extra, "Bank A/c"));
        Assert.Equal("", Extras.Get(t.Repo.Contractors().Single(c => c.Name == "G.R ENTERPRISES").Extra, "Bank A/c"));

        // Upload the same file again: everything is already saved now.
        var again = SmartImport.Analyze(t.Repo, k, 4, ImportTarget.Contractors, SmartImport.AutoMap(k, 4, ImportTarget.Contractors, t.Repo.CustomFields(Entities.Contractor)));
        Assert.Equal("Bank A/c", SmartImport.AutoMap(k, 4, ImportTarget.Contractors, t.Repo.CustomFields(Entities.Contractor)).Single(m => m.Header == "Bank A/c").CustomField);
        Assert.Equal(0, again.Count(RowStatus.New));
        Assert.Equal(3, again.Count(RowStatus.Exists));
    }

    [Fact]
    public void Update_existing_adds_new_field_values_to_saved_records()
    {
        using var t = new TempDb();
        var k = RawSheet.ReadWorkbook(MessyWorkbook(t))[0];
        var map = SmartImport.AutoMap(k, 4, ImportTarget.Contractors, Array.Empty<CustomField>());
        var bank = map.Single(m => m.Header == "Bank A/c");
        bank.CustomField = "Bank A/c"; bank.NewCustomField = true; bank.NeedsDecision = false;
        var p = SmartImport.Analyze(t.Repo, k, 4, ImportTarget.Contractors, map);
        foreach (var r in p.Rows.Where(r => r.Status == RowStatus.Exists)) r.Include = true;
        var res = SmartImport.Apply(t.Repo, p, new ImportOptions { Existing = ExistingAction.Update });
        Assert.Equal(1, res.Updated);
        var gr = t.Repo.Contractors().Single(c => c.Name == "G.R ENTERPRISES");
        Assert.Equal("SBI 1234", Extras.Get(gr.Extra, "Bank A/c"));
        Assert.Equal("20ABCDE1234F1Z5", gr.Gstin);
    }

    [Fact]
    public void Work_orders_clean_amounts_and_dates_and_add_missing_contractors()
    {
        using var t = new TempDb();
        var w = RawSheet.ReadWorkbook(MessyWorkbook(t))[1];
        var map = SmartImport.AutoMap(w, 2, ImportTarget.WorkOrders, Array.Empty<CustomField>());
        Assert.Equal("wo", map[0].FieldKey);
        Assert.Equal("contractor", map[1].FieldKey);
        Assert.Equal("service", map[2].FieldKey);
        Assert.Equal("value", map[3].FieldKey);
        Assert.Equal("date", map[4].FieldKey);
        var eng = map.Single(m => m.Header == "Site Engineer");
        Assert.True(eng.NeedsDecision);
        eng.CustomField = "Site Engineer"; eng.NewCustomField = true; eng.NeedsDecision = false;

        var p = SmartImport.Analyze(t.Repo, w, 2, ImportTarget.WorkOrders, map);
        Assert.Equal(RowStatus.New, p.Rows.Single(r => r.SourceRow == 3).Status);
        Assert.Equal(12_50_000m, p.Rows.Single(r => r.SourceRow == 3).Numbers["value"]);
        Assert.Equal(new DateTime(2025, 11, 2), p.Rows.Single(r => r.SourceRow == 3).Dates["date"]);
        Assert.Equal(RowStatus.Exists, p.Rows.Single(r => r.SourceRow == 4).Status);
        Assert.Equal(RowStatus.Invalid, p.Rows.Single(r => r.SourceRow == 5).Status);
        Assert.Contains("not a number", p.Rows.Single(r => r.SourceRow == 5).Note);
        Assert.Equal(RowStatus.Total, p.Rows.Single(r => r.SourceRow == 6).Status);

        var res = SmartImport.Apply(t.Repo, p, new ImportOptions());
        Assert.Equal(1, res.Added);
        var wo = t.Repo.WorkOrders().Single(x => x.WoNo == "MCK/WO-MECH/BSL/2025-26/020");
        Assert.Equal("Shakti Fabricators", wo.ContractorName);
        Assert.Equal(12_50_000m, wo.Value);
        Assert.Equal("R. Kumar", Extras.Get(wo.Extra, "Site Engineer"));
    }

    [Fact]
    public void Bom_parts_of_one_drawing_are_added_up_and_exact_copies_flagged()
    {
        using var t = new TempDb();
        var path = t.File("bom.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.Worksheets.Add("STOVE");
            ws.Cell(1, 1).Value = "Drg No"; ws.Cell(1, 2).Value = "Mark"; ws.Cell(1, 3).Value = "Wt (kg)"; ws.Cell(1, 4).Value = "Grade";
            ws.Cell(2, 1).Value = "D-1"; ws.Cell(2, 2).Value = "A1"; ws.Cell(2, 3).Value = 1000; ws.Cell(2, 4).Value = "E250";
            ws.Cell(3, 1).Value = "D-1"; ws.Cell(3, 2).Value = "A1"; ws.Cell(3, 3).Value = 500; ws.Cell(3, 4).Value = "E250";
            ws.Cell(4, 1).Value = "D-1"; ws.Cell(4, 2).Value = "A1"; ws.Cell(4, 3).Value = 500; ws.Cell(4, 4).Value = "E250";
            ws.Cell(5, 1).Value = "D-2"; ws.Cell(5, 2).Value = ""; ws.Cell(5, 3).Value = 2500; ws.Cell(5, 4).Value = "E350";
            wb.SaveAs(path);
        }
        var s = RawSheet.ReadWorkbook(path)[0];
        Assert.Equal(ImportTarget.Bom, SmartImport.SuggestTarget(s, 1));
        var map = SmartImport.AutoMap(s, 1, ImportTarget.Bom, Array.Empty<CustomField>());
        var grade = map.Single(m => m.Header == "Grade");
        grade.CustomField = "Grade"; grade.NewCustomField = true; grade.NeedsDecision = false;
        var p = SmartImport.Analyze(t.Repo, s, 1, ImportTarget.Bom, map);
        Assert.Equal(RowStatus.New, p.Rows[1].Status);        // 500 kg part, added to row 2
        Assert.Equal(RowStatus.Repeated, p.Rows[2].Status);   // exact copy of row 3
        Assert.Equal(p.Rows[0].Group, p.Rows[2].Group);
        SmartImport.Apply(t.Repo, p, new ImportOptions());
        var items = t.Repo.BomItems();
        Assert.Equal(1.5m, items.Single(i => i.DrawingNo == "D-1").Tonnes);
        Assert.Equal("STOVE", items.Single(i => i.DrawingNo == "D-1").System);
        Assert.Equal("E350", Extras.Get(items.Single(i => i.DrawingNo == "D-2").Extra, "Grade"));
    }

    [Fact]
    public void Bill_line_extra_fields_survive_editing_the_bill()
    {
        using var t = new TempDb();
        var wo = t.Repo.WorkOrders().First();
        var id = t.Repo.SaveBill(new Bill { WorkOrderId = wo.Id, BillNo = "RA-1" },
            new[] { new BillLine { Description = "x", Qty = 1, Rate = 1, Extra = Extras.Write(new Dictionary<string, string> { ["MB page"] = "12" }) } });
        var (b, lines) = t.Repo.GetBill(id)!.Value;
        t.Repo.SaveBill(b, lines);
        Assert.Equal("12", Extras.Get(t.Repo.GetBill(id)!.Value.Lines[0].Extra, "MB page"));
    }

    [Fact]
    public void Custom_fields_rename_and_delete_move_their_values()
    {
        using var t = new TempDb();
        var f = t.Repo.AddCustomField(Entities.Contractor, "Bank");
        Assert.Equal(f.Id, t.Repo.AddCustomField(Entities.Contractor, "bank").Id);
        var k = t.Repo.Contractors().First();
        k.Extra = Extras.Write(new Dictionary<string, string> { ["Bank"] = "SBI" });
        t.Repo.SaveContractor(k);
        t.Repo.RenameCustomField(f.Id, "Bank account");
        Assert.Equal("SBI", Extras.Get(t.Repo.Contractors().First(c => c.Id == k.Id).Extra, "Bank account"));
        t.Repo.DeleteCustomField(f.Id);
        Assert.Empty(t.Repo.CustomFields(Entities.Contractor));
        Assert.Equal("", Extras.Get(t.Repo.Contractors().First(c => c.Id == k.Id).Extra, "Bank account"));
        Assert.Throws<BillingException>(() => t.Repo.AddCustomField(Entities.Contractor, "bad \"name\""));
    }

    [Fact]
    public void Clean_reads_untidy_numbers()
    {
        Assert.True(Clean.Number("₹ 1,23,456.50", out var v)); Assert.Equal(123456.50m, v);
        Assert.True(Clean.Number("Rs. 500/-", out v)); Assert.Equal(500m, v);
        Assert.True(Clean.Number("25 MT", out v)); Assert.Equal(25m, v);
        Assert.True(Clean.Number("(1,000)", out v)); Assert.Equal(-1000m, v);
        Assert.False(Clean.Number("9/12", out _));
        Assert.False(Clean.Number("12-03-2026", out _));
        Assert.False(Clean.Number("abc", out _));
        Assert.Equal(Clean.NameKey("G.R. Enterprises"), Clean.NameKey("G.R ENTERPRISES"));
    }

    [Fact]
    public void Preview_is_written_with_colours()
    {
        using var t = new TempDb();
        var k = RawSheet.ReadWorkbook(MessyWorkbook(t))[0];
        var p = SmartImport.Analyze(t.Repo, k, 4, ImportTarget.Contractors, SmartImport.AutoMap(k, 4, ImportTarget.Contractors, Array.Empty<CustomField>()));
        var out1 = t.File("check.xlsx");
        SmartImport.WritePreview(p, out1);
        using var wb = new XLWorkbook(out1);
        Assert.Equal("Repeated in file", wb.Worksheet("Import check").Cell(4, 2).GetString());
    }
}
