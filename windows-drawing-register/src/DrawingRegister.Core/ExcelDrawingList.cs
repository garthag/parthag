using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace DrawingRegister.Core;

/// <summary>Drawings read from Excel (BOM workbooks, drawing lists, contractor drawing scope).</summary>
public sealed class DrawingListResult
{
    public List<Drawing> Drawings { get; } = [];
    public List<string> Notes { get; } = [];

    /// <summary>Drawings found per worksheet ("BOM-5.xlsx › STOVE HOUSE" → 42).</summary>
    public Dictionary<string, int> PerSheet { get; } = [];

    /// <summary>Rows with text in the drawing column that are not drawings (bolt / nut lines etc.).</summary>
    public int NotDrawingRows { get; set; }

    /// <summary>Scope items that have no drawing number (equipment tags, lots …).</summary>
    public int ScopeItemsWithoutNumber { get; set; }

    /// <summary>Same drawing sheet + revision listed in more than one worksheet (kept once).</summary>
    public int MergedDuplicates { get; set; }

    public string Plant { get; set; } = "";
}

/// <summary>
/// Reads drawing numbers, sheets, revisions and descriptions out of Excel
/// workbooks such as a BOM (one worksheet per package, a "DRG NO" column) or
/// a drawing list. Each distinct drawing sheet + revision becomes one
/// register row without a file; the file attaches when the drawing ZIP is
/// imported.
/// </summary>
public static class ExcelDrawingList
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    static string H(string s) => Regex.Replace(s.ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();

    static readonly HashSet<string> NumberHeaders =
        ["DRG NO", "DRG NUMBER", "DRAWING NO", "DRAWING NUMBER", "DRG NOS", "DWG NO", "DRAWING"];
    static readonly HashSet<string> SheetHeaders = ["SHEET", "SHT", "SHEET NO", "SHT NO", "SH"];
    static readonly HashSet<string> RevHeaders = ["REV", "REVISION", "REV NO", "R"];
    static readonly HashSet<string> TitleHeaders = ["DRG DESCRIPTION", "DRAWING DESCRIPTION", "DRAWING TITLE", "DRG TITLE", "TITLE"];
    static readonly HashSet<string> DescHeaders = ["DESCRIPTION", "ITEM DESCRIPTION"];
    static readonly HashSet<string> SubAreaHeaders = ["SUB AREA", "SUBAREA"];
    static readonly HashSet<string> AgencyHeaders = ["AGENCY", "CONTRACTOR", "VENDOR"];

    static readonly Regex PlantInTitle = new(@"^([A-Z]{2,})[_\s]+([A-Z]{1,4}\s*#\s*\d+)[_\s]", I);
    static readonly Regex NotDrawing = new(@"\b(BOLT|NUT|WASHER|SCREW|RIVET)\b", I);
    static readonly Regex SheetInNumber = new(
        @"[\s,(_\-]*\b(?:SH(?:EET|T)?\.?[\s\-#]*)(\d{1,3})\s*(?:OF|/)\s*(\d{1,3})(?:-[A-Z]\b)?\s*\)?", I);
    static readonly Regex SheetTrailing = new(@"\s+(\d{1,3})\s+OF\s+(\d{1,3})\s*$", I);
    static readonly Regex RevAfterBracket = new(@"\)\s*R(\d{1,2})\s*$", I);
    static readonly Regex RevSeparated = new(@"[\s,_\-]+R(\d{1,2})(?=[\s,]|$)", I);
    static readonly Regex RevGlued = new(@"^(\d{6,})R(\d{1,2})$", I);
    static readonly Regex DrgInText = new(@"\b(\d{7,8})\b(?=\s*-?\s*DRG\b)", I);

    // ------------------------------------------------------------------ public API

    public static DrawingListResult Read(IEnumerable<string> paths, string defaultPlant)
    {
        var result = new DrawingListResult();
        var byKey = new Dictionary<string, (Drawing D, int Lines)>();
        foreach (var path in paths)
        {
            using var wb = new XLWorkbook(path);
            var file = Path.GetFileName(path);
            foreach (var ws in wb.Worksheets)
            {
                try { ReadSheet(ws, file, defaultPlant, result, byKey); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    result.Notes.Add($"{file} › {ws.Name}: skipped ({e.Message})");
                }
            }
        }
        foreach (var (d, lines) in byKey.Values)
        {
            if (lines > 0) d.Remarks = AppendNote($"BOM: {lines} item line(s)", d.Remarks);
            result.Drawings.Add(d);
        }
        return result;
    }

    static string AppendNote(string first, string rest) =>
        first.Length == 0 ? rest : rest.Length == 0 ? first : $"{first}. {rest}";

    // ------------------------------------------------------------------ cell helpers

    static object? Val(IXLCell c)
    {
        var v = c.HasFormula ? c.CachedValue : c.Value;
        return v.Type switch
        {
            XLDataType.Blank or XLDataType.Error => null,
            XLDataType.Number => v.GetNumber(),
            XLDataType.Text => v.GetText(),
            XLDataType.DateTime => v.GetDateTime(),
            XLDataType.Boolean => v.GetBoolean(),
            _ => v.ToString(CultureInfo.InvariantCulture),
        };
    }

    static string Text(object? v) => v switch
    {
        null => "",
        double d when d == Math.Floor(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("dd-MM-yyyy"),
        _ => Regex.Replace(v.ToString()!.Replace(' ', ' '), @"\s+", " ").Trim(),
    };

    static string Tidy(string s) => Regex.Replace(s.Replace('_', ' ').Replace(' ', ' '), @"\s+", " ").Trim().Trim(',', '-', ' ');

    // ------------------------------------------------------------------ worksheet

    static void ReadSheet(IXLWorksheet ws, string file, string defaultPlant, DrawingListResult result,
        Dictionary<string, (Drawing D, int Lines)> byKey)
    {
        var used = ws.RangeUsed();
        if (used == null) return;
        var lastRow = used.LastRow().RowNumber();
        var lastCol = Math.Min(used.LastColumn().ColumnNumber(), 60);

        // Header row: the first of the top 10 rows with a drawing-number column.
        int headerRow = 0, colNo = 0;
        string title = "";
        for (var r = 1; r <= Math.Min(10, lastRow) && headerRow == 0; r++)
        {
            for (var c = 1; c <= lastCol; c++)
            {
                var h = H(Text(Val(ws.Cell(r, c))));
                if (h.Length == 0) continue;
                if (title.Length == 0 && r < 3) title = Text(Val(ws.Cell(r, c)));
                if (NumberHeaders.Contains(h)) { headerRow = r; colNo = c; break; }
            }
        }
        if (headerRow == 0) return;

        int Find(HashSet<string> names) =>
            Enumerable.Range(1, lastCol).FirstOrDefault(c => names.Contains(H(Text(Val(ws.Cell(headerRow, c))))));
        var colSheet = Find(SheetHeaders);
        var colRev = Find(RevHeaders);
        var colTitle = Find(TitleHeaders);
        var colDesc = Find(DescHeaders);
        var colSubArea = Find(SubAreaHeaders);
        var colAgency = Find(AgencyHeaders);
        var scope = colSubArea > 0 && colSheet == 0 && colRev == 0;

        var pm = PlantInTitle.Match(title);
        var plant = pm.Success ? $"{pm.Groups[1].Value.ToUpperInvariant()} {Regex.Replace(pm.Groups[2].Value, @"\s+", "").ToUpperInvariant()}" : defaultPlant;
        if (result.Plant.Length == 0 && pm.Success) result.Plant = plant;
        if (!pm.Success && result.Plant.Length > 0) plant = result.Plant;
        var unit = scope ? "Contractor scope" : Tidy(ws.Name).ToUpperInvariant();
        // "BSL_BF#3_STOVE CONNECTING PLATFORM_BOQ" → "STOVE CONNECTING PLATFORM": title for rows without a description.
        var package = Tidy(Regex.Replace(pm.Success ? title[pm.Length..] : "", @"[_\s]*BOQ\s*$", "", RegexOptions.IgnoreCase));
        var ercSeparator = '\0';
        var label = $"{file} › {ws.Name.Trim()}";
        var found = 0;
        var subArea = "";

        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            var raw = Text(Val(ws.Cell(r, colNo)));
            if (scope)
            {
                var sa = colSubArea > 0 ? Text(Val(ws.Cell(r, colSubArea))) : "";
                var desc = colDesc > 0 ? Text(Val(ws.Cell(r, colDesc))) : "";
                if (sa.Length > 0) subArea = sa;
                var text = string.Join(" ", new[] { sa.Length > 0 ? sa : "", desc }.Where(x => x.Length > 0));
                if (raw.Length == 0)
                {
                    var m = DrgInText.Match(text);
                    if (m.Success) raw = m.Groups[1].Value;
                }
                if (raw.Length == 0)
                {
                    if (text.Length > 0) result.ScopeItemsWithoutNumber++;
                    continue;
                }
                var (no, sheet, rev) = SplitNumber(raw, null, null);
                var t = Tidy(text.Length > 0 ? text : subArea);
                var agency = colAgency > 0 ? Text(Val(ws.Cell(r, colAgency))) : "";
                if (Add(byKey, result, MakeDrawing(plant, unit, no, sheet, rev, t, label, agency), countLine: false)) found++;
                continue;
            }

            if (raw.Length == 0) continue;
            if (!raw.Any(char.IsDigit) || NotDrawing.IsMatch(raw)) { result.NotDrawingRows++; continue; }
            if (ercSeparator == '\0' && Regex.IsMatch(raw, @"^ERC[/_\-]", RegexOptions.IgnoreCase)) ercSeparator = raw[3];
            var (number, sh, rv) = SplitNumber(raw, colSheet > 0 ? Val(ws.Cell(r, colSheet)) : null, colRev > 0 ? Val(ws.Cell(r, colRev)) : null,
                ercSeparator == '\0' ? '/' : ercSeparator);
            var drgTitle = colTitle > 0 ? Tidy(Text(Val(ws.Cell(r, colTitle)))) : "";
            if (drgTitle.Length == 0) drgTitle = package;
            if (Add(byKey, result, MakeDrawing(plant, unit, number, sh, rv, drgTitle, label, ""), countLine: true)) found++;
        }
        if (found > 0) result.PerSheet[label] = found;
    }

    static Drawing MakeDrawing(string plant, string unit, string number, string sheet, string rev, string title, string source, string agency)
    {
        // Single letters in numbers (the "F" of ERC "F-087") are not discipline codes here, so no number fallback.
        var type = Classify(title) ?? Classify(unit) ?? DrawingType.Other;
        var kind = Regex.IsMatch(title, @"\bGA\b|GENERAL ARRANGEMENT", RegexOptions.IgnoreCase) ? "General Arrangement"
            : Regex.IsMatch(title, @"\bBILL OF MATERIAL", RegexOptions.IgnoreCase) ? "Bill of Materials"
            : Regex.IsMatch(title, @"\bDETAIL", RegexOptions.IgnoreCase) ? "Detail"
            : Regex.IsMatch(title, @"\bPLAN\b", RegexOptions.IgnoreCase) ? "Plan" : "";
        return new Drawing
        {
            Plant = plant, Unit = unit, DrawingNo = number, Sheet = sheet, Revision = rev, Title = title,
            TypeCode = type.Code, Kind = kind, Agency = agency, SourcePath = source,
            Status = int.TryParse(rev, out _) ? DrawingStatus.ForConstruction : DrawingStatus.ForApproval,
        };
    }

    /// <returns>True when this is a new drawing sheet + revision.</returns>
    static bool Add(Dictionary<string, (Drawing D, int Lines)> byKey, DrawingListResult result, Drawing d, bool countLine)
    {
        var key = $"{d.NumberKey}|{d.Sheet}|{d.Revision.ToUpperInvariant()}";
        if (byKey.TryGetValue(key, out var existing))
        {
            if (existing.D.SourcePath != d.SourcePath && !existing.D.Remarks.Contains(d.SourcePath))
            {
                existing.D.Remarks = AppendNote(existing.D.Remarks, $"Also listed in {d.SourcePath}");
                result.MergedDuplicates++;
            }
            if (existing.D.Agency.Length == 0) existing.D.Agency = d.Agency;
            byKey[key] = (existing.D, existing.Lines + (countLine ? 1 : 0));
            return false;
        }
        byKey[key] = (d, countLine ? 1 : 0);
        return true;
    }

    // ------------------------------------------------------------------ number / sheet / revision

    /// <summary>
    /// Splits a drawing reference into number, sheet and revision:
    /// "ERC/MCK/BSL/STH/F-001_R3 (SH. 1OF 1)" → ("ERC/MCK/BSL/STH/F-001", "1/1", "3");
    /// "BSL-BF3-REV-SMS-D-R-M-4026 R1, Sht-1/5" → ("BSL-BF3-REV-SMS-D-R-M-4026", "1/5", "1");
    /// "16282205R0" → ("16282205", "", "0"); "DRW 16153298 001 -" → ("16153298 001", "", "0").
    /// Sheet and revision columns win over what is written in the number.
    /// </summary>
    public static (string Number, string Sheet, string Revision) SplitNumber(string raw, object? sheetCell, object? revCell,
        char ercSeparator = '/')
    {
        var s = Regex.Replace(raw.Replace(' ', ' '), @"\s+", " ").Trim();
        var sheet = "";
        var m = SheetInNumber.Match(s);
        if (!m.Success) m = SheetTrailing.Match(s);
        if (m.Success)
        {
            sheet = $"{int.Parse(m.Groups[1].Value)}/{int.Parse(m.Groups[2].Value)}";
            s = s.Remove(m.Index, m.Length).Insert(m.Index, " ").Trim();
        }

        var rev = "";
        Match r;
        if ((r = RevAfterBracket.Match(s)).Success || (r = RevSeparated.Match(s)).Success)
        {
            rev = r.Groups[1].Value;
            s = s.Remove(r.Index, r.Length).Insert(r.Index, " ").Trim();
        }
        else if ((r = RevGlued.Match(s)).Success)
        {
            rev = r.Groups[2].Value;
            s = r.Groups[1].Value;
        }

        var revText = RevisionText(revCell);
        // "...-4024_B" with revision column "B": the suffix is the revision.
        if (revText.Length > 0 && Regex.IsMatch(s, $@"[_\-\s]{Regex.Escape(revText)}$", RegexOptions.IgnoreCase) && !int.TryParse(revText, out _))
            s = s[..^(revText.Length + 1)];
        if (revText.Length > 0) rev = revText;
        if (rev.Length == 0) rev = "0";

        var sheetText = SheetText(sheetCell);
        if (sheetText.Length > 0) sheet = sheetText;

        s = Regex.Replace(s, @"^DRW\s+", "", RegexOptions.IgnoreCase);
        s = s.Trim().TrimEnd(',', '-', '_', ' ').TrimStart(',', '-', '_', ' ');
        if (Regex.IsMatch(s, @"^ERC\s", RegexOptions.IgnoreCase)) s = Regex.Replace(s, @"\s+", ercSeparator.ToString());
        return (s.ToUpperInvariant(), sheet, rev.ToUpperInvariant());
    }

    static string RevisionText(object? v)
    {
        var t = Text(v).ToUpperInvariant();
        if (t.Length == 0) return "";
        var m = Regex.Match(t, @"^R(?:EV)?[\s.\-]*([0-9]{1,2}|[A-Z])$");
        if (m.Success) return m.Groups[1].Value;
        return Regex.IsMatch(t, @"^([0-9]{1,2}|[A-Z]{1,2})$") ? t : "";
    }

    /// <summary>"2/6", " SHEET 9/32" → "9/32", a date Excel made out of "2/3" → "2/3", 0 → "".</summary>
    static string SheetText(object? v)
    {
        switch (v)
        {
            case null: return "";
            case DateTime dt: return $"{dt.Day}/{dt.Month}";
            case double d: return d >= 1 && d == Math.Floor(d) ? ((int)d).ToString(CultureInfo.InvariantCulture) : "";
        }
        var t = Text(v);
        var m = Regex.Match(t, @"(\d{1,3})\s*(?:OF|/)\s*(\d{1,3})", RegexOptions.IgnoreCase);
        return m.Success ? $"{int.Parse(m.Groups[1].Value)}/{int.Parse(m.Groups[2].Value)}" : "";
    }

    // ------------------------------------------------------------------ type

    static readonly string[] StructuralWords =
    [
        "STRUCTURE", "STR", "STEELWORK", "PLATFORM", "SUPPORT", "SUPP", "SUPPORTING", "TRESTLE", "COLUMN", "BEAM",
        "FLOOR", "ROOF", "BRACING", "GIRDER", "ANCHOR", "TEMPLATE", "HOUSE", "FOUNDATION", "PURLIN", "SHEETING",
        "STAIR", "STAIRCASE", "LADDER", "GRATING", "MONORAIL", "MONO RAIL", "PLT", "STEEL",
    ];
    static readonly string[] PipingWords =
    [
        "DUCT", "PIPE", "PIPING", "LINE", "BEND", "NOZZLE", "HEADER", "MAIN", "BLAST", "TRAP", "DIFFUSER",
        "DIFFUSERS", "VENT", "FLANGE", "BUSTLE",
    ];
    static readonly string[] MechanicalWords =
    [
        "VESSEL", "SHELL", "STACK", "SCRUBBER", "DEMISTER", "VALVE", "BELLOW", "CYCLONE", "FAN", "EQUIPMENT",
        "STOVE", "HOIST", "CRANE", "PROBE",
    ];

    /// <summary>Structural, then piping / ducts, then mechanical equipment, by words in the description.</summary>
    public static DrawingType? Classify(string text)
    {
        var t = $" {Regex.Replace(text.ToUpperInvariant(), @"[^A-Z0-9#]+", " ")} ";
        bool Any(string[] words) => words.Any(w => t.Contains($" {w} "));
        if (Any(StructuralWords)) return DrawingType.Structural;
        if (Any(PipingWords)) return DrawingType.Piping;
        if (Any(MechanicalWords)) return DrawingType.Mechanical;
        var hinted = DrawingParser.Parse(text + ".pdf").Type;
        return hinted == DrawingType.Other ? null : hinted;
    }
}
