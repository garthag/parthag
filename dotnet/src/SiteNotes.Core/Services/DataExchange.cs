using System.Globalization;
using System.Reflection;
using ClosedXML.Excel;
using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core;

/// <summary>One column of an edit-in-Excel sheet: how to read it from a record and how to write a cell back.</summary>
/// <param name="Set">Applies the cell text; returns a warning (value not understood – the old value is kept) or null.</param>
public record ExchangeColumn<T>(string Header, Func<T, object?> Get, Func<T, string, string?> Set, string[]? Options = null, bool ReadOnly = false);

/// <summary>Describes one module (subcontractors, activities, hindrances…) as an Excel sheet that can be exported, edited and imported back.</summary>
public class ExchangeSheet<T> where T : SyncEntity, new()
{
    public required string Name { get; init; }
    public required List<ExchangeColumn<T>> Columns { get; init; }
    /// <summary>Natural key (code, number, name) used to recognise a row when the ID column is empty or was removed.</summary>
    public Func<T, string> Key { get; init; } = _ => "";
    public Func<T> Create { get; init; } = () => new T();
    /// <summary>Last check of a row; gets the raw cells by heading. Returns an error to skip the row, or null.</summary>
    public Func<T, IReadOnlyDictionary<string, string>, string?>? Validate { get; init; }
    /// <summary>Runs before saving (serial numbers, names kept next to ids…).</summary>
    public Func<ExchangeResult<T>, Task>? BeforeSave { get; init; }
}

public record ExchangeResult<T>(List<T> Added, List<T> Updated, int Unchanged, List<string> Warnings)
{
    public string Summary => $"{Added.Count} new, {Updated.Count} changed, {Unchanged} unchanged" + (Warnings.Count > 0 ? $", {Warnings.Count} warning(s)" : "");
}

/// <summary>
/// Excel round trip for every module: export the rows shown (after search, filter and sort), edit or add rows in Excel,
/// import the file back. Rows are matched by the ID column, else by their natural key; nothing is ever deleted by an import.
/// </summary>
public static class DataExchange
{
    public const string IdHeader = "ID (do not change)";

