using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Bf3Billing.Core.Bsl;

public static class ExcelUtil
{
    /// <summary>Cell value as .NET object; formula cells return the value Excel saved (no recalculation).</summary>
    public static object? Val(IXLCell c)
    {
        XLCellValue v;
        try { v = c.HasFormula ? c.CachedValue : c.Value; }
        catch { return null; }
        if (v.IsBlank || v.IsError) return null;
        if (v.IsNumber) return v.GetNumber();
        if (v.IsBoolean) return v.GetBoolean();
        if (v.IsDateTime) return v.GetDateTime();
        if (v.IsTimeSpan) return v.GetTimeSpan().TotalDays;
        var s = v.GetText();
        s = s.Replace(' ', ' ').Trim();
        return s.Length == 0 ? null : s;
    }

    public static string? Text(IXLCell c)
    {
        var v = Val(c);
        return v switch
        {
            null => null,
            double d => FormatNumber(d),
            DateTime dt => dt.ToString("dd-MM-yyyy"),
            _ => CollapseSpaces(v.ToString()!)
        };
    }

    public static string FormatNumber(double d) =>
        Math.Abs(d - Math.Round(d)) < 1e-9 && Math.Abs(d) < 1e15
            ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
            : d.ToString("0.######", CultureInfo.InvariantCulture);

    public static double? Num(IXLCell c) => ToNumber(Val(c));

    public static double? ToNumber(object? v) => v switch
    {
        null => null,
        double d => d,
        int i => i,
        decimal m => (double)m,
        string s when double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public static string CollapseSpaces(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>Header key used for alias matching: lower case letters and digits only.</summary>
    public static string Key(string? header) =>
        header == null ? "" : new string(header.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Parses dd.MM.yyyy, dd-MM-yyyy, dd/MM/yyyy or Excel date.</summary>
    public static DateTime? ToDate(object? v)
    {
        if (v is DateTime d) return d.Date;
        if (v is double n && n > 20000 && n < 80000) return DateTime.FromOADate(n).Date;
        if (v is string s)
        {
            string[] fmts = ["dd.MM.yyyy", "d.M.yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd.MM.yy", "dd-MMM-yyyy", "dd-MMM-yy"];
            if (DateTime.TryParseExact(s.Trim(), fmts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r)) return r;
        }
        return null;
    }

    public static string SafeSheetName(string name, ISet<string>? used = null)
    {
        var s = Regex.Replace(name, @"[:\\/?*\[\]]", "-").Trim();
        if (s.Length > 31) s = s[..31];
        if (used == null) return s;
        var b = s; int i = 2;
        while (!used.Add(s.ToUpperInvariant())) { var suf = "_" + i++; s = (b.Length + suf.Length > 31 ? b[..(31 - suf.Length)] : b) + suf; }
        return s;
    }
}
