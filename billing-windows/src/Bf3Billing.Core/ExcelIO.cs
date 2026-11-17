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

    // ───────────────────────── Import (see SmartImport for the checks) ─────────────────────────

    /// <summary>Imports the BOM from every sheet with a drawing and a weight column; rows already saved get their weights updated.</summary>
    public static ImportResult ImportBom(BillingRepository repo, string path) =>
        SmartImport.ImportAuto(repo, path, ImportTarget.Bom, new ImportOptions { Existing = ExistingAction.Update });

    /// <summary>Imports bills (one row per bill line). Bills that are already saved are skipped.</summary>
    public static ImportResult ImportBills(BillingRepository repo, string path) =>
        SmartImport.ImportAuto(repo, path, ImportTarget.BillLines, new ImportOptions());
}
