using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Bf3Billing.Core.Bsl;

/// <summary>One BOM line in the standard layout, whatever sheet it came from.</summary>
public sealed class StdBomRow
{
    public string Package { get; set; } = "";
    public int SNo { get; set; }
    public string? DrawingRaw { get; set; }
    public string? DrawingNo { get; set; }
    public string? SheetNo { get; set; }
    public string? Revision { get; set; }
    public string? DrawingDescription { get; set; }
    public string? ItemDescription { get; set; }
    public string? AssemblyMark { get; set; }
    public string? PartMark { get; set; }
    public string? ItemNo { get; set; }
    public double? PartQty { get; set; }
    public string? SectionDrg { get; set; }
    public string? SectionStd { get; set; }
    public double? LengthMm { get; set; }
    public double? WidthMm { get; set; }
    public double? ThicknessMm { get; set; }
    public double? Qty { get; set; }
    public string? QtyText { get; set; }
    public double? SourceUnitWt { get; set; }
    public double? UnitWeightKg { get; set; }
    public double? TotalWeightKg { get; set; }
    public string? MaterialGrade { get; set; }
    public string? MaterialGradeRaw { get; set; }
    public string? Remarks { get; set; }
    public string? SectionChange { get; set; }
    public string? DimensionStd { get; set; }
    public string RowType { get; set; } = "PART";
    public bool IsExcluded { get; set; }
    public List<string> Flags { get; } = [];
    public string SourceSheet { get; set; } = "";
    public int SourceRow { get; set; }
    public string FlagText => string.Join("; ", Flags);
}

public sealed record DataIssue(string Sheet, int Row, string Severity, string Code, string Message);

public sealed class BomPackage
{
    public required string SheetName { get; init; }
    public string Title { get; set; } = "";
    public int HeaderRow { get; set; }
    public int NoOfSets { get; set; } = 1;
    public double? SheetTotalMt { get; set; }
    public string? SheetTotalFormula { get; set; }
    public Dictionary<string, string> ColumnMap { get; } = [];
    public List<StdBomRow> Rows { get; } = [];
    /// <summary>Drawings listed in the sheet without any item lines (drawing index rows).</summary>
    public List<StdBomRow> DrawingOnlyRows { get; } = [];
    public double CountedWeightKg => Rows.Where(r => !r.IsExcluded).Sum(r => r.TotalWeightKg ?? 0);
}

public sealed class BomWorkbook
{
    public string FileName { get; init; } = "";
    public List<BomPackage> Packages { get; } = [];
    public List<DataIssue> Issues { get; } = [];
    public IEnumerable<StdBomRow> AllRows => Packages.SelectMany(p => p.Rows);
}

/// <summary>
/// Reads the multi-sheet BOM workbook. Each sheet may use a different header wording, header row,
/// weight unit and column order; everything is mapped to <see cref="StdBomRow"/>.
/// </summary>
public static class BomWorkbookReader
{
    enum F
    {
        SNo, Drawing, Sheet, Revision, DrgDesc, ItemDesc, AssemblyMark, PartMark, ItemNo, PartQty, SectionAlt, Section,
        Length, Width, Thickness, Qty, WeightKg, UnitWt, TotalKg, TotalMtRow, GroupTotalMt, Grade, Remarks, SectionChange, DimStd, Ignore
    }

