using System.Text.RegularExpressions;

namespace DrawingRegister.Core;

public sealed record ParsedDrawing(
    string DrawingNo, string Title, string Revision, string Plant, string Unit,
    DrawingType Type, string Kind, DrawingStatus Status, string FileName, string Folder, string Sheet = "");

/// <summary>
/// Reads plant, unit, drawing type, drawing number, revision and status from a
/// file's path inside a drawing ZIP. Examples:
/// <code>
/// RF-100-ST-GA-0001_R2.pdf                       → Unit 100 · Structural · Rev 2
/// Plant-2/Unit 300/Piping/ISO-300-1023 Rev B.pdf → Plant 2 · Unit 300 · Piping · Rev B
/// U200-EL-SLD-001-R0 Main SLD (IFC).pdf          → Unit 200 · Electrical · Rev 0 · IFC
/// Refinery/CDU/Piping/ISO-1023_R1.pdf            → Plant Refinery · Unit CDU · Piping
/// </code>
/// </summary>
public static class DrawingParser
{
    const string Sep = @"[\s_\-.()\[\]]";
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    static readonly Regex RevWord = new($@"(?:^|{Sep})REV(?:ISION)?[\s.\-_#:]*([0-9]{{1,2}}|[A-Z]{{1,2}})(?=$|{Sep})", I);
    static readonly Regex RevShort = new($@"(?:^|{Sep})R([0-9]{{1,2}})(?=$|{Sep})", I);

    static readonly (Regex Re, DrawingStatus Status)[] StatusWords =
    [
        (new($@"(?:^|{Sep})(?:IFC|AFC|GFC)(?=$|{Sep})", I), DrawingStatus.ForConstruction),
        (new($@"(?:^|{Sep})(?:IFA|AFA)(?=$|{Sep})", I), DrawingStatus.ForApproval),
        (new($@"(?:^|{Sep})IFR(?=$|{Sep})", I), DrawingStatus.ForReview),
        (new($@"(?:^|{Sep})(?:AS[\s_\-]?BUILT|ABD)(?=$|{Sep})", I), DrawingStatus.AsBuilt),
        (new($@"(?:^|{Sep})FOR[\s_\-]?INFO(?:RMATION)?(?=$|{Sep})", I), DrawingStatus.ForInformation),
    ];

    /// <summary>"(SH. 1 OF 2)", "(SHT.1 OF 2)", "Sht-1/5", "SHEET 9/32", "1 OF 1".</summary>
    public static readonly Regex SheetRe = new(
        @"[\s,(_\-]*\b(?:SH(?:EET|T)?\.?[\s\-#]*)?(\d{1,3})\s*(?:OF|/)\s*(\d{1,3})\b\s*\)?", I);
    static readonly Regex SheetWordRe = new(@"\bSH(?:EET|T)?\.?[\s\-#]*\d{1,3}\s*(?:OF|/)\s*\d{1,3}\b", I);

    /// <summary>Takes a sheet reference ("SH. 1 OF 2", "Sht-1/5") out of <paramref name="text"/>; returns "1/2".</summary>
    public static string ExtractSheet(ref string text, bool requireWord = true)
    {
        var w = SheetWordRe.Match(text);
        if (requireWord && !w.Success) return "";
        var m = SheetRe.Match(text, w.Success ? Math.Max(0, w.Index - 3) : 0);
        if (!m.Success) return "";
        text = text.Remove(m.Index, m.Length).Insert(m.Index, " ");
        return $"{int.Parse(m.Groups[1].Value)}/{int.Parse(m.Groups[2].Value)}";
    }

