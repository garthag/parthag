using System.Data;
using System.Globalization;
using ClosedXML.Excel;

namespace Bf3Billing.Core.Bsl;

/// <summary>What the BSL workbooks hold and what importing them will do (nothing is saved yet).</summary>
public sealed class BslPlan
{
    public string? BomFile { get; init; }
    public string? ContractorFile { get; init; }
    public BomWorkbook? Bom { get; init; }
    public ContractorWorkbook? Contractors { get; init; }
    public List<(string Ref, string Agency, string Description, decimal? Tonnes, string Remarks, int Row)> Extras { get; } = new();
    public List<DataIssue> Issues { get; } = new();

    public IEnumerable<(BomPackage Package, string DrawingNo, string Sheet, string Rev, List<StdBomRow> Rows)> Drawings()
    {
        if (Bom == null) yield break;
        foreach (var p in Bom.Packages)
        {
            var groups = p.Rows.Concat(p.DrawingOnlyRows)
                .GroupBy(r => (Drawing: r.DrawingNo ?? BslImport.NoDrawing, Sheet: r.SheetNo ?? "", Rev: r.Revision ?? ""));
            foreach (var g in groups) yield return (p, g.Key.Drawing, g.Key.Sheet, g.Key.Rev, g.ToList());
        }
    }

    /// <summary>One row per BOM sheet: lines, drawings, sets, tonnes as standardised vs the sheet's own total.</summary>
    public DataTable Reconciliation()
    {
        var t = new DataTable("BOM check");
        t.Columns.Add("Sheet"); t.Columns.Add("Header row", typeof(int)); t.Columns.Add("Lines", typeof(int)); t.Columns.Add("Counted lines", typeof(int));
        t.Columns.Add("Drawings", typeof(int)); t.Columns.Add("Sets", typeof(int)); t.Columns.Add("Per set t", typeof(decimal));
        t.Columns.Add("Sheet total t", typeof(decimal)); t.Columns.Add("Difference t", typeof(decimal)); t.Columns.Add("BOM t (all sets)", typeof(decimal)); t.Columns.Add("Result");
        if (Bom == null) return t;
        foreach (var p in Bom.Packages)
        {
            var perSet = (decimal)Math.Round(p.CountedWeightKg / 1000.0, 3);
            var sheet = p.SheetTotalMt is { } st ? (decimal)Math.Round(st, 3) : (decimal?)null;
            var diff = sheet is { } s ? perSet - s : (decimal?)null;
            var drawings = p.Rows.Concat(p.DrawingOnlyRows).Select(r => (r.DrawingNo, r.SheetNo, r.Revision)).Distinct().Count();
            var result = sheet == null ? "No total in the sheet" : Math.Abs(diff!.Value) <= Math.Max(0.01m, sheet.Value * 0.005m) ? "Matches" : "Differs – see issues";
            t.Rows.Add(p.SheetName, p.HeaderRow, p.Rows.Count, p.Rows.Count(r => !r.IsExcluded), drawings, p.NoOfSets, perSet,
                (object?)sheet ?? DBNull.Value, (object?)diff ?? DBNull.Value, perSet * p.NoOfSets, result);
        }
        return t;
    }

    public DataTable IssueTable()
    {
        var t = new DataTable("Issues");
        t.Columns.Add("Severity"); t.Columns.Add("Workbook"); t.Columns.Add("Sheet"); t.Columns.Add("Row", typeof(int)); t.Columns.Add("Code"); t.Columns.Add("What was found and done");
        foreach (var i in Issues.OrderBy(i => i.Severity == "Warning" ? 0 : 1).ThenBy(i => i.Sheet).ThenBy(i => i.Row))
            t.Rows.Add(i.Severity, i.Sheet.StartsWith("[C]") ? "Contractor master" : "BOM", i.Sheet.Replace("[C] ", ""), i.Row, i.Code, i.Message);
        return t;
    }

