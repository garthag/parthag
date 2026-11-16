using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace BSL.Core.Excel;

public sealed class ContractorRec
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ContactPerson, ContactNo, Email, PAN, GSTIN;
}

public sealed class WorkOrderRec
{
    public string UniqueNo { get; set; } = "";
    public string ContractorCode { get; set; } = "";
    public string? ServiceType, WorkCategory, WorkOrderNo, Remarks;
    public DateTime? WODate;
    public decimal? WOValue, ActualWorkDone, ActualPaid;
    public int SourceRow;
}

public sealed class BoqRec
{
    public int SourceRow;
    public int SortOrder;
    public string? TechSpecItem, GroupName, Description, Unit, Remarks, DrawingQty, AgencyRef, ActivityType;
    public bool IsGroupHeader;
    public decimal? Qty, UnitWeightKg, R1WeightTon, ExternalRate, InternalRate;
    public string? WorkOrderUniqueNo;
    /// <summary>RA no → quantity (MT).</summary>
    public Dictionary<int, decimal> Ra { get; } = [];
}

public sealed class DrawingScopeRec
{
    public int SourceRow;
    public string? Area, DrawingNo, RefNo, SubArea, Description, Qty, Remarks, Agency, ContractItemNo;
    public decimal? WeightKg;
}

public sealed class ContractorWorkbook
{
    public string FileName { get; init; } = "";
    public List<ContractorRec> Contractors { get; } = [];
    public List<WorkOrderRec> WorkOrders { get; } = [];
    public List<BoqRec> Boq { get; } = [];
    public List<DrawingScopeRec> DrawingScope { get; } = [];
    public List<DataIssue> Issues { get; } = [];
}

/// <summary>
/// Reads the contractor workbook: Contractor_Master (one row per work order), the BOQ sheet with
/// external (Supply Unit Rate) and internal (Agency Rate) rates and RA-1…RA-n quantity columns,
/// and the DRAWING scope sheet. Sheets are found by their headers, not by name.
/// </summary>
public static class ContractorWorkbookReader
{
    public static ContractorWorkbook Read(string path)
    {
        using var wb = new XLWorkbook(path);
        var res = new ContractorWorkbook { FileName = Path.GetFileName(path) };
        foreach (var ws in wb.Worksheets)
        {
            var (row, keys) = FindHeader(ws);
            if (row == 0) continue;
            if (keys.ContainsKey("uniqueno") && keys.ContainsKey("contractor")) ReadMaster(ws, row, keys, res);
            else if (keys.Keys.Any(k => k.Contains("description")) && keys.ContainsKey("uniqueno") && keys.Keys.Any(k => k.StartsWith("ra")))
                ReadBoq(ws, row, keys, res);
            else if (keys.ContainsKey("drawingno") && keys.ContainsKey("description")) ReadDrawingScope(ws, row, keys, res);
        }
        return res;
    }