    static readonly Regex NumberRe = new(@"^[A-Za-z0-9&]+(?:[-_.][A-Za-z0-9&]+)*", RegexOptions.Compiled);
    static readonly Regex PlantFolder = new(@"^(?:PLANT|PLT)[\s_\-#.]*([A-Z0-9][A-Z0-9 \-&]*)$", I);
    static readonly Regex UnitFolder = new(@"^(UNIT|UNT|U|AREA|BLOCK|BLK)[\s_\-#.]*([A-Z0-9]+)$", RegexOptions.Compiled);
    static readonly Regex UnitInText = new(@"(?:^|[\s_\-])(UNIT|AREA)[\s_\-#.]*(\d{2,4}[A-Z]?)(?=$|[\s_\-])", RegexOptions.Compiled);
    static readonly Regex OrderPrefix = new(@"^\d{1,2}[\s._\-]+(?=\D)", RegexOptions.Compiled);

    /// <summary>Common sheet kinds by code.</summary>
    public static readonly Dictionary<string, string> Kinds = new()
    {
        ["GA"] = "General Arrangement", ["GAD"] = "General Arrangement",
        ["P&ID"] = "Piping & Instrumentation Diagram", ["PID"] = "Piping & Instrumentation Diagram",
        ["PFD"] = "Process Flow Diagram", ["UFD"] = "Utility Flow Diagram",
        ["ISO"] = "Isometric", ["SLD"] = "Single Line Diagram",
        ["LAY"] = "Layout", ["LAYOUT"] = "Layout", ["PLN"] = "Plan", ["PLAN"] = "Plan",
        ["SEC"] = "Section", ["SECTION"] = "Section", ["ELEV"] = "Elevation",
        ["DET"] = "Detail", ["DTL"] = "Detail", ["DETAIL"] = "Detail",
        ["FDN"] = "Foundation", ["BBS"] = "Bar Bending Schedule", ["SCH"] = "Schematic",
        ["LOOP"] = "Loop Diagram", ["HU"] = "Hook-up", ["HOOKUP"] = "Hook-up",
        ["SUP"] = "Support Detail", ["KP"] = "Key Plan",
    };

    static readonly (string Word, string Kind)[] KindWords =
    [
        ("ISOMETRIC", "Isometric"), ("LAYOUT", "Layout"), ("SECTION", "Section"), ("ELEVATION", "Elevation"),
        ("DETAIL", "Detail"), ("SCHEMATIC", "Schematic"), ("PLAN", "Plan"),
    ];

    static readonly HashSet<string> JunkNames = ["thumbs.db", ".ds_store", "desktop.ini"];
    static readonly HashSet<string> JunkExt = [".bak", ".tmp", ".dwl", ".dwl2", ".sv$", ".lck"];

    /// <summary>False for OS / CAD system files that are never drawings.</summary>
    public static bool IsDrawingFile(string path)
    {
        var p = path.Replace('\\', '/');
        var lower = p.ToLowerInvariant();
        var name = lower.Split('/').Last();
        return name.Length > 0
               && !lower.StartsWith("__macosx/") && !lower.Contains("/__macosx/")
               && !name.StartsWith("._") && !name.StartsWith("~$")
               && !JunkNames.Contains(name) && !JunkExt.Contains(Path.GetExtension(name));
    }

    static string StripPrefix(string f) => OrderPrefix.Replace(f, "", 1).Trim();
    static string CleanFolder(string f) => StripPrefix(f).ToUpperInvariant();

    static string UnitLabel(string word, string id)
    {
        var w = word.ToUpperInvariant();
        if (w.StartsWith("AREA")) return $"Area {id}";
        if (w.StartsWith('B')) return $"Block {id}";
        return $"Unit {id}";
    }

    static DrawingType? TypeFromWord(string word, bool allowSingleLetter)
    {
        var w = word.ToUpperInvariant();
        if (w.Length == 0 || (!allowSingleLetter && w.Length == 1)) return null;
        return DrawingType.All.FirstOrDefault(t => t.Aliases.Contains(w));
    }

    static DrawingType? TypeFromHints(string text)
    {
        var padded = $" {Regex.Replace(text.ToUpperInvariant(), @"[_\-.]+", " ")} ";
        foreach (var t in DrawingType.All)
            foreach (var h in t.KindHints)
                if (padded.Contains($" {h} ") || (h.Contains('&') && padded.Contains(h)))
                    return t;
        return null;
    }