    /// <summary>RA bills that will be created: one per work order and RA column.</summary>
    public DataTable RaBills()
    {
        var t = new DataTable("RA bills");
        t.Columns.Add("Work order ref"); t.Columns.Add("Bill no."); t.Columns.Add("Lines", typeof(int)); t.Columns.Add("Tonnes", typeof(decimal));
        t.Columns.Add("Agency amount ₹", typeof(decimal)); t.Columns.Add("Client amount ₹", typeof(decimal)); t.Columns.Add("Lines without agency rate", typeof(int));
        if (Contractors == null) return t;
        foreach (var g in Contractors.Boq.Where(b => b.WorkOrderUniqueNo != null)
                     .SelectMany(b => b.Ra.Select(r => (b, Ra: r.Key, Qty: r.Value)))
                     .GroupBy(x => (x.b.WorkOrderUniqueNo, x.Ra)).OrderBy(g => g.Key.WorkOrderUniqueNo).ThenBy(g => g.Key.Ra))
            t.Rows.Add(g.Key.WorkOrderUniqueNo, "RA-" + g.Key.Ra, g.Count(), g.Sum(x => x.Qty),
                g.Sum(x => BillMath.LineAmount(x.Qty, x.b.InternalRate ?? 0)), g.Sum(x => BillMath.LineAmount(x.Qty, x.b.ExternalRate ?? 0)),
                g.Count(x => x.b.InternalRate == null));
        return t;
    }
}

public sealed class BslResult
{
    public int Contractors, WorkOrders, BomRows, BomParts, BoqItems, Bills, BillsSkipped;
    public List<string> Notes { get; } = new();
    public string Summary =>
        $"Contractors: {Contractors} · Work orders: {WorkOrders} · BOM drawings: {BomRows} ({BomParts:N0} part lines) · " +
        $"BOQ items: {BoqItems} · RA bills added: {Bills}" + (BillsSkipped > 0 ? $" ({BillsSkipped} already saved, kept)" : "") +
        (Notes.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, Notes) : "");
}

/// <summary>
/// Imports the BSL BF#3 workbooks: the multi-sheet master BOM (drawings with their part lines) and the
/// contractor master (contractors, work orders, the BOQ with client and agency rates, the RA-1…RA-n quantities,
/// the DRAWING scope sheet and the REMARKS extra items). Built on the readers in this folder.
/// </summary>
public static class BslImport
{
    public const string NoDrawing = "(no drawing no.)";

    public static BslPlan Analyze(string? bomPath, string? contractorPath)
    {
        var plan = new BslPlan
        {
            BomFile = bomPath, ContractorFile = contractorPath,
            Bom = bomPath == null ? null : BomWorkbookReader.Read(bomPath),
            Contractors = contractorPath == null ? null : ContractorWorkbookReader.Read(contractorPath),
        };
        if (plan.Bom != null) plan.Issues.AddRange(plan.Bom.Issues);
        if (plan.Contractors != null)
        {
            plan.Issues.AddRange(plan.Contractors.Issues.Select(i => i with { Sheet = "[C] " + i.Sheet }));
            ReadRemarksSheet(contractorPath!, plan);
            var unused = plan.Contractors.Boq.Count(b => b.Ra.Count > 0 && b.InternalRate == null);
            if (unused > 0)
                plan.Issues.Add(new("[C] Sheet2", 0, "Warning", "RA_WITHOUT_AGENCY_RATE",
                    $"{unused} BOQ item(s) have RA quantities but no Agency Rate: their bill lines get rate 0 – enter the rate in the bill (or the BOQ) to price them."));
            plan.Issues.Add(new("[C] Sheet2", 0, "Info", "RA_PER_BILL",
                "RA columns are read as the quantity of each bill (not a running total): in most items billed more than once the quantity goes down from one RA to the next, which a running total cannot do."));
            plan.Issues.Add(new("[C] Sheet2", 0, "Info", "RA_BILL_DATE",
                "The workbook has no RA bill dates: imported bills get the work order date (today if the WO has none) – correct them in each bill."));
        }
        return plan;
    }

