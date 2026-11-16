using System.Data;
using BSL.Core.Data;
using BSL.Core.Excel;
using BSL.Core.Import;
using BSL.Core.Reports;

// bsl bom <bom.xlsx> <out.xlsx>                 standardise the BOM workbook (all sheets same layout)
// bsl contractor <contractor.xlsx>              read & check the contractor workbook
// bsl import <conn> <bom.xlsx> <contractor.xlsx> create database and import both workbooks
// bsl pdftest <bom.xlsx> <out.pdf>               render a sample PDF report (no database)
// bsl dbtest <conn> <folder>                      load every screen, run every report to PDF, CRUD + UDF round trip
// bsl export <conn> <out.xlsx>                     normalised masters from the database to one workbook
// bsl selftest                                     formula engine + standardisation rules (no files needed)
PdfReport.LogoPath = Environment.GetEnvironmentVariable("BSL_LOGO");
PdfReport.CompanyAddress = Environment.GetEnvironmentVariable("BSL_ADDRESS") ?? "";
TableExcel.LogoPath = PdfReport.LogoPath;
switch (args.FirstOrDefault())
{
    case "selftest":
    {
        var t = new DataTable();
        t.Columns.Add("ContractorCode", typeof(string)); t.Columns.Add("ContractorName", typeof(string));
        t.Columns.Add("ExternalRate", typeof(decimal)); t.Columns.Add("InternalRate", typeof(decimal)); t.Columns.Add("R1WeightTon", typeof(decimal));
        t.Columns.Add("LengthMm", typeof(decimal)); t.Columns.Add("WidthMm", typeof(decimal)); t.Columns.Add("ThicknessMm", typeof(decimal));
        t.Columns.Add("Qty", typeof(decimal)); t.Columns.Add("TotalWeightKg", typeof(decimal)); t.Columns.Add("Target", typeof(decimal));
        t.Rows.Add("GRE", "G.R ENTERPRISES", 19500m, 8000m, 40m, 2527m, 403m, 25m, 10m, 1655m, DBNull.Value);
        t.Rows.Add("MDE", DBNull.Value, DBNull.Value, 7000m, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        (string Key, string F)[] fs =
        [
            ("U_Margin", "([ExternalRate] - [InternalRate]) * [R1WeightTon]"),
            ("U_MarginPct", "IIF([ExternalRate] > 0, ([ExternalRate] - [InternalRate]) * 100 / [ExternalRate], 0)"),
            ("U_PlateWtKg", "IIF([ThicknessMm] > 0 AND [WidthMm] > 0, [LengthMm] * [WidthMm] * [ThicknessMm] * ISNULL([Qty],1) * 7.85 / 1000000, 0)"),
            ("U_WtDiffKg", "IIF([U_PlateWtKg] > 0, ISNULL([TotalWeightKg],0) - [U_PlateWtKg], 0)"),
            ("U_Label", "[ContractorCode] + ' - ' + ISNULL([ContractorName],'?')")
        ];
        foreach (var (k, f) in fs)
        {
            var err = BSL.Core.Formula.FormulaEngine.Validate(t, f, k);
            Console.WriteLine($"{k}: {(err ?? "valid")}");
            BSL.Core.Formula.FormulaEngine.AddFormulaColumn(t, k, k, f);
        }
        foreach (DataRow r in t.Rows) Console.WriteLine(string.Join(" | ", fs.Select(x => $"{x.Key}={r[x.Key]}")));
        Console.WriteLine("bad: " + BSL.Core.Formula.FormulaEngine.Validate(t, "[Nope] * 2"));
        Console.WriteLine("self: " + BSL.Core.Formula.FormulaEngine.Validate(t, "[U_Margin] + 1", "U_Margin"));
        Console.WriteLine("apply rows: " + BSL.Core.Formula.FormulaEngine.Apply(t, "Target", "[LengthMm] * 2"));
        Console.WriteLine("target: " + string.Join(",", t.Rows.Cast<DataRow>().Select(r => r["Target"])));
        Console.WriteLine("eval: " + BSL.Core.Formula.FormulaEngine.Evaluate(t.Rows[0], "[U_Margin] / 1000"));
        foreach (var d in new[] { "ERC/MCK/BSL/STH/F-001_R3 (SH. 1OF 1)", "ERC MCK BSL  STH F-004 (SHT.1 OF 1)R1", "BSL-BF3-REV-SMS-D-R-M-4028-R1, Sht-2/3-B",
                                  "BSL-BF3-REVV-SMS-D-R-M-4033, R1, Sht-1/1", "16282205R0", "ERC-SMS-BSL-HBS-F-002 (SHT.2 OF 2)", "30038076_001_S002" })
            Console.WriteLine($"  {d,-45} -> {Standardizer.ParseDrawing(d)}");
        foreach (var sct in new[] { "PL THK 25", "PLATE 16 THK. x 556 x 7552", "PL.398x10", "PLT16*230", "ISMC 150", "WPB200*200*61.", "Pipe Ø60.3x5.54 Thk",
                                    "PIPE 0168.3 x 5.56 x 212 Lg.", "SORF Flange DN-600 # 150 Class", "HEX HEAD BOLT M20 x 100 Lg.", "20", "ISA75X75X6" })
            Console.WriteLine($"  {sct,-35} -> {Standardizer.Section(sct, null)}");
        foreach (var g in new[] { "15:2002 Gr.2 (2009) + Z35", "IS 2002. Gr .- 2", "IS:2002, Gr2", "IS2062 E250 A", "152002, Gr. 2 / SA105" })
            Console.WriteLine($"  {g,-35} -> {Standardizer.Grade(g, out var ch)} {ch}");
        return 0;
    }
    case "bom" when args.Length >= 3:
    {
        var wb = BomWorkbookReader.Read(args[1]);
        StandardBomWriter.Write(wb, args[2]);
        foreach (var p in wb.Packages)
            Console.WriteLine($"{p.SheetName,-30} hdr={p.HeaderRow} rows={p.Rows.Count,5} excl={p.Rows.Count(r => r.IsExcluded),4} sets={p.NoOfSets} wt={p.CountedWeightKg / 1000,10:0.000} MT sheet={p.SheetTotalMt,10:0.000}");
        Console.WriteLine($"issues: {wb.Issues.Count}");
        foreach (var g in wb.Issues.GroupBy(i => i.Code)) Console.WriteLine($"  {g.Key,-30} {g.Count()}");
        return 0;
    }
    case "contractor" when args.Length >= 2:
    {
        var wb = ContractorWorkbookReader.Read(args[1]);
        Console.WriteLine($"contractors {wb.Contractors.Count}, work orders {wb.WorkOrders.Count}, BOQ {wb.Boq.Count} (groups {wb.Boq.Count(b => b.IsGroupHeader)}), " +
                          $"RA lines {wb.Boq.Sum(b => b.Ra.Count)} in {wb.Boq.SelectMany(b => b.Ra.Keys).Distinct().Count()} RA nos, scope rows {wb.DrawingScope.Count}");
        foreach (var c in wb.Contractors) Console.WriteLine($"  {c.Code,-5} {c.Name}");
        foreach (var w in wb.WorkOrders) Console.WriteLine($"  {w.UniqueNo} {w.ContractorCode} {w.ServiceType} {w.WorkOrderNo} {w.WODate:dd-MM-yyyy} {w.WOValue}");
        foreach (var b in wb.Boq.Take(8)) Console.WriteLine($"  [{b.GroupName}] {b.TechSpecItem} {b.Description} {b.Unit} R1={b.R1WeightTon} ext={b.ExternalRate} int={b.InternalRate} wo={b.WorkOrderUniqueNo} ra={string.Join(",", b.Ra.Select(x => $"{x.Key}:{x.Value}"))}");
        foreach (var g in wb.Issues.GroupBy(i => i.Code)) Console.WriteLine($"  {g.Key,-28} {g.Count(),4}  e.g. {g.First().Message}");
        return 0;
    }
    case "import" when args.Length >= 4:
    {
        var db = new Database(args[1]);
        db.EnsureCreated();
        var svc = new ImportService(db);
        svc.Progress += Console.WriteLine;
        Console.WriteLine(svc.ImportContractors(ContractorWorkbookReader.Read(args[3])).Text);
        Console.WriteLine(svc.ImportBom(BomWorkbookReader.Read(args[2])).Text);
        return 0;
    }
    case "dbtest" when args.Length >= 3:
    {
        var db = new Database(args[1]);
        db.EnsureCreated();   // second run must be idempotent
        var repo = new EntityRepository(db);
        foreach (var e in BSL.Core.Metadata.EntityCatalog.All)
        {
            var t = repo.Load(e);
            var missing = e.Fields.Where(f => !t.Columns.Contains(f.Name)).Select(f => f.Name).ToList();
            var lookups = e.Fields.Where(f => f.Lookup != null).Select(f => repo.LookupData(f.Lookup!).Rows.Count);
            Console.WriteLine($"{e.Name,-16} rows={t.Rows.Count,6} cols={t.Columns.Count,3} udfCols={t.Columns.Cast<DataColumn>().Count(c => c.ColumnName.StartsWith("U_")),2} lookups=[{string.Join(",", lookups)}] {(missing.Count > 0 ? "MISSING " + string.Join(",", missing) : "ok")}");
            foreach (var (child, fk) in e.Children)
                db.Query($"SELECT TOP 1 * FROM dbo.{BSL.Core.Metadata.EntityCatalog.Get(child).View} WHERE {fk} = 1");
        }
        foreach (var r in ReportCatalog.All)
        {
            var t = db.Query(r.Sql);
            var path = Path.Combine(args[2], "report_" + string.Concat(r.Name.Where(char.IsLetterOrDigit)) + ".pdf");
            PdfReport.Table(path, r.Name, r.Description, t, PdfReport.AutoColumns(t), r.GroupBy, r.Landscape);
            Console.WriteLine($"report {r.Name,-45} rows={t.Rows.Count,6} pdf={new FileInfo(path).Length / 1024} KB");
        }
        // CRUD round trip on Contractor with UDF + formula
        var udf = new UdfRepository(db);
        var def = new UdfDefinition { EntityName = "Contractor", FieldKey = "U_TestRating", Caption = "Rating", DataType = UdfType.Number, SortOrder = 1 };
        udf.Save(def);
        var fdef = new UdfDefinition { EntityName = "Contractor", FieldKey = "U_TestLabel", Caption = "Label", DataType = UdfType.Formula, Formula = "[ContractorCode] + ' / ' + ISNULL([GSTIN],'no GST') + ' / ' + CONVERT(ISNULL([U_TestRating],0), 'System.String')", SortOrder = 2 };
        udf.Save(fdef);
        var ce = BSL.Core.Metadata.EntityCatalog.Contractor;
        var tc = repo.Load(ce);
        var nr = tc.NewRow(); nr["ContractorCode"] = "TST"; nr["Name"] = "Test Agency"; nr["ContractorType"] = "Executing Agency"; nr["IsActive"] = true; nr["U_TestRating"] = 4.5m;
        tc.Rows.Add(nr);
        Console.WriteLine("save insert: " + repo.Save(ce, tc));
        tc = repo.Load(ce, "ContractorCode = @c", ("@c", "TST"));
        Console.WriteLine($"reloaded: id={tc.Rows[0]["Id"]} rating={tc.Rows[0]["U_TestRating"]} label={tc.Rows[0]["U_TestLabel"]}");
        tc.Rows[0]["GSTIN"] = "20AAAAA0000A1Z5"; tc.Rows[0]["U_TestRating"] = 3m;
        Console.WriteLine("save update: " + repo.Save(ce, tc));
        tc = repo.Load(ce, "ContractorCode = @c", ("@c", "TST"));
        Console.WriteLine($"after update: label={tc.Rows[0]["U_TestLabel"]} modified={tc.Rows[0]["ModifiedOn"]}");
        var dup = repo.Load(ce); var d2 = dup.NewRow(); d2["ContractorCode"] = "TST"; d2["Name"] = "dup"; d2["ContractorType"] = "x"; d2["IsActive"] = true; dup.Rows.Add(d2);
        try { repo.Save(ce, dup); } catch (Exception ex) { Console.WriteLine("dup rejected: " + ex.Message.Split('\n')[0]); }
        tc.Rows[0].Delete();
        Console.WriteLine("save delete: " + repo.Save(ce, tc));
        udf.Delete(def.Id); udf.Delete(fdef.Id);
        // delete a contractor in use must fail with friendly text
        var used = repo.Load(ce, "ContractorCode = 'GRE'"); used.Rows[0].Delete();
        try { repo.Save(ce, used); } catch (Exception ex) { Console.WriteLine("in-use rejected: " + ex.Message.Split('\n')[0]); }
        // UDF formulas seeded for ContractItem / BomItem evaluate on real data
        var ci = repo.Load(BSL.Core.Metadata.EntityCatalog.ContractItem, "InternalRate IS NOT NULL");
        var r0 = ci.Rows[0];
        Console.WriteLine($"ContractItem formulas: margin%={r0["U_MarginPct"]} balAmt={r0["U_BalanceAmt"]} tag={r0["U_AgencyTag"]}");
        var bi = repo.Load(BSL.Core.Metadata.EntityCatalog.BomItem, "PackageId = (SELECT Id FROM dbo.Package WHERE PackageCode = 'STOVE')");
        Console.WriteLine($"BomItem formulas: plateWt={bi.Rows[0]["U_PlateWtKg"]} diff={bi.Rows[0]["U_WtDiffKg"]} total={bi.Rows[0]["TotalWeightKg"]}");
        return 0;
    }
    case "export" when args.Length >= 3:
    {
        // normalised contractor / contract / drawing data from the database → one workbook
        var db = new Database(args[1]);
        var repo = new EntityRepository(db);
        TableExcel.LookupText = lk => repo.LookupData(lk).Rows.Cast<DataRow>().ToDictionary(r => Convert.ToInt32(r[0]), r => r[1]?.ToString() ?? "");
        using var xl = new ClosedXML.Excel.XLWorkbook();
        foreach (var e in new[] { "Contractor", "WorkOrder", "ContractItem", "RaBill", "RaBillLine", "Package", "DrawingMaster", "DrawingRegister" })
        {
            var def = BSL.Core.Metadata.EntityCatalog.Get(e);
            TableExcel.AddSheet(xl, def.Caption, repo.Load(def), repo.AllFields(def));
        }
        xl.SaveAs(args[2]);
        Console.WriteLine("saved " + args[2]);
        return 0;
    }
    case "rulestest" when args.Length >= 2:
    {
        var db = new Database(args[1]);
        var repo = new EntityRepository(db);
        var rules = new EntityRules(db);
        var t = repo.Load(BSL.Core.Metadata.EntityCatalog.WorkOrder);
        t.ColumnChanged += (_, e) => rules.OnChanged("WorkOrder", e.Row, e.Column!.ColumnName);
        var r = t.NewRow();
        rules.OnNew("WorkOrder", r);
        t.Rows.Add(r);
        var act = Convert.ToInt32(db.Scalar("SELECT Id FROM dbo.Activity WHERE ActivityCode = 'ERE-EQP'"));
        r["ActivityId"] = act;
        Console.WriteLine($"WO new: {r["UniqueNo"]} service={r["ServiceType"]} category={r["WorkCategory"]}");
        var ci = repo.Load(BSL.Core.Metadata.EntityCatalog.ContractItem, "1 = 0");
        ci.ColumnChanged += (_, e) => rules.OnChanged("ContractItem", e.Row, e.Column!.ColumnName);
        var x = ci.NewRow(); x["Description"] = "test"; ci.Rows.Add(x);
        x["WorkOrderId"] = Convert.ToInt32(db.Scalar("SELECT Id FROM dbo.WorkOrder WHERE UniqueNo = 'BM07'"));
        Console.WriteLine($"BOQ item: activity={x["ActivityId"]} int={x["InternalRate"]} ext={x["ExternalRate"]} type={x["ActivityType"]}");
        return 0;
    }
    case "doctest" when args.Length >= 3:
    {
        var db = new Database(args[1]);
        db.EnsureCreated();
        var docs = new DocumentService(db);
        var drg = db.Query("SELECT TOP 1 Id, DrawingNo FROM dbo.DrawingMaster WHERE DrawingNo LIKE 'ERC%' ORDER BY Id").Rows[0];
        var dir = Path.Combine(args[2], "drawings"); Directory.CreateDirectory(dir);
        var f1 = Path.Combine(dir, ((string)drg[1]).Replace('/', '_') + " R2.pdf"); File.WriteAllText(f1, "%PDF-1.4 test");
        var f2 = Path.Combine(dir, "UNKNOWN-123.pdf"); File.WriteAllText(f2, "%PDF-1.4 test");
        foreach (var m in docs.MatchDrawings([f1, f2])) Console.WriteLine($"match {Path.GetFileName(m.File)} -> {m.DrawingNo} {m.Revision}");
        var id = docs.Upload("DrawingMaster", (int)drg[0], f1, "DRAWING", "R2", "test upload");
        var back = docs.Extract(id);
        Console.WriteLine($"uploaded #{id}, extracted {new FileInfo(back).Length} bytes, files on drawing = {docs.Count("Attachment", "DrawingMaster", (int)drg[0])}");
        var nid = docs.AddNote("DrawingMaster", (int)drg[0], "check revision with consultant", "query", true);
        Console.WriteLine($"note #{nid}: {docs.Notes("DrawingMaster", (int)drg[0]).Rows[0]["NoteText"]}");
        Console.WriteLine("view file count = " + db.Scalar("SELECT FileCount FROM dbo.vw_DrawingMaster WHERE Id = @i", ("@i", drg[0])));
        docs.DeleteAttachment(id); docs.DeleteNote(nid);
        Console.WriteLine("cleaned up: files = " + docs.Count("Attachment", "DrawingMaster", (int)drg[0]));
        return 0;
    }
    case "pdftest" when args.Length >= 3:
    {
        var wb = BomWorkbookReader.Read(args[1]);
        var t = new DataTable();
        t.Columns.Add("Package"); t.Columns.Add("Section"); t.Columns.Add("Lines", typeof(int)); t.Columns.Add("Wt per set (MT)", typeof(decimal));
        foreach (var g in wb.AllRows.Where(r => !r.IsExcluded).GroupBy(r => (r.Package, r.SectionStd)).OrderBy(g => g.Key.Package))
            t.Rows.Add(g.Key.Package, g.Key.SectionStd ?? "(blank)", g.Count(), Math.Round((decimal)g.Sum(r => r.TotalWeightKg ?? 0) / 1000, 3));
        PdfReport.Table(args[2], "Section-wise weight (per package)", "Test render", t, PdfReport.AutoColumns(t), "Package", true);
        PdfReport.RecordCard(Path.ChangeExtension(args[2], ".card.pdf"), "Contractor – GRE", "Record card test",
            [("General", [("Code", "GRE"), ("Name", "Test"), ("Type", "Executing Agency")]), ("Contact", [("Phone", "-")])]);
        Console.WriteLine("ok");
        return 0;
    }
}
Console.WriteLine("usage: bsl bom|contractor|import|pdftest ...");
return 1;