    /// <summary>Header aliases (normalised: lower-case letters/digits only).</summary>
    static readonly Dictionary<string, F> Aliases = new()
    {
        ["sno"] = F.SNo, ["srno"] = F.SNo, ["slno"] = F.SNo, ["sino"] = F.SNo, ["serialno"] = F.SNo,
        ["drgnumber"] = F.Drawing, ["drgno"] = F.Drawing, ["drawingno"] = F.Drawing, ["drawingnumber"] = F.Drawing,
        ["sheet"] = F.Sheet, ["sht"] = F.Sheet, ["sheetno"] = F.Sheet, ["shtno"] = F.Sheet,
        ["revision"] = F.Revision, ["rev"] = F.Revision, ["revno"] = F.Revision,
        ["drgdescription"] = F.DrgDesc, ["drawingdescription"] = F.DrgDesc, ["drgtitle"] = F.DrgDesc,
        ["itemdescription"] = F.ItemDesc, ["description"] = F.ItemDesc,
        ["markno"] = F.AssemblyMark, ["assemblymarkno"] = F.AssemblyMark, ["assemblymark"] = F.AssemblyMark,
        ["partno"] = F.PartMark, ["partmarkno"] = F.PartMark, ["partmark"] = F.PartMark,
        ["itemno"] = F.ItemNo,
        ["partqty"] = F.PartQty,
        ["sections"] = F.SectionAlt, ["dsg"] = F.SectionAlt, ["mainsection"] = F.SectionAlt,
        ["section"] = F.Section, ["usedsection"] = F.Ignore,
        ["lengthmm"] = F.Length, ["length"] = F.Length,
        ["widthmm"] = F.Width, ["width"] = F.Width,
        ["thicknessmm"] = F.Thickness, ["thickness"] = F.Thickness, ["thikness"] = F.Thickness, ["thk"] = F.Thickness,
        ["noofpcs"] = F.Qty, ["qty"] = F.Qty, ["totalqty"] = F.Qty, ["nos"] = F.Qty, ["quantity"] = F.Qty,
        ["weightkg"] = F.WeightKg, ["weight"] = F.WeightKg,
        ["unitwt"] = F.UnitWt, ["unitwtinmt"] = F.UnitWt, ["unitweight"] = F.UnitWt,
        ["totalwtinkgs"] = F.TotalKg, ["totalwtkg"] = F.TotalKg, ["totalweightkg"] = F.TotalKg,
        ["unitwtforx1stovemt"] = F.TotalMtRow,
        ["totalwtinmt"] = F.GroupTotalMt,
        ["materialgrade"] = F.Grade, ["material"] = F.Grade, ["grade"] = F.Grade,
        ["remarks"] = F.Remarks, ["remark"] = F.Remarks,
        ["sectionchange"] = F.SectionChange,
        ["dimnstd"] = F.DimStd, ["dimensionstd"] = F.DimStd,
        ["sectionwt"] = F.Ignore, ["calwt"] = F.Ignore
    };

    public static BomWorkbook Read(string path)
    {
        using var wb = new XLWorkbook(path);
        var result = new BomWorkbook { FileName = Path.GetFileName(path) };
        foreach (var ws in wb.Worksheets)
        {
            var pkg = ReadSheet(ws, result.Issues);
            if (pkg != null) result.Packages.Add(pkg);
        }
        FlagCrossPackageDuplicates(result);
        return result;
    }

    static BomPackage? ReadSheet(IXLWorksheet ws, List<DataIssue> issues)
    {
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastRow == 0) return null;

        // 1. find the header row: the first row (1..8) with at least 5 known headers incl. a drawing column
        int hdr = 0;
        Dictionary<int, F> map = [];
        for (int r = 1; r <= Math.Min(8, lastRow) && hdr == 0; r++)
        {
            var m = new Dictionary<int, F>();
            for (int c = 1; c <= lastCol; c++)
                if (Aliases.TryGetValue(ExcelUtil.Key(ExcelUtil.Text(ws.Cell(r, c))), out var f)) m[c] = f;
            if (m.Count >= 5 && m.ContainsValue(F.Drawing)) { hdr = r; map = m; }
        }
        if (hdr == 0)
        {
            issues.Add(new(ws.Name, 0, "Info", "NOT_A_BOM_SHEET", "No BOM header row found – sheet skipped."));
            return null;
        }

        var pkg = new BomPackage { SheetName = ws.Name, HeaderRow = hdr, Title = Standardizer.PackageTitle(ExcelUtil.Text(ws.Cell(1, 1)), ws.Name) };

        // "SECTION" is the drawing section, unless a separate drawing-section column exists
        // (SECTION S / DSG / MAIN SECTION) – then "SECTION" holds the standardised code.

