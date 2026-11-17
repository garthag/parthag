using System.Data;
using System.Globalization;
using ClosedXML.Excel;

namespace Bf3Billing.Core;

public sealed class ImportResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<string> Messages { get; } = new();
    public string Summary => $"{Added} added, {Updated} updated, {Skipped} skipped." +
        (Messages.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, Messages.Take(30)) +
            (Messages.Count > 30 ? $"{Environment.NewLine}… and {Messages.Count - 30} more." : "") : "");
}

/// <summary>Excel export of any table, and Excel import of the BOM and of bills.</summary>
public static class ExcelIO
{
    // ───────────────────────── Export ─────────────────────────

    public static void WriteTables(string path, params DataTable[] tables)
    {
        using var wb = new XLWorkbook();
        foreach (var t in tables)
        {
            var name = SheetName(t.TableName, wb);
            var ws = wb.Worksheets.Add(name);
            for (var c = 0; c < t.Columns.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = t.Columns[c].ColumnName;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF0F3");
            }
            for (var r = 0; r < t.Rows.Count; r++)
                for (var c = 0; c < t.Columns.Count; c++)
                {
                    var v = t.Rows[r][c];
                    var cell = ws.Cell(r + 2, c + 1);
                    switch (v)
                    {
                        case DBNull: break;
                        case decimal d: cell.Value = d; cell.Style.NumberFormat.Format = DecimalFormat(t.Columns[c].ColumnName); break;
                        case int i: cell.Value = i; break;
                        case long l: cell.Value = l; break;
                        case double db: cell.Value = db; break;
                        case DateTime dt: cell.Value = dt; cell.Style.NumberFormat.Format = "dd-mm-yyyy"; break;
                        case bool b: cell.Value = b ? "Yes" : "No"; break;
                        default: cell.Value = v.ToString(); break;
                    }
                }
            ws.SheetView.FreezeRows(1);
            if (t.Columns.Count > 0) ws.Range(1, 1, Math.Max(1, t.Rows.Count + 1), t.Columns.Count).SetAutoFilter();
            ws.Columns().AdjustToContents(1, Math.Min(t.Rows.Count + 1, 500), 6, 60);
        }
        if (tables.Length == 0) wb.Worksheets.Add("Sheet1");
        wb.SaveAs(path);
    }

    static string DecimalFormat(string column) =>
        column.Contains(" t", StringComparison.Ordinal) || column is "Qty" or "Quantity" or "Tonnes" or "BOM t" ? "#,##0.000" :
        column.Contains('%') ? "0.0" : "#,##,##0.00";

    static string SheetName(string name, XLWorkbook wb)
    {
        var bad = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var s = new string((string.IsNullOrWhiteSpace(name) ? "Sheet" : name).Select(ch => bad.Contains(ch) ? '-' : ch).ToArray());
        if (s.Length > 31) s = s[..31];
        var baseName = s; var n = 2;
        while (wb.Worksheets.Contains(s)) { var suffix = $" ({n++})"; s = baseName[..Math.Min(baseName.Length, 31 - suffix.Length)] + suffix; }
        return s;
    }

    /// <summary>All the data in one workbook: masters, BOM, bills and bill lines.</summary>
    public static void ExportAll(BillingRepository repo, string path)
    {
        var k = new DataTable("Contractors");
        foreach (var n in new[] { "Code", "Name", "GSTIN", "PAN", "Phone", "Address", "Remarks" }) k.Columns.Add(n);
        foreach (var x in repo.Contractors()) k.Rows.Add(x.Code, x.Name, x.Gstin, x.Pan, x.Phone, x.Address, x.Remarks);

        var w = new DataTable("Work orders");
        w.Columns.Add("Work order"); w.Columns.Add("Contractor"); w.Columns.Add("Service");
        w.Columns.Add("Value ₹", typeof(decimal)); w.Columns.Add("WO date"); w.Columns.Add("Status"); w.Columns.Add("Remarks");
        foreach (var x in repo.WorkOrders()) w.Rows.Add(x.WoNo, x.ContractorName, x.Service, x.Value, Fmt.Date(x.WoDate), x.Status, x.Remarks);

        var b = BomTemplateTable();
        foreach (var x in repo.BomItems()) b.Rows.Add(x.System, x.DrawingNo, x.Sheet, x.Rev, x.Item, x.Tonnes);

        var bills = new DataTable("Bills");
        foreach (var n in new[] { "Bill no.", "Date", "Contractor", "Work order", "Type", "Status" }) bills.Columns.Add(n);
        bills.Columns.Add("Lines", typeof(int));
        foreach (var n in new[] { "Gross ₹", "Net payable ₹", "Paid ₹", "Due ₹" }) bills.Columns.Add(n, typeof(decimal));
        bills.Columns.Add("Remarks");
        foreach (var x in repo.BillSummaries().OrderBy(x => x.BillDate))
            bills.Rows.Add(x.BillNo, Fmt.Date(x.BillDate), x.Contractor, x.WorkOrder, x.BillType, x.Status, x.Lines, x.Gross, x.Net, x.Paid, x.Due, x.Remarks);

        WriteTables(path, k, w, b, BillLinesForExport(repo), bills, Reports.Run(repo, "Work order-wise abstract"));
    }