    public static void Export<T>(Stream output, ExchangeSheet<T> sheet, IEnumerable<T> rows, string note = "") where T : SyncEntity, new()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheet.Name.Length > 31 ? sheet.Name[..31] : sheet.Name);
        var cols = sheet.Columns;
        for (var c = 0; c < cols.Count; c++) ws.Cell(1, c + 1).Value = cols[c].Header;
        ws.Cell(1, cols.Count + 1).Value = IdHeader;
        var head = ws.Range(1, 1, 1, cols.Count + 1);
        head.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E79"));
        var r = 1;
        foreach (var row in rows)
        {
            r++;
            for (var c = 0; c < cols.Count; c++) Put(ws.Cell(r, c + 1), cols[c].Get(row));
            ws.Cell(r, cols.Count + 1).Value = row.Id;
        }
        var last = Math.Max(r, 2);
        for (var c = 0; c < cols.Count; c++)
        {
            if (cols[c].ReadOnly) { ws.Cell(1, c + 1).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#7F7F7F")); ws.Range(2, c + 1, last, c + 1).Style.Font.SetFontColor(XLColor.FromHtml("#7F7F7F")); }
            if (cols[c].Options is { Length: > 0 } o && string.Join(",", o).Length < 250 && !o.Any(x => x.Contains(',')))
                ws.Range(2, c + 1, last + 500, c + 1).CreateDataValidation().List("\"" + string.Join(",", o) + "\"", true);
        }
        ws.Range(2, cols.Count + 1, last, cols.Count + 1).Style.Font.SetFontColor(XLColor.FromHtml("#A6A6A6"));
        ws.Range(1, 1, last, cols.Count + 1).SetAutoFilter();
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, cols.Count + 1).AdjustToContents(1, Math.Min(last, 300), 8, 50);
        ws.Column(cols.Count + 1).Width = 12;

        var help = wb.Worksheets.Add("How to use");
        string[] lines =
        [
            $"{sheet.Name} – edit in Excel and import back into SiteNotes",
            note,
            "• Change any white cell and save the file, then use Import in the same module.",
            "• To add records, type new rows at the bottom and leave the last column (ID) empty.",
            "• Sort and filter freely – rows are recognised by the ID column, not by their position.",
            "• Do not rename the headings in row 1. Columns you delete are simply left unchanged.",
            "• Grey columns are calculated by the app and are ignored on import.",
            "• Deleting a row in Excel does not delete the record – delete records in the app.",
            "• Dates: use real Excel dates or dd-MM-yyyy. Yes/No columns: Yes or No.",
        ];
        for (var i = 0; i < lines.Length; i++) help.Cell(i + 1, 1).Value = lines[i];
        help.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(13);
        wb.SaveAs(output);
    }

    private static void Put(IXLCell cell, object? v)
    {
        switch (v)
        {
            case null: break;
            case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = d.TimeOfDay == TimeSpan.Zero ? "dd-mmm-yyyy" : "dd-mmm-yyyy hh:mm"; break;
            case double x: cell.Value = x; break;
            case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00"; break;
            case int i: cell.Value = i; break;
            case bool b: cell.Value = b ? "Yes" : "No"; break;
            default: cell.Value = v.ToString(); break;
        }
    }

    /// <summary>Reads an edited sheet (.xlsx or .csv) and works out what is new and what changed. Nothing is saved here.</summary>
    public static ExchangeResult<T> Import<T>(Stream stream, string fileName, ExchangeSheet<T> sheet, IEnumerable<T> existing) where T : SyncEntity, new()
    {
        var rows = Path.GetExtension(fileName).ToLowerInvariant() is ".csv" or ".txt" ? BoqService.ReadCsv(stream) : BoqService.ReadExcel(stream);
        var result = new ExchangeResult<T>([], [], 0, []);
        var cols = sheet.Columns.Where(c => !c.ReadOnly).ToList();
        static string Norm(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        var headerIndex = -1;
        var map = new Dictionary<int, ExchangeColumn<T>>();
        var idCol = -1;
        for (var r = 0; r < Math.Min(15, rows.Count) && headerIndex < 0; r++)
        {
            var m = new Dictionary<int, ExchangeColumn<T>>();
            var id = -1;
            for (var c = 0; c < rows[r].Count; c++)
            {
                var h = Norm(rows[r][c]);
                if (h == "") continue;
                if (h == Norm(IdHeader) || h == "id") { id = c; continue; }
                var col = cols.FirstOrDefault(x => Norm(x.Header) == h && !m.ContainsValue(x));
                if (col != null) m[c] = col;
            }
            if (m.Count >= Math.Min(2, cols.Count)) { headerIndex = r; map = m; idCol = id; }
        }
        if (headerIndex < 0)
        {
            result.Warnings.Add($"No heading row found. Row 1 must have the headings of the {sheet.Name} sheet ({string.Join(", ", cols.Take(4).Select(c => c.Header))}…). Export the module first to get the right layout.");
            return result;
        }

        var all = existing.ToList();
        var byId = all.ToDictionary(e => e.Id);
        var byKey = all.Select(e => (Key: sheet.Key(e).Trim().ToLowerInvariant(), e)).Where(x => x.Key != "").GroupBy(x => x.Key).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().e);
        var seen = new HashSet<string>();
        // cells are applied in the sheet's own column order (a later column may depend on an earlier one), whatever the order in the file
        var ordered = map.OrderBy(kv => cols.IndexOf(kv.Value)).ToList();

        for (var r = headerIndex + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            string Cell(int c) => c >= 0 && c < row.Count ? row[c].Trim() : "";
            if (map.Keys.All(c => Cell(c) == "")) continue;
            var raw = map.ToDictionary(kv => kv.Value.Header, kv => Cell(kv.Key));

            var id = Cell(idCol);
            T? target = id != "" && byId.TryGetValue(id, out var found) ? found : null;
            var isNew = false;
            if (target == null)
            {
                // recognise by natural key: build a scratch record to compute the key of this row
                var probe = sheet.Create();
                foreach (var (c, col) in ordered) col.Set(probe, Cell(c));
                var key = sheet.Key(probe).Trim().ToLowerInvariant();
                if (key != "" && byKey.TryGetValue(key, out var same)) target = same;
                else { target = sheet.Create(); isNew = true; if (key != "") byKey[key] = target; }
            }
            if (!seen.Add(target.Id)) { result.Warnings.Add($"Row {r + 1}: the same record appears twice in the file – the second row was skipped."); continue; }

            var before = isNew ? "" : Snapshot(sheet, target);
            var backup = ordered.Select(kv => Text(kv.Value.Get(target))).ToList();
            foreach (var (c, col) in ordered)
            {
                var warn = col.Set(target, Cell(c));
                if (warn != null) result.Warnings.Add($"Row {r + 1}, {col.Header}: {warn}");
            }
            var error = sheet.Validate?.Invoke(target, raw);
            if (error != null)
            {
                result.Warnings.Add($"Row {r + 1}: {error} – row skipped.");
                if (!isNew) for (var i = 0; i < ordered.Count; i++) ordered[i].Value.Set(target, backup[i]); // put the record back as it was
                continue;
            }
            if (isNew) result.Added.Add(target);
            else if (Snapshot(sheet, target) != before) result.Updated.Add(target);
            else result = result with { Unchanged = result.Unchanged + 1 };
        }
        if (result.Added.Count + result.Updated.Count + result.Unchanged == 0 && result.Warnings.Count == 0) result.Warnings.Add("No rows found under the headings.");
        return result;
    }

    private static string Snapshot<T>(ExchangeSheet<T> sheet, T e) where T : SyncEntity, new() =>
        string.Join("\u001f", sheet.Columns.Where(c => !c.ReadOnly).Select(c => Text(c.Get(e))));

    /// <summary>Cell text of a value in the form the importer reads back.</summary>
    public static string Text(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString(d.TimeOfDay == TimeSpan.Zero ? "yyyy-MM-dd" : "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        double x => x.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        _ => v.ToString() ?? "",
    };

    private static readonly string[] DateFormats =
        ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "dd-MM-yyyy", "dd-MM-yyyy HH:mm", "dd/MM/yyyy", "dd/MM/yyyy HH:mm", "dd-MMM-yyyy", "dd-MMM-yyyy HH:mm", "dd MMM yyyy", "d-M-yyyy", "d/M/yyyy", "dd.MM.yyyy", "dd-MMM-yy", "dd-MM-yy", "dd/MM/yy"];

    public static bool TryDate(string s, out DateTime d) =>
        DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ||
        (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000 && (d = DateTime.FromOADate(serial)) != default) ||
        DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d);

    // ---------------------------------------------------------------- column builders

    /// <summary>A column bound to a property; the conversion follows the property type (text, number, money, date, yes/no, list).</summary>
    public static ExchangeColumn<T> Col<T>(string header, string property, string[]? options = null, bool readOnly = false)
    {
        var p = typeof(T).GetProperty(property, BindingFlags.Public | BindingFlags.Instance) ?? throw new ArgumentException($"{typeof(T).Name}.{property} not found");
        var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
        var nullable = Nullable.GetUnderlyingType(p.PropertyType) != null;
        if (type.IsEnum) options ??= Enum.GetNames(type);
        string? Set(T e, string s)
        {
            if (readOnly || !p.CanWrite) return null;
            if (type == typeof(string)) { p.SetValue(e, s); return null; }
            if (s == "")
            {
                if (nullable) p.SetValue(e, null);
                else if (type == typeof(double)) p.SetValue(e, 0d);
                else if (type == typeof(decimal)) p.SetValue(e, 0m);
                else if (type == typeof(int)) p.SetValue(e, 0);
                return null;
            }
            if (type == typeof(double)) { if (!BoqService.TryNumber(s, out var v)) return $"“{s}” is not a number"; p.SetValue(e, v); }
            else if (type == typeof(decimal))
            {
                if (!decimal.TryParse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && !decimal.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out m)) return $"“{s}” is not a number";
                p.SetValue(e, m);
            }
            else if (type == typeof(int)) { if (!BoqService.TryNumber(s, out var v)) return $"“{s}” is not a number"; p.SetValue(e, (int)Math.Round(v)); }
            else if (type == typeof(bool))
            {
                var t = s.ToLowerInvariant();
                if (t is "yes" or "y" or "true" or "1" or "active") p.SetValue(e, true);
                else if (t is "no" or "n" or "false" or "0" or "inactive") p.SetValue(e, false);
                else return $"“{s}” is not Yes or No";
            }
            else if (type == typeof(DateTime)) { if (!TryDate(s, out var d)) return $"“{s}” is not a date"; p.SetValue(e, d); }
            else if (type.IsEnum)
            {
                var name = Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, s.Replace(" ", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase));
                if (name == null) return $"“{s}” is not one of {string.Join(" / ", Enum.GetNames(type))}";
                p.SetValue(e, Enum.Parse(type, name));
            }
            return null;
        }
        return new ExchangeColumn<T>(header, e => type.IsEnum ? p.GetValue(e)?.ToString() : p.GetValue(e), Set, options, readOnly || !p.CanWrite);
    }

    /// <summary>A column that stores the id of another record but shows (and reads) its name.</summary>
    public static ExchangeColumn<T> Lookup<T>(string header, string idProperty, IReadOnlyDictionary<string, string> idToName, string what, Action<T, string>? alsoSetName = null)
    {
        var p = typeof(T).GetProperty(idProperty) ?? throw new ArgumentException($"{typeof(T).Name}.{idProperty} not found");
        var byName = idToName.GroupBy(kv => kv.Value.Trim().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Key);
        return new ExchangeColumn<T>(header, e => idToName.GetValueOrDefault((string)p.GetValue(e)!, ""), (e, s) =>
        {
            if (s == "") { p.SetValue(e, ""); alsoSetName?.Invoke(e, ""); return null; }
            if (!byName.TryGetValue(s.ToLowerInvariant(), out var id)) return $"{what} “{s}” does not exist – add it first (left unchanged)";
            p.SetValue(e, id);
            alsoSetName?.Invoke(e, idToName[id]);
            return null;
        }, idToName.Count is > 0 and <= 20 ? idToName.Values.OrderBy(x => x).ToArray() : null);
    }

    public static ExchangeColumn<T> Info<T>(string header, Func<T, object?> get) => new(header, get, (_, _) => null, null, true);
}