        // 3. sub header "UNIT | TOTAL" under a weight column
        int firstData = hdr + 1;
        int? unitCol = null, totalCol = null;
        double totalFactor = 1;
        var sub = Enumerable.Range(1, lastCol).ToDictionary(c => c, c => ExcelUtil.Key(ExcelUtil.Text(ws.Cell(hdr + 1, c))));
        if (sub.Values.Contains("unit") || sub.Values.Contains("total"))
        {
            firstData = hdr + 2;
            unitCol = sub.FirstOrDefault(x => x.Value == "unit").Key is var u and > 0 ? u : null;
            totalCol = sub.FirstOrDefault(x => x.Value == "total").Key is var t and > 0 ? t : null;
        }
        var weightCol = Col(map, F.WeightKg);
        if (weightCol != null && totalCol == null) totalCol = weightCol;
        if (weightCol != null && unitCol == null && totalCol != weightCol) unitCol = weightCol;
        if (Col(map, F.TotalKg) is { } tk) totalCol = tk;
        if (Col(map, F.UnitWt) is { } uw) unitCol = uw;
        if (Col(map, F.TotalMtRow) is { } tm) { totalCol = tm; totalFactor = 1000; }

        foreach (var (c, f) in map) pkg.ColumnMap[f.ToString() + "@" + ws.Cell(hdr, c).Address.ColumnLetter] = ExcelUtil.Text(ws.Cell(hdr, c)) ?? "";
        if (unitCol != null) pkg.ColumnMap["UnitWt@" + ws.Cell(hdr, unitCol.Value).Address.ColumnLetter] = "unit weight";
        if (totalCol != null) pkg.ColumnMap["TotalWt@" + ws.Cell(hdr, totalCol.Value).Address.ColumnLetter] = totalFactor == 1000 ? "total weight (MT → kg)" : "total weight (kg)";
        if (totalFactor == 1000) issues.Add(new(ws.Name, hdr, "Info", "WEIGHT_IN_MT", "Weights in this sheet are in MT; converted to kg."));

        string? S(IXLRow row, F f) => Col(map, f) is { } c ? ExcelUtil.Text(row.Cell(c)) : null;
        double? N(IXLRow row, F f) => Col(map, f) is { } c ? ExcelUtil.Num(row.Cell(c)) : null;
        object? V(IXLRow row, F f) => Col(map, f) is { } c ? ExcelUtil.Val(row.Cell(c)) : null;

        string? lastDrg = null, lastSheet = null, lastRev = null, lastDesc = null;
        bool markPartMode = map.ContainsValue(F.AssemblyMark) && (map.ContainsValue(F.PartMark) || map.ContainsValue(F.PartQty));
        int sno = 0;