    /// <summary>Bill lines in the same layout as the bill import, so an export can be imported again.</summary>
    public static DataTable BillLinesForExport(BillingRepository repo)
    {
        var t = BillTemplateTable();
        t.TableName = "Bill lines";
        var acts = repo.Activities().ToDictionary(a => a.Id, a => a.Name);
        var wos = repo.WorkOrders().ToDictionary(w => w.Id);
        var bom = repo.BomItems().ToDictionary(i => i.Id);
        foreach (var s in repo.BillSummaries().OrderBy(s => s.BillDate).ThenBy(s => s.Id))
        {
            var (b, lines) = repo.GetBill(s.Id)!.Value;
            foreach (var l in lines)
            {
                var i = l.BomItemId.HasValue ? bom.GetValueOrDefault(l.BomItemId.Value) : null;
                t.Rows.Add(wos[b.WorkOrderId].WoNo, b.BillNo, b.BillType, b.BillDate.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture), b.Status,
                    b.GstPct, b.RetentionPct, b.TdsPct, b.OtherDeduction, b.PaidAmount, Fmt.Date(b.PaidOn),
                    i?.System ?? "", i?.DrawingNo ?? "", i?.Sheet ?? "", i?.Rev ?? "", i?.Item ?? "",
                    l.IsBom ? "" : l.Description, l.ActivityId.HasValue ? acts.GetValueOrDefault(l.ActivityId.Value, "") : "", l.Unit, l.Qty, l.Rate, l.Remarks);
            }
        }
        return t;
    }

    public static DataTable BomTemplateTable()
    {
        var t = new DataTable("BOM");
        foreach (var n in new[] { "System", "Drawing no", "Sheet", "Rev", "Item" }) t.Columns.Add(n);
        t.Columns.Add("Tonnes", typeof(decimal));
        return t;
    }

    public static DataTable BillTemplateTable()
    {
        var t = new DataTable("Bills");
        foreach (var n in new[] { "Work order", "Bill no", "Type", "Bill date", "Status" }) t.Columns.Add(n);
        foreach (var n in new[] { "GST %", "Retention %", "TDS %", "Other deduction", "Paid" }) t.Columns.Add(n, typeof(decimal));
        foreach (var n in new[] { "Paid on", "System", "Drawing no", "Sheet", "Rev", "Item", "Description", "Activity", "Unit" }) t.Columns.Add(n);
        t.Columns.Add("Qty", typeof(decimal)); t.Columns.Add("Rate", typeof(decimal)); t.Columns.Add("Remarks");
        return t;
    }

    public static void WriteTemplates(string bomPath, string billPath)
    {
        var b = BomTemplateTable();
        b.Rows.Add("1 STOVE", "16193442", "3/6", "A", "", 309.714m);
        WriteTables(bomPath, b);
        var t = BillTemplateTable();
        t.Rows.Add("MCK/WO-MECH/BSL/2024-25/012", "RA-1", "RA bill", "09-03-2026", "Draft", 18m, 0m, 0m, 0m, 0m, "",
            "1 STOVE", "16193442", "3/6", "A", "", "", "Erection", "t", 25m, 6500m, "");
        t.Rows.Add("MCK/WO-MECH/BSL/2024-25/012", "RA-1", "RA bill", "09-03-2026", "Draft", 18m, 0m, 0m, 0m, 0m, "",
            "", "", "", "", "", "Temporary scaffolding for dome lift", "Utility", "LS", 1m, 85000m, "Per site measurement");
        WriteTables(billPath, t);
    }

    // ───────────────────────── Reading helpers ─────────────────────────

    sealed class Sheet
    {
        public required IXLWorksheet Ws { get; init; }
        public required int HeaderRow { get; init; }
        public required Dictionary<string, int> Cols { get; init; }

        public string Text(IXLRow row, string field)
        {
            if (!Cols.TryGetValue(field, out var c)) return "";
            var cell = row.Cell(c);
            if (cell.DataType == XLDataType.DateTime)
            {
                // Excel turns sheet numbers such as 2/3 into dates; turn them back.
                var d = cell.GetDateTime();
                return field == "sheet" ? $"{d.Day}/{d.Month}" : d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
            }
            if (cell.DataType == XLDataType.Number) return cell.GetDouble().ToString(CultureInfo.InvariantCulture);
            return cell.GetFormattedString().Trim();
        }

        public decimal? Number(IXLRow row, string field)
        {
            if (!Cols.TryGetValue(field, out var c)) return null;
            var cell = row.Cell(c);
            if (cell.IsEmpty()) return null;
            if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();
            return Fmt.TryParse(cell.GetFormattedString(), out var v) ? v : null;
        }

        public DateTime? Date(IXLRow row, string field)
        {
            if (!Cols.TryGetValue(field, out var c)) return null;
            var cell = row.Cell(c);
            if (cell.IsEmpty()) return null;
            if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime().Date;
            if (cell.DataType == XLDataType.Number) return DateTime.FromOADate(cell.GetDouble()).Date;
            return Fmt.TryParseDate(cell.GetFormattedString(), out var d) ? d : null;
        }
    }

    static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Finds the header row in the first 15 rows by matching known column names.</summary>
    static Sheet? FindHeader(IXLWorksheet ws, Dictionary<string, string[]> aliases, string[] required)
    {
        var last = Math.Min(15, ws.LastRowUsed()?.RowNumber() ?? 0);
        for (var r = 1; r <= last; r++)
        {
            var cols = new Dictionary<string, int>();
            foreach (var cell in ws.Row(r).CellsUsed())
            {
                var h = Norm(cell.GetFormattedString());
                if (h.Length == 0) continue;
                foreach (var (field, names) in aliases)
                    if (!cols.ContainsKey(field) && names.Any(n => Norm(n) == h)) { cols[field] = cell.Address.ColumnNumber; break; }
            }
            if (required.All(cols.ContainsKey)) return new Sheet { Ws = ws, HeaderRow = r, Cols = cols };
        }
        return null;
    }

    // ───────────────────────── BOM import ─────────────────────────

    static readonly Dictionary<string, string[]> BomAliases = new()
    {
        ["system"] = new[] { "System", "Package", "Area", "Structure" },
        ["drawing"] = new[] { "Drawing no", "Drawing no.", "Drawing number", "Drawing", "DRG NO", "DRG NUMBER", "Drg No", "Drg. No.", "Dwg no" },
        ["sheet"] = new[] { "Sheet", "Sht", "Sheet no", "Sht no" },
        ["rev"] = new[] { "Rev", "Revision", "Rev no" },
        ["item"] = new[] { "Item", "Mark", "Mark no", "Assembly", "Assembly mark", "Assembly mark no", "Item no" },
        ["tonnes"] = new[] { "Tonnes", "Tonne", "Weight (t)", "Weight t", "Wt (t)", "Wt (MT)", "Weight (MT)", "MT", "Total weight (MT)", "Total wt (MT)", "Total (MT)" },
        ["kg"] = new[] { "Weight (kg)", "Weight kg", "Wt (kg)", "Total weight (kg)", "Total wt (kg)", "Total (kg)", "kg", "Total weight", "Total wt" },
    };

    /// <summary>
    /// Reads BOM rows from every worksheet that has a drawing column and a weight column
    /// (tonnes, or kg which is divided by 1000). Without a System column, the worksheet name is the system.
    /// Rows with the same system, drawing, sheet, rev and item are added together.
    /// </summary>
    public static List<BomItem> ReadBom(string path, ImportResult result)
    {
        using var wb = new XLWorkbook(path);
        var items = new Dictionary<(string, string, string, string, string), BomItem>();
        foreach (var ws in wb.Worksheets)
        {
            var sh = FindHeader(ws, BomAliases, new[] { "drawing" });
            if (sh == null || (!sh.Cols.ContainsKey("tonnes") && !sh.Cols.ContainsKey("kg")))
            {
                result.Messages.Add($"Sheet '{ws.Name}': no drawing and weight columns found, skipped.");
                continue;
            }
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            for (var r = sh.HeaderRow + 1; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var drawing = sh.Text(row, "drawing");
                if (drawing.Length == 0 || drawing.StartsWith("total", StringComparison.OrdinalIgnoreCase)) { result.Skipped++; continue; }
                var system = sh.Cols.ContainsKey("system") ? sh.Text(row, "system") : ws.Name.Trim();
                if (system.Length == 0) system = ws.Name.Trim();
                var t = sh.Number(row, "tonnes") ?? (sh.Number(row, "kg") is { } kg ? kg / 1000m : null);
                if (t == null) { result.Skipped++; result.Messages.Add($"Sheet '{ws.Name}' row {r}: no weight, skipped."); continue; }
                if (t < 0) { result.Skipped++; result.Messages.Add($"Sheet '{ws.Name}' row {r}: negative weight, skipped."); continue; }
                var key = (system, drawing, sh.Text(row, "sheet"), sh.Text(row, "rev"), sh.Text(row, "item"));
                if (items.TryGetValue(key, out var existing)) existing.Tonnes += t.Value;
                else items[key] = new BomItem { System = key.system, DrawingNo = key.drawing, Sheet = key.Item3, Rev = key.Item4, Item = key.Item5, Tonnes = t.Value };
            }
        }
        foreach (var i in items.Values) i.Tonnes = BillMath.Round3(i.Tonnes);
        return items.Values.ToList();
    }

    public static ImportResult ImportBom(BillingRepository repo, string path)
    {
        var result = new ImportResult();
        var items = ReadBom(path, result);
        var (added, updated) = repo.UpsertBom(items);
        result.Added = added;
        result.Updated = updated;
        return result;
    }

    // ───────────────────────── Bill import ─────────────────────────

    static readonly Dictionary<string, string[]> BillAliases = new()
    {
        ["wo"] = new[] { "Work order", "Work order no", "WO", "WO no", "WO number" },
        ["billno"] = new[] { "Bill no", "Bill no.", "Bill number", "RA no", "RA bill no" },
        ["type"] = new[] { "Type", "Bill type" },
        ["date"] = new[] { "Bill date", "Date" },
        ["status"] = new[] { "Status" },
        ["gst"] = new[] { "GST %", "GST" },
        ["ret"] = new[] { "Retention %", "Retention" },
        ["tds"] = new[] { "TDS %", "TDS" },
        ["other"] = new[] { "Other deduction", "Other deduction ₹", "Deduction" },
        ["paid"] = new[] { "Paid", "Paid ₹", "Paid amount" },
        ["paidon"] = new[] { "Paid on", "Payment date" },
        ["system"] = new[] { "System" },
        ["drawing"] = new[] { "Drawing no", "Drawing", "DRG NO" },
        ["sheet"] = new[] { "Sheet", "Sht" },
        ["rev"] = new[] { "Rev", "Revision" },
        ["item"] = new[] { "Item", "Mark" },
        ["desc"] = new[] { "Description", "Free line", "Work" },
        ["activity"] = new[] { "Activity" },
        ["unit"] = new[] { "Unit" },
        ["qty"] = new[] { "Qty", "Quantity" },
        ["rate"] = new[] { "Rate", "Rate ₹" },
        ["remarks"] = new[] { "Remarks", "Line remarks" },
    };

    /// <summary>
    /// Imports bills from a sheet with one row per bill line (the layout of the bill template and of
    /// "Bill lines" in the full export). Rows are grouped into bills by work order and bill number.
    /// A bill that already exists is skipped, so the same file can be imported twice safely.
    /// </summary>
    public static ImportResult ImportBills(BillingRepository repo, string path)
    {
        var result = new ImportResult();
        using var wb = new XLWorkbook(path);
        Sheet? sh = null;
        foreach (var ws in wb.Worksheets)
            if ((sh = FindHeader(ws, BillAliases, new[] { "wo", "billno", "qty", "rate" })) != null) break;
        if (sh == null)
        {
            result.Messages.Add("No sheet with the columns Work order, Bill no, Qty and Rate was found. Use 'Save Excel templates' to see the layout.");
            return result;
        }

        var wos = repo.WorkOrders().ToDictionary(w => w.WoNo.Trim(), StringComparer.OrdinalIgnoreCase);
        var acts = repo.Activities().ToDictionary(a => a.Name.Trim(), StringComparer.OrdinalIgnoreCase);
        var bom = repo.BomItems();
        var existing = repo.BillSummaries().Select(b => (b.WorkOrderId, b.BillNo.ToUpperInvariant())).ToHashSet();
        var bills = new Dictionary<(long, string), (Bill Bill, List<BillLine> Lines, bool Bad)>();
        var ws1 = sh.Ws;
        var lastRow = ws1.LastRowUsed()?.RowNumber() ?? 0;

        for (var r = sh.HeaderRow + 1; r <= lastRow; r++)
        {
            var row = ws1.Row(r);
            var woNo = sh.Text(row, "wo");
            var billNo = sh.Text(row, "billno");
            if (woNo.Length == 0 && billNo.Length == 0) continue;
            string where = $"Row {r}";
            if (!wos.TryGetValue(woNo, out var wo)) { result.Skipped++; result.Messages.Add($"{where}: work order '{woNo}' is not in the work order list."); continue; }
            if (billNo.Length == 0) { result.Skipped++; result.Messages.Add($"{where}: no bill number."); continue; }
            var key = (wo.Id, billNo.ToUpperInvariant());
            if (existing.Contains(key)) { result.Skipped++; continue; }

            if (!bills.TryGetValue(key, out var entry))
            {
                var status = sh.Text(row, "status");
                status = BillStatus.All.FirstOrDefault(s => s.Equals(status, StringComparison.OrdinalIgnoreCase)) ?? BillStatus.Draft;
                var type = sh.Text(row, "type");
                var b = new Bill
                {
                    WorkOrderId = wo.Id, BillNo = billNo, BillType = type.Length > 0 ? type : "RA bill",
                    BillDate = sh.Date(row, "date") ?? DateTime.Today, Status = status,
                    GstPct = sh.Number(row, "gst") ?? repo.GetSettingDecimal("DefaultGstPct", 18),
                    RetentionPct = sh.Number(row, "ret") ?? 0, TdsPct = sh.Number(row, "tds") ?? 0,
                    OtherDeduction = sh.Number(row, "other") ?? 0, PaidAmount = sh.Number(row, "paid") ?? 0,
                    PaidOn = sh.Date(row, "paidon"),
                };
                if (sh.Date(row, "date") == null) result.Messages.Add($"{where}: no valid bill date, today's date used for {billNo}.");
                entry = (b, new List<BillLine>(), false);
            }

            var line = new BillLine
            {
                Qty = sh.Number(row, "qty") ?? 0, Rate = sh.Number(row, "rate") ?? 0,
                Unit = sh.Text(row, "unit"), Remarks = sh.Text(row, "remarks"), Description = sh.Text(row, "desc"),
            };
            var actName = sh.Text(row, "activity");
            if (actName.Length > 0)
            {
                if (acts.TryGetValue(actName, out var a)) line.ActivityId = a.Id;
                else result.Messages.Add($"{where}: activity '{actName}' is not in the list, left blank.");
            }
            var drawing = sh.Text(row, "drawing");
            if (drawing.Length > 0)
            {
                var sys = sh.Text(row, "system"); var sheet = sh.Text(row, "sheet"); var rev = sh.Text(row, "rev"); var item = sh.Text(row, "item");
                var match = bom.Where(i => i.DrawingNo.Equals(drawing, StringComparison.OrdinalIgnoreCase)
                    && (sys.Length == 0 || i.System.Equals(sys, StringComparison.OrdinalIgnoreCase))
                    && i.Sheet.Equals(sheet, StringComparison.OrdinalIgnoreCase)
                    && (rev.Length == 0 || i.Rev.Equals(rev, StringComparison.OrdinalIgnoreCase))
                    && i.Item.Equals(item, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count == 1) { line.BomItemId = match[0].Id; if (line.Unit.Length == 0) line.Unit = "t"; }
                else
                {
                    result.Messages.Add($"{where}: drawing {BomItem.FormatDrawing(drawing, sheet, rev)} {(match.Count == 0 ? "is not in the BOM" : "matches more than one BOM row")}; bill {billNo} skipped.");
                    entry.Bad = true;
                }
            }
            else if (line.Description.Length == 0) line.Description = "(no description)";
            entry.Lines.Add(line);
            bills[key] = entry;
        }

        foreach (var (bill, lines, bad) in bills.Values)
        {
            if (bad) { result.Skipped++; continue; }
            try { repo.SaveBill(bill, lines); result.Added++; }
            catch (BillingException ex) { result.Skipped++; result.Messages.Add($"Bill {bill.BillNo}: {ex.Message}"); }
        }
        return result;
    }
}
