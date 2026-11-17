using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Bf3Billing.Core;

// ═══════════════════════════════════════════════════════════════════════════
//  Import of any Excel workbook: reads every sheet as it is, finds the header
//  row, fits the columns to the app's fields (or to new fields the user adds),
//  checks for duplicates within the file and against the data already saved,
//  and adds the rows the user keeps.
// ═══════════════════════════════════════════════════════════════════════════

public enum ImportTarget { None, Contractors, WorkOrders, Bom, BillLines }

public enum FieldKind { Text, Number, Date }

public sealed record TargetField(string Key, string Label, FieldKind Kind, bool Required, string[] Aliases);

public static class ImportTargets
{
    public static readonly ImportTarget[] All = { ImportTarget.Contractors, ImportTarget.WorkOrders, ImportTarget.Bom, ImportTarget.BillLines };

    public static string Label(ImportTarget t) => t switch
    {
        ImportTarget.Contractors => "Contractors",
        ImportTarget.WorkOrders => "Work orders",
        ImportTarget.Bom => "BOM",
        ImportTarget.BillLines => "Bill lines",
        _ => "Don't import",
    };

    public static string Entity(ImportTarget t) => t switch
    {
        ImportTarget.Contractors => Entities.Contractor,
        ImportTarget.WorkOrders => Entities.WorkOrder,
        ImportTarget.Bom => Entities.BomItem,
        ImportTarget.BillLines => Entities.BillLine,
        _ => "",
    };

    static TargetField F(string key, string label, FieldKind kind, bool req, params string[] aliases) => new(key, label, kind, req, aliases);
    const FieldKind T = FieldKind.Text, N = FieldKind.Number, D = FieldKind.Date;

    static readonly Dictionary<ImportTarget, TargetField[]> Fields = new()
    {
        [ImportTarget.Contractors] = new[]
        {
            F("name", "Name", T, true, "Name", "Contractor", "Contractor name", "Name of contractor", "Agency", "Agency name", "Firm", "Firm name", "Vendor", "Vendor name", "Party", "Party name"),
            F("code", "Code", T, false, "Code", "Contractor code", "Agency code", "Vendor code", "Party code", "Short name"),
            F("gstin", "GSTIN", T, false, "GSTIN", "GST no", "GST number", "GSTIN no", "GST", "GST reg no"),
            F("pan", "PAN", T, false, "PAN", "PAN no", "PAN number"),
            F("phone", "Phone", T, false, "Phone", "Phone no", "Mobile", "Mobile no", "Contact", "Contact no", "Contact number", "Tel"),
            F("address", "Address", T, false, "Address", "Office address", "Registered address"),
            F("remarks", "Remarks", T, false, "Remarks", "Remark", "Notes", "Note"),
        },
        [ImportTarget.WorkOrders] = new[]
        {
            F("wo", "Work order no.", T, true, "Work order", "Work order no", "Work order number", "WO", "WO no", "WO number", "Order no", "LOA no", "PO no"),
            F("contractor", "Contractor", T, true, "Contractor", "Contractor name", "Agency", "Agency name", "Firm", "Vendor", "Party", "Name of contractor"),
            F("service", "Service", T, false, "Service", "Service type", "Type of work", "Work type", "Nature of work", "Scope"),
            F("value", "Value ₹", N, false, "Value", "WO value", "Order value", "Contract value", "Work order value", "Amount", "WO amount"),
            F("date", "WO date", D, false, "WO date", "Date", "Order date", "Work order date", "LOA date"),
            F("status", "Status", T, false, "Status", "WO status"),
            F("remarks", "Remarks", T, false, "Remarks", "Remark", "Notes"),
        },
        [ImportTarget.Bom] = new[]
        {
            F("system", "System", T, false, "System", "Package", "Area", "Structure", "Unit name"),
            F("drawing", "Drawing no.", T, true, "Drawing no", "Drawing number", "Drawing", "DRG NO", "DRG NUMBER", "Drg No", "Dwg no", "Drawing ref"),
            F("sheet", "Sheet", T, false, "Sheet", "Sht", "Sheet no", "Sht no"),
            F("rev", "Rev", T, false, "Rev", "Revision", "Rev no"),
            F("item", "Item / mark", T, false, "Item", "Mark", "Mark no", "Assembly", "Assembly mark", "Assembly mark no", "Item no", "Part mark"),
            F("tonnes", "Weight (t)", N, false, "Tonnes", "Tonne", "Weight (t)", "Weight t", "Wt (t)", "Wt (MT)", "Weight (MT)", "MT", "Total weight (MT)", "Total wt (MT)", "Total (MT)", "Weight in MT", "Qty (MT)"),
            F("kg", "Weight (kg)", N, false, "Weight (kg)", "Weight kg", "Wt (kg)", "Total weight (kg)", "Total wt (kg)", "Total (kg)", "kg", "Total weight", "Total wt", "Weight in kg"),
        },
        [ImportTarget.BillLines] = new[]
        {
            F("wo", "Work order", T, true, "Work order", "Work order no", "WO", "WO no", "WO number"),
            F("billno", "Bill no.", T, true, "Bill no", "Bill number", "RA no", "RA bill no", "Bill"),
            F("type", "Bill type", T, false, "Type", "Bill type"),
            F("date", "Bill date", D, false, "Bill date", "Date"),
            F("status", "Status", T, false, "Status", "Bill status"),
            F("gst", "GST %", N, false, "GST %", "GST", "GST percent"),
            F("ret", "Retention %", N, false, "Retention %", "Retention"),
            F("tds", "TDS %", N, false, "TDS %", "TDS"),
            F("other", "Other deduction", N, false, "Other deduction", "Other deduction ₹", "Deduction"),
            F("paid", "Paid", N, false, "Paid", "Paid ₹", "Paid amount", "Amount paid"),
            F("paidon", "Paid on", D, false, "Paid on", "Payment date", "Date of payment"),
            F("system", "System", T, false, "System"),
            F("drawing", "Drawing no.", T, false, "Drawing no", "Drawing", "DRG NO", "Drg No"),
            F("sheet", "Sheet", T, false, "Sheet", "Sht"),
            F("rev", "Rev", T, false, "Rev", "Revision"),
            F("item", "Item / mark", T, false, "Item", "Mark"),
            F("desc", "Description", T, false, "Description", "Description of work", "Particulars", "Work", "Free line"),
            F("activity", "Activity", T, false, "Activity"),
            F("unit", "Unit", T, false, "Unit", "UOM"),
            F("qty", "Qty", N, true, "Qty", "Quantity", "Qty this bill", "Billed qty"),
            F("rate", "Rate ₹", N, true, "Rate", "Rate ₹", "Unit rate"),
            F("remarks", "Remarks", T, false, "Remarks", "Line remarks"),
        },
    };