    /// <summary>The REMARKS sheet: extra items not in the drawing / DOW list (ref, agency, description, tonnes, notes).</summary>
    static void ReadRemarksSheet(string path, BslPlan plan)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Trim().Equals("REMARKS", StringComparison.OrdinalIgnoreCase));
        if (ws == null) return;
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        for (var r = 1; r <= last; r++)
        {
            var row = ws.Row(r);
            var desc = ExcelUtil.Text(row.Cell(3));
            if (desc == null) continue;
            var t = ExcelUtil.Num(row.Cell(4));
            var notes = string.Join(" · ", new[] { ExcelUtil.Text(row.Cell(5)), ExcelUtil.Text(row.Cell(6)) }.Where(x => x != null));
            plan.Extras.Add((ExcelUtil.Text(row.Cell(1)) ?? "", ExcelUtil.Text(row.Cell(2))?.ToUpperInvariant() ?? "", desc, t is { } d ? (decimal)d : null, notes, r));
        }
        if (plan.Extras.Count > 0)
            plan.Issues.Add(new("[C] REMARKS", 0, "Info", "EXTRA_ITEMS", $"{plan.Extras.Count} extra item(s) not in the DOW imported as BOQ items without a work order (source 'REMARKS sheet')."));
    }

    static string? ActivityName(string? type) => type?.Trim().ToUpperInvariant() switch
    {
        "ERECTION" => "Erection",
        "DISMANTLING" => "Dismantling",
        "MODIFICATION/FABRICATION" or "FABRICATION" => "Fabrication",
        "PAINTING" => "Painting",
        _ => null,
    };

    static decimal Dec(double? v, int places = 3) => v is { } d ? Math.Round((decimal)d, places) : 0m;
    static decimal? NDec(double? v, int places = 3) => v is { } d ? Math.Round((decimal)d, places) : null;

    public static BslResult Apply(BillingRepository repo, BslPlan plan)
    {
        var res = new BslResult();
        if (plan.Bom != null) ApplyBom(repo, plan, res);
        if (plan.Contractors != null) ApplyContractors(repo, plan, res);
        return res;
    }

    // ───────────────────────── BOM ─────────────────────────

    static void ApplyBom(BillingRepository repo, BslPlan plan, BslResult res)
    {
        repo.AddCustomField(Entities.BomItem, "Package title");
        repo.AddCustomField(Entities.BomItem, "Tonnes per set");
        var drawings = plan.Drawings().ToList();
        var items = new List<(BomItem Item, List<StdBomRow> Rows)>();
        foreach (var (p, drg, sheet, rev, rows) in drawings)
        {
            var kg = rows.Where(r => !r.IsExcluded && p.Rows.Contains(r)).Sum(r => r.TotalWeightKg ?? 0);
            var perSet = Math.Round((decimal)kg / 1000m, 3);
            var extra = new Dictionary<string, string> { ["Package title"] = p.Title };
            if (p.NoOfSets > 1) extra["Tonnes per set"] = perSet.ToString("0.000", CultureInfo.InvariantCulture);
            items.Add((new BomItem
            {
                System = p.SheetName.Trim(), DrawingNo = drg, Sheet = sheet, Rev = rev, Item = "",
                Tonnes = perSet * p.NoOfSets, Sets = p.NoOfSets,
                Description = rows.Select(r => r.DrawingDescription).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)) ?? "",
                Extra = Extras.Write(extra),
            }, rows));
        }
        var before = repo.BomItems().Select(b => b.Id).ToHashSet();
        repo.UpsertBom(items.Select(i => i.Item));
        repo.ReplaceBomParts(items.Where(i => i.Item.Id != 0).Select(i => (i.Item.Id, i.Rows.Select(ToPart))));
        res.BomRows = items.Count;
        res.BomParts = items.Sum(i => i.Rows.Count);
        var imported = items.Select(i => i.Item.Id).ToHashSet();
        var stale = repo.BomItems().Where(b => before.Contains(b.Id) && !imported.Contains(b.Id)).ToList();
        if (stale.Count > 0)
            res.Notes.Add($"{stale.Count} BOM row(s) saved earlier are not in this BOM (kept). Data › Delete unused BOM rows removes those no bill uses.");
        var total = items.Sum(i => i.Item.Tonnes);
        res.Notes.Add($"BOM: {items.Count:N0} drawings, {Fmt.Tonnes(total)} t in total (sheets with several sets counted for all sets).");
    }

    static BomPart ToPart(StdBomRow r) => new()
    {
        SourceSheet = r.SourceSheet, SourceRow = r.SourceRow, RowType = r.RowType, Counted = !r.IsExcluded,
        AssemblyMark = r.AssemblyMark ?? "", PartMark = r.PartMark ?? "", ItemNo = r.ItemNo ?? "",
        Description = r.ItemDescription ?? "", Section = r.SectionDrg ?? "", SectionStd = r.SectionStd ?? "",
        LengthMm = NDec(r.LengthMm, 1), WidthMm = NDec(r.WidthMm, 1), ThicknessMm = NDec(r.ThicknessMm, 2),
        Qty = NDec(r.Qty, 3), QtyText = r.QtyText ?? "", UnitWeightKg = NDec(r.UnitWeightKg, 3), TotalWeightKg = NDec(r.TotalWeightKg, 3),
        Grade = r.MaterialGrade ?? "", GradeRaw = r.MaterialGradeRaw ?? "", Remarks = r.Remarks ?? "", Flags = r.FlagText,
    };

    // ───────────────────────── Contractor master ─────────────────────────

    static void ApplyContractors(BillingRepository repo, BslPlan plan, BslResult res)
    {
        var cw = plan.Contractors!;
        foreach (var f in new[] { "Contact person", "Email" }) repo.AddCustomField(Entities.Contractor, f);
        foreach (var f in new[] { "Ref no", "Work category", "Actual work done", "Actual paid" }) repo.AddCustomField(Entities.WorkOrder, f);

        // Contractors: one per firm (the sheet repeats the firm on every work order row).
        var contractors = repo.Contractors();
        var byCode = new Dictionary<string, Contractor>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cw.Contractors)
        {
            var k = contractors.FirstOrDefault(x => Clean.NameKey(x.Name) == Clean.NameKey(c.Name)) ?? new Contractor { Name = c.Name.Trim() };
            var reserved = c.Code is "BSL" or "MCK" or "ERC" or "SMS";
            if (!(reserved && k.Code.Length > 0)) k.Code = c.Code;
            if (reserved && k.Id != 0 && k.Code != c.Code)
                res.Notes.Add($"{k.Name}: the file gives code {c.Code} (MCK's own code); kept {k.Code}.");
            k.Pan = c.PAN ?? k.Pan;
            k.Gstin = c.GSTIN ?? k.Gstin;
            k.Phone = c.ContactNo ?? k.Phone;
            k.Extra = Extras.Merge(k.Extra, new Dictionary<string, string> { ["Contact person"] = c.ContactPerson ?? "", ["Email"] = c.Email ?? "" });
            var isNew = k.Id == 0;
            repo.SaveContractor(k);
            if (isNew) contractors.Add(k);
            byCode[c.Code] = k;
            res.Contractors++;
        }

        // Work orders, matched by WO number (or by the "contractor · service" name used when the number is blank).
        var wos = repo.WorkOrders();
        var byUnique = new Dictionary<string, WorkOrder>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in cw.WorkOrders)
        {
            if (!byCode.TryGetValue(w.ContractorCode, out var k)) continue;
            var service = w.ServiceType?.Trim() ?? "";
            var no = w.WorkOrderNo?.Trim() is { Length: > 0 } n ? n : $"{k.Name} · {(service.Length > 0 ? service : w.WorkCategory ?? w.UniqueNo)}";
            var wo = wos.FirstOrDefault(x => Clean.Key(x.WoNo) == Clean.Key(no))
                     ?? wos.FirstOrDefault(x => Extras.Get(x.Extra, "Ref no").Equals(w.UniqueNo, StringComparison.OrdinalIgnoreCase))
                     ?? new WorkOrder { WoNo = no, Status = "Open" };
            wo.ContractorId = k.Id;
            if (service.Length > 0) wo.Service = service;
            if (w.WOValue is { } v) wo.Value = Math.Round(v, 2);
            if (w.WODate is { } d) wo.WoDate = d;
            if (w.Remarks != null) wo.Remarks = w.Remarks;
            wo.Extra = Extras.Merge(wo.Extra, new Dictionary<string, string>
            {
                ["Ref no"] = w.UniqueNo, ["Work category"] = w.WorkCategory?.Trim() ?? "",
                ["Actual work done"] = w.ActualWorkDone?.ToString(CultureInfo.InvariantCulture) ?? "",
                ["Actual paid"] = w.ActualPaid?.ToString(CultureInfo.InvariantCulture) ?? "",
            });
            var isNew = wo.Id == 0;
            repo.SaveWorkOrder(wo);
            if (isNew) wos.Add(wo);
            byUnique[w.UniqueNo] = wo;
            res.WorkOrders++;
        }

        // BOQ items (re-import updates them in place: same work order, group, spec item and description).
        var existingBoq = repo.BoqItems();
        string BoqKey(long? wo, string src, string group, string spec, string desc) => $"{wo}|{src}|{Clean.Key(group)}|{Clean.Key(spec)}|{Clean.Key(desc)}";
        // Saved items are keyed the same way as new ones (repeats get their row number), so a re-import updates them in place.
        var boqByKey = new Dictionary<string, BoqItem>();
        foreach (var q in existingBoq.OrderBy(q => q.SourceRow))
        {
            var k = BoqKey(q.WorkOrderId, q.Source, q.GroupName, q.SpecItem, q.Description);
            if (!boqByKey.TryAdd(k, q)) boqByKey.TryAdd(k + "|row" + q.SourceRow.ToString(CultureInfo.InvariantCulture), q);
        }
        var boqForRec = new Dictionary<BoqRec, BoqItem>();
        // Two rows with the same work order, group, spec item and description are kept apart by their row number.
        var usedKeys = new HashSet<string>();
        string Unique(string key, int row)
        {
            if (usedKeys.Add(key)) return key;
            var k2 = key + "|row" + row.ToString(CultureInfo.InvariantCulture);
            usedKeys.Add(k2);
            return k2;
        }
        foreach (var b in cw.Boq.Where(b => !b.IsGroupHeader))
        {
            long? woId = b.WorkOrderUniqueNo != null && byUnique.TryGetValue(b.WorkOrderUniqueNo, out var wo) ? wo.Id : null;
            var key = Unique(BoqKey(woId, "Sheet2", b.GroupName ?? "", b.TechSpecItem ?? "", b.Description ?? ""), b.SourceRow);
            var q = boqByKey.GetValueOrDefault(key) ?? new BoqItem();
            q.WorkOrderId = woId; q.Source = "Sheet2"; q.SourceRow = b.SourceRow; q.SortOrder = b.SortOrder;
            q.SpecItem = b.TechSpecItem ?? ""; q.GroupName = b.GroupName ?? ""; q.Description = b.Description ?? "";
            q.Unit = b.Unit ?? ""; q.Qty = b.Qty; q.UnitWeightKg = b.UnitWeightKg; q.ScopeTonnes = b.R1WeightTon;
            q.ExternalRate = b.ExternalRate; q.InternalRate = b.InternalRate; q.Activity = ActivityName(b.ActivityType) ?? b.ActivityType ?? "";
            q.AgencyRef = b.AgencyRef ?? b.WorkOrderUniqueNo ?? ""; q.DrawingQty = b.DrawingQty ?? ""; q.Remarks = b.Remarks ?? "";
            repo.SaveBoqItem(q);
            boqByKey[key] = q;
            boqForRec[b] = q;
            res.BoqItems++;
        }
        var sort = 100_000;
        foreach (var s in cw.DrawingScope)
        {
            var group = string.Join(" › ", new[] { s.Area, s.SubArea }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var key = Unique(BoqKey(null, "DRAWING sheet", group, s.ContractItemNo ?? "", s.Description ?? ""), s.SourceRow);
            var q = boqByKey.GetValueOrDefault(key) ?? new BoqItem();
            q.Source = "DRAWING sheet"; q.SourceRow = s.SourceRow; q.SortOrder = sort++; q.GroupName = group; q.SpecItem = s.ContractItemNo ?? "";
            q.Description = s.Description ?? ""; q.DrawingQty = s.Qty ?? ""; q.ScopeTonnes = s.WeightKg is { } kg ? Math.Round(kg / 1000m, 3) : null;
            q.AgencyRef = s.Agency ?? ""; q.Remarks = string.Join(" · ", new[] { s.DrawingNo, s.RefNo, s.Remarks }.Where(x => !string.IsNullOrWhiteSpace(x)));
            q.Unit = "t";
            repo.SaveBoqItem(q);
            boqByKey[key] = q;
            res.BoqItems++;
        }
        foreach (var x in plan.Extras)
        {
            var key = Unique(BoqKey(null, "REMARKS sheet", "Extra items", x.Ref, x.Description), x.Row);
            var q = boqByKey.GetValueOrDefault(key) ?? new BoqItem();
            q.Source = "REMARKS sheet"; q.SourceRow = x.Row; q.SortOrder = sort++; q.GroupName = "Extra items"; q.SpecItem = x.Ref;
            q.Description = x.Description; q.ScopeTonnes = x.Tonnes; q.AgencyRef = x.Agency; q.Remarks = x.Remarks; q.Unit = "t";
            repo.SaveBoqItem(q);
            boqByKey[key] = q;
            res.BoqItems++;
        }

        // RA bills: one bill per work order and RA column, lines = BOQ items with a quantity in that column.
        var acts = repo.Activities().ToDictionary(a => a.Name, a => a.Id, StringComparer.OrdinalIgnoreCase);
        var savedBills = repo.BillSummaries().Select(b => (b.WorkOrderId, Clean.Key(b.BillNo))).ToHashSet();
        var file = Path.GetFileName(plan.ContractorFile);
        foreach (var g in cw.Boq.Where(b => b.WorkOrderUniqueNo != null && boqForRec.ContainsKey(b))
                     .SelectMany(b => b.Ra.Select(r => (Rec: b, Ra: r.Key, Qty: r.Value)))
                     .GroupBy(x => (x.Rec.WorkOrderUniqueNo!, x.Ra)).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Ra))
        {
            if (!byUnique.TryGetValue(g.Key.Item1, out var wo)) continue;
            var billNo = "RA-" + g.Key.Ra.ToString(CultureInfo.InvariantCulture);
            if (savedBills.Contains((wo.Id, Clean.Key(billNo)))) { res.BillsSkipped++; continue; }
            var noRate = g.Count(x => x.Rec.InternalRate == null);
            var bill = new Bill
            {
                WorkOrderId = wo.Id, BillNo = billNo, BillType = "RA bill", BillDate = wo.WoDate ?? DateTime.Today,
                Status = BillStatus.Certified, GstPct = repo.GetSettingDecimal("DefaultGstPct", 18),
                RetentionPct = repo.GetSettingDecimal("DefaultRetentionPct", 0), TdsPct = repo.GetSettingDecimal("DefaultTdsPct", 0),
                Remarks = $"Imported from {file} (Sheet2, column {billNo}). Bill date not in the workbook – set to the WO date." +
                          (noRate > 0 ? $" {noRate} line(s) have no agency rate." : ""),
            };
            var lines = g.Select(x => new BillLine
            {
                BoqItemId = boqForRec[x.Rec].Id, Description = x.Rec.Description ?? "",
                ActivityId = ActivityName(x.Rec.ActivityType) is { } an && acts.TryGetValue(an, out var aid) ? aid : null,
                Unit = "t", Qty = Math.Round(x.Qty, 3), Rate = x.Rec.InternalRate ?? 0,
                Remarks = x.Rec.InternalRate == null ? "Agency rate not in the BOQ" : "",
                Extra = x.Rec.ExternalRate is { } er ? Extras.Write(new Dictionary<string, string> { ["Client rate"] = er.ToString("0.##", CultureInfo.InvariantCulture) }) : "{}",
            }).ToList();
            repo.SaveBill(bill, lines);
            savedBills.Add((wo.Id, Clean.Key(billNo)));
            res.Bills++;
        }
        repo.AddCustomField(Entities.BillLine, "Client rate");
        foreach (var w in repo.GetDashboard().WorkOrders.Where(w => w.Value > 0 && w.Gross > w.Value))
            res.Notes.Add($"{w.WoNo} ({w.Contractor}): billed ₹ {Fmt.Money(w.Gross)} is more than the WO value ₹ {Fmt.Money(w.Value)}.");
        foreach (var w in repo.GetDashboard().WorkOrders.Where(w => w.Value == 0 && w.Gross > 0))
            res.Notes.Add($"{w.WoNo} ({w.Contractor}): billed ₹ {Fmt.Money(w.Gross)} but the work order has no value or number in the file.");
        var over = repo.BillSummaries().Sum(b => b.OverScopeLines);
        if (over > 0) res.Notes.Add($"{over} bill line(s) take a BOQ item or BOM drawing above its scope – see Bills › Over BOM scope and Reports › Lines above BOM scope.");
    }

    /// <summary>Writes the check (sheets, RA bills, issues) to one workbook.</summary>
    public static void WriteReport(BslPlan plan, string path) =>
        ExcelIO.WriteTables(path, plan.Reconciliation(), plan.RaBills(), plan.IssueTable());
}
