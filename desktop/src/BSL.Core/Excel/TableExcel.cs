using System.Data;
using BSL.Core.Metadata;
using ClosedXML.Excel;

namespace BSL.Core.Excel;

/// <summary>Generic Excel export / import for any grid (DataTable + field metadata).</summary>
public static class TableExcel
{
    /// <summary>Exports rows to a formatted sheet: captions as headers, number/date formats, totals, filter, frozen header.</summary>
    public static void Export(string path, string title, DataTable table, IList<FieldDef> fields, IEnumerable<DataRow>? rows = null,
                              string? subtitle = null, bool includeIdColumn = true)
    {
        using var wb = new XLWorkbook();
        AddSheet(wb, title, table, fields, rows, subtitle, includeIdColumn);
        wb.SaveAs(path);
    }

    public static IXLWorksheet AddSheet(XLWorkbook wb, string title, DataTable table, IList<FieldDef> fields, IEnumerable<DataRow>? rows = null,
                                        string? subtitle = null, bool includeIdColumn = true)
    {
        var ws = wb.AddWorksheet(ExcelUtil.SafeSheetName(title, new HashSet<string>(wb.Worksheets.Select(w => w.Name.ToUpperInvariant()))));
        var cols = new List<(string Name, string Caption, FieldDef? F)>();
        if (includeIdColumn && table.Columns.Contains("Id")) cols.Add(("Id", "Id", null));
        foreach (var f in fields.Where(f => table.Columns.Contains(f.Name) && !f.HiddenInGrid))
            cols.Add((f.Name, f.Caption, f));
        var data = (rows ?? table.Rows.Cast<DataRow>()).Where(r => r.RowState != DataRowState.Deleted).ToList();

        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).Value = subtitle ?? $"Exported {DateTime.Now:dd-MM-yyyy HH:mm}  •  {data.Count} rows";
        ws.Cell(2, 1).Style.Font.SetItalic();
        const int hr = 4;
        for (int c = 0; c < cols.Count; c++) ws.Cell(hr, c + 1).Value = cols[c].Caption;