/// <summary>The edit-in-Excel sheets of every module, and saving an import.</summary>
public class ExchangeSheets(LocalDatabase db)
{
    private static ExchangeColumn<T> C<T>(string h, string p, string[]? o = null) => DataExchange.Col<T>(h, p, o);

    private async Task<Dictionary<string, string>> NamesAsync<T>(string projectId, Func<T, string> name) where T : SyncEntity, new() =>
        (await db.ListForProjectAsync<T>(projectId)).ToDictionary(x => x.Id, name);

    public Task<ExchangeSheet<Subcontractor>> SubcontractorsAsync() => Task.FromResult(new ExchangeSheet<Subcontractor>
    {
        Name = "Subcontractors", Key = s => s.Name,
        Columns = [C<Subcontractor>("Name", nameof(Subcontractor.Name)), C<Subcontractor>("Trade / scope", nameof(Subcontractor.Trade)), C<Subcontractor>("Contact person", nameof(Subcontractor.ContactPerson)),
                   C<Subcontractor>("Phone", nameof(Subcontractor.Phone)), C<Subcontractor>("Active", nameof(Subcontractor.IsActive), ["Yes", "No"])],
        Validate = (s, _) => s.Name.Trim() == "" ? "Name is empty" : null,
    });

    public Task<ExchangeSheet<Department>> DepartmentsAsync() => Task.FromResult(new ExchangeSheet<Department>
    {
        Name = "Departments", Key = d => d.Name,
        Columns = [C<Department>("Department", nameof(Department.Name)), C<Department>("Head / in-charge", nameof(Department.Head))],
        Validate = (d, _) => d.Name.Trim() == "" ? "Department is empty" : null,
    });

