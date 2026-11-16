using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace BSL.Core.Formula;

/// <summary>
/// Formula support built on ADO.NET DataColumn expressions, so formulas recalculate live
/// in the grid and form while the user types. Syntax (Excel-like help is shown in the editor):
///   [ColumnName]  + - * / %  &lt; &gt; = &lt;&gt; AND OR NOT  'text'  IIF(cond, a, b)  ISNULL(x, y)
///   LEN(s) TRIM(s) SUBSTRING(s, start, len) CONVERT(x, 'System.Decimal')
/// Any column of the record can be used: contractor detail, internal / external rate,
/// weights, other user defined fields, etc.
/// </summary>
public static class FormulaEngine
{
    public static readonly string[] Functions =
    [
        "IIF(condition, true_value, false_value)", "ISNULL(value, replacement)", "LEN(text)", "TRIM(text)",
        "SUBSTRING(text, start, length)", "CONVERT(value, 'System.Decimal')", "CONVERT(value, 'System.String')"
    ];

    public static readonly (string Name, string Formula)[] Templates =
    [
        ("Margin amount", "([ExternalRate] - [InternalRate]) * [R1WeightTon]"),
        ("Margin %", "IIF([ExternalRate] > 0, ([ExternalRate] - [InternalRate]) * 100 / [ExternalRate], 0)"),
        ("Plate weight (kg) L x W x T x 7.85", "[LengthMm] * [WidthMm] * [ThicknessMm] * ISNULL([Qty],1) * 7.85 / 1000000"),
        ("Section weight (kg) L(mm) x kg/m", "[LengthMm] / 1000 * [SourceUnitWt] * ISNULL([Qty],1)"),
        ("Weight in MT", "ISNULL([TotalWeightKg],0) / 1000"),
        ("Contractor label", "[ContractorCode] + ' - ' + [ContractorName]"),
        ("GST @18% on WO value", "ISNULL([WOValue],0) * 0.18"),
        ("Balance WO value", "ISNULL([WOValue],0) - ISNULL([ActualPaid],0)"),
        ("Status flag", "IIF(ISNULL([BalanceTon],0) <= 0, 'Completed', 'In progress')")
    ];

    /// <summary>Column references used by a formula.</summary>
    public static IEnumerable<string> References(string formula) =>
        Regex.Matches(formula, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Checks a formula against a table schema. Returns null when OK, else the error text.</summary>
    public static string? Validate(DataTable schema, string formula, string? selfColumn = null)
    {
        if (string.IsNullOrWhiteSpace(formula)) return "Formula is empty.";
        if (selfColumn != null && References(formula).Any(r => r.Equals(selfColumn, StringComparison.OrdinalIgnoreCase)))
            return "A formula cannot refer to itself.";
        var missing = References(formula).Where(r => !schema.Columns.Contains(r)).ToList();
        if (missing.Count > 0) return "Unknown field(s): " + string.Join(", ", missing);

        using var probe = schema.Clone();
        if (selfColumn != null && probe.Columns.Contains(selfColumn)) probe.Columns.Remove(selfColumn);
        try
        {
            var col = new DataColumn("__probe__", typeof(object), formula);
            probe.Columns.Add(col);
            var row = probe.NewRow();
            probe.Rows.Add(row);
            _ = row["__probe__"];
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Evaluates a formula for one row (used by preview).</summary>
    public static object? Evaluate(DataRow source, string formula)
    {
        using var t = source.Table.Clone();
        t.PrimaryKey = [];
        foreach (DataColumn c in t.Columns) { c.Expression = ""; c.ReadOnly = false; c.AutoIncrement = false; }
        t.Columns.Add(new DataColumn("__eval__", typeof(object), formula));
        var r = t.NewRow();
        foreach (DataColumn c in source.Table.Columns)
            if (c.ColumnName != "__eval__") r[c.ColumnName] = source[c];
        t.Rows.Add(r);
        var v = r["__eval__"];
        return v is DBNull ? null : v;
    }

    /// <summary>Adds (or replaces) a computed column. Formula columns are evaluated by ADO.NET live.</summary>
    public static void AddFormulaColumn(DataTable table, string name, string caption, string formula)
    {
        if (table.Columns.Contains(name)) table.Columns.Remove(name);
        var col = new DataColumn(name, typeof(object)) { Caption = caption, ReadOnly = true };
        table.Columns.Add(col);
        try { col.Expression = formula; }
        catch (Exception ex)
        {
            table.Columns.Remove(col);
            var fallback = new DataColumn(name, typeof(string)) { Caption = caption + " (formula error)" };
            table.Columns.Add(fallback);
            foreach (DataRow r in table.Rows) r[fallback] = "#ERR: " + ex.Message;
            fallback.ReadOnly = true;
        }
    }

    /// <summary>
    /// "Bulk calculate": writes the result of a formula into a normal (stored) column,
    /// for all rows or for the rows selected by a filter expression. Returns rows changed.
    /// </summary>
    public static int Apply(DataTable table, string targetColumn, string formula, string? rowFilter = null, IEnumerable<DataRow>? onlyRows = null)
    {
        var target = table.Columns[targetColumn] ?? throw new ArgumentException("Unknown column " + targetColumn);
        if (target.ReadOnly || target.Expression.Length > 0) throw new InvalidOperationException(targetColumn + " is read only.");
        var err = Validate(table, formula, targetColumn);
        if (err != null) throw new InvalidOperationException(err);

        const string tmp = "__apply__";
        var col = new DataColumn(tmp, typeof(object), formula);
        table.Columns.Add(col);
        try
        {
            var rows = onlyRows?.ToList() ?? (string.IsNullOrWhiteSpace(rowFilter) ? table.Rows.Cast<DataRow>().ToList() : table.Select(rowFilter).ToList());
            int n = 0;
            foreach (var r in rows)
            {
                if (r.RowState == DataRowState.Deleted) continue;
                var v = r[tmp];
                object newValue = v is DBNull or null ? DBNull.Value : ConvertTo(v, target.DataType);
                if (!Equals(r[target], newValue)) { r[target] = newValue; n++; }
            }
            return n;
        }
        finally { table.Columns.Remove(col); }
    }

    public static object ConvertTo(object v, Type t)
    {
        if (t == typeof(string)) return Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        if (t == typeof(decimal)) return Math.Round(Convert.ToDecimal(v, CultureInfo.InvariantCulture), 4);
        return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
    }
}
