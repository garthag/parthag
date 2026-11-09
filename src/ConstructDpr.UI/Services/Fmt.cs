using System.Globalization;
using ConstructDpr.Shared.Model;

namespace ConstructDpr.UI.Services;

/// <summary>Number and date formats used on every screen (Indian digit grouping for money).</summary>
public static class Fmt
{
    private static readonly CultureInfo In = CultureInfo.GetCultureInfo("en-IN");

    public static string N(decimal? v, int dp = 2) => v is null ? "—" : Math.Round(v.Value, dp).ToString(dp == 0 ? "#,##0" : "#,##0." + new string('#', dp), In);
    public static string N(double? v, int dp = 1) => v is null ? "—" : Math.Round(v.Value, dp).ToString(dp == 0 ? "#,##0" : "#,##0." + new string('#', dp), In);
    public static string Money(decimal v) => "₹ " + Math.Round(v).ToString("#,##0", In);
    public static string D(DateOnly? d) => d?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? "—";
    public static string Ds(DateOnly? d) => d?.ToString("dd-MMM-yy", CultureInfo.InvariantCulture) ?? "—";
    public static string Dt(DateTime? d) => d?.ToLocalTime().ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—";

    public static string StatusClass(DprStatus s) => s switch
    {
        DprStatus.Approved => "good",
        DprStatus.Submitted => "warn",
        DprStatus.Rejected => "bad",
        _ => "muted",
    };

    public static decimal? ParseDec(string? s) =>
        decimal.TryParse((s ?? "").Replace(",", "").Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
}