    public async Task<ExchangeSheet<SiteResource>> ResourcesAsync(string projectId)
    {
        var subs = await NamesAsync<Subcontractor>(projectId, s => s.Name);
        var depts = await NamesAsync<Department>(projectId, d => d.Name);
        return new ExchangeSheet<SiteResource>
        {
            Name = "Resources & rates", Key = r => r.Code != "" ? r.Code : $"{r.Type}|{r.Name}",
            Columns = [C<SiteResource>("Type", nameof(SiteResource.Type)), C<SiteResource>("Code", nameof(SiteResource.Code)), C<SiteResource>("Name", nameof(SiteResource.Name)),
                       C<SiteResource>("Unit", nameof(SiteResource.Unit), Lists.Units), C<SiteResource>("Rate", nameof(SiteResource.Rate)), C<SiteResource>("Rate basis", nameof(SiteResource.Basis)),
                       DataExchange.Lookup<SiteResource>("Supplied by (agency)", nameof(SiteResource.SubcontractorId), subs, "Subcontractor"),
                       DataExchange.Lookup<SiteResource>("Department", nameof(SiteResource.DepartmentId), depts, "Department"), C<SiteResource>("Remarks", nameof(SiteResource.Remarks))],
            Validate = (r, _) => r.Name.Trim() == "" ? "Name is empty" : null,
        };
    }

