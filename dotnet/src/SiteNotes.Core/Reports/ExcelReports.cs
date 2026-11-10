using ClosedXML.Excel;
using SiteNotes.Core.Models;

namespace SiteNotes.Core.Reports;

/// <summary>Excel workbooks with filters, frozen headers, totals formulas and one sheet per section.</summary>
public static class ExcelReports
{
    private static readonly XLColor Header = XLColor.FromHtml("#1F4E79");
    private static readonly XLColor Band = XLColor.FromHtml("#DCE8F5");

    /// <summary>One workbook with every section of the filtered reports on its own sheet.</summary>
    public static void ReportWorkbook(Stream output, Project? project, IReadOnlyList<DprBundle> bundles, string filterText, WorkCalendar? cal = null)
    {
        cal ??= new WorkCalendar();
        using var wb = new XLWorkbook();
        var title = project?.Name ?? "Daily Progress Reports";

        Sheet(wb, "DPR Summary", title, filterText,
            ["DPR No", "Date", "Day", "Status", "Shift", "Weather AM", "Weather PM", "Temp °C", "Rain mm",
             "Subcontractor manpower", "Department manpower", "Total manpower", "Man-hours", "Machines",
             "Activities", "Hindrances", "Toolbox talk", "Prepared by", "Plan for tomorrow", "Remarks", "Cost of the day"],
            bundles.Select(b =>
            {
                var r = b.Report;
                return new object?[]
                {
                    r.ReportNo, r.Date, r.Date.ToString("ddd"), r.Status.ToString(), r.Shift.ToString(), r.WeatherMorning,
                    r.WeatherEvening, r.TemperatureC, r.RainfallMm,
                    DprCalculator.Total(b.Manpower, ManpowerSource.Subcontractor),
                    DprCalculator.Total(b.Manpower, ManpowerSource.Department),
                    b.Manpower.Sum(m => m.Count), b.Manpower.Sum(m => m.ManHours), b.Machines.Sum(m => m.Count),
                    b.Activities.Count, b.Hindrances.Count, r.ToolboxTalkTopic, r.PreparedBy, r.PlanForTomorrow, r.Remarks,
                    CostCalculator.ForReport(b, cal).Total
                };
            }), sumColumns: [10, 11, 12, 13, 14, 15, 16, 21]);

        Sheet(wb, "Activities", title, filterText,
            ["DPR No", "Date", "Activity", "Location", "Agency", "Unit", "Planned qty", "Today qty", "Cumulative qty", "% complete", "Remarks"],
            bundles.SelectMany(b => b.Activities.Select(a => new object?[]
            {
                b.Report.ReportNo, b.Report.Date, a.ActivityName, a.Location, a.SubcontractorName, a.Unit, a.PlannedQty,
                a.TodayQty, a.CumulativeQty, a.PlannedQty > 0 ? a.PercentComplete / 100 : null, a.Remarks
            })), sumColumns: [8], percentColumns: [10]);

        Sheet(wb, "Manpower", title, filterText,
            ["DPR No", "Date", "Type", "Subcontractor / Department", "Trade", "Activity", "Nos", "Hours", "Man-hours", "Rate", "Rate basis", "Cost", "Remarks"],
            bundles.SelectMany(b => b.Manpower.Select(m => new object?[]
            {
                b.Report.ReportNo, b.Report.Date, m.Source.ToString(), m.PartyName, m.Trade, m.ActivityName, m.Count, m.Hours, m.ManHours,
                m.Rate, m.Basis.ToString(), CostCalculator.Cost(m, cal), m.Remarks
            })), sumColumns: [7, 9, 12]);

        ManpowerPivot(wb, bundles, title, filterText);

        Sheet(wb, "Machinery", title, filterText,
            ["DPR No", "Date", "Machine / equipment", "Own / Hired", "Activity", "Nos", "Working hrs", "Idle hrs", "Fuel (L)", "Rate", "Rate basis", "Cost", "Remarks"],
            bundles.SelectMany(b => b.Machines.Select(m => new object?[]
            {
                b.Report.ReportNo, b.Report.Date, m.Name, m.Ownership.ToString(), m.ActivityName, m.Count, m.WorkingHours, m.IdleHours,
                m.FuelLitres, m.Rate, m.Basis.ToString(), CostCalculator.Cost(m, cal), m.Remarks
            })), sumColumns: [6, 7, 8, 9, 12]);

        Sheet(wb, "Materials", title, filterText,
            ["DPR No", "Date", "Material / resource", "Type", "Activity", "Unit", "Received", "Consumed", "Rate", "Cost", "Remarks"],
            bundles.SelectMany(b => b.Materials.Select(m => new object?[]
            {
                b.Report.ReportNo, b.Report.Date, m.Material, m.Kind == ResourceType.Other ? "Non-material" : "Material", m.ActivityName,
                m.Unit, m.Received, m.Consumed, m.Rate, CostCalculator.Cost(m), m.Remarks
            })), sumColumns: [10]);

        AddHindranceSheet(wb, bundles.SelectMany(b => b.Hindrances).ToList(), title, filterText);
        AddRequirementSheet(wb, bundles.SelectMany(b => b.Requirements).ToList(), title, filterText);

        wb.SaveAs(output);
    }