    public static IReadOnlyList<TargetField> FieldsOf(ImportTarget t) => Fields.GetValueOrDefault(t) ?? Array.Empty<TargetField>();

    /// <summary>What makes two rows "the same record".</summary>
    public static string KeyDescription(ImportTarget t) => t switch
    {
        ImportTarget.Contractors => "same contractor name",
        ImportTarget.WorkOrders => "same work order no.",
        ImportTarget.Bom => "same system, drawing, sheet, rev and item",
        ImportTarget.BillLines => "same bill line (work order, bill no., item, activity, qty and rate)",
        _ => "",
    };
}

/// <summary>Cleans the values found in untidy sheets.</summary>
public static class Clean
{
    static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    static readonly Regex LeadingNumber = new(@"^[+-]?(\d+(\.\d*)?|\.\d+)", RegexOptions.Compiled);

    public static string Text(string? s) => Spaces.Replace(s ?? "", " ").Trim();

    /// <summary>Reads "₹ 1,23,456.50", "Rs. 500/-", "25 MT", "(1,000)", "12.5%". Returns false for blank or text.</summary>
    public static bool Number(string? s, out decimal value)
    {
        value = 0;
        var t = Text(s);
        if (t.Length == 0) return false;
        var neg = t.StartsWith('(') && t.EndsWith(')');
        t = t.Trim('(', ')').Replace("₹", "").Replace(",", "").Replace(" ", "");
        foreach (var p in new[] { "Rs.", "Rs", "INR" })
            if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) t = t[p.Length..];
        t = t.TrimEnd('-', '/').TrimEnd('/');
        var m = LeadingNumber.Match(t);
        if (!m.Success) return false;
        var rest = t[m.Length..];
        // Accept a unit or % after the number, but not "9/12" or "12-03-2026".
        if (rest.Length > 0 && (rest[0] is '/' or '-' or ':' || char.IsDigit(rest[0]))) return false;
        if (!decimal.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (neg) value = -value;
        return true;
    }

    public static bool Date(object? raw, out DateTime date)
    {
        date = default;
        switch (raw)
        {
            case DateTime d: date = d.Date; return true;
            case double n when n > 20000 && n < 80000: date = DateTime.FromOADate(n).Date; return true;
            default: return Fmt.TryParseDate(Text(raw?.ToString()), out date);
        }
    }

    /// <summary>Key for comparing codes and numbers: case and spaces ignored.</summary>
    public static string Key(string? s) => Spaces.Replace(s ?? "", "").ToUpperInvariant();

