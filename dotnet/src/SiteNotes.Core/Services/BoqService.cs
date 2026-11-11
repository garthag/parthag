using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core;

public record BoqImportResult(List<DrawingItem> Items, List<string> Warnings);

/// <summary>
/// BOQ for a drawing: import from Excel (.xlsx) or CSV, export a template, and create planned activities from items.
/// Columns are found by their heading (Item / Description / Type / Unit / Qty / Rate), in any order.
/// </summary>
public class BoqService(LocalDatabase db)
{
    private static readonly (string Field, string[] Names)[] Columns =
    [
        ("Item", ["item no", "item", "boq no", "boq item", "s no", "sl no", "code", "tag"]),
        ("Description", ["description", "desc", "particulars", "item description", "name"]),
        ("Type", ["type", "kind", "category"]),
        ("Unit", ["unit", "uom", "units"]),
        ("Qty", ["qty", "quantity", "drawing qty", "boq qty", "total qty"]),
        ("Rate", ["rate", "unit rate", "boq rate", "price", "unit price"]),
    ];

    public static BoqImportResult Import(Stream stream, string fileName)
    {
        var rows = Path.GetExtension(fileName).ToLowerInvariant() is ".csv" or ".txt" ? ReadCsv(stream) : ReadExcel(stream);
        var warnings = new List<string>();
        // header = first row (within the first 15) that has a description and a quantity column
        var headerIndex = -1;
        Dictionary<string, int> map = [];
        for (var r = 0; r < Math.Min(15, rows.Count) && headerIndex < 0; r++)
        {
            var m = MapHeader(rows[r]);
            if (m.ContainsKey("Description") && m.ContainsKey("Qty")) { headerIndex = r; map = m; }
        }
        if (headerIndex < 0)
            return new BoqImportResult([], ["No heading row found. The sheet needs at least a 'Description' and a 'Qty' column (see the BOQ template)."]);

        var items = new List<DrawingItem>();
        for (var r = headerIndex + 1; r < rows.Count; r++)
        {
            string Cell(string f) => map.TryGetValue(f, out var c) && c < rows[r].Count ? rows[r][c].Trim() : "";
            var desc = Cell("Description");
            var qtyText = Cell("Qty");
            if (desc == "" && qtyText == "") continue;
            if (!TryNumber(qtyText, out var qty))
            {
                warnings.Add($"Row {r + 1}: quantity “{qtyText}” is not a number – skipped ({desc}).");
                continue;
            }
            TryNumber(Cell("Rate"), out var rate);
            var type = Cell("Type").ToLowerInvariant();
            items.Add(new DrawingItem
            {
                ItemNo = Cell("Item"), Description = desc, Unit = Cell("Unit") is { Length: > 0 } u ? u : "Nos", Qty = qty, Rate = (decimal)rate,
                Kind = type.Contains("equip") ? DrawingItemKind.Equipment : type.Contains("part") ? DrawingItemKind.Part : DrawingItemKind.BoqItem,
                SortOrder = items.Count,
            });
        }
        if (items.Count == 0) warnings.Add("No item rows found under the heading.");
        return new BoqImportResult(items, warnings);
    }

    private static Dictionary<string, int> MapHeader(List<string> row)
    {
        var map = new Dictionary<string, int>();
        for (var c = 0; c < row.Count; c++)
        {
            var h = new string(row[c].ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim();
            if (h == "") continue;
            foreach (var (field, names) in Columns)
                if (!map.ContainsKey(field) && names.Any(n => h == n || (n.Length > 3 && h.StartsWith(n))))
                {
                    map[field] = c;
                    break;
                }
        }
        return map;
    }

    internal static bool TryNumber(string s, out double v) =>
        double.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
        double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);

    internal static List<List<string>> ReadExcel(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();
        if (used == null) return [];
        return used.RowsUsed().Select(r => Enumerable.Range(1, used.ColumnCount())
            .Select(c => CellText(r.Cell(c))).ToList()).ToList();
    }