    static DrawingType? TypeFromFolder(string folder)
    {
        var f = CleanFolder(folder);
        var direct = TypeFromWord(f, false);
        if (direct != null) return direct;
        foreach (var t in DrawingType.All)
        {
            if (t == DrawingType.Other) continue;
            var main = Regex.Split(t.Name, @"[\s/&]+")[0].ToUpperInvariant();
            if (f.Contains(main)) return t;
        }
        return TypeFromHints(f);
    }

    static string Tidy(string s)
    {
        var t = Regex.Replace(s.Replace('_', ' '), @"\(\s*\)|\[\s*\]", " ");
        // Brackets left unbalanced after taking out "(IFC)" or "[Rev 2]".
        if (!t.Contains('(')) t = t.Replace(')', ' ');
        if (!t.Contains(')')) t = t.Replace('(', ' ');
        if (!t.Contains('[')) t = t.Replace(']', ' ');
        if (!t.Contains(']')) t = t.Replace('[', ' ');
        t = Regex.Replace(t, @"\s+", " ");
        return Regex.Replace(t, @"^[\s\-.,]+|[\s\-.,]+$", "").Trim();
    }

    /// <summary>Parses one path. <paramref name="defaultPlant"/> is used when nothing names a plant.</summary>
    public static ParsedDrawing Parse(string path, string defaultPlant = "")
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = parts.Length == 0 ? path : parts[^1];
        var folders = parts.Length > 1 ? parts[..^1] : [];
        var dot = fileName.LastIndexOf('.');
        var stem = dot > 0 ? fileName[..dot] : fileName;
        var sheet = ExtractSheet(ref stem);

        // Revision.
        var revision = "";
        var m = RevWord.Match(stem);
        if (!m.Success) m = RevShort.Match(stem);
        if (m.Success)
        {
            revision = m.Groups[1].Value.ToUpperInvariant();
            stem = stem.Remove(m.Index, m.Length).Insert(m.Index, " ");
        }

        // Status words.
        DrawingStatus? status = null;
        foreach (var (re, st) in StatusWords)
        {
            var sm = re.Match(stem);
            if (!sm.Success) continue;
            status ??= st;
            stem = stem.Remove(sm.Index, sm.Length).Insert(sm.Index, " ");
        }
        status ??= revision.Length > 0 && !int.TryParse(revision, out _) ? DrawingStatus.ForApproval : DrawingStatus.ForConstruction;
        if (revision.Length == 0) revision = "0";
        stem = stem.Trim();

        // Drawing number = leading code containing a digit; the rest is the title.
        string number, title;
        var nm = NumberRe.Match(stem);
        if (nm.Success && nm.Value.Any(char.IsDigit))
        {
            number = nm.Value.TrimEnd('-', '_', '.').ToUpperInvariant();
            title = Tidy(stem[nm.Length..]);
        }
        else
        {
            number = Tidy(stem);
            title = "";
        }
        if (number.Length == 0) number = Tidy(fileName);

