using System.Globalization;
using System.Text.RegularExpressions;

namespace Bf3Billing.Core.Bsl;

/// <summary>Rules that make values from the different BOM sheets look the same.</summary>
public static partial class Standardizer
{
    public sealed record DrawingParts(string BaseNo, string? Sheet, string? Revision);

    /// <summary>
    /// Splits a drawing reference into base number, sheet and revision, e.g.
    /// "ERC/MCK/BSL/STH/F-001_R3 (SH. 1OF 1)" → ("ERC/MCK/BSL/STH/F-001", "1/1", "R3"),
    /// "BSL-BF3-REV-SMS-D-R-M-4028-R1, Sht-2/3-B" → ("BSL-BF3-REV-SMS-D-R-M-4028", "2/3", "R1"),
    /// "16282205R0" → ("16282205", null, "R0").
    /// </summary>
    public static DrawingParts ParseDrawing(string raw)
    {
        var s = ExcelUtil.CollapseSpaces(raw);
        string? sheet = null, rev = null;

        // revision written after the sheet bracket: "... (SHT.1 OF 1)R1"
        var m0 = Regex.Match(s, @"\)\s*R(\d{1,2})\s*$", RegexOptions.IgnoreCase);
        if (m0.Success) { rev = "R" + int.Parse(m0.Groups[1].Value, CultureInfo.InvariantCulture); s = s[..(m0.Index + 1)]; }

        // "..._1 OF 1 - Rev 0" / "... 1 OF 1 Rev. B": sheet and revision written after the number without "SHT"
        var mr = Regex.Match(s, @"[\s\-_,]+REV\.?\s*[-:]?\s*([A-Z0-9]{1,2})\s*$", RegexOptions.IgnoreCase);
        if (mr.Success)
        {
            var r = mr.Groups[1].Value.ToUpperInvariant();
            rev ??= int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var rn) ? "R" + rn : r;
            s = s[..mr.Index];
        }
        var ms = Regex.Match(s, @"[\s_]+(\d{1,3})\s*OF\s*(\d{1,3})\s*$", RegexOptions.IgnoreCase);
        if (ms.Success) { sheet = $"{ms.Groups[1].Value}/{ms.Groups[2].Value}"; s = s[..ms.Index]; }

        var m = Regex.Match(s, @"[\s,]*\(?\s*SH(?:T|EET)?\s*[.\-]?\s*(\d+)\s*(?:OF|/)\s*(\d+)\s*\)?\s*(?:-\s*[A-Z])?\s*$", RegexOptions.IgnoreCase);
        if (m.Success) { sheet = $"{m.Groups[1].Value}/{m.Groups[2].Value}"; s = s[..m.Index]; }

