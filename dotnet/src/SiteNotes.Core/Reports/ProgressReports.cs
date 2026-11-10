using ClosedXML.Excel;
using SiteNotes.Core.Models;
using static SiteNotes.Core.Reports.PdfReports;

namespace SiteNotes.Core.Reports;

/// <summary>Activity progress, cost per activity / day / hour, subcontractor-wise and department-wise reports.</summary>
public static class ProgressReports
{
    public static string StateText(ActivityState s) => s switch
    {
        ActivityState.NotStarted => "Not started",
        ActivityState.InProgress => "In progress",
        ActivityState.Completed => "Completed",
        _ => "Delayed",
    };

    public static void Pdf(Stream output, Project? project, ProjectProgress pp, string periodText, bool includeCost = true)
    {
        var cur = pp.Calendar.Currency;
        using var pdf = new PdfDoc(output, project?.Name ?? "Progress", $"Progress & cost · {periodText}", landscape: true);
        pdf.NewPage();
        pdf.Text("ACTIVITY PROGRESS & COST REPORT", 14, bold: true, color: PdfDoc.Accent, spacingAfter: 2);
        pdf.Text($"{periodText} · calendar {pp.Calendar.HoursPerDay:0.#} h/day", 9, color: PdfDoc.Muted, spacingAfter: 8);

        var acts = pp.Activities;
        pdf.Tiles([
            ("Overall progress", P(pp.OverallPercent) + " %"),
            ("Planned to date", P(pp.PlannedPercent) + " %"),
            ("Completed", $"{acts.Count(a => a.State == ActivityState.Completed)} / {acts.Count}"),
            ("In progress", acts.Count(a => a.State == ActivityState.InProgress).ToString()),
            ("Delayed", acts.Count(a => a.State == ActivityState.Delayed).ToString()),
            ("Man-hours", N(pp.Actual.ManHours)),
        ]);

        // Progress grouped by WBS (plant / unit / area…)
        foreach (var group in acts.GroupBy(a => string.IsNullOrWhiteSpace(a.WbsPath) ? "Activities without WBS" : a.WbsPath))
        {
            pdf.Section(group.Key, $"{group.Count()} activities");
            pdf.Table([
                    new("Code", 0.8f), new("Activity", 2.4f), new("Method", 1.2f), new("Agency / Dept.", 1.5f), new("Unit", 0.6f),
                    new("Planned", 0.9f, true), new("Done", 0.9f, true), new("Balance", 0.9f, true), new("Progress", 1.5f, false, true),
                    new("Plan %", 0.7f, true), new("Start", 1.2f), new("Forecast finish", 1.2f), new("Avg/day", 0.8f, true), new("Status", 1f)
                ],
                group.Select(a => new[]
                {
                    a.Activity.Code, a.Activity.Name, a.Activity.Method, Join(a.Agency, a.Department), a.Activity.Unit,
                    N(a.Activity.PlannedQty), N(a.DoneQty), N(a.RemainingQty), P(a.Percent), P(a.PlannedPercentToday),
                    D(a.ActualStart), D(a.ForecastFinish), N(a.AvgPerDay), StateText(a.State)
                }));
        }

        if (includeCost)
        {
            pdf.Section("Cost by activity", $"{cur} {M(pp.Actual.Total)}");
            pdf.Tiles([
                ($"Manpower ({cur})", M(pp.Actual.Manpower)), ($"Machinery ({cur})", M(pp.Actual.Machinery)),
                ($"Material ({cur})", M(pp.Actual.Material)), ($"Non-material ({cur})", M(pp.Actual.Other)),
                ($"Actual total ({cur})", M(pp.Actual.Total)), ($"Planned budget ({cur})", M(pp.PlannedCost)),
            ]);
            var rows = acts.Where(a => a.Actual.Total > 0 || a.PlannedCost > 0).Select(a => new[]
            {
                a.Activity.ToString(), M(a.Actual.Manpower), M(a.Actual.Machinery), M(a.Actual.Material), M(a.Actual.Other),
                M(a.Actual.Total), a.DoneQty > 0 ? $"{M(a.CostPerUnit)}/{a.Activity.Unit}" : "-", M(a.CostPerDay),
                M(a.CostPerManHour), N(a.Actual.ManHours), M(a.PlannedCost), M(a.EarnedValue)
            }).ToList();
            if (pp.Unallocated.Total > 0)
                rows.Add(["General / overheads", M(pp.Unallocated.Manpower), M(pp.Unallocated.Machinery), M(pp.Unallocated.Material),
                    M(pp.Unallocated.Other), M(pp.Unallocated.Total), "-", "-", "-", N(pp.Unallocated.ManHours), "-", "-"]);
            pdf.Table([
                    new("Activity", 2.6f), new("Manpower", 1, true), new("Machinery", 1, true), new("Material", 1, true),
                    new("Non-material", 1, true), new("Total", 1.1f, true), new("Cost / unit", 1.1f, true), new("Cost / day", 1, true),
                    new("Labour / man-hr", 0.9f, true), new("Man-hrs", 0.8f, true), new("Budget", 1, true), new("Earned value", 1, true)
                ], rows,
                ["TOTAL", M(pp.Actual.Manpower), M(pp.Actual.Machinery), M(pp.Actual.Material), M(pp.Actual.Other), M(pp.Actual.Total),
                 "", "", "", N(pp.Actual.ManHours), M(pp.PlannedCost), M(acts.Sum(a => a.EarnedValue))]);
        }

        Parties(pdf, "Subcontractor-wise manpower & work done", "Subcontractor", pp.BySubcontractor, cur, includeCost);
        Parties(pdf, "Department-wise manpower & activities", "Department", pp.ByDepartment, cur, includeCost);
    }