        var tokens = number.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);

        // Plant.
        var plant = "";
        foreach (var f in folders)
        {
            var pm = PlantFolder.Match(StripPrefix(f));
            if (pm.Success) { plant = $"Plant {pm.Groups[1].Value.Trim()}"; break; }
        }
        if (plant.Length == 0)
        {
            foreach (var t in tokens)
            {
                var pm = Regex.Match(t, @"^(?:PLANT|PLT)(\d+[A-Z]?)$");
                if (pm.Success) { plant = $"Plant {pm.Groups[1].Value}"; break; }
            }
        }
        // Folders that are not plant / unit patterns or disciplines are names:
        // "Refinery/CDU/..." → plant Refinery, unit CDU; "External Works/..." → unit.
        var named = folders
            .Where(f => !PlantFolder.IsMatch(StripPrefix(f)) && !UnitFolder.IsMatch(CleanFolder(f)) && TypeFromFolder(f) == null)
            .Select(f => Tidy(StripPrefix(f)))
            .Where(f => f.Length > 0)
            .ToList();
        var plantFromFolder = plant.Length > 0;
        if (plant.Length == 0 && named.Count >= 2) { plant = named[0]; named.RemoveAt(0); }
        if (plant.Length == 0) plant = defaultPlant;

        // Type: folder, then discipline code in the number, then keywords.
        DrawingType? type = null;
        var typeIdx = -1;
        foreach (var f in Enumerable.Reverse(folders))
        {
            if (UnitFolder.IsMatch(CleanFolder(f)) || PlantFolder.IsMatch(StripPrefix(f))) continue;
            type = TypeFromFolder(f);
            if (type != null) break;
        }
        if (type == null)
        {
            // Multi-letter codes first; the project code in position 0 is checked last.
            var order = Enumerable.Range(1, Math.Max(0, tokens.Length - 1)).Concat(tokens.Length > 0 ? [0] : []).ToArray();
            foreach (var single in new[] { false, true })
            {
                foreach (var i in order)
                {
                    if (single && tokens[i].Length != 1) continue;
                    var t = TypeFromWord(tokens[i], single);
                    if (t != null) { type = t; typeIdx = i; break; }
                }
                if (type != null) break;
            }
        }
        else typeIdx = Array.FindIndex(tokens, t => TypeFromWord(t, false) == type);
        type ??= TypeFromHints($"{number} {title}") ?? DrawingType.Other;

        // Unit / area.
        var unit = "";
        foreach (var f in folders)
        {
            var um = UnitFolder.Match(CleanFolder(f));
            if (um.Success) { unit = UnitLabel(um.Groups[1].Value, um.Groups[2].Value); break; }
        }
        if (unit.Length == 0)
        {
            for (var i = 0; i < tokens.Length; i++)
            {
                var um = Regex.Match(tokens[i], @"^U(\d{2,4}[A-Z]?)$");
                if (um.Success) { unit = $"Unit {um.Groups[1].Value}"; break; }
                if (tokens[i] is "UNIT" or "AREA" && i + 1 < tokens.Length) { unit = UnitLabel(tokens[i], tokens[i + 1]); break; }
            }
        }
        if (unit.Length == 0 && typeIdx > 0)
        {
            // Area code just before the discipline: PROJECT-100-ST-GA-0001.
            for (var i = typeIdx - 1; i >= 1; i--)
                if (Regex.IsMatch(tokens[i], @"^\d{2,4}[A-Z]?$")) { unit = $"Unit {tokens[i]}"; break; }
        }
        if (unit.Length == 0 && named.Count > 0 && (plantFromFolder || named.Count == 1 || plant != defaultPlant))
            unit = named[0];
        if (unit.Length == 0)
        {
            var um = UnitInText.Match($" {title.ToUpperInvariant()} ");
            if (um.Success) unit = UnitLabel(um.Groups[1].Value, um.Groups[2].Value);
        }

        // Sheet kind.
        var kind = tokens.Concat(Regex.Split(title.ToUpperInvariant(), @"[\s\-_]+"))
            .Select(w => Kinds.GetValueOrDefault(w.ToUpperInvariant()))
            .FirstOrDefault(k => k != null) ?? "";
        if (kind.Length == 0)
        {
            var upper = $" {title.ToUpperInvariant()} ";
            kind = KindWords.FirstOrDefault(k => upper.Contains(k.Word)).Kind ?? "";
        }

        return new ParsedDrawing(number, title, revision, plant, unit, type, kind, status.Value, fileName, string.Join('/', folders), sheet);
    }

    /// <summary>When every path sits under one wrapper folder, returns it so it can be ignored.</summary>
    public static string CommonRootFolder(IEnumerable<string> paths)
    {
        string? root = null;
        foreach (var p in paths)
        {
            var parts = p.Replace('\\', '/').Split('/');
            if (parts.Length < 2) return "";
            root ??= parts[0];
            if (parts[0] != root) return "";
        }
        return root ?? "";
    }
}