    public async Task<ExchangeSheet<WbsNode>> WbsAsync(string projectId)
    {
        var nodes = await NamesAsync<WbsNode>(projectId, n => n.Code != "" ? n.Code : n.Name);
        return new ExchangeSheet<WbsNode>
        {
            Name = "WBS", Key = n => n.Code != "" ? n.Code : n.Name,
            Columns = [C<WbsNode>("Code", nameof(WbsNode.Code)), C<WbsNode>("Name", nameof(WbsNode.Name)), C<WbsNode>("Level", nameof(WbsNode.Level)),
                       DataExchange.Lookup<WbsNode>("Parent (code)", nameof(WbsNode.ParentId), nodes, "Parent WBS"), C<WbsNode>("Order", nameof(WbsNode.SortOrder))],
            Validate = (n, _) => n.Name.Trim() == "" ? "Name is empty" : n.ParentId == n.Id ? "A WBS node cannot be its own parent" : null,
        };
    }

    public async Task<ExchangeSheet<Activity>> ActivitiesAsync(string projectId)
    {
        var subs = await NamesAsync<Subcontractor>(projectId, s => s.Name);
        var depts = await NamesAsync<Department>(projectId, d => d.Name);
        var wbs = await NamesAsync<WbsNode>(projectId, n => n.Code != "" ? n.Code : n.Name);
        return new ExchangeSheet<Activity>
        {
            Name = "Activities", Key = a => a.Code != "" ? a.Code : a.Name,
            Columns = [C<Activity>("Code", nameof(Activity.Code)), C<Activity>("Activity", nameof(Activity.Name)), C<Activity>("Location", nameof(Activity.Location)),
                       DataExchange.Lookup<Activity>("WBS (code)", nameof(Activity.WbsId), wbs, "WBS"), C<Activity>("Unit", nameof(Activity.Unit), Lists.Units),
                       C<Activity>("Planned qty", nameof(Activity.PlannedQty)), C<Activity>("Planned start", nameof(Activity.PlannedStart)), C<Activity>("Planned finish", nameof(Activity.PlannedFinish)),
                       DataExchange.Lookup<Activity>("Subcontractor", nameof(Activity.SubcontractorId), subs, "Subcontractor"),
                       DataExchange.Lookup<Activity>("Department", nameof(Activity.DepartmentId), depts, "Department"),
                       C<Activity>("Method", nameof(Activity.Method)), C<Activity>("Weightage", nameof(Activity.Weightage)), C<Activity>("Planned rate / day", nameof(Activity.PlannedRatePerDay)),
                       C<Activity>("Remarks", nameof(Activity.Remarks))],
            Validate = (a, _) => a.Name.Trim() == "" ? "Activity name is empty" : a.PlannedQty < 0 ? "Planned qty is negative" :
                a is { PlannedStart: { } s, PlannedFinish: { } f } && f < s ? "Planned finish is before planned start" : null,
        };
    }

