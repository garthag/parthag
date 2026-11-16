using System.Data;
using System.Globalization;
using BSL.Core.Excel;
using BSL.Core.Metadata;

namespace BSL.Core.Import;

public sealed record TableImportResult(int Added, int Updated, int Skipped, List<string> Messages);

/// <summary>
/// Copies rows from an Excel sheet (read with <see cref="TableExcel.Read"/>) into an entity DataTable.
/// Columns are matched by caption or field name; lookups accept a code (GRE, BM05, drawing no.) or display text.
/// Rows with an Id that exists are updated, others are added. Nothing is saved until the user presses Save.
/// </summary>
public static class TableImporter
{
    public static TableImportResult Merge(DataTable target, IList<FieldDef> fields, DataTable source,
                                          Func<LookupDef, DataTable> lookupData, IDictionary<string, string>? manualMap = null)
    {
        var msgs = new List<string>();
        var map = new Dictionary<FieldDef, string>();
        foreach (var f in fields.Where(f => !f.ReadOnly && target.Columns.Contains(f.Name)))
        {
            string? src = null;
            if (manualMap != null && manualMap.TryGetValue(f.Name, out var m) && source.Columns.Contains(m)) src = m;
            src ??= source.Columns.Cast<DataColumn>().Select(c => c.ColumnName)
                          .FirstOrDefault(c => ExcelUtil.Key(c) == ExcelUtil.Key(f.Caption) || ExcelUtil.Key(c) == ExcelUtil.Key(f.Name));
            if (src != null) map[f] = src;
        }
        if (map.Count == 0) return new(0, 0, source.Rows.Count, ["No column of the sheet matches a field of this screen. Use the template (Excel ▸ Template)."]);
        msgs.Add("Mapped: " + string.Join(", ", map.Select(x => $"{x.Value} → {x.Key.Caption}")));
        var unmapped = source.Columns.Cast<DataColumn>().Select(c => c.ColumnName).Except(map.Values).Where(c => c != "Id").ToList();
        if (unmapped.Count > 0) msgs.Add("Ignored columns: " + string.Join(", ", unmapped));

        var lookupCache = new Dictionary<LookupDef, List<(int Id, string Display)>>();
        var idCol = source.Columns.Cast<DataColumn>().FirstOrDefault(c => c.ColumnName.Equals("Id", StringComparison.OrdinalIgnoreCase));
        int added = 0, updated = 0, skipped = 0, rowNo = 1;

        foreach (DataRow s in source.Rows)
        {
            rowNo++;
            DataRow? t = null;
            if (idCol != null && ExcelUtil.ToNumber(s[idCol] is DBNull ? null : s[idCol]) is { } idv && target.PrimaryKey.Length == 1)
                t = target.Rows.Find((int)idv);
            bool isNew = t == null;
            t ??= target.NewRow();
            bool any = false;
            foreach (var (f, col) in map)
            {
                var raw = s[col] is DBNull ? null : s[col];
                try
                {
                    var v = Convert(f, raw, lookupData, lookupCache, out var warn);
                    if (warn != null) msgs.Add($"Row {rowNo}, {f.Caption}: {warn}");
                    t[f.Name] = v ?? DBNull.Value;
                    if (v != null) any = true;
                }
                catch (Exception ex) { msgs.Add($"Row {rowNo}, {f.Caption}: '{raw}' – {ex.Message}"); }
            }
            if (!any) { skipped++; if (isNew) continue; }
            if (isNew) { target.Rows.Add(t); added++; } else updated++;
        }
        return new(added, updated, skipped, msgs);
    }

    static object? Convert(FieldDef f, object? raw, Func<LookupDef, DataTable> lookupData,
                           Dictionary<LookupDef, List<(int, string)>> cache, out string? warn)
    {
        warn = null;
        if (raw == null) return null;
        switch (f.Kind)
        {
            case FieldKind.Int:
                return ExcelUtil.ToNumber(raw) is { } i ? (int)Math.Round(i) : throw new FormatException("not a whole number");
            case FieldKind.Decimal or FieldKind.Money:
                return ExcelUtil.ToNumber(raw) is { } d ? (decimal)d : throw new FormatException("not a number");
            case FieldKind.Date:
                return ExcelUtil.ToDate(raw) ?? throw new FormatException("not a date");
            case FieldKind.Bool:
                var b = raw.ToString()!.Trim().ToLowerInvariant();
                return b is "1" or "yes" or "y" or "true" or "x";
            case FieldKind.Lookup:
            {
                if (!cache.TryGetValue(f.Lookup!, out var list))
                    cache[f.Lookup!] = list = lookupData(f.Lookup!).Rows.Cast<DataRow>().Select(r => (System.Convert.ToInt32(r[0]), r[1]?.ToString() ?? "")).ToList();
                var text = raw is double n ? ExcelUtil.FormatNumber(n) : raw.ToString()!.Trim();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var asId) && list.Any(x => x.Item1 == asId)
                    && !list.Any(x => Code(x.Item2) == text)) return asId;
                var hit = list.FirstOrDefault(x => x.Item2.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (hit.Item2 == null) hit = list.FirstOrDefault(x => Code(x.Item2).Equals(text, StringComparison.OrdinalIgnoreCase));
                if (hit.Item2 == null) hit = list.FirstOrDefault(x => x.Item2.StartsWith(text, StringComparison.OrdinalIgnoreCase));
                if (hit.Item2 == null) { warn = $"'{text}' not found in {f.Lookup!.Entity} – left blank"; return null; }
                return hit.Item1;
            }
            default:
                var str = raw is double dd ? ExcelUtil.FormatNumber(dd) : raw is DateTime dt ? dt.ToString("dd-MM-yyyy") : raw.ToString()!.Trim();
                if (f.MaxLength > 0 && str.Length > f.MaxLength) { warn = $"cut to {f.MaxLength} characters"; str = str[..f.MaxLength]; }
                return str.Length == 0 ? null : str;
        }
    }

    /// <summary>First token of a lookup display text: "GRE - G.R ENTERPRISES" → "GRE", "BM05 | GRE | …" → "BM05".</summary>
    static string Code(string display)
    {
        var p = display.IndexOf(" | ", StringComparison.Ordinal);
        if (p < 0) p = display.IndexOf(" - ", StringComparison.Ordinal);
        return (p > 0 ? display[..p] : display).Trim();
    }
}