    static (int Row, Dictionary<string, int> Keys) FindHeader(IXLWorksheet ws)
    {
        var lastCol = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 120);
        for (int r = 1; r <= 6; r++)
        {
            var keys = new Dictionary<string, int>();
            for (int c = 1; c <= lastCol; c++)
            {
                var k = ExcelUtil.Key(ExcelUtil.Text(ws.Cell(r, c)));
                if (k.Length > 0 && !keys.ContainsKey(k)) keys[k] = c;
            }
            if (keys.Count >= 6 && (keys.ContainsKey("uniqueno") || keys.ContainsKey("drawingno"))) return (r, keys);
        }
        return (0, []);
    }

    static int? C(Dictionary<string, int> k, params string[] names)
    {
        foreach (var n in names) if (k.TryGetValue(n, out var c)) return c;
        return null;
    }

    static string? T(IXLRow row, int? c) => c is { } i ? ExcelUtil.Text(row.Cell(i)) : null;
    static decimal? D(IXLRow row, int? c) => c is { } i && ExcelUtil.Num(row.Cell(i)) is { } d ? (decimal)d : null;

    static void ReadMaster(IXLWorksheet ws, int hr, Dictionary<string, int> k, ContractorWorkbook res)
    {
        int last = ws.LastRowUsed()?.RowNumber() ?? hr;
        for (int r = hr + 1; r <= last; r++)
        {
            var row = ws.Row(r);
            var uno = T(row, C(k, "uniqueno"));
            if (uno == null) continue;
            var name = T(row, C(k, "contractor", "contractorname"));
            var code = T(row, C(k, "code", "contractorcode"))?.ToUpperInvariant();
            if (code == null && name != null)
            {
                code = new string(name.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
                res.Issues.Add(new(ws.Name, r, "Warning", "NO_CODE", $"{uno}: contractor code missing – generated '{code}'."));
            }
            if (code == null) { res.Issues.Add(new(ws.Name, r, "Warning", "NO_CONTRACTOR", $"{uno}: no contractor – skipped.")); continue; }

            if (code is "BSL" or "MCK" or "ERC" or "SMS")
                res.Issues.Add(new(ws.Name, r, "Warning", "RESERVED_CODE",
                    $"{uno}: contractor '{name}' uses code {code}, which is also an owner / main-contractor / consultant code in drawing numbers – consider a unique code."));
            var phone = ExcelUtil.Val(row.Cell(C(k, "contactno") ?? 1)) switch { double p => ExcelUtil.FormatNumber(p), var o => o?.ToString() };
            var con = res.Contractors.FirstOrDefault(x => x.Code == code);
            if (con == null) res.Contractors.Add(con = new ContractorRec { Code = code, Name = name ?? code });
            else if (name != null && !string.Equals(con.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                res.Issues.Add(new(ws.Name, r, "Warning", "NAME_MISMATCH", $"Code {code} has two names: '{con.Name}' / '{name}'."));
            con.ContactPerson ??= T(row, C(k, "contactperson"));
            con.ContactNo ??= C(k, "contactno") != null ? phone : null;
            con.Email ??= T(row, C(k, "mailid", "email"));
            con.PAN ??= T(row, C(k, "pan"))?.ToUpperInvariant();
            con.GSTIN ??= T(row, C(k, "gst", "gstin"))?.ToUpperInvariant();
            if (con.PAN != null && !Regex.IsMatch(con.PAN, "^[A-Z]{5}[0-9]{4}[A-Z]$"))
                res.Issues.Add(new(ws.Name, r, "Warning", "PAN_FORMAT", $"{code}: PAN '{con.PAN}' is not in AAAAA9999A format."));
            if (con.GSTIN != null && con.PAN != null && con.GSTIN.Length == 15 && con.GSTIN.Substring(2, 10) != con.PAN)
                res.Issues.Add(new(ws.Name, r, "Warning", "GST_PAN", $"{code}: GSTIN {con.GSTIN} does not contain PAN {con.PAN}."));

            var wo = new WorkOrderRec
            {
                SourceRow = r, UniqueNo = uno.ToUpperInvariant(), ContractorCode = code,
                ServiceType = T(row, C(k, "typeofservice"))?.ToUpperInvariant(),
                WorkCategory = T(row, C(k, "type"))?.ToUpperInvariant(),
                WorkOrderNo = T(row, C(k, "workorderno")),
                WODate = ExcelUtil.ToDate(C(k, "wodated", "wodate") is { } dc ? ExcelUtil.Val(row.Cell(dc)) : null),
                WOValue = D(row, C(k, "workordervalue", "wovalue")),
                ActualWorkDone = D(row, C(k, "actualworkdone")),
                ActualPaid = D(row, C(k, "actualpaid")),
                Remarks = T(row, C(k, "remarks"))
            };
            if (wo.ServiceType == null) res.Issues.Add(new(ws.Name, r, "Info", "NO_SERVICE_TYPE", $"{uno}: type of service is blank."));
            if (wo.WorkOrderNo == null) res.Issues.Add(new(ws.Name, r, "Info", "NO_WO", $"{uno}: work order number is blank."));
            if (C(k, "workordervalue") is { } vc && ws.Cell(r, vc).HasFormula)
                res.Issues.Add(new(ws.Name, r, "Info", "WO_VALUE_FORMULA", $"{uno}: WO value is a formula ({ws.Cell(r, vc).FormulaA1}) – value {wo.WOValue:0.##} used."));
            res.WorkOrders.Add(wo);
        }
    }

    static void ReadBoq(IXLWorksheet ws, int hr, Dictionary<string, int> k, ContractorWorkbook res)
    {
        int last = ws.LastRowUsed()?.RowNumber() ?? hr;
        var raCols = k.Where(x => Regex.IsMatch(x.Key, @"^ra\d+$")).ToDictionary(x => int.Parse(x.Key[2..]), x => x.Value);
        var cSpec = C(k, "techspecitem"); var cDesc = C(k, "description"); var cUnit = C(k, "mu", "unit");
        var cQty = C(k, "qty", "quantity"); var cUw = C(k, "unitweightkg", "unitweight"); var cR1 = C(k, "r1weightton", "r1weight");
        var cExt = C(k, "supplyunitrate", "externalrate"); var cInt = C(k, "agencyrate", "internalrate");
        var cRem = C(k, "remarks"); var cDq = C(k, "drawingqty"); var cUno = C(k, "uniqueno"); var cAct = C(k, "activitytype");
        string? group = null;
        int sort = 0;
        for (int r = hr + 1; r <= last; r++)
        {
            var row = ws.Row(r);
            var desc = T(row, cDesc);
            if (desc == null) continue;
            if (Regex.IsMatch(desc, @"^grand\s*total", RegexOptions.IgnoreCase)) continue;
            var spec = T(row, cSpec);
            var unit = T(row, cUnit);
            var qty = D(row, cQty);
            var rec = new BoqRec
            {
                SourceRow = r, SortOrder = sort += 10, TechSpecItem = spec, Description = desc,
                Unit = Standardizer.Unit(unit), Qty = qty, UnitWeightKg = D(row, cUw), R1WeightTon = D(row, cR1),
                ExternalRate = D(row, cExt), InternalRate = D(row, cInt), Remarks = T(row, cRem), DrawingQty = T(row, cDq),
                ActivityType = T(row, cAct)?.ToUpperInvariant()
            };
            var uno = T(row, cUno)?.ToUpperInvariant();
            if (uno != null && Regex.IsMatch(uno, @"^BM\d+$")) rec.WorkOrderUniqueNo = uno;
            else if (uno != null) rec.AgencyRef = uno;

            // group header: numeric spec code, text, no unit / qty
            if (spec != null && Regex.IsMatch(spec, @"^\d+$") && unit == null && qty == null && rec.UnitWeightKg == null)
            {
                rec.IsGroupHeader = true;
                group = desc;
            }
            rec.GroupName = group;
            if (unit != null && rec.Unit != unit.Trim()) res.Issues.Add(new(ws.Name, r, "Info", "UNIT_NORMALISED", $"Unit '{unit}' → '{rec.Unit}'."));
            if (rec.Unit is not (null or "nr" or "set" or "m" or "MT" or "kg"))
                res.Issues.Add(new(ws.Name, r, "Warning", "UNKNOWN_UNIT", $"Unit '{unit}' is not a standard unit (nr/set/m/MT/kg)."));
            if (rec.AgencyRef != null) res.Issues.Add(new(ws.Name, r, "Info", "AGENCY_NOT_WO", $"Unique_No '{rec.AgencyRef}' is not a work order (kept as agency reference)."));
            if (!rec.IsGroupHeader && rec.R1WeightTon == null && rec.Qty != null)
                res.Issues.Add(new(ws.Name, r, "Info", "NO_R1_WEIGHT", $"'{desc}': R1 weight blank."));

            foreach (var (ra, col) in raCols)
                if (ExcelUtil.Num(row.Cell(col)) is { } q && q != 0) rec.Ra[ra] = (decimal)q;
            if (rec.Ra.Count > 0 && rec.WorkOrderUniqueNo == null)
                res.Issues.Add(new(ws.Name, r, "Warning", "RA_WITHOUT_WO", $"'{desc}' has RA quantities but no work order (Unique_No '{uno}')."));
            var exec = rec.Ra.Values.Sum();
            if (rec.R1WeightTon is { } r1 && exec > r1 * 1.001m)
                res.Issues.Add(new(ws.Name, r, "Warning", "OVER_EXECUTED", $"'{desc}': executed {exec:0.###} MT > R1 scope {r1:0.###} MT."));
            res.Boq.Add(rec);
        }
        // the RA labels in the row above the header are shifted by 5 columns – note it
        if (hr > 1 && raCols.Count > 0)
        {
            var above = ExcelUtil.Text(ws.Cell(hr - 1, raCols.Values.Min()));
            if (above != null && !ExcelUtil.Key(above).Equals("ra" + raCols.Keys.Min(), StringComparison.OrdinalIgnoreCase))
                res.Issues.Add(new(ws.Name, hr - 1, "Info", "RA_LABELS_SHIFTED", $"Row {hr - 1} RA labels do not line up with row {hr} (e.g. '{above}' above RA-{raCols.Keys.Min()}). Row {hr} used."));
        }
    }

    static void ReadDrawingScope(IXLWorksheet ws, int hr, Dictionary<string, int> k, ContractorWorkbook res)
    {
        int last = ws.LastRowUsed()?.RowNumber() ?? hr;
        var cArea = C(k, "area"); var cDrg = C(k, "drawingno"); var cSub = C(k, "subarea"); var cDesc = C(k, "description");
        var cQty = C(k, "qty"); var cWt = C(k, "weightinkg", "weightkg", "weight"); var cRem = C(k, "remarks");
        var cAg = C(k, "agency"); var cItem = C(k, "contractitemno");
        int? cRef = cDrg is { } dc && ExcelUtil.Text(ws.Cell(hr, dc + 1)) == null ? dc + 1 : null; // unnamed column after Drawing_No
        string? area = null, sub = null;
        for (int r = hr + 1; r <= last; r++)
        {
            var row = ws.Row(r);
            area = T(row, cArea) ?? area;
            sub = T(row, cSub) ?? sub;
            var desc = T(row, cDesc);
            if (desc == null) continue;
            res.DrawingScope.Add(new DrawingScopeRec
            {
                SourceRow = r, Area = area, SubArea = sub, Description = desc, DrawingNo = T(row, cDrg),
                RefNo = T(row, cRef), Qty = T(row, cQty), WeightKg = D(row, cWt), Remarks = T(row, cRem),
                Agency = T(row, cAg)?.ToUpperInvariant(), ContractItemNo = T(row, cItem)
            });
        }
        var noDrg = res.DrawingScope.Count(d => d.DrawingNo == null);
        if (noDrg > 0) res.Issues.Add(new(ws.Name, 0, "Warning", "SCOPE_WITHOUT_DRAWING_NO",
            $"{noDrg} of {res.DrawingScope.Count} scope rows have no drawing number – imported as SCOPE-<row> placeholders."));
    }
}