    public Task<ExchangeSheet<Hindrance>> HindrancesAsync() => Task.FromResult(new ExchangeSheet<Hindrance>
    {
        Name = "Hindrance register", Create = () => new Hindrance { FromDate = DateTime.Today },
        Columns = [DataExchange.Info<Hindrance>("No.", h => h.SerialNo), C<Hindrance>("From", nameof(Hindrance.FromDate)), C<Hindrance>("Resolved on", nameof(Hindrance.ToDate)),
                   C<Hindrance>("Category", nameof(Hindrance.Category), Lists.HindranceCategories), C<Hindrance>("Description", nameof(Hindrance.Description)),
                   C<Hindrance>("Affected activity", nameof(Hindrance.AffectedActivity)), C<Hindrance>("Responsible", nameof(Hindrance.ResponsibleParty), Lists.ResponsibleParties),
                   C<Hindrance>("Hours lost", nameof(Hindrance.HoursLost)), C<Hindrance>("Status", nameof(Hindrance.Status)), C<Hindrance>("Action taken", nameof(Hindrance.ActionTaken))],
        Validate = (h, _) => h.Description.Trim() == "" ? "Description is empty" : h.ToDate is { } t && t.Date < h.FromDate.Date ? "Resolved date is before the from date" : null,
        BeforeSave = async r => { foreach (var h in r.Added.Concat(r.Updated).Where(h => h.SerialNo == 0)) { h.SerialNo = await db.NextSerialAsync<Hindrance>(h.ProjectId); await db.SaveAsync(h); } },
    });

    public Task<ExchangeSheet<Requirement>> RequirementsAsync() => Task.FromResult(new ExchangeSheet<Requirement>
    {
        Name = "Requirement register", Create = () => new Requirement { RaisedDate = DateTime.Today, RaisedBy = db.UserName },
        Columns = [DataExchange.Info<Requirement>("No.", r => r.SerialNo), C<Requirement>("Raised", nameof(Requirement.RaisedDate)), C<Requirement>("Category", nameof(Requirement.Category), Lists.RequirementCategories),
                   C<Requirement>("Item / description", nameof(Requirement.Item)), C<Requirement>("Qty", nameof(Requirement.Quantity)), C<Requirement>("Unit", nameof(Requirement.Unit), Lists.Units),
                   C<Requirement>("Required by", nameof(Requirement.RequiredBy)), C<Requirement>("Priority", nameof(Requirement.Priority)), C<Requirement>("Raised by", nameof(Requirement.RaisedBy)),
                   C<Requirement>("Status", nameof(Requirement.Status)), C<Requirement>("Remarks", nameof(Requirement.Remarks))],
        Validate = (r, _) => r.Item.Trim() == "" ? "Item / description is empty" : null,
        BeforeSave = async r => { foreach (var q in r.Added.Concat(r.Updated).Where(q => q.SerialNo == 0)) { q.SerialNo = await db.NextSerialAsync<Requirement>(q.ProjectId); await db.SaveAsync(q); } },
    });

    public async Task<ExchangeSheet<Drawing>> DrawingsAsync(string projectId)
    {
        var wbs = await NamesAsync<WbsNode>(projectId, n => n.Code != "" ? n.Code : n.Name);
        return new ExchangeSheet<Drawing>
        {
            Name = "Drawings", Key = d => d.Number,
            Columns = [C<Drawing>("Drawing no.", nameof(Drawing.Number)), C<Drawing>("Title", nameof(Drawing.Title)), C<Drawing>("Revision", nameof(Drawing.Revision)),
                       C<Drawing>("Discipline", nameof(Drawing.Discipline)), DataExchange.Lookup<Drawing>("WBS (code)", nameof(Drawing.WbsId), wbs, "WBS"),
                       C<Drawing>("Status", nameof(Drawing.Status)), C<Drawing>("Issue date", nameof(Drawing.IssueDate)), C<Drawing>("Remarks", nameof(Drawing.Remarks))],
            Validate = (d, _) => d.Number.Trim() == "" ? "Drawing no. is empty" : null,
        };
    }