        for (int r = firstData; r <= lastRow; r++)
        {
            var row = ws.Row(r);
            var texts = Enumerable.Range(1, lastCol).Select(c => ExcelUtil.Text(row.Cell(c))).ToList();
            if (texts.All(t => t == null)) continue;

            var drgRaw = S(row, F.Drawing);
            var itemDesc = S(row, F.ItemDesc);
            var secDrg = S(row, F.SectionAlt) ?? S(row, F.Section);
            var qtyObj = V(row, F.Qty);
            var total = totalCol is { } tc ? ExcelUtil.Num(row.Cell(tc)) : null;
            var unit = unitCol is { } uc && uc != totalCol ? ExcelUtil.Num(row.Cell(uc)) : null;

            // total / summary rows
            var joined = string.Join(" ", texts.Where(t => t != null));
            if (drgRaw == null && itemDesc == null && Regex.IsMatch(joined, @"\btotal\b|boq#", RegexOptions.IgnoreCase))
            {
                CaptureSheetTotal(ws, row, lastCol, pkg);
                continue;
            }
            if (drgRaw == null && itemDesc == null && secDrg == null && qtyObj == null && S(row, F.AssemblyMark) == null && S(row, F.PartMark) == null)
            {
                if (total != null) issues.Add(new(ws.Name, r, "Info", "LOOSE_VALUE", $"Row with only a number ({total}) ignored – probably a manual total."));
                continue;
            }

            var std = new StdBomRow { Package = ws.Name, SourceSheet = ws.Name, SourceRow = r };

            // text like "Hex Head MS Bolt M24X100 LG" in the drawing column is an item description, not a drawing number
            if (drgRaw != null && LooksLikeDescription(drgRaw))
            {
                std.Flags.Add($"drawing column holds a description ('{drgRaw}')");
                itemDesc ??= drgRaw;
                drgRaw = null;
                lastDrg = null;
            }

            // drawing columns (forward filled when blank)
            if (drgRaw != null) lastDrg = drgRaw; else if (lastDrg != null) { drgRaw = lastDrg; std.Flags.Add("drawing filled down"); }
            std.DrawingRaw = drgRaw;
            if (drgRaw != null)
            {
                var parts = Standardizer.ParseDrawing(drgRaw);
                std.DrawingNo = parts.BaseNo;
                var sheetVal = Standardizer.Sheet(V(row, F.Sheet), out var wasDate);
                if (wasDate) std.Flags.Add("sheet no. was stored as a date");
                if (sheetVal != null && !Regex.IsMatch(sheetVal, @"^\d{1,3}(/\d{1,3})?$"))
                {
                    std.Flags.Add($"sheet column holds text ('{sheetVal}')");
                    sheetVal = null;
                }
                std.SheetNo = sheetVal ?? parts.Sheet ?? (drgRaw == lastDrg ? lastSheet : null);
                std.Revision = Standardizer.Revision(V(row, F.Revision)) ?? parts.Revision ?? (drgRaw == lastDrg ? lastRev : null);
                lastSheet = std.SheetNo; lastRev = std.Revision;
            }
            else std.Flags.Add("no drawing number");

            var desc = S(row, F.DrgDesc);
            if (desc != null) lastDesc = desc; else desc = lastDesc;
            std.DrawingDescription = desc;

            std.AssemblyMark = S(row, F.AssemblyMark);
            std.PartMark = S(row, F.PartMark);
            std.ItemNo = S(row, F.ItemNo);
            std.PartQty = N(row, F.PartQty);
            std.ItemDescription = itemDesc;

            // drawing index row (drawing listed, nothing else)
            if (itemDesc == null && secDrg == null && qtyObj == null && total == null && std.AssemblyMark == null && std.PartMark == null && std.ItemNo == null)
            {
                pkg.DrawingOnlyRows.Add(std);
                continue;
            }

            std.SectionDrg = secDrg;
            std.LengthMm = Positive(N(row, F.Length));
            std.WidthMm = Positive(N(row, F.Width));
            std.ThicknessMm = Positive(N(row, F.Thickness));
            var stdSecRaw = map.ContainsValue(F.SectionAlt) ? S(row, F.Section) : null;
            std.SectionStd = Standardizer.Section(stdSecRaw ?? secDrg, IsPlate(stdSecRaw ?? secDrg) ? std.ThicknessMm : null);

            switch (qtyObj)
            {
                case double q: std.Qty = q; break;
                case string qs when ExcelUtil.ToNumber(qs) is { } qn: std.Qty = qn; break;
                case string qs when Regex.Match(qs, @"^(\d+)\s*/\s*(\d+)$") is { Success: true } pm
                                    && int.Parse(pm.Groups[1].Value) <= int.Parse(pm.Groups[2].Value):
                    std.QtyText = qs; std.Qty = 1; std.Flags.Add($"qty '{qs}' read as piece {pm.Groups[1].Value} of {pm.Groups[2].Value} = 1 no."); break;
                case string qs: std.QtyText = qs; std.Flags.Add($"qty '{qs}' is not a number"); break;
                case DateTime qd: std.QtyText = $"{qd.Day}/{qd.Month}"; std.Flags.Add("qty was stored as a date"); break;
            }

            std.SourceUnitWt = unit;
            std.TotalWeightKg = total is { } tw ? Math.Round(tw * totalFactor, 3) : null;
            if (std.TotalWeightKg == null && unit != null && std.Qty is > 0 && totalFactor == 1)
            {
                std.TotalWeightKg = Math.Round(unit.Value * std.Qty.Value, 3);
                std.Flags.Add("total = unit x qty (total was blank)");
            }
            if (std.TotalWeightKg == null) std.Flags.Add("no weight");
            else if (std.TotalWeightKg < 0) std.Flags.Add("negative weight");
            std.UnitWeightKg = std.TotalWeightKg != null ? Math.Round(std.TotalWeightKg.Value / (std.Qty is > 0 ? std.Qty.Value : 1), 4) : null;

            var gradeRaw = S(row, F.Grade);
            std.MaterialGradeRaw = gradeRaw;
            std.MaterialGrade = Standardizer.Grade(gradeRaw, out var gradeChanged);
            if (gradeChanged) std.Flags.Add($"grade corrected from '{gradeRaw}'");

            std.Remarks = S(row, F.Remarks);
            std.SectionChange = S(row, F.SectionChange);
            if (std.SectionChange is "NA" or "N/A" or "-") std.SectionChange = null;
            std.DimensionStd = S(row, F.DimStd);
            std.SNo = ++sno;
            pkg.Rows.Add(std);
        }