    /// <summary>Key for comparing names: only letters and digits count ("G.R. Enterprises" = "GR ENTERPRISES").</summary>
    public static string NameKey(string? s) => new string((s ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static string Norm(string? s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>0..1, how alike two header names are (edit distance).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    /// <summary>Shows a cell the way the user typed it.</summary>
    public static string Display(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
        double n => n.ToString("0.######", CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        _ => Text(v.ToString()),
    };
}

/// <summary>A worksheet read exactly as it is (every used cell, no header assumed).</summary>
public sealed class RawSheet
{
    public string Name { get; init; } = "";
    /// <summary>Excel row number and the cells of that row (string, double, DateTime, bool or null).</summary>
    public List<(int Row, object?[] Cells)> Rows { get; } = new();
    public int Width { get; set; }
    public int SuggestedHeaderRow { get; set; }

    public static List<RawSheet> ReadWorkbook(string path, int maxRows = 100_000)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var wb = new XLWorkbook(stream);
        var sheets = new List<RawSheet>();
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            var raw = new RawSheet { Name = ws.Name.Trim() };
            if (used == null) { sheets.Add(raw); continue; }
            var firstCol = used.FirstColumn().ColumnNumber();
            var lastCol = used.LastColumn().ColumnNumber();
            raw.Width = lastCol - firstCol + 1;
            var lastRow = Math.Min(used.LastRow().RowNumber(), used.FirstRow().RowNumber() + maxRows);
            for (var r = used.FirstRow().RowNumber(); r <= lastRow; r++)
            {
                var cells = new object?[raw.Width];
                var any = false;
                for (var c = 0; c < raw.Width; c++)
                {
                    var cell = ws.Cell(r, firstCol + c);
                    if (cell.IsEmpty() && cell.IsMerged())
                    {
                        // A merged block keeps its value in the first cell only; repeat it so headers and groups read right.
                        var m = cell.MergedRange();
                        if (m != null && m.FirstRow().RowNumber() == r) cell = m.FirstCell();
                    }
                    cells[c] = Value(cell);
                    any |= cells[c] != null;
                }
                if (any) raw.Rows.Add((r, cells));
            }
            raw.SuggestedHeaderRow = raw.FindHeaderRow();
            sheets.Add(raw);
        }
        return sheets;
    }

    static object? Value(IXLCell cell)
    {
        try
        {
            return cell.DataType switch
            {
                XLDataType.Blank => null,
                XLDataType.Number => cell.GetDouble(),
                XLDataType.DateTime => cell.GetDateTime(),
                XLDataType.Boolean => cell.GetBoolean(),
                XLDataType.Error => null,
                _ => Clean.Text(cell.GetFormattedString()) is { Length: > 0 } s ? s : null,
            };
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException) { return Clean.Text(cell.GetFormattedString()); }
    }

    static readonly HashSet<string> KnownHeaders = ImportTargets.All
        .SelectMany(t => ImportTargets.FieldsOf(t).SelectMany(f => f.Aliases)).Select(Clean.Norm).ToHashSet();

    /// <summary>The row in the first 25 that looks most like a header: many text cells, especially known column names.</summary>
    public int FindHeaderRow()
    {
        var best = Rows.Count > 0 ? Rows[0].Row : 1;
        var bestScore = -1.0;
        foreach (var (row, cells) in Rows.Take(25))
        {
            var texts = cells.OfType<string>().ToList();
            var filled = cells.Count(c => c != null);
            if (filled < 2) continue;
            var known = texts.Count(t => KnownHeaders.Contains(Clean.Norm(t)));
            var score = texts.Count + known * 3 - (filled - texts.Count) * 0.5;
            if (score > bestScore) { bestScore = score; best = row; }
        }
        return best;
    }

    /// <summary>Column names taken from the header row ("Column C" for a blank one; repeats get "(2)").</summary>
    public List<string> Headers(int headerRow)
    {
        var cells = Rows.FirstOrDefault(r => r.Row == headerRow).Cells ?? new object?[Width];
        var names = new List<string>();
        for (var c = 0; c < Width; c++)
        {
            var h = Clean.Display(cells[c]);
            if (h.Length == 0) h = "Column " + ColumnLetter(c + 1);
            var name = h; var n = 2;
            while (names.Contains(name, StringComparer.OrdinalIgnoreCase)) name = $"{h} ({n++})";
            names.Add(name);
        }
        return names;
    }

    /// <summary>Data rows under the header: blank rows are dropped, total rows are reported as skipped.</summary>
    public IEnumerable<(int Row, object?[] Cells, bool IsTotal)> DataRows(int headerRow)
    {
        foreach (var (row, cells) in Rows)
        {
            if (row <= headerRow) continue;
            var first = cells.Select(Clean.Display).FirstOrDefault(s => s.Length > 0) ?? "";
            var isTotal = Regex.IsMatch(first, @"^(grand\s*|sub\s*)?total\b", RegexOptions.IgnoreCase);
            yield return (row, cells, isTotal);
        }
    }

    public static string ColumnLetter(int n)
    {
        var s = "";
        while (n > 0) { var m = (n - 1) % 26; s = (char)('A' + m) + s; n = (n - 1) / 26; }
        return s;
    }
}

/// <summary>Where one Excel column goes.</summary>
public sealed class ColumnMap
{
    public int Column { get; init; }
    public string Header { get; init; } = "";
    public string Sample { get; set; } = "";
    /// <summary>A field of the target (its key), when the column fits one.</summary>
    public string? FieldKey { get; set; }
    /// <summary>A user-added field, existing or to be created, when the column fits no built-in field.</summary>
    public string? CustomField { get; set; }
    public bool NewCustomField { get; set; }
    /// <summary>Not matched: the user must choose a field, add a new one or leave it out.</summary>
    public bool NeedsDecision { get; set; }
    public bool Ignored => FieldKey == null && CustomField == null;
    public bool AutoMatched { get; set; }
}

public enum RowStatus { New, Exists, Repeated, Invalid, Total }

public sealed class PreviewRow
{
    public int SourceRow { get; init; }
    public RowStatus Status { get; set; }
    /// <summary>Rows with the same group number are the same record (repeated in the file).</summary>
    public int? Group { get; set; }
    public string Note { get; set; } = "";
    public bool Include { get; set; }
    public long? ExistingId { get; set; }
    public string Key { get; set; } = "";
    /// <summary>Cleaned values by field key, and user-added fields by name (prefix "+").</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> Numbers { get; } = new();
    public Dictionary<string, DateTime> Dates { get; } = new();
}

public enum ExistingAction { Skip, Update }

public sealed class ImportOptions
{
    /// <summary>What to do with rows that are already saved (not used for bills: an existing bill is always kept).</summary>
    public ExistingAction Existing { get; set; } = ExistingAction.Skip;
    /// <summary>Work orders whose contractor is not in the list: add the contractor.</summary>
    public bool CreateMissingContractors { get; set; } = true;
}

public sealed class ImportPreview
{
    public required ImportTarget Target { get; init; }
    public required string SheetName { get; init; }
    public required List<ColumnMap> Columns { get; init; }
    public List<PreviewRow> Rows { get; } = new();
    public int Count(RowStatus s) => Rows.Count(r => r.Status == s);
    public int Groups => Rows.Where(r => r.Group.HasValue).Select(r => r.Group).Distinct().Count();
    public string Summary =>
        $"{Rows.Count(r => r.Status != RowStatus.Total)} row(s): {Count(RowStatus.New)} new · {Count(RowStatus.Exists)} already saved · " +
        $"{Count(RowStatus.Repeated)} repeated in the file ({Groups} group(s)) · {Count(RowStatus.Invalid)} with problems" +
        (Count(RowStatus.Total) > 0 ? $" · {Count(RowStatus.Total)} total row(s) left out" : "") +
        $" · {Rows.Count(r => r.Include)} ticked to import";

    /// <summary>The columns shown in the check: mapped fields first, then user-added fields.</summary>
    public IEnumerable<(string Key, string Title)> ValueColumns()
    {
        foreach (var f in ImportTargets.FieldsOf(Target))
            if (Columns.Any(c => c.FieldKey == f.Key)) yield return (f.Key, f.Label);
        foreach (var c in Columns.Where(c => c.CustomField != null))
            yield return ("+" + c.CustomField, c.CustomField! + (c.NewCustomField ? " (new field)" : ""));
    }
}

public static class SmartImport
{
    /// <summary>The target whose column names fit the sheet best (None when nothing fits).</summary>
    public static ImportTarget SuggestTarget(RawSheet sheet, int headerRow)
    {
        var headers = sheet.Headers(headerRow);
        var best = ImportTarget.None;
        var bestScore = 0.0;
        foreach (var t in ImportTargets.All)
        {
            var map = AutoMap(sheet, headerRow, t, Array.Empty<CustomField>());
            var fields = ImportTargets.FieldsOf(t);
            var mapped = map.Where(m => m.FieldKey != null).Select(m => m.FieldKey!).ToHashSet();
            var required = fields.Where(f => f.Required).ToList();
            var reqHit = required.Count(f => mapped.Contains(f.Key));
            if (reqHit < required.Count) continue;
            if (t == ImportTarget.Bom && !mapped.Contains("tonnes") && !mapped.Contains("kg")) continue;
            var score = mapped.Count + reqHit * 2 + (double)mapped.Count / fields.Count;
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    /// <summary>Fits each column to a field by its name: exact, then close spelling. User-added fields count too.</summary>
    public static List<ColumnMap> AutoMap(RawSheet sheet, int headerRow, ImportTarget target, IEnumerable<CustomField> customFields)
    {
        var headers = sheet.Headers(headerRow);
        var data = sheet.DataRows(headerRow).Where(r => !r.IsTotal).Take(200).ToList();
        var fields = ImportTargets.FieldsOf(target);
        var customs = customFields.ToList();
        var taken = new HashSet<string>();
        var result = new List<ColumnMap>();

        for (var c = 0; c < headers.Count; c++)
        {
            var samples = data.Select(r => Clean.Display(r.Cells[c])).Where(s => s.Length > 0).Distinct().Take(3).ToList();
            var map = new ColumnMap { Column = c, Header = headers[c], Sample = string.Join(" | ", samples) };
            result.Add(map);
            var blankHeader = headers[c].StartsWith("Column ", StringComparison.Ordinal) && headers[c].Length <= 10;
            if (samples.Count == 0) continue; // empty column: left out
            var h = Clean.Norm(headers[c]);

            TargetField? hit = null;
            if (!blankHeader)
            {
                hit = fields.FirstOrDefault(f => !taken.Contains(f.Key) && f.Aliases.Any(a => Clean.Norm(a) == h));
                hit ??= fields.Where(f => !taken.Contains(f.Key))
                    .Select(f => (f, score: f.Aliases.Max(a => Clean.Similarity(Clean.Norm(a), h))))
                    .Where(x => x.score >= 0.84).OrderByDescending(x => x.score).Select(x => x.f).FirstOrDefault();
            }
            if (hit != null)
            {
                map.FieldKey = hit.Key;
                map.AutoMatched = true;
                taken.Add(hit.Key);
                continue;
            }
            var custom = blankHeader ? null : customs.FirstOrDefault(f => Clean.Norm(f.Name) == h);
            if (custom != null && !taken.Contains("+" + custom.Name))
            {
                map.CustomField = custom.Name;
                map.AutoMatched = true;
                taken.Add("+" + custom.Name);
                continue;
            }
            map.NeedsDecision = true;
        }
        return result;
    }

    /// <summary>Reads, cleans and checks every row; nothing is saved.</summary>
    public static ImportPreview Analyze(BillingRepository repo, RawSheet sheet, int headerRow, ImportTarget target, List<ColumnMap> columns)
    {
        var preview = new ImportPreview { Target = target, SheetName = sheet.Name, Columns = columns };
        var fields = ImportTargets.FieldsOf(target).ToDictionary(f => f.Key);

        foreach (var (rowNo, cells, isTotal) in sheet.DataRows(headerRow))
        {
            var row = new PreviewRow { SourceRow = rowNo };
            preview.Rows.Add(row);
            var problems = new List<string>();
            foreach (var m in columns.Where(m => !m.Ignored))
            {
                var raw = m.Column < cells.Length ? cells[m.Column] : null;
                var text = Clean.Display(raw);
                if (m.CustomField != null) { if (text.Length > 0) row.Values["+" + m.CustomField] = text; continue; }
                var f = fields[m.FieldKey!];
                if (text.Length == 0) continue;
                switch (f.Kind)
                {
                    case FieldKind.Number:
                        if (raw is double dn) { row.Numbers[f.Key] = (decimal)dn; row.Values[f.Key] = Fmt.TryParse(text, out _) ? text : dn.ToString(CultureInfo.InvariantCulture); }
                        else if (Clean.Number(text, out var n)) { row.Numbers[f.Key] = n; row.Values[f.Key] = n.ToString(CultureInfo.InvariantCulture); }
                        else { row.Values[f.Key] = text; problems.Add($"{f.Label} '{text}' is not a number"); }
                        break;
                    case FieldKind.Date:
                        if (Clean.Date(raw, out var d)) { row.Dates[f.Key] = d; row.Values[f.Key] = Fmt.Date(d); }
                        else { row.Values[f.Key] = text; problems.Add($"{f.Label} '{text}' is not a date"); }
                        break;
                    default:
                        // Excel turns sheet numbers such as 2/3 into dates; turn them back.
                        if (f.Key == "sheet" && raw is DateTime sd) text = $"{sd.Day}/{sd.Month}";
                        row.Values[f.Key] = text;
                        break;
                }
            }
            if (isTotal) { row.Status = RowStatus.Total; row.Note = "Total row, left out"; continue; }
            if (row.Values.Count == 0) { row.Status = RowStatus.Total; row.Note = "No data in the chosen columns"; continue; }
            foreach (var f in fields.Values.Where(f => f.Required && !row.Values.ContainsKey(f.Key)))
                problems.Add($"{f.Label} is missing");
            if (target == ImportTarget.Bom && !row.Numbers.ContainsKey("tonnes") && !row.Numbers.ContainsKey("kg") && !problems.Any(p => p.StartsWith("Weight")))
                problems.Add("Weight is missing");
            if (row.Numbers.Values.Any(v => v < 0) && target != ImportTarget.WorkOrders) problems.Add("Negative numbers are not allowed");
            if (problems.Count > 0) { row.Status = RowStatus.Invalid; row.Note = string.Join("; ", problems); }
        }

        switch (target)
        {
            case ImportTarget.Contractors: CheckContractors(repo, preview); break;
            case ImportTarget.WorkOrders: CheckWorkOrders(repo, preview); break;
            case ImportTarget.Bom: CheckBom(repo, preview); break;
            case ImportTarget.BillLines: CheckBillLines(repo, preview); break;
        }
        foreach (var r in preview.Rows) r.Include = r.Status == RowStatus.New;
        return preview;
    }

    static string V(PreviewRow r, string key) => r.Values.GetValueOrDefault(key, "");

    /// <summary>The cleaned values of a row as one string, to find exact copies.</summary>
    static string Content(PreviewRow r) => string.Join("\u0001", r.Values.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key + "=" + Clean.Key(kv.Value)));

    /// <summary>
    /// Groups valid rows with the same key. The first row of a group is kept; the others are marked
    /// Repeated ("exact copy of row N" or "same … as row N, different details").
    /// </summary>
    static void MarkRepeats(ImportPreview p, Func<PreviewRow, string> key, string what, bool sumInstead = false)
    {
        var group = p.Rows.Where(r => r.Group.HasValue).Select(r => r.Group!.Value).DefaultIfEmpty(0).Max();
        foreach (var g in p.Rows.Where(r => r.Status is RowStatus.New or RowStatus.Exists).GroupBy(key).Where(g => g.Count() > 1))
        {
            group++;
            var first = g.First();
            var seen = new Dictionary<string, int>();
            foreach (var r in g)
            {
                r.Group = group;
                var content = Content(r);
                if (r == first) { seen[content] = r.SourceRow; continue; }
                if (seen.TryGetValue(content, out var copyOf))
                {
                    r.Status = RowStatus.Repeated;
                    r.Note = $"Exact copy of row {copyOf}";
                    continue;
                }
                seen[content] = r.SourceRow;
                if (sumInstead) { r.Note = $"Same {what} as row {first.SourceRow}: weights are added together"; continue; }
                r.Status = RowStatus.Repeated;
                r.Note = $"Same {what} as row {first.SourceRow}, different details";
            }
        }
    }

    static void CheckContractors(BillingRepository repo, ImportPreview p)
    {
        var existing = repo.Contractors().GroupBy(k => Clean.NameKey(k.Name)).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in p.Rows.Where(r => r.Status == RowStatus.New))
        {
            r.Key = Clean.NameKey(V(r, "name"));
            if (existing.TryGetValue(r.Key, out var k)) { r.Status = RowStatus.Exists; r.ExistingId = k.Id; r.Note = $"Already saved as {k.Name}"; }
        }
        MarkRepeats(p, r => r.Key, "contractor name");
    }

    static void CheckWorkOrders(BillingRepository repo, ImportPreview p)
    {
        var existing = repo.WorkOrders().GroupBy(w => Clean.Key(w.WoNo)).ToDictionary(g => g.Key, g => g.First());
        var contractors = repo.Contractors().Select(k => Clean.NameKey(k.Name)).ToHashSet();
        foreach (var r in p.Rows.Where(r => r.Status == RowStatus.New))
        {
            r.Key = Clean.Key(V(r, "wo"));
            if (existing.TryGetValue(r.Key, out var w)) { r.Status = RowStatus.Exists; r.ExistingId = w.Id; r.Note = $"Already saved ({w.ContractorName})"; }
            else if (!contractors.Contains(Clean.NameKey(V(r, "contractor")))) r.Note = $"New contractor: {V(r, "contractor")}";
        }
        MarkRepeats(p, r => r.Key, "work order no.");
    }

    static string BomKey(string system, string drawing, string sheet, string rev, string item) =>
        string.Join("|", Clean.Key(system), Clean.Key(drawing), Clean.Key(sheet), Clean.Key(rev), Clean.Key(item));

    static void CheckBom(BillingRepository repo, ImportPreview p)
    {
        var existing = repo.BomItems().GroupBy(b => BomKey(b.System, b.DrawingNo, b.Sheet, b.Rev, b.Item)).ToDictionary(g => g.Key, g => g.First());
        foreach (var r in p.Rows.Where(r => r.Status == RowStatus.New))
        {
            if (!r.Values.ContainsKey("system")) r.Values["system"] = p.SheetName;
            r.Key = BomKey(V(r, "system"), V(r, "drawing"), V(r, "sheet"), V(r, "rev"), V(r, "item"));
            var t = Tonnes(r);
            if (existing.TryGetValue(r.Key, out var b))
            {
                r.Status = RowStatus.Exists;
                r.ExistingId = b.Id;
                r.Note = $"Already saved: {Fmt.Tonnes(b.Tonnes)} t";
            }
            else r.Note = $"{Fmt.Tonnes(t)} t";
        }
        // Parts of the same drawing are rows with the same key and different details: they are added up, not dropped.
        MarkRepeats(p, r => r.Key, "drawing / item", sumInstead: true);
    }

    internal static decimal Tonnes(PreviewRow r) =>
        r.Numbers.TryGetValue("tonnes", out var t) ? t : r.Numbers.TryGetValue("kg", out var kg) ? kg / 1000m : 0m;

    static void CheckBillLines(BillingRepository repo, ImportPreview p)
    {
        var wos = repo.WorkOrders().GroupBy(w => Clean.Key(w.WoNo)).ToDictionary(g => g.Key, g => g.First());
        var bills = repo.BillSummaries().Select(b => (b.WorkOrderId, Clean.Key(b.BillNo))).ToHashSet();
        var acts = repo.Activities().Select(a => Clean.NameKey(a.Name)).ToHashSet();
        var bom = repo.BomItems();
        foreach (var r in p.Rows.Where(r => r.Status == RowStatus.New))
        {
            r.Key = string.Join("|", Clean.Key(V(r, "wo")), Clean.Key(V(r, "billno")), Clean.Key(V(r, "drawing")), Clean.Key(V(r, "sheet")),
                Clean.Key(V(r, "rev")), Clean.Key(V(r, "item")), Clean.Key(V(r, "desc")), Clean.NameKey(V(r, "activity")),
                r.Numbers.GetValueOrDefault("qty").ToString(CultureInfo.InvariantCulture), r.Numbers.GetValueOrDefault("rate").ToString(CultureInfo.InvariantCulture));
            if (!wos.TryGetValue(Clean.Key(V(r, "wo")), out var wo))
            {
                r.Status = RowStatus.Invalid; r.Note = $"Work order '{V(r, "wo")}' is not in the work order list";
                continue;
            }
            if (bills.Contains((wo.Id, Clean.Key(V(r, "billno")))))
            {
                r.Status = RowStatus.Exists; r.Note = $"Bill {V(r, "billno")} of this work order is already saved";
                continue;
            }
            var notes = new List<string>();
            if (V(r, "activity").Length > 0 && !acts.Contains(Clean.NameKey(V(r, "activity")))) notes.Add($"activity '{V(r, "activity")}' is not in the list, left blank");
            if (V(r, "drawing").Length > 0)
            {
                var match = FindBom(bom, r);
                if (match.Count != 1)
                {
                    r.Status = RowStatus.Invalid;
                    r.Note = $"Drawing {BomItem.FormatDrawing(V(r, "drawing"), V(r, "sheet"), V(r, "rev"))} {(match.Count == 0 ? "is not in the BOM" : "matches more than one BOM row")}";
                    continue;
                }
                notes.Insert(0, "BOM line: " + match[0].Label);
            }
            else notes.Insert(0, "Free line");
            r.Note = string.Join("; ", notes);
        }
        MarkRepeats(p, r => r.Key, "bill line");

        // A bill is only added whole: one bad line holds back the whole bill.
        foreach (var bad in p.Rows.Where(r => r.Status == RowStatus.Invalid && r.Values.ContainsKey("wo") && r.Values.ContainsKey("billno")).ToList())
            foreach (var r in p.Rows.Where(x => x.Status == RowStatus.New && Clean.Key(V(x, "wo")) == Clean.Key(V(bad, "wo")) && Clean.Key(V(x, "billno")) == Clean.Key(V(bad, "billno"))))
            {
                r.Status = RowStatus.Invalid;
                r.Note = $"Bill {V(r, "billno")} has a line with a problem (row {bad.SourceRow})";
            }
    }

    static List<BomItem> FindBom(List<BomItem> bom, PreviewRow r)
    {
        string sys = V(r, "system"), drawing = V(r, "drawing"), sheet = V(r, "sheet"), rev = V(r, "rev"), item = V(r, "item");
        return bom.Where(i => Clean.Key(i.DrawingNo) == Clean.Key(drawing)
            && (sys.Length == 0 || Clean.Key(i.System) == Clean.Key(sys))
            && Clean.Key(i.Sheet) == Clean.Key(sheet)
            && (rev.Length == 0 || Clean.Key(i.Rev) == Clean.Key(rev))
            && Clean.Key(i.Item) == Clean.Key(item)).ToList();
    }

    static Dictionary<string, string> CustomValues(PreviewRow r) =>
        r.Values.Where(kv => kv.Key.StartsWith('+')).ToDictionary(kv => kv.Key[1..], kv => kv.Value);

    /// <summary>
    /// Adds the ticked rows. New fields chosen in the column list are created first.
    /// Rows already saved are updated only when <see cref="ImportOptions.Existing"/> is Update.
    /// </summary>
    public static ImportResult Apply(BillingRepository repo, ImportPreview p, ImportOptions o)
    {
        var result = new ImportResult();
        var entity = ImportTargets.Entity(p.Target);
        foreach (var c in p.Columns.Where(c => c.CustomField != null))
            repo.AddCustomField(entity, c.CustomField!);

        var rows = p.Rows.Where(r => r.Include && r.Status is RowStatus.New or RowStatus.Exists or RowStatus.Repeated).ToList();
        result.Skipped = p.Rows.Count(r => r.Status != RowStatus.Total) - rows.Count;
        switch (p.Target)
        {
            case ImportTarget.Contractors: ApplyContractors(repo, rows, o, result); break;
            case ImportTarget.WorkOrders: ApplyWorkOrders(repo, rows, o, result); break;
            case ImportTarget.Bom: ApplyBom(repo, rows, o, result); break;
            case ImportTarget.BillLines: ApplyBills(repo, rows, result); break;
        }
        return result;
    }

    /// <summary>Takes the uploaded value when there is one; a blank cell keeps what is saved.</summary>
    static void Set(ref string target, PreviewRow r, string key, bool update)
    {
        _ = update;
        if (r.Values.TryGetValue(key, out var v) && v.Length > 0) target = v;
    }

    static void ApplyContractors(BillingRepository repo, List<PreviewRow> rows, ImportOptions o, ImportResult result)
    {
        var all = repo.Contractors();
        foreach (var r in rows)
        {
            var existing = all.FirstOrDefault(k => Clean.NameKey(k.Name) == Clean.NameKey(V(r, "name")));
            if (existing != null && o.Existing == ExistingAction.Skip) { result.Skipped++; continue; }
            var k = existing ?? new Contractor();
            var name = k.Name; var code = k.Code; var gstin = k.Gstin; var pan = k.Pan; var phone = k.Phone; var address = k.Address; var remarks = k.Remarks;
            if (existing == null) Set(ref name, r, "name", false); // the saved name is kept on update
            Set(ref code, r, "code", existing != null); Set(ref gstin, r, "gstin", existing != null);
            Set(ref pan, r, "pan", existing != null); Set(ref phone, r, "phone", existing != null);
            Set(ref address, r, "address", existing != null); Set(ref remarks, r, "remarks", existing != null);
            k.Name = name; k.Code = code.ToUpperInvariant(); k.Gstin = gstin.ToUpperInvariant(); k.Pan = pan.ToUpperInvariant();
            k.Phone = phone; k.Address = address; k.Remarks = remarks;
            k.Extra = Extras.Merge(k.Extra, CustomValues(r));
            try
            {
                repo.SaveContractor(k);
                if (existing == null) { result.Added++; all.Add(k); } else result.Updated++;
            }
            catch (BillingException ex) { result.Skipped++; result.Messages.Add($"Row {r.SourceRow}: {ex.Message}"); }
        }
    }

    static void ApplyWorkOrders(BillingRepository repo, List<PreviewRow> rows, ImportOptions o, ImportResult result)
    {
        var all = repo.WorkOrders();
        var contractors = repo.Contractors();
        foreach (var r in rows)
        {
            var existing = all.FirstOrDefault(w => Clean.Key(w.WoNo) == Clean.Key(V(r, "wo")));
            if (existing != null && o.Existing == ExistingAction.Skip) { result.Skipped++; continue; }
            var k = contractors.FirstOrDefault(c => Clean.NameKey(c.Name) == Clean.NameKey(V(r, "contractor")));
            if (k == null)
            {
                if (!o.CreateMissingContractors) { result.Skipped++; result.Messages.Add($"Row {r.SourceRow}: contractor '{V(r, "contractor")}' is not in the list."); continue; }
                k = new Contractor { Name = V(r, "contractor") };
                repo.SaveContractor(k);
                contractors.Add(k);
                result.Messages.Add($"Contractor added: {k.Name}");
            }
            var w = existing ?? new WorkOrder { Status = "Open" };
            w.ContractorId = k.Id;
            var wo = w.WoNo; var service = w.Service; var status = w.Status; var remarks = w.Remarks;
            Set(ref wo, r, "wo", existing != null); Set(ref service, r, "service", existing != null);
            Set(ref status, r, "status", existing != null); Set(ref remarks, r, "remarks", existing != null);
            w.WoNo = wo; w.Service = service; w.Status = status.Length > 0 ? status : "Open"; w.Remarks = remarks;
            if (r.Numbers.TryGetValue("value", out var v)) w.Value = v;
            if (r.Dates.TryGetValue("date", out var d)) w.WoDate = d;
            w.Extra = Extras.Merge(w.Extra, CustomValues(r));
            try
            {
                repo.SaveWorkOrder(w);
                if (existing == null) { result.Added++; all.Add(w); } else result.Updated++;
            }
            catch (BillingException ex) { result.Skipped++; result.Messages.Add($"Row {r.SourceRow}: {ex.Message}"); }
        }
    }

    static void ApplyBom(BillingRepository repo, List<PreviewRow> rows, ImportOptions o, ImportResult result)
    {
        var existingKeys = repo.BomItems().Select(b => BomKey(b.System, b.DrawingNo, b.Sheet, b.Rev, b.Item)).ToHashSet();
        var items = new List<BomItem>();
        // Rows with the same key are parts of one drawing or item: their weights are added together.
        foreach (var g in rows.GroupBy(r => r.Key))
        {
            var first = g.First();
            if (existingKeys.Contains(g.Key) && o.Existing == ExistingAction.Skip) { result.Skipped += g.Count(); continue; }
            var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in g) foreach (var (k, v) in CustomValues(r)) extra.TryAdd(k, v);
            items.Add(new BomItem
            {
                System = V(first, "system"), DrawingNo = V(first, "drawing"), Sheet = V(first, "sheet"), Rev = V(first, "rev"), Item = V(first, "item"),
                Tonnes = BillMath.Round3(g.Sum(Tonnes)), Extra = Extras.Write(extra),
            });
        }
        var (added, updated) = repo.UpsertBom(items);
        result.Added += added;
        result.Updated += updated;
    }

    static void ApplyBills(BillingRepository repo, List<PreviewRow> rows, ImportResult result)
    {
        var wos = repo.WorkOrders().GroupBy(w => Clean.Key(w.WoNo)).ToDictionary(g => g.Key, g => g.First());
        var acts = repo.Activities().GroupBy(a => Clean.NameKey(a.Name)).ToDictionary(g => g.Key, g => g.First());
        var bom = repo.BomItems();
        var defaultGst = repo.GetSettingDecimal("DefaultGstPct", 18);
        foreach (var g in rows.Where(r => r.Status != RowStatus.Exists).GroupBy(r => (Clean.Key(V(r, "wo")), Clean.Key(V(r, "billno")))))
        {
            var first = g.First();
            if (!wos.TryGetValue(g.Key.Item1, out var wo)) { result.Skipped += g.Count(); continue; }
            var status = BillStatus.All.FirstOrDefault(s => s.Equals(V(first, "status"), StringComparison.OrdinalIgnoreCase)) ?? BillStatus.Draft;
            var bill = new Bill
            {
                WorkOrderId = wo.Id, BillNo = V(first, "billno"), BillType = V(first, "type") is { Length: > 0 } t ? t : "RA bill",
                BillDate = first.Dates.TryGetValue("date", out var bd) ? bd : DateTime.Today, Status = status,
                GstPct = first.Numbers.TryGetValue("gst", out var gst) ? gst : defaultGst,
                RetentionPct = first.Numbers.GetValueOrDefault("ret"), TdsPct = first.Numbers.GetValueOrDefault("tds"),
                OtherDeduction = first.Numbers.GetValueOrDefault("other"), PaidAmount = first.Numbers.GetValueOrDefault("paid"),
                PaidOn = first.Dates.TryGetValue("paidon", out var po) ? po : null,
            };
            if (!first.Dates.ContainsKey("date")) result.Messages.Add($"Row {first.SourceRow}: no bill date, today's date used for {bill.BillNo}.");
            var lines = new List<BillLine>();
            foreach (var r in g)
            {
                var l = new BillLine
                {
                    Qty = r.Numbers.GetValueOrDefault("qty"), Rate = r.Numbers.GetValueOrDefault("rate"), Unit = V(r, "unit"),
                    Remarks = V(r, "remarks"), Description = V(r, "desc"), Extra = Extras.Write(CustomValues(r)),
                    ActivityId = acts.TryGetValue(Clean.NameKey(V(r, "activity")), out var a) ? a.Id : null,
                };
                if (V(r, "drawing").Length > 0)
                {
                    var m = FindBom(bom, r);
                    if (m.Count == 1) { l.BomItemId = m[0].Id; if (l.Unit.Length == 0) l.Unit = "t"; }
                }
                else if (l.Description.Length == 0) l.Description = "(no description)";
                lines.Add(l);
            }
            try { repo.SaveBill(bill, lines); result.Added++; }
            catch (BillingException ex) { result.Skipped++; result.Messages.Add($"Bill {bill.BillNo}: {ex.Message}"); }
        }
        result.Skipped += rows.Count(r => r.Status == RowStatus.Exists);
    }

    /// <summary>Saves the check as a workbook, each row coloured by what was found.</summary>
    public static void WritePreview(ImportPreview p, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Import check");
        var cols = p.ValueColumns().ToList();
        var heads = new[] { "Excel row", "Result", "Group", "Import", "Note" }.Concat(cols.Select(c => c.Title)).ToList();
        for (var i = 0; i < heads.Count; i++) { ws.Cell(1, i + 1).Value = heads[i]; ws.Cell(1, i + 1).Style.Font.Bold = true; }
        var r = 2;
        foreach (var row in p.Rows)
        {
            ws.Cell(r, 1).Value = row.SourceRow;
            ws.Cell(r, 2).Value = StatusText(row.Status);
            if (row.Group.HasValue) ws.Cell(r, 3).Value = "D" + row.Group;
            ws.Cell(r, 4).Value = row.Include ? "Yes" : "No";
            ws.Cell(r, 5).Value = row.Note;
            for (var i = 0; i < cols.Count; i++) ws.Cell(r, 6 + i).Value = row.Values.GetValueOrDefault(cols[i].Key, "");
            ws.Range(r, 1, r, heads.Count).Style.Fill.BackgroundColor = XLColor.FromHtml(StatusColor(row.Status));
            r++;
        }
        var legend = wb.Worksheets.Add("Key");
        var k = 1;
        foreach (var s in Enum.GetValues<RowStatus>())
        {
            legend.Cell(k, 1).Value = StatusText(s);
            legend.Cell(k, 1).Style.Fill.BackgroundColor = XLColor.FromHtml(StatusColor(s));
            legend.Cell(k++, 2).Value = StatusHelp(s, p.Target);
        }
        ws.SheetView.FreezeRows(1);
        ws.Range(1, 1, Math.Max(1, r - 1), heads.Count).SetAutoFilter();
        ws.Columns().AdjustToContents(1, Math.Min(r, 300), 6, 50);
        legend.Columns().AdjustToContents();
        wb.SaveAs(path);
    }

    public static string StatusText(RowStatus s) => s switch
    {
        RowStatus.New => "New",
        RowStatus.Exists => "Already saved",
        RowStatus.Repeated => "Repeated in file",
        RowStatus.Invalid => "Problem",
        _ => "Left out",
    };

    public static string StatusColor(RowStatus s) => s switch
    {
        RowStatus.New => "#E3F4E1",
        RowStatus.Exists => "#FFE9C7",
        RowStatus.Repeated => "#FFF6B3",
        RowStatus.Invalid => "#F9D6D3",
        _ => "#EEEEEE",
    };

    public static string StatusHelp(RowStatus s, ImportTarget t) => s switch
    {
        RowStatus.New => "Not saved yet. Ticked to import.",
        RowStatus.Exists => t == ImportTarget.BillLines
            ? "This bill is already saved. It is kept as it is."
            : $"A record with the {ImportTargets.KeyDescription(t)} is already saved. Skipped, or updated if you choose 'Update'.",
        RowStatus.Repeated => $"Appears more than once in this file ({ImportTargets.KeyDescription(t)}). Rows with the same group number belong together; only the first is ticked.",
        RowStatus.Invalid => "Something is missing or not readable. Fix the Excel file and check again.",
        _ => "Total or empty row. Not imported.",
    };

    /// <summary>Imports every sheet that fits the target, with automatic column matching (unmatched columns are left out).</summary>
    public static ImportResult ImportAuto(BillingRepository repo, string path, ImportTarget target, ImportOptions options)
    {
        var total = new ImportResult();
        var found = false;
        foreach (var sheet in RawSheet.ReadWorkbook(path))
        {
            var hr = sheet.SuggestedHeaderRow;
            if (sheet.Rows.Count == 0 || SuggestTarget(sheet, hr) != target) continue;
            found = true;
            var map = AutoMap(sheet, hr, target, repo.CustomFields(ImportTargets.Entity(target)));
            var preview = Analyze(repo, sheet, hr, target, map);
            if (options.Existing == ExistingAction.Update && target != ImportTarget.BillLines)
                foreach (var r in preview.Rows.Where(r => r.Status == RowStatus.Exists)) r.Include = true;
            var r1 = Apply(repo, preview, options);
            total.Added += r1.Added; total.Updated += r1.Updated; total.Skipped += r1.Skipped;
            foreach (var bad in preview.Rows.Where(r => r.Status == RowStatus.Invalid)) total.Messages.Add($"Sheet '{sheet.Name}' row {bad.SourceRow}: {bad.Note}");
            total.Messages.AddRange(r1.Messages.Select(m => $"Sheet '{sheet.Name}': {m}"));
        }
        if (!found) total.Messages.Add($"No sheet looks like {ImportTargets.Label(target)} data. Use 'Import from Excel (check and match columns)' to choose the columns yourself.");
        return total;
    }
}
