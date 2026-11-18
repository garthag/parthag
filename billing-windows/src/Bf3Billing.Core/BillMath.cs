using System.Globalization;

namespace Bf3Billing.Core;

public static class BillMath
{
    public static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    public static decimal Round3(decimal v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    public static decimal LineAmount(decimal qty, decimal rate) => Round2(qty * rate);

    /// <summary>
    /// Gross = sum of line amounts. GST is added on gross; retention and TDS are
    /// deducted as a percentage of gross; the other deduction is a fixed amount.
    /// Net payable = gross + GST − retention − TDS − other. Due = net − paid.
    /// </summary>
    public static BillTotals Totals(Bill bill, IEnumerable<BillLine> lines)
    {
        var gross = lines.Sum(l => l.Amount);
        var gst = Round2(gross * bill.GstPct / 100m);
        var ret = Round2(gross * bill.RetentionPct / 100m);
        var tds = Round2(gross * bill.TdsPct / 100m);
        var net = gross + gst - ret - tds - bill.OtherDeduction;
        return new BillTotals(gross, gst, ret, tds, bill.OtherDeduction, net, bill.PaidAmount, net - bill.PaidAmount);
    }
}

/// <summary>Indian number and date formats: 1,23,45,678.00, ₹ 29.43 L, 09-03-2026.</summary>
public static class Fmt
{
    public static readonly CultureInfo Culture = CreateCulture();

    static CultureInfo CreateCulture()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.NumberGroupSizes = new[] { 3, 2 };
        c.NumberFormat.CurrencyGroupSizes = new[] { 3, 2 };
        c.NumberFormat.NumberDecimalSeparator = ".";
        c.NumberFormat.NumberGroupSeparator = ",";
        c.DateTimeFormat.ShortDatePattern = "dd-MM-yyyy";
        c.DateTimeFormat.DateSeparator = "-";
        return c;
    }

    public static string Money(decimal v) => v.ToString("N2", Culture);
    public static string Rupees(decimal v) => v.ToString("N0", Culture);
    public static string Tonnes(decimal v) => v.ToString("N3", Culture);
    public static string Pct(decimal v) => v.ToString("0.0", Culture);
    public static string Date(DateTime? d) => d?.ToString("dd-MM-yyyy", Culture) ?? "";

    /// <summary>Short amount for tiles: ₹ 29.43 L, ₹ 1.25 Cr, ₹ 45,000.</summary>
    public static string Short(decimal v)
    {
        var a = Math.Abs(v);
        var sign = v < 0 ? "−" : "";
        if (a >= 1_00_00_000m) return $"₹ {sign}{(a / 1_00_00_000m).ToString("0.00", Culture)} Cr";
        if (a >= 1_00_000m) return $"₹ {sign}{(a / 1_00_000m).ToString("0.00", Culture)} L";
        return $"₹ {sign}{a.ToString("N0", Culture)}";
    }

    /// <summary>Parses "6,500", "1,62,500.50", " 25 " and "" (as 0).</summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Replace("₹", "").Replace(",", "").Trim();
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    public static decimal Parse(string? text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a number.");

    public static bool TryParseDate(string? text, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] formats = { "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MM-yy" };
        return DateTime.TryParseExact(text.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