    public static void HindranceRegister(Stream output, Project? project, IReadOnlyList<Hindrance> list, string filterText)
    {
        using var wb = new XLWorkbook();
        AddHindranceSheet(wb, list, project?.Name ?? "Hindrance register", filterText);
        wb.SaveAs(output);
    }

    public static void RequirementRegister(Stream output, Project? project, IReadOnlyList<Requirement> list, string filterText)
    {
        using var wb = new XLWorkbook();
        AddRequirementSheet(wb, list, project?.Name ?? "Requirement register", filterText);
        wb.SaveAs(output);
    }

    private static void AddHindranceSheet(XLWorkbook wb, IReadOnlyList<Hindrance> list, string title, string filterText) =>
        Sheet(wb, "Hindrances", title, filterText,
            ["No", "From", "To", "Days", "Category", "Description", "Affected activity", "Responsible", "Hours lost", "Status", "Action taken"],
            list.OrderBy(h => h.SerialNo).Select(h => new object?[]
            {
                h.SerialNo, h.FromDate, h.ToDate, h.DaysOpen, h.Category, h.Description, h.AffectedActivity, h.ResponsibleParty,
                h.HoursLost, h.Status.ToString(), h.ActionTaken
            }), sumColumns: [9]);

    private static void AddRequirementSheet(XLWorkbook wb, IReadOnlyList<Requirement> list, string title, string filterText) =>
        Sheet(wb, "Requirements", title, filterText,
            ["No", "Raised", "Category", "Item / description", "Qty", "Unit", "Required by", "Priority", "Raised by", "Status", "Overdue", "Remarks"],
            list.OrderBy(q => q.SerialNo).Select(q => new object?[]
            {
                q.SerialNo, q.RaisedDate, q.Category, q.Item, q.Quantity, q.Unit, q.RequiredBy, q.Priority.ToString(), q.RaisedBy,
                q.Status.ToString(), q.IsOverdue ? "Yes" : "", q.Remarks
            }));