        m = Regex.Match(s, @"(?:[\s,_\-]+|(?<=\d))R(\d{1,2})\s*$", RegexOptions.IgnoreCase);
        if (m.Success) { rev ??= "R" + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); s = s[..m.Index]; }

        s = s.Trim().TrimEnd(',', '-', '_').Trim();
        // "ERC MCK BSL STH F-004" written with spaces instead of "/" → same series as "ERC/MCK/BSL/STH/F-001"
        if (Regex.IsMatch(s, @"^ERC\s+[A-Z]")) s = Regex.Replace(s, @"\s+", "/");
        return new DrawingParts(s.Length == 0 ? ExcelUtil.CollapseSpaces(raw) : s, sheet, rev);
    }

    /// <summary>"SHEET 9/32" → "9/32", Excel date 02-Mar → "2/3", 1 → "1".</summary>
    public static string? Sheet(object? v, out bool wasDate)
    {
        wasDate = false;
        switch (v)
        {
            case null: return null;
            case DateTime d: wasDate = true; return $"{d.Day}/{d.Month}";
            case double n: return ExcelUtil.FormatNumber(n);
        }
        var s = ExcelUtil.CollapseSpaces(v.ToString()!);
        s = Regex.Replace(s, @"^(SHEET|SHT|SH)\.?\s*[-:]?\s*", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*(OF)\s*", "/", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*/\s*", "/");
        return s.Length == 0 ? null : s;
    }

    /// <summary>0 → R0, "R1" → R1, "r 2" → R2, letters (A, B) kept.</summary>
    public static string? Revision(object? v)
    {
        if (v == null) return null;
        var s = v is double d ? ExcelUtil.FormatNumber(d) : ExcelUtil.CollapseSpaces(v.ToString()!).ToUpperInvariant();
        if (Regex.IsMatch(s, @"^\d{1,2}$")) return "R" + int.Parse(s, CultureInfo.InvariantCulture);
        var m = Regex.Match(s, @"^R(?:EV)?\.?\s*-?\s*(\d{1,2})$");
        if (m.Success) return "R" + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return s.Length == 0 ? null : s;
    }

    static readonly string[] RolledPrefixes =
        ["ISMC", "ISMB", "ISJB", "ISLB", "ISWB", "ISHB", "ISMCP", "ISA", "ISNB", "MC", "MB", "UB", "UC", "UPN", "IPE", "HEA", "HEB", "HE",
         "NPB", "WPB", "SHS", "RHS", "CHS", "ISJC", "ISLC", "RSJ", "WH", "TUBE"];

    /// <summary>Standard section code: PL-16, ISMC150, ISA75X75X6, PIPE OD168.3X5.56, FLANGE DN600, BOLT M20 ...</summary>
    public static string? Section(string? drg, double? thickness)
    {
        if (string.IsNullOrWhiteSpace(drg)) return thickness is > 0 ? "PL-" + ExcelUtil.FormatNumber(thickness.Value) : null;
        var s = ExcelUtil.CollapseSpaces(drg).ToUpperInvariant().Replace('*', 'X').Replace('×', 'X');

        if (Regex.IsMatch(s, @"^\d+(\.\d+)?$")) return "PL-" + s;   // "20" in the "Used Section" columns = plate thickness

        if (Regex.IsMatch(s, @"^(PL|PLT|PLATE)\b|^PL[\-.\d]|^PLT\d"))
        {
            if (thickness is > 0) return "PL-" + ExcelUtil.FormatNumber(thickness.Value);
            var m = Regex.Match(s, @"(\d+(?:\.\d+)?)\s*(?:MM\s*)?THK");
            if (!m.Success) m = Regex.Match(s, @"THK\.?\s*(\d+(?:\.\d+)?)");
            if (m.Success) return "PL-" + m.Groups[1].Value;
            var nums = Regex.Matches(s, @"\d+(?:\.\d+)?").Select(x => double.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
            if (nums.Count > 0) return "PL-" + ExcelUtil.FormatNumber(nums.Min());
            return "PL";
        }

        if (s.Contains("PIPE") || s.StartsWith("TUBE"))
        {
            var m = Regex.Match(s, @"[Ø0O]?\s*(\d+(?:\.\d+)?)\s*X\s*(\d+(?:\.\d+)?)");
            return m.Success ? $"PIPE OD{TrimLeadingZero(m.Groups[1].Value)}X{m.Groups[2].Value}" : "PIPE";
        }
        if (s.Contains("FLANGE") || s.Contains("WNF") || s.Contains("SORF"))
        {
            var m = Regex.Match(s, @"DN\s*-?\s*(\d+)");
            return m.Success ? "FLANGE DN" + m.Groups[1].Value : "FLANGE";
        }
        foreach (var (word, code) in new[] { ("BOLT", "BOLT"), ("NUT", "NUT"), ("WASHER", "WASHER"), ("STUD", "STUD") })
            if (s.Contains(word))
            {
                var m = Regex.Match(s, @"M\s*(\d+)");
                return m.Success ? $"{code} M{m.Groups[1].Value}" : code;
            }
        if (s.StartsWith("SPLIT PIN")) return "SPLIT PIN";

        var compact = Regex.Replace(s, @"\s+", "").TrimEnd('.');
        if (RolledPrefixes.Any(p => compact.StartsWith(p) && compact.Length > p.Length && char.IsDigit(compact[p.Length])))
            return compact;
        return s;
    }

    static string TrimLeadingZero(string v) => v.Length > 1 && v[0] == '0' && v[1] != '.' ? v[1..] : v;

    /// <summary>Fixes common OCR/typing errors in material grade: "15:2002" → "IS 2002", "IS2062" → "IS 2062", "Gr .- 2" → "Gr.2".</summary>
    public static string? Grade(string? raw, out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "-") return null;
        var s = ExcelUtil.CollapseSpaces(raw);
        var o = s;
        s = Regex.Replace(s, @"^15\s*[:.]?\s*(?=(2002|2062|1239|3589))", "IS ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^IS\s*[:.]?\s*(\d{3,5})", "IS $1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=IS \d{3,5})\s*[.,]?\s*G\s*R\s*[.\-\s]*(\d)", " Gr.$1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^(IS \d{3,5})-(\d{4})$", "$1 ($2)");
        changed = !string.Equals(Regex.Replace(o, @"\W", ""), Regex.Replace(s, @"\W", ""), StringComparison.OrdinalIgnoreCase);
        return s;
    }

    public static string? Unit(string? u)
    {
        if (string.IsNullOrWhiteSpace(u)) return null;
        var s = u.Trim().ToLowerInvariant();
        return s switch
        {
            "nr" or "nos" or "no" or "no." or "nos." => "nr",
            "set" or "sets" => "set",
            "m" or "mtr" or "rm" => "m",
            "mt" or "ton" or "t" => "MT",
            "kg" or "kgs" => "kg",
            _ => s
        };
    }

    /// <summary>"BSL_BF#3_STOVE_SHELL_BOQ" → "Stove Shell".</summary>
    public static string PackageTitle(string? a1, string sheetName)
    {
        if (string.IsNullOrWhiteSpace(a1)) return sheetName;
        var s = Regex.Replace(a1, @"^BSL_BF#?3_", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"_BOQ$", "", RegexOptions.IgnoreCase).Replace('_', ' ').Trim();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
    }

    /// <summary>ERC / SMS originator from drawing number patterns.</summary>
    public static string? Originator(string baseNo)
    {
        if (baseNo.StartsWith("ERC", StringComparison.OrdinalIgnoreCase)) return "ERC";
        if (Regex.IsMatch(baseNo, @"^BSL-BF3-REVV?-SMS", RegexOptions.IgnoreCase)) return "SMS";
        return null;
    }
}