        ResolveAssemblies(pkg.Rows, markPartMode);

        foreach (var row in pkg.Rows.Where(x => x.Flags.Count > 0))
            foreach (var flag in row.Flags.Where(fl => !fl.StartsWith("drawing filled") && !fl.StartsWith("detail of")))
                issues.Add(new(ws.Name, row.SourceRow, flag.StartsWith("no weight") || flag.StartsWith("negative") || flag.Contains("differs") ? "Warning" : "Info",
                    Code(flag), flag));

        if (pkg.SheetTotalMt is { } st)
        {
            var mine = pkg.CountedWeightKg / 1000.0;
            if (Math.Abs(mine - st) > Math.Max(0.01, st * 0.005))
                issues.Add(new(ws.Name, 0, "Warning", "TOTAL_MISMATCH",
                    $"Sheet total per set = {st:0.###} MT, standardised lines = {mine:0.###} MT (difference {mine - st:0.###} MT). Formula: {pkg.SheetTotalFormula}"));
        }
        if (pkg.NoOfSets > 1)
            issues.Add(new(ws.Name, 0, "Info", "SETS", $"Sheet total is multiplied by {pkg.NoOfSets} (no. of sets) – stored as Package.NoOfSets."));
        if (pkg.DrawingOnlyRows.Count > 0)
            issues.Add(new(ws.Name, 0, "Info", "DRAWING_INDEX_ROWS", $"{pkg.DrawingOnlyRows.Count} rows list a drawing without items – imported to the drawing register only."));
        return pkg;
    }

    /// <summary>
    /// Finds assembly header rows and their detail rows. Two patterns exist in the sheets:
    ///  (a) ASSEMBLY MARK row followed by PART MARK rows (parts listed for ONE assembly);
    ///  (b) item no "37" (no section / qty) followed by items "37a", "37b" ...
    /// The assembly row is counted (it is what the sheet totals use); detail rows are kept but
    /// excluded. If the assembly row has no weight, its parts are counted instead.
    /// </summary>
    static void ResolveAssemblies(List<StdBomRow> rows, bool markPartMode)
    {
        var groups = new List<(StdBomRow Asm, List<StdBomRow> Kids)>();
        (StdBomRow Asm, List<StdBomRow> Kids)? cur = null;
        string? pattern = null;   // item no of the block ("37"), kept when a weighted sub-heading opens a sub-assembly
        foreach (var r in rows)
        {
            bool isAsm = markPartMode
                ? r.AssemblyMark != null && r.PartMark == null && r.LengthMm == null && r.WidthMm == null
                : r.ItemNo != null && Regex.IsMatch(r.ItemNo, @"^\d+$") && r.Qty == null && r.QtyText == null && r.LengthMm == null && r.WidthMm == null;
            if (isAsm) { cur = (r, []); groups.Add(cur.Value); pattern = r.ItemNo; continue; }
            if (cur == null) continue;
            bool isKid;
            if (markPartMode)
                isKid = r.PartMark != null && (r.AssemblyMark == null || r.AssemblyMark == cur.Value.Asm.AssemblyMark);
            else
            {
                var n = Regex.Escape(pattern ?? cur.Value.Asm.ItemNo ?? "\u0001");
                isKid = r.ItemNo != null && Regex.IsMatch(r.ItemNo, "^" + n + @"[a-zA-Z]\d?$");
                if (!isKid && r.ItemNo != null && Regex.IsMatch(r.ItemNo, "^" + n + "[10]$") && (r.Qty != null || r.QtyText != null))
                {
                    // OCR: letter l / o read as digit 1 / 0 ("34l" → "341")
                    isKid = true;
                    r.Flags.Add($"item no '{r.ItemNo}' looks like '{r.ItemNo[..^1]}{(r.ItemNo[^1] == '1' ? 'l' : 'o')}' (OCR)");
                }
                if (!isKid && r.ItemNo == null && r.SectionDrg == null && r.Qty == null && r.QtyText == null)
                {
                    if (r.TotalWeightKg is > 0)
                    {
                        // weighted sub-heading = a second assembly of the same block; following parts belong to it
                        r.Flags.Add($"sub-assembly heading inside block {pattern}");
                        cur = (r, []); groups.Add(cur.Value);
                        continue;
                    }
                    isKid = true;
                    r.Flags.Add("sub-heading row inside assembly block");
                }
            }
            if (isKid) cur.Value.Kids.Add(r); else cur = null;
        }

        foreach (var (asm, kids) in groups.Where(g => g.Kids.Count > 0))
        {
            var label = asm.AssemblyMark ?? asm.ItemNo ?? asm.ItemDescription ?? "?";
            asm.RowType = "ASSEMBLY";
            foreach (var k in kids)
            {
                k.AssemblyMark ??= asm.AssemblyMark ?? asm.ItemNo;
                k.ItemDescription ??= asm.ItemDescription;
            }
            var asmQty = asm.Qty is > 0 ? asm.Qty.Value : 1;
            var partSum = kids.Sum(k => k.TotalWeightKg ?? 0) * asmQty;
            if (asm.TotalWeightKg is > 0)
            {
                foreach (var k in kids)
                {
                    k.RowType = "DETAIL";
                    k.IsExcluded = true;
                    k.Flags.Add(asmQty > 1 ? $"detail of assembly {label} (listed for 1 of {asmQty:0.##}; counted in assembly row)"
                                           : $"detail of assembly {label} (counted in assembly row)");
                }
                if (Math.Abs(partSum - asm.TotalWeightKg.Value) / asm.TotalWeightKg.Value > 0.02)
                    asm.Flags.Add($"parts total {partSum:0.##} kg (x{asmQty:0.##}) differs from assembly {asm.TotalWeightKg:0.##} kg");
            }
            else
            {
                asm.IsExcluded = true;
                asm.Flags.Add($"assembly row without weight – its {kids.Count} parts are counted");
            }
        }
    }

    static string Code(string flag) => flag switch
    {
        _ when flag.StartsWith("qty") && flag.Contains("read as piece") => "QTY_PIECE_OF_N",
        _ when flag.StartsWith("qty") => "QTY_NOT_NUMERIC",
        _ when flag.StartsWith("sheet no") => "SHEET_AS_DATE",
        _ when flag.StartsWith("grade") => "GRADE_CORRECTED",
        _ when flag.StartsWith("no weight") => "NO_WEIGHT",
        _ when flag.StartsWith("total =") => "WEIGHT_CALCULATED",
        _ when flag.StartsWith("no drawing") => "NO_DRAWING",
        _ when flag.StartsWith("drawing column holds") => "DESCRIPTION_IN_DRAWING_COL",
        _ when flag.StartsWith("sheet column holds") => "TEXT_IN_SHEET_COL",
        _ when flag.Contains("differs") => "ASSEMBLY_MISMATCH",
        _ when flag.StartsWith("assembly row without") => "ASSEMBLY_NO_WEIGHT",
        _ when flag.StartsWith("item no") => "ITEM_NO_OCR",
        _ when flag.StartsWith("sub-heading") || flag.StartsWith("sub-assembly") => "SUB_HEADING_IN_BLOCK",
        _ when flag.StartsWith("duplicate") => "DUPLICATE",
        _ => "OTHER"
    };

    /// <summary>Two or more ordinary words with lower-case letters (Head, Bolt, with …) → a description.</summary>
    static bool LooksLikeDescription(string s) =>
        Regex.Matches(s, @"\b[A-Za-z]*[a-z][A-Za-z]{2,}\b").Count >= 2 && !Regex.IsMatch(s, @"(?i)\b(sht|sheet)\b");

    static bool IsPlate(string? s) => s != null && Regex.IsMatch(s.Trim(), @"^(PL|PLT|PLATE)\b|^PL[\-.\d]|^PLT\d|^\d+(\.\d+)?$", RegexOptions.IgnoreCase);

    static double? Positive(double? v) => v is > 0 ? v : null;

    static int? Col(Dictionary<int, F> map, F f) => map.Where(x => x.Value == f).Select(x => (int?)x.Key).FirstOrDefault();

    /// <summary>Reads "Total" rows: cached total value and an "*N" set multiplier in the formula.</summary>
    static void CaptureSheetTotal(IXLWorksheet ws, IXLRow row, int lastCol, BomPackage pkg)
    {
        for (int c = lastCol; c >= 1; c--)
        {
            var cell = row.Cell(c);
            if (!cell.HasFormula && ExcelUtil.Num(cell) == null) continue;
            var label = string.Join(" ", Enumerable.Range(1, c - 1).Select(i => ExcelUtil.Text(row.Cell(i))).Where(t => t != null));
            var f = cell.HasFormula ? cell.FormulaA1 : null;
            var v = ExcelUtil.Num(cell);
            var mult = f != null ? Regex.Match(f, @"\*\s*(\d+)\s*$") : Match.Empty;
            var labelSets = Regex.Match(label, @"(\d+)\s*(sets|stoves)", RegexOptions.IgnoreCase);
            if (mult.Success || labelSets.Success)
            {
                var n = int.Parse(mult.Success ? mult.Groups[1].Value : labelSets.Groups[1].Value, CultureInfo.InvariantCulture);
                if (n > 1 && n <= 20)
                {
                    pkg.NoOfSets = n;
                    if (v != null) { pkg.SheetTotalMt = v / n; pkg.SheetTotalFormula = f; }
                }
            }
            else if (v != null)
            {
                pkg.SheetTotalMt = v;
                pkg.SheetTotalFormula = f ?? "(value)";
            }
            return;
        }
    }

    static void FlagCrossPackageDuplicates(BomWorkbook wb)
    {
        var byDrawing = wb.AllRows.Where(r => r.DrawingNo != null && Regex.IsMatch(r.DrawingNo, @"\d{3}"))
            .GroupBy(r => (r.DrawingNo!.ToUpperInvariant(), r.SheetNo))
            .Where(g => g.Select(r => r.Package).Distinct().Count() > 1);
        foreach (var g in byDrawing)
        {
            var pk = g.Select(r => r.Package).Distinct().ToList();
            wb.Issues.Add(new(string.Join(" / ", pk), 0, "Warning", "DRAWING_IN_MULTIPLE_PACKAGES",
                $"Drawing {g.Key.Item1} sheet {g.Key.SheetNo} appears in {pk.Count} sheets ({string.Join(", ", pk)}) – check for double counting."));
            var first = pk[0];
            var firstKeys = g.Where(r => r.Package == first).Select(Signature).ToHashSet();
            foreach (var r in g.Where(r => r.Package != first && firstKeys.Contains(Signature(r))))
                r.Flags.Add($"duplicate of a line in '{first}'");
        }
    }

    static string Signature(StdBomRow r) =>
        $"{r.SheetNo}|{r.ItemNo}|{r.PartMark}|{r.SectionStd}|{r.LengthMm}|{r.WidthMm}|{r.Qty}|{r.TotalWeightKg}";
}