    /// <summary>Date × subcontractor/department grid of headcount.</summary>
    private static void ManpowerPivot(XLWorkbook wb, IReadOnlyList<DprBundle> bundles, string title, string filterText)
    {
        var parties = bundles.SelectMany(b => b.Manpower)
            .Select(m => (m.Source, Name: string.IsNullOrWhiteSpace(m.PartyName) ? "(unassigned)" : m.PartyName))
            .Distinct().OrderBy(p => p.Source).ThenBy(p => p.Name).ToList();
        var headers = new List<string> { "Date", "DPR No" };
        headers.AddRange(parties.Select(p => $"{p.Name} ({(p.Source == ManpowerSource.Subcontractor ? "Sub" : "Dept")})"));
        headers.Add("Total");
        var rows = bundles.OrderBy(b => b.Report.Date).Select(b =>
        {
            var row = new List<object?> { b.Report.Date, b.Report.ReportNo };
            foreach (var p in parties)
                row.Add(b.Manpower.Where(m => m.Source == p.Source &&
                                              (string.IsNullOrWhiteSpace(m.PartyName) ? "(unassigned)" : m.PartyName) == p.Name)
                    .Sum(m => m.Count));
            row.Add(b.Manpower.Sum(m => m.Count));
            return row.ToArray();
        });
        Sheet(wb, "Manpower Grid", title, filterText, headers, rows,
            sumColumns: Enumerable.Range(3, parties.Count + 1).ToArray());
    }

    /// <summary>Writes a titled, filterable table. Column numbers in sumColumns/percentColumns are 1-based.</summary>
    private static void Sheet(XLWorkbook wb, string name, string title, string filterText, IReadOnlyList<string> headers,
        IEnumerable<object?[]> rows, int[]? sumColumns = null, int[]? percentColumns = null)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(Header);
        ws.Cell(2, 1).Value = $"{name} · {filterText} · generated {DateTime.Now:dd-MMM-yyyy HH:mm}";
        ws.Cell(2, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);

        const int headerRow = 4;
        for (var c = 0; c < headers.Count; c++) ws.Cell(headerRow, c + 1).Value = headers[c];
        var hdr = ws.Range(headerRow, 1, headerRow, headers.Count);
        hdr.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(Header)
            .Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center);

        var r = headerRow;
        foreach (var row in rows)
        {
            r++;
            for (var c = 0; c < row.Length; c++)
            {
                var cell = ws.Cell(r, c + 1);
                switch (row[c])
                {
                    case null: break;
                    case DateTime d: cell.Value = d; cell.Style.DateFormat.Format = "dd-mmm-yyyy"; break;
                    case int i: cell.Value = i; break;
                    case double dbl: cell.Value = dbl; cell.Style.NumberFormat.Format = "#,##0.##"; break;
                    case decimal dec: cell.Value = dec; cell.Style.NumberFormat.Format = "#,##0.00"; break;
                    default: cell.Value = row[c]!.ToString(); break;
                }
            }
        }

        if (percentColumns != null)
            foreach (var pc in percentColumns)
                ws.Range(headerRow + 1, pc, Math.Max(headerRow + 1, r), pc).Style.NumberFormat.Format = "0.0%";

        if (r > headerRow)
        {
            var table = ws.Range(headerRow, 1, r, headers.Count);
            table.Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Thin)
                .Border.SetInsideBorderColor(XLColor.FromHtml("#B8C4D0")).Border.SetOutsideBorderColor(XLColor.FromHtml("#B8C4D0"));
            table.SetAutoFilter();

            if (sumColumns is { Length: > 0 })
            {
                var t = r + 1;
                ws.Cell(t, 1).Value = "TOTAL";
                foreach (var sc in sumColumns)
                    ws.Cell(t, sc).FormulaA1 = $"SUBTOTAL(9,{ws.Cell(headerRow + 1, sc).Address}:{ws.Cell(r, sc).Address})";
                ws.Range(t, 1, t, headers.Count).Style.Font.SetBold().Fill.SetBackgroundColor(Band);
            }
        }
        else
        {
            ws.Cell(headerRow + 1, 1).Value = "No entries";
        }

        ws.SheetView.FreezeRows(headerRow);
        ws.Columns(1, headers.Count).AdjustToContents(headerRow, Math.Min(r, headerRow + 300), 8, 50);
        ws.Row(headerRow).Height = 30;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(headerRow, headerRow);
    }
}