    private static string CellText(IXLCell cell)
    {
        var v = cell.CachedValue;
        if (v.IsDateTime) return v.GetDateTime().ToString(v.GetDateTime().TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (v.IsNumber) return v.GetNumber().ToString("R", CultureInfo.InvariantCulture);
        if (v.IsBoolean) return v.GetBoolean() ? "Yes" : "No";
        return cell.GetFormattedString();
    }

    internal static List<List<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var rows = new List<List<string>>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var sep = line.Count(c => c == ';') > line.Count(c => c == ',') ? ';' : ',';
            var cells = new List<string>();
            var cur = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else quoted = !quoted; }
                else if (ch == sep && !quoted) { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            cells.Add(cur.ToString());
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>An Excel template with the expected columns and two example rows.</summary>
    public static void Template(Stream output)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("BOQ");
        string[] headers = ["Item no", "Description", "Type", "Unit", "Qty", "Rate"];
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        ws.Range(1, 1, 1, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DCE8F5"));
        object[][] sample = [["1457", "Main steel – columns & beams", "BOQ", "MT", 100, 85000], ["EQ-01", "Pump set with motor", "Equipment", "Nos", 4, 350000]];
        for (var r = 0; r < sample.Length; r++)
            for (var c = 0; c < sample[r].Length; c++)
                ws.Cell(r + 2, c + 1).Value = sample[r][c] is double or int ? Convert.ToDouble(sample[r][c]) : sample[r][c].ToString();
        ws.Cell(5, 1).Value = "Type: BOQ, Equipment or Part. Rate = BOQ / contract rate per unit (optional).";
        ws.Columns().AdjustToContents();
        wb.SaveAs(output);
    }

    /// <summary>Adds imported items to a drawing (items with the same item no. are updated, not duplicated).</summary>
    public async Task<(int Added, int Updated)> SaveImportAsync(Drawing d, IEnumerable<DrawingItem> imported)
    {
        var existing = await db.ListForProjectAsync<DrawingItem>(d.ProjectId, i => i.DrawingId == d.Id);
        int added = 0, updated = 0;
        var order = existing.Count == 0 ? 0 : existing.Max(i => i.SortOrder) + 1;
        var save = new List<DrawingItem>();
        foreach (var it in imported)
        {
            var same = it.ItemNo != "" ? existing.FirstOrDefault(e => e.ItemNo.Equals(it.ItemNo, StringComparison.OrdinalIgnoreCase)) : null;
            if (same != null)
            {
                same.Description = it.Description; same.Unit = it.Unit; same.Qty = it.Qty; same.Kind = it.Kind;
                if (it.Rate > 0) same.Rate = it.Rate;
                save.Add(same);
                updated++;
            }
            else
            {
                it.ProjectId = d.ProjectId;
                it.DrawingId = d.Id;
                it.SortOrder = order++;
                save.Add(it);
                added++;
            }
        }
        await db.SaveAllAsync(save);
        return (added, updated);
    }

    /// <summary>
    /// Creates a planned activity for every item of the drawing that has none (code = drawing no. / item no.,
    /// quantity and unit from the item, WBS from the drawing, agency when the item has exactly one).
    /// Progress then flows: daily drawing work → item → activity.
    /// </summary>
    public async Task<int> CreateActivitiesAsync(Drawing d, DateTime? start = null, DateTime? finish = null)
    {
        var items = (await db.ListForProjectAsync<DrawingItem>(d.ProjectId, i => i.DrawingId == d.Id)).Where(i => i.ActivityId == "").ToList();
        if (items.Count == 0) return 0;
        var allocs = (await db.ListForProjectAsync<DrawingAllocation>(d.ProjectId, a => a.DrawingId == d.Id)).ToLookup(a => a.DrawingItemId);
        var acts = new List<Activity>();
        foreach (var it in items)
        {
            var agencies = allocs[it.Id].Select(a => a.SubcontractorId).Distinct().ToList();
            var a = new Activity
            {
                ProjectId = d.ProjectId, Code = $"{d.Number}/{(it.ItemNo == "" ? (it.SortOrder + 1).ToString() : it.ItemNo)}", Name = it.Description,
                Unit = it.Unit, PlannedQty = it.Qty, WbsId = d.WbsId, SubcontractorId = agencies.Count == 1 ? agencies[0] : "",
                PlannedStart = start, PlannedFinish = finish, Remarks = $"From drawing {d.Number} {d.Revision}",
            };
            acts.Add(a);
            it.ActivityId = a.Id;
        }
        await db.SaveAllAsync(acts);
        await db.SaveAllAsync(items);
        return acts.Count;
    }
}