        var lookups = new Dictionary<string, Dictionary<int, string>>();
        for (int i = 0; i < data.Count; i++)
            for (int c = 0; c < cols.Count; c++)
            {
                var v = data[i][cols[c].Name];
                var cell = ws.Cell(hr + 1 + i, c + 1);
                if (v is DBNull or null) continue;
                if (cols[c].F?.Kind == FieldKind.Lookup && LookupText != null)
                {
                    if (!lookups.TryGetValue(cols[c].Name, out var map)) lookups[cols[c].Name] = map = LookupText(cols[c].F!.Lookup!);
                    cell.Value = map.TryGetValue(Convert.ToInt32(v), out var t) ? t : v.ToString();
                    continue;
                }
                cell.Value = v switch
                {
                    decimal d => (double)d, double d => d, int n => n, long l => l, short s => s,
                    DateTime dt => dt, bool b => b ? "Yes" : "No", _ => v.ToString()
                };
            }
        int last = hr + data.Count;
        for (int c = 0; c < cols.Count; c++)
        {
            var f = cols[c].F;
            var rng = ws.Range(hr + 1, c + 1, Math.Max(hr + 1, last), c + 1);
            if (f?.Kind == FieldKind.Money) rng.Style.NumberFormat.Format = "#,##,##0.00";
            else if (f?.Kind == FieldKind.Decimal) rng.Style.NumberFormat.Format = "#,##0.000";
            else if (f?.Kind == FieldKind.Date) rng.Style.DateFormat.Format = "dd-mm-yyyy";
            if (f != null && f.IsNumeric && f.Kind != FieldKind.Int && data.Count > 0)
            {
                ws.Cell(last + 1, c + 1).FormulaA1 = $"SUBTOTAL(9,{ws.Cell(hr + 1, c + 1).Address}:{ws.Cell(last, c + 1).Address})";
                ws.Cell(last + 1, c + 1).Style.NumberFormat.Format = rng.Style.NumberFormat.Format;
            }
            ws.Column(c + 1).Width = Math.Clamp((f?.Width ?? 60) / 7.0, 6, 60);
        }
        if (data.Count > 0)
        {
            ws.Cell(last + 1, 1).Value = "TOTAL";
            ws.Range(last + 1, 1, last + 1, cols.Count).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DDEBF7"));
            ws.Range(hr, 1, last, cols.Count).SetAutoFilter();
        }
        var h = ws.Range(hr, 1, hr, Math.Max(1, cols.Count));
        h.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E78")).Alignment.SetWrapText();
        ws.SheetView.FreezeRows(hr);
        return ws;
    }

    /// <summary>Set by the app: resolves lookup ids to display text for export.</summary>
    public static Func<LookupDef, Dictionary<int, string>>? LookupText { get; set; }

    /// <summary>Blank template with the entity's columns (for filling in Excel and importing back).</summary>
    public static void Template(string path, EntityDef e, IList<FieldDef> fields)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(ExcelUtil.SafeSheetName(e.Name));
        var writable = fields.Where(f => !f.ReadOnly).ToList();
        ws.Cell(1, 1).Value = "Id";
        for (int c = 0; c < writable.Count; c++)
        {
            ws.Cell(1, c + 2).Value = writable[c].Caption;
            var note = writable[c].Kind switch
            {
                FieldKind.Lookup => "Type the code (e.g. GRE / BM05 / drawing no.) or the text shown in the app",
                FieldKind.Date => "Date (dd-mm-yyyy)",
                FieldKind.Bool => "Yes / No",
                FieldKind.Choice => "Pick or type a value",
                _ => writable[c].Required ? "Required" : null
            };
            if (note != null) ws.Cell(1, c + 2).CreateComment().AddText(note);
            ws.Column(c + 2).Width = Math.Clamp(writable[c].Width / 7.0, 8, 50);
        }
        ws.Cell(1, 1).CreateComment().AddText("Leave blank for new records. Keep the Id to update an existing record.");
        ws.Range(1, 1, 1, writable.Count + 1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DDEBF7"));
        ws.SheetView.FreezeRows(1);
        wb.SaveAs(path);
    }

    /// <summary>Reads the first sheet (or the named one). Header row = first row with ≥2 text cells in rows 1..10.</summary>
    public static DataTable Read(string path, string? sheet = null)
    {
        using var wb = new XLWorkbook(path);
        var ws = sheet != null ? wb.Worksheet(sheet) : wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var dt = new DataTable(ws.Name);
        if (lastRow == 0) return dt;
        int hr = 1;
        for (int r = 1; r <= Math.Min(10, lastRow); r++)
        {
            var n = Enumerable.Range(1, lastCol).Count(c => ExcelUtil.Val(ws.Cell(r, c)) is string);
            if (n >= Math.Min(2, lastCol) && n >= lastCol / 2) { hr = r; break; }
        }
        var colIdx = new List<int>();
        for (int c = 1; c <= lastCol; c++)
        {
            var h = ExcelUtil.Text(ws.Cell(hr, c));
            if (h == null) continue;
            var name = h; int k = 2;
            while (dt.Columns.Contains(name)) name = h + "_" + k++;
            dt.Columns.Add(name, typeof(object));
            colIdx.Add(c);
        }
        for (int r = hr + 1; r <= lastRow; r++)
        {
            var vals = colIdx.Select(c => ExcelUtil.Val(ws.Cell(r, c))).ToArray();
            if (vals.All(v => v == null)) continue;
            if (vals.FirstOrDefault() is string s && s.Equals("TOTAL", StringComparison.OrdinalIgnoreCase)) continue;
            dt.Rows.Add(vals.Select(v => v ?? DBNull.Value).ToArray());
        }
        return dt;
    }

    public static List<string> SheetNames(string path)
    {
        using var wb = new XLWorkbook(path);
        return wb.Worksheets.Select(w => w.Name).ToList();
    }
}
