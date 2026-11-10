using ClosedXML.Excel;
using ConstructDpr.Shared.Model;

namespace ConstructDpr.Reports;

/// <summary>Excel export/import with ClosedXML (MIT licence; no Excel installation needed).</summary>
public static class ExcelService
{
    public static readonly string[] ActivityColumns =
        { "Code", "Name", "WBS", "Area", "Unit", "Total qty", "Rate (INR)", "Weight", "Duration (days)", "Milestone", "Responsible" };

    /// <summary>Schedule workbook: activities with CPM results, and the BOQ value summary.</summary>
    public static byte[] ExportSchedule(Project p, IReadOnlyList<Activity> acts)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Schedule");
        ws.Cell(1, 1).Value = $"{p.Code} — {p.Name}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        var head = new[] { "Code", "Activity", "WBS", "Unit", "Total qty", "Accepted qty", "Progress %", "Duration", "Planned start", "Planned finish",
            "Baseline start", "Baseline finish", "Early start", "Early finish", "Late start", "Late finish", "Total float", "Free float", "Critical", "Actual start", "Actual finish", "Status" };
        for (var i = 0; i < head.Length; i++) ws.Cell(3, i + 1).Value = head[i];
        var r = 4;
        foreach (var a in acts.OrderBy(a => a.EarlyStart ?? a.PlannedStart).ThenBy(a => a.Code))
        {
            object?[] v = { a.Code, a.Name, a.Wbs, a.Unit, a.TotalQty, a.AcceptedQty, a.ProgressPct / 100m, a.Duration, D(a.PlannedStart), D(a.PlannedFinish),
                D(a.BaselineStart), D(a.BaselineFinish), D(a.EarlyStart), D(a.EarlyFinish), D(a.LateStart), D(a.LateFinish), a.TotalFloat, a.FreeFloat,
                a.Critical ? "Yes" : "No", D(a.ActualStart), D(a.ActualFinish), a.Status.ToString() };
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(v[i]);
            if (a.Critical) ws.Range(r, 1, r, head.Length).Style.Font.SetFontColor(XLColor.FromHtml("#B91C1C"));
            r++;
        }
        var table = ws.Range(3, 1, Math.Max(4, r - 1), head.Length).CreateTable("Schedule");
        table.Theme = XLTableTheme.TableStyleMedium2;
        ws.Column(7).Style.NumberFormat.Format = "0.0%";
        foreach (var c in new[] { 9, 10, 11, 12, 13, 14, 15, 16, 20, 21 }) ws.Column(c).Style.DateFormat.Format = "dd-mmm-yy";
        ws.SheetView.FreezeRows(3);
        ws.Columns().AdjustToContents(3, Math.Max(4, r - 1));

        var boq = wb.Worksheets.Add("BOQ value");
        var bh = new[] { "Code", "Activity", "Unit", "Total qty", "Rate (INR)", "Contract value", "Accepted qty", "Executed value" };
        for (var i = 0; i < bh.Length; i++) boq.Cell(1, i + 1).Value = bh[i];
        var br = 2;
        foreach (var a in acts.Where(a => a.Rate > 0))
        {
            boq.Cell(br, 1).Value = a.Code; boq.Cell(br, 2).Value = a.Name; boq.Cell(br, 3).Value = a.Unit;
            boq.Cell(br, 4).Value = a.TotalQty; boq.Cell(br, 5).Value = a.Rate; boq.Cell(br, 6).FormulaA1 = $"D{br}*E{br}";
            boq.Cell(br, 7).Value = a.AcceptedQty; boq.Cell(br, 8).FormulaA1 = $"MIN(G{br},D{br})*E{br}";
            br++;
        }
        boq.Cell(br, 2).Value = "Total"; boq.Cell(br, 2).Style.Font.SetBold();
        boq.Cell(br, 6).FormulaA1 = $"SUM(F2:F{br - 1})"; boq.Cell(br, 8).FormulaA1 = $"SUM(H2:H{br - 1})";
        boq.Range(1, 1, 1, bh.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E6EEF4"));
        boq.Columns(5, 8).Style.NumberFormat.Format = "₹ #,##,##0.00";
        boq.Columns().AdjustToContents();
        return Save(wb);
    }

    public static byte[] ExportDpr(Dpr d, Project p)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("DPR");
        ws.Cell(1, 1).Value = $"{p.Name} — {d.Number} R{d.Revision}";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).Value = $"{d.ReportDate:dd MMM yyyy} · {d.Shift} shift · {d.Status} · Weather: {d.Weather}";
        var row = 4;
        void Block(string title, string[] head, IEnumerable<object?[]> rows)
        {
            ws.Cell(row, 1).Value = title; ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontColor(XLColor.FromHtml("#0F3D5E"));
            row++;
            for (var i = 0; i < head.Length; i++) ws.Cell(row, i + 1).Value = head[i];
            ws.Range(row, 1, row, head.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E6EEF4"));
            row++;
            foreach (var r in rows) { for (var i = 0; i < r.Length; i++) ws.Cell(row, i + 1).Value = XLCellValue.FromObject(r[i]); row++; }
            row++;
        }
        Block("Activity progress", new[] { "Code", "Activity", "Unit", "Previous", "Today", "Accepted", "Cumulative", "Balance", "Plan %", "Actual %", "Delay reason" },
            d.Activities.Select(l => new object?[] { l.Activity?.Code, l.Activity?.Name, l.Activity?.Unit, l.PrevCum, l.QtyToday, l.QtyAccepted, l.CumQty, l.Balance, l.PlannedPct, l.ActualPct, l.DelayReason }));
        Block("Manpower", new[] { "Agency", "Trade", "Skilled", "Semi-skilled", "Unskilled", "Supervisors", "Total", "Man-hours" },
            d.Manpower.Select(m => new object?[] { m.Agency, m.Trade, m.Skilled, m.SemiSkilled, m.Unskilled, m.Supervisors, m.Headcount, m.ManHours }));
        Block("Equipment", new[] { "Code", "Type", "Working h", "Idle h", "Breakdown h", "Utilisation %", "Breakdown reason" },
            d.Equipment.Select(e => new object?[] { e.Code, e.Type, e.WorkingHours, e.IdleHours, e.BreakdownHours, e.UtilisationPct, e.BreakdownReason }));
        Block("Materials", new[] { "Code", "Description", "Unit", "Opening", "Received", "Issued", "Closing", "Challan" },
            d.Materials.Select(m => new object?[] { m.Code, m.Description, m.Unit, m.Opening, m.Received, m.Issued, m.Closing, m.ChallanNo }));
        ws.Columns().AdjustToContents();
        return Save(wb);
    }

    /// <summary>Blank activity import template with instructions.</summary>
    public static byte[] ActivityTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Activities");
        for (var i = 0; i < ActivityColumns.Length; i++) ws.Cell(1, i + 1).Value = ActivityColumns[i];
        ws.Row(1).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E6EEF4"));
        object?[] sample = { "A1000", "Excavation — foundations", "1 Civil", "Area 1", "m³", 1200, 350, 4, 12, "No", "Civil engineer" };
        for (var i = 0; i < sample.Length; i++) ws.Cell(2, i + 1).Value = XLCellValue.FromObject(sample[i]);
        ws.Columns().AdjustToContents();
        var help = wb.Worksheets.Add("How to use");
        help.Cell(1, 1).Value = "One row per activity. Code and Name are required. Existing codes are updated; new codes are added. Duration is in working days; 0 or Milestone = Yes makes a milestone.";
        return Save(wb);
    }

    /// <summary>Reads activities from an .xlsx (first sheet, header row 1). Rows are validated, never partially applied.</summary>
    public static (List<Activity> Rows, List<string> Errors) ReadActivities(Stream xlsx)
    {
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheets.First();
        var header = ws.Row(1).CellsUsed().ToDictionary(c => c.GetString().Trim().ToLowerInvariant(), c => c.Address.ColumnNumber);
        var errors = new List<string>();
        foreach (var required in new[] { "code", "name" })
            if (!header.ContainsKey(required)) errors.Add($"Missing column \"{required}\" in row 1.");
        if (errors.Count > 0) return (new(), errors);
        string S(IXLRow r, string col) => header.TryGetValue(col, out var c) ? r.Cell(c).GetString().Trim() : "";
        decimal Dec(IXLRow r, string col, int rowNo)
        {
            var s = S(r, col);
            if (s == "") return 0;
            if (decimal.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0) return v;
            errors.Add($"Row {rowNo}: \"{col}\" must be a number ≥ 0 (found \"{s}\")."); return 0;
        }
        var list = new List<Activity>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ws.RowsUsed().Skip(1))
        {
            var n = r.RowNumber();
            var code = S(r, "code");
            if (code == "" && S(r, "name") == "") continue;
            if (code == "") { errors.Add($"Row {n}: Code is empty."); continue; }
            if (!seen.Add(code)) { errors.Add($"Row {n}: duplicate code {code}."); continue; }
            if (S(r, "name") == "") errors.Add($"Row {n}: Name is empty.");
            var dur = Dec(r, "duration (days)", n);
            list.Add(new Activity
            {
                Code = code, Name = S(r, "name"), Wbs = NullIfEmpty(S(r, "wbs")), Area = NullIfEmpty(S(r, "area")), Unit = NullIfEmpty(S(r, "unit")),
                TotalQty = Dec(r, "total qty", n), Rate = Dec(r, "rate (inr)", n), Weight = header.ContainsKey("weight") && S(r, "weight") != "" ? Dec(r, "weight", n) : 1,
                Duration = (int)Math.Round(dur), IsMilestone = S(r, "milestone").StartsWith("y", StringComparison.OrdinalIgnoreCase) || (header.ContainsKey("duration (days)") && dur == 0),
                Responsible = NullIfEmpty(S(r, "responsible")), SortOrder = n,
            });
        }
        return (list, errors);
    }

    /// <summary>Progress report for a period as a workbook: one sheet per section, numbers ready for pivot tables.</summary>
    public static byte[] ExportPeriod(ConstructDpr.Shared.Api.PeriodSummary s)
    {
        using var wb = new XLWorkbook();
        void Sheet(string name, string[] head, IEnumerable<object?[]> rows)
        {
            var ws = wb.Worksheets.Add(name);
            ws.Cell(1, 1).Value = $"{s.ProjectCode} — {s.ProjectName} · {s.From:dd MMM yyyy} – {s.To:dd MMM yyyy}";
            ws.Cell(1, 1).Style.Font.SetBold();
            for (var i = 0; i < head.Length; i++) ws.Cell(3, i + 1).Value = head[i];
            var r = 4;
            foreach (var row in rows) { for (var i = 0; i < row.Length; i++) ws.Cell(r, i + 1).Value = XLCellValue.FromObject(row[i]); r++; }
            if (r > 4) ws.Range(3, 1, r - 1, head.Length).CreateTable(name.Replace(" ", ""));
            else ws.Range(3, 1, 3, head.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#E6EEF4"));
            ws.Columns().AdjustToContents();
        }
        Sheet("Summary", new[] { "Item", "Value" }, new object?[][]
        {
            new object?[] { "From", s.From.ToDateTime(TimeOnly.MinValue) }, new object?[] { "To", s.To.ToDateTime(TimeOnly.MinValue) },
            new object?[] { "Approved daily reports", s.ApprovedReports }, new object?[] { "Not yet approved (not counted)", s.NotYetApproved },
            new object?[] { "Planned progress %", s.PlannedPct }, new object?[] { "Actual progress %", s.ActualPct },
            new object?[] { "Work done in period (INR)", s.ValueInPeriod }, new object?[] { "Work done to date (INR)", s.ValueToDate },
        });
        Sheet("Quantities", new[] { "Code", "Activity", "Unit", "BOQ qty", "This period", "Cumulative", "Actual %", "Plan %", "Days worked" },
            s.Activities.Select(a => new object?[] { a.Code, a.Name, a.Unit, a.TotalQty, a.InPeriod, a.Cumulative, a.ActualPct, a.PlannedPct, a.WorkingDays }));
        Sheet("Manpower", new[] { "Agency", "Trade", "Man-days", "Man-hours", "Peak headcount" },
            s.Crews.Select(c => new object?[] { c.Agency, c.Trade, c.ManDays, c.ManHours, c.PeakHeadcount }));
        Sheet("Equipment", new[] { "Code", "Type", "Working h", "Idle h", "Breakdown h", "Utilisation %" },
            s.Equipment.Select(e => new object?[] { e.Code, e.Type, e.Working, e.Idle, e.Breakdown, e.UtilisationPct }));
        Sheet("Materials", new[] { "Code", "Description", "Unit", "Received", "Issued", "Latest closing" },
            s.Materials.Select(m => new object?[] { m.Code, m.Description, m.Unit, m.Received, m.Issued, m.LatestClosing }));
        Sheet("Notes", new[] { "Date", "Type", "Report", "Note" },
            s.Notes.Select(n => new object?[] { n.Date.ToDateTime(TimeOnly.MinValue), n.Kind, n.Number, n.Text }));
        Sheet("Reports", new[] { "Report", "Revision", "Date", "Shift", "Prepared by", "Approved by" },
            s.Reports.Select(d => new object?[] { d.Number, d.Revision, d.ReportDate.ToDateTime(TimeOnly.MinValue), d.Shift.ToString(), d.PreparedBy, d.ApprovedBy }));
        foreach (var ws in wb.Worksheets) foreach (var c in ws.CellsUsed(c => c.DataType == XLDataType.DateTime)) c.Style.DateFormat.Format = "dd-mmm-yyyy";
        return Save(wb);
    }

    private static string? NullIfEmpty(string s) => s == "" ? null : s;
    private static DateTime? D(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);
    private static byte[] Save(XLWorkbook wb) { using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray(); }
}