    private static string Join(string a, string b) => string.Join(" / ", new[] { a, b }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static void Parties(PdfDoc pdf, string title, string what, List<PartySummary> list, string cur, bool cost)
    {
        pdf.Section(title);
        pdf.Table([
                new(what, 1.8f), new("Man-days", 0.8f, true), new("Man-hours", 0.9f, true), new($"Cost ({cur})", cost ? 1 : 0.01f, true),
                new("Activities", 0.7f, true), new("Work done (quantity)", 4.5f)
            ],
            list.Select(p => new[] { p.Name, N(p.ManDays), N(p.ManHours), cost ? M(p.Cost) : "", p.Activities.ToString(), p.ActivitiesDone }),
            ["TOTAL", N(list.Sum(p => p.ManDays)), N(list.Sum(p => p.ManHours)), cost ? M(list.Sum(p => p.Cost)) : "", "", ""]);
    }

    public static void Excel(Stream output, Project? project, ProjectProgress pp, string periodText)
    {
        using var wb = new XLWorkbook();
        var title = project?.Name ?? "Progress";
        var cur = pp.Calendar.Currency;

        var ws = Sheet(wb, "Progress", title, periodText,
            ["WBS", "Code", "Activity", "Method", "Agency", "Department", "Location", "Unit", "Planned qty", "Done qty", "Balance qty",
             "% complete", "Planned % to date", "Planned start", "Planned finish", "Actual start", "Last worked", "Days worked",
             "Avg / day", "Forecast finish", "Status"],
            pp.Activities.Select(a => new object?[]
            {
                a.WbsPath, a.Activity.Code, a.Activity.Name, a.Activity.Method, a.Agency, a.Department, a.Activity.Location, a.Activity.Unit,
                a.Activity.PlannedQty, a.DoneQty, a.RemainingQty, a.Percent / 100, a.PlannedPercentToday / 100, a.Activity.PlannedStart,
                a.Activity.PlannedFinish, a.ActualStart, a.LastWorked, a.DaysWorked, a.AvgPerDay, a.ForecastFinish, StateText(a.State)
            }), [12, 13]);
        // Data bar on % complete
        var last = ws.LastRowUsed()!.RowNumber();
        if (last > 4) ws.Range(5, 12, last, 12).AddConditionalFormat().DataBar(XLColor.FromHtml("#1F4E79")).LowestValue().HighestValue();

        Sheet(wb, "Cost by activity", title, $"{periodText} · {cur}",
            ["Activity", "Unit", "Done qty", "Manpower", "Machinery", "Material", "Non-material", "Total cost", "Cost / unit",
             "Days worked", "Cost / day", "Man-hours", "Labour cost / man-hour", "Machine hours", "Planned budget", "Earned value", "Cost variance (EV − actual)"],
            pp.Activities.Select(a => new object?[]
            {
                a.Activity.ToString(), a.Activity.Unit, a.DoneQty, a.Actual.Manpower, a.Actual.Machinery, a.Actual.Material, a.Actual.Other,
                a.Actual.Total, a.CostPerUnit, a.DaysWorked, a.CostPerDay, a.Actual.ManHours, a.CostPerManHour, a.Actual.MachineHours,
                a.PlannedCost, a.EarnedValue, a.EarnedValue - a.Actual.Total
            }).Append(["General / overheads", "", null, pp.Unallocated.Manpower, pp.Unallocated.Machinery, pp.Unallocated.Material,
                pp.Unallocated.Other, pp.Unallocated.Total, null, null, null, pp.Unallocated.ManHours, null, pp.Unallocated.MachineHours, null, null, null]),
            sum: [4, 5, 6, 7, 8, 12, 14, 15, 16, 17]);

        Sheet(wb, "Subcontractors", title, periodText, ["Subcontractor", "Man-days", "Man-hours", $"Cost ({cur})", "Activities", "Work done"],
            pp.BySubcontractor.Select(p => new object?[] { p.Name, p.ManDays, p.ManHours, p.Cost, p.Activities, p.ActivitiesDone }), sum: [2, 3, 4]);
        Sheet(wb, "Departments", title, periodText, ["Department", "Man-days", "Man-hours", $"Cost ({cur})", "Activities", "Work done"],
            pp.ByDepartment.Select(p => new object?[] { p.Name, p.ManDays, p.ManHours, p.Cost, p.Activities, p.ActivitiesDone }), sum: [2, 3, 4]);
        wb.SaveAs(output);
    }

    private static IXLWorksheet Sheet(XLWorkbook wb, string name, string title, string sub, string[] headers, IEnumerable<object?[]> rows,
        int[]? percent = null, int[]? sum = null)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14).Font.SetFontColor(XLColor.FromHtml("#1F4E79"));
        ws.Cell(2, 1).Value = $"{name} · {sub} · generated {DateTime.Now:dd-MMM-yyyy HH:mm}";
        ws.Cell(2, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.Gray);
        for (var c = 0; c < headers.Length; c++) ws.Cell(4, c + 1).Value = headers[c];
        ws.Range(4, 1, 4, headers.Length).Style.Font.SetBold().Font.SetFontColor(XLColor.White)
            .Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E79")).Alignment.SetWrapText();
        var r = 4;
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
                    case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00"; break;
                    case double v: cell.Value = v; cell.Style.NumberFormat.Format = "#,##0.###"; break;
                    case int i: cell.Value = i; break;
                    default: cell.Value = row[c]!.ToString(); break;
                }
            }
        }
        foreach (var pc in percent ?? []) ws.Range(5, pc, Math.Max(5, r), pc).Style.NumberFormat.Format = "0.0%";
        if (r > 4)
        {
            ws.Range(4, 1, r, headers.Length).SetAutoFilter();
            ws.Range(4, 1, r, headers.Length).Style.Border.SetInsideBorder(XLBorderStyleValues.Thin).Border.SetOutsideBorder(XLBorderStyleValues.Thin);
            if (sum is { Length: > 0 })
            {
                ws.Cell(r + 1, 1).Value = "TOTAL";
                foreach (var sc in sum) ws.Cell(r + 1, sc).FormulaA1 = $"SUBTOTAL(9,{ws.Cell(5, sc).Address}:{ws.Cell(r, sc).Address})";
                ws.Range(r + 1, 1, r + 1, headers.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DCE8F5"));
                foreach (var sc in sum) ws.Cell(r + 1, sc).Style.NumberFormat.Format = "#,##0.00";
            }
        }
        ws.SheetView.FreezeRows(4);
        ws.Columns(1, headers.Length).AdjustToContents(4, Math.Min(r, 300), 8, 45);
        ws.Row(4).Height = 30;
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        return ws;
    }
}