    /// <summary>All BOQ / equipment / part items of the project's drawings (one sheet, drawing no. in the first column).</summary>
    public async Task<ExchangeSheet<DrawingItem>> DrawingItemsAsync(string projectId)
    {
        var drawings = await NamesAsync<Drawing>(projectId, d => d.Number);
        return new ExchangeSheet<DrawingItem>
        {
            Name = "Drawing items (BOQ)", Key = i => i.ItemNo == "" ? "" : $"{drawings.GetValueOrDefault(i.DrawingId, "")}|{i.ItemNo}",
            Columns = [DataExchange.Lookup<DrawingItem>("Drawing no.", nameof(DrawingItem.DrawingId), drawings, "Drawing"), C<DrawingItem>("Item no.", nameof(DrawingItem.ItemNo)),
                       C<DrawingItem>("Description", nameof(DrawingItem.Description)), C<DrawingItem>("Type", nameof(DrawingItem.Kind)), C<DrawingItem>("Unit", nameof(DrawingItem.Unit), Lists.Units),
                       C<DrawingItem>("Qty", nameof(DrawingItem.Qty)), C<DrawingItem>("BOQ rate", nameof(DrawingItem.Rate)), C<DrawingItem>("Remarks", nameof(DrawingItem.Remarks)),
                       C<DrawingItem>("Order", nameof(DrawingItem.SortOrder))],
            Validate = (i, _) => i.DrawingId == "" ? "Drawing no. is missing or unknown" : i.Description.Trim() == "" ? "Description is empty" : i.Qty < 0 ? "Qty is negative" : null,
        };
    }

    /// <summary>Allocations to agencies with work order no. and unit price payable.</summary>
    public async Task<ExchangeSheet<DrawingAllocation>> AllocationsAsync(string projectId)
    {
        var drawings = await NamesAsync<Drawing>(projectId, d => d.Number);
        var items = await db.ListForProjectAsync<DrawingItem>(projectId);
        var itemById = items.ToDictionary(i => i.Id);
        var subs = await NamesAsync<Subcontractor>(projectId, s => s.Name);
        string ItemNo(DrawingAllocation a) => itemById.TryGetValue(a.DrawingItemId, out var i) ? i.ItemNo : "";
        return new ExchangeSheet<DrawingAllocation>
        {
            Name = "Drawing allocations", Key = a => a.DrawingItemId == "" ? "" : $"{a.DrawingItemId}|{a.SubcontractorId}|{a.WorkOrderNo}",
            Columns = [
                DataExchange.Lookup<DrawingAllocation>("Drawing no.", nameof(DrawingAllocation.DrawingId), drawings, "Drawing"),
                new ExchangeColumn<DrawingAllocation>("Item no.", ItemNo, (a, s) =>
                {
                    var match = items.Where(i => i.DrawingId == a.DrawingId && i.ItemNo.Equals(s, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (s == "" || match.Count != 1) return a.DrawingItemId != "" && ItemNo(a).Equals(s, StringComparison.OrdinalIgnoreCase) ? null : $"item “{s}” not found on that drawing";
                    a.DrawingItemId = match[0].Id;
                    return null;
                }),
                DataExchange.Info<DrawingAllocation>("Description", a => itemById.TryGetValue(a.DrawingItemId, out var i) ? i.Description : ""),
                DataExchange.Info<DrawingAllocation>("Unit", a => itemById.TryGetValue(a.DrawingItemId, out var i) ? i.Unit : ""),
                DataExchange.Lookup<DrawingAllocation>("Agency", nameof(DrawingAllocation.SubcontractorId), subs, "Subcontractor", (a, n) => a.SubcontractorName = n),
                C<DrawingAllocation>("Work order no.", nameof(DrawingAllocation.WorkOrderNo)), C<DrawingAllocation>("Allocated qty", nameof(DrawingAllocation.AllocatedQty)),
                C<DrawingAllocation>("Unit price payable", nameof(DrawingAllocation.Rate)), C<DrawingAllocation>("Allocated on", nameof(DrawingAllocation.AllocatedOn)),
                C<DrawingAllocation>("Remarks", nameof(DrawingAllocation.Remarks))],
            Validate = (a, _) => a.DrawingId == "" ? "Drawing no. is missing or unknown" :
                !itemById.TryGetValue(a.DrawingItemId, out var it) || it.DrawingId != a.DrawingId ? "Item no. is missing or not on that drawing" :
                a.SubcontractorId == "" ? "Agency is missing or unknown" : a.AllocatedQty < 0 ? "Allocated qty is negative" : null,
        };
    }

    /// <summary>Saves what an import found (new rows get the project).</summary>
    public async Task SaveAsync<T>(ExchangeSheet<T> sheet, ExchangeResult<T> result, string projectId) where T : SyncEntity, new()
    {
        foreach (var a in result.Added) a.ProjectId = projectId;
        await db.SaveAllAsync(result.Added.Concat(result.Updated).ToList());
        if (sheet.BeforeSave != null) await sheet.BeforeSave(result);
    }
}
