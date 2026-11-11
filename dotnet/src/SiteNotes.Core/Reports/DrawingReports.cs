using ClosedXML.Excel;
using SiteNotes.Core.Models;
using static SiteNotes.Core.Reports.PdfReports;

namespace SiteNotes.Core.Reports;

/// <summary>Drawing register &amp; work completion: allocated, unallocated, completed and balance per drawing, item and agency.</summary>
public static class DrawingReports
{
    public static string StateText(DrawingState s) => s switch
    {
        DrawingState.NotAllocated => "Not allocated",
        DrawingState.PartlyAllocated => "Partly allocated",
        DrawingState.Allocated => "Allocated",
        DrawingState.InProgress => "In progress",
        _ => "Completed",
    };

    private static string Agencies(DrawingItemProgress i) => string.Join("; ", i.Agencies.Select(a =>
        a.Unallocated ? $"{a.Agency}: done {N(a.Done)} (no allocation!)"
            : $"{a.Agency}: {N(a.Allocated)} → done {N(a.Done)} ({P(a.Percent)} %)" + (a.Excess > 0 ? $" ⚠ +{N(a.Excess)} over allocation" : "")));

    public static void Pdf(Stream output, Project? project, DrawingRegister reg, string title, ReportOptions? opt = null)
    {
        PdfDoc.Render(output, project?.Name ?? "Drawing register", $"Drawing register & work completion · {title}", true, opt?.Layout, pdf =>
        {
            pdf.NewPage();
            pdf.Text("DRAWING REGISTER & WORK COMPLETION", 14, bold: true, color: PdfDoc.Accent, spacingAfter: 2);
            pdf.Text($"{title} · quantities from the drawings, allocations to agencies, completion from the daily reports", 9, color: PdfDoc.Muted, spacingAfter: 8);
            var items = reg.AllItems.ToList();
            pdf.Tiles([
                ("Drawings", reg.Drawings.Count.ToString()),
                ("Completed drawings", reg.Drawings.Count(d => d.State == DrawingState.Completed).ToString()),
                ("Items / equipment", items.Count.ToString()),
                ("Items not fully allocated", items.Count(i => i.Unallocated > 1e-9).ToString()),
                ("Items over-allocated", items.Count(i => i.OverAllocated > 1e-9).ToString()),
                ("Work without allocation", items.Count(i => i.CompletedWithoutAllocation > 0).ToString()),
                ("Done beyond drawing qty", items.Count(i => i.Excess > 0).ToString()),
            ]);

            var hasValue = items.Any(i => i.Item.Rate > 0 || i.Agencies.Any(a => a.Rate > 0));
            if (hasValue)
                pdf.Tiles([
                    ("BOQ value", M(reg.Drawings.Sum(d => d.Value))),
                    ("Value of work completed", M(reg.Drawings.Sum(d => d.CompletedValue))),
                    ("Unallocated value", M(reg.Drawings.Sum(d => d.UnallocatedValue))),
                    ("Allocated to agencies (work orders)", M(reg.Drawings.Sum(d => d.AllocatedValue))),
                    ("Payable for work done", M(reg.Drawings.Sum(d => d.PayableValue))),
                ]);

            pdf.Section("Drawing summary");
            pdf.Table([
                    new("Drawing", 1.6f), new("Title", 2.2f), new("Rev", 0.5f), new("WBS", 1.6f), new("Quantity", 1.4f),
                    new("Allocated", 0.9f, false, true), new("Completed", 1.3f, false, true), new("Agencies", 0.7f, true), new("Status", 1.1f)
                ],
                reg.Drawings.Select(d => new[]
                {
                    d.Drawing.Number, d.Drawing.Title, d.Drawing.Revision, d.WbsPath, d.TotalsText, P(d.AllocatedPercent), P(d.Percent),
                    d.Agencies.ToString(), StateText(d.State)
                }));

            if (hasValue)
            {
                pdf.Section("Value by drawing", "BOQ value = qty × BOQ rate · allocated / payable = qty × unit price payable to the agency");
                pdf.Table([
                        new("Drawing", 1.6f), new("Title", 2.4f), new("BOQ value", 1.2f, true), new("Completed value", 1.2f, true), new("Unallocated value", 1.2f, true),
                        new("Allocated value (WO)", 1.2f, true), new("Payable for work done", 1.2f, true), new("Balance on work orders", 1.2f, true)
                    ],
                    reg.Drawings.Select(d => new[]
                    {
                        d.Drawing.Number, d.Drawing.Title, M(d.Value), M(d.CompletedValue), M(d.UnallocatedValue), M(d.AllocatedValue), M(d.PayableValue), M(d.AllocatedValue - d.PayableValue)
                    }),
                    ["", "TOTAL", M(reg.Drawings.Sum(d => d.Value)), M(reg.Drawings.Sum(d => d.CompletedValue)), M(reg.Drawings.Sum(d => d.UnallocatedValue)),
                     M(reg.Drawings.Sum(d => d.AllocatedValue)), M(reg.Drawings.Sum(d => d.PayableValue)), M(reg.Drawings.Sum(d => d.AllocatedValue - d.PayableValue))]);
            }

            foreach (var d in reg.Drawings)
            {
                pdf.Section($"{d.Drawing.Number} {d.Drawing.Revision} – {d.Drawing.Title}", $"{StateText(d.State)} · {P(d.Percent)} % done");
                pdf.Table([
                        new("Item", 0.8f), new("Description", 2.2f), new("Type", 0.8f), new("Unit", 0.5f), new("Qty", 0.85f, true),
                        new("Allocated", 0.85f, true), new("Unallocated", 0.85f, true), new("Completed", 0.85f, true), new("Balance", 0.85f, true),
                        new("Progress", 1.1f, false, true), new("Agencies (allocated → done)", 3.2f)
                    ],
                    d.Items.Select(i => new[]
                    {
                        i.Item.ItemNo, i.Item.Description, i.Item.Kind == DrawingItemKind.BoqItem ? "BOQ" : i.Item.Kind.ToString(), i.Item.Unit,
                        N(i.Qty), N(i.Allocated) + (i.OverAllocated > 0 ? " ⚠" : ""), N(i.Unallocated), N(i.Completed), N(i.Balance), P(i.Percent), Agencies(i)
                    }),
                    d.Totals.Count == 1
                        ? ["", "TOTAL", "", d.Totals[0].Unit, N(d.Totals[0].Qty), N(d.Totals[0].Allocated), N(d.Totals[0].Unallocated), N(d.Totals[0].Completed), N(d.Totals[0].Balance), P(d.Totals[0].Percent), ""]
                        : null, fontSize: 7.5f);
            }

            if (hasValue)
            {
                pdf.Section("Work orders: agency, unit price and value", "one line per allocation");
                pdf.Table([
                        new("Drawing", 1.3f), new("Item", 0.7f), new("Description", 2f), new("Agency", 1.6f), new("Work order", 1.1f), new("Unit", 0.5f),
                        new("Allocated", 0.8f, true), new("Done", 0.8f, true), new("Unit price", 0.9f, true), new("Allocated value", 1.1f, true),
                        new("Payable", 1.1f, true), new("Balance value", 1.1f, true)
                    ],
                    reg.Drawings.SelectMany(d => d.Items.SelectMany(i => i.Agencies.Select(a => new[]
                    {
                        d.Drawing.Number, i.Item.ItemNo, i.Item.Description, a.Agency, a.Unallocated ? "(no allocation)" : a.WorkOrders, i.Item.Unit,
                        N(a.Allocated), N(a.Done), M(a.Rate), M(a.AllocatedValue), M(a.PayableValue), M(a.BalanceValue)
                    }))), fontSize: 7.5f);
            }

            pdf.Section("Subcontractor-wise drawing work");
            pdf.Table([
                    new("Agency", 2.2f), new("Unit", 0.6f), new("Allocated", 1, true), new("Done", 1, true), new("Balance", 1, true),
                    new("Progress", 1.3f, false, true), new("Drawings", 0.95f, true), new("Items", 0.7f, true), new("Done without allocation", 1.1f, true),
                    new("Allocated value", 1.2f, true), new("Payable", 1.2f, true), new("Work orders", 1.4f)
                ],
                reg.ByAgency.Select(a => new[] { a.Agency, a.Unit, N(a.Allocated), N(a.Done), N(a.Balance), P(a.Percent), a.Drawings.ToString(), a.Items.ToString(), N(a.WithoutAllocation),
                    M(a.AllocatedValue), M(a.PayableValue), a.WorkOrders }), fontSize: 7.5f);

            var open = items.Where(i => i.Unallocated > 1e-9 || i.OverAllocated > 1e-9 || i.CompletedWithoutAllocation > 0 || i.Excess > 0 || i.Agencies.Any(a => a.Excess > 0)).ToList();
            pdf.Section("Needs attention: unallocated, over-allocated, worked without allocation or done beyond quantity", $"{open.Count} items");
            pdf.Table([new("Drawing", 1.4f), new("Item", 0.8f), new("Description", 2.2f), new("Unit", 0.5f), new("Qty", 0.8f, true),
                    new("Unallocated", 0.9f, true), new("Over-allocated", 0.9f, true), new("Done without allocation", 1.1f, true),
                    new("Done beyond drawing qty", 1.1f, true), new("Agencies beyond allocation", 2f)],
                open.Select(i => new[]
                {
                    reg.Drawings.First(d => d.Items.Contains(i)).Drawing.Number, i.Item.ItemNo, i.Item.Description, i.Item.Unit, N(i.Qty),
                    N(i.Unallocated), N(i.OverAllocated), N(i.CompletedWithoutAllocation), N(i.Excess),
                    string.Join("; ", i.Agencies.Where(a => a.Excess > 0).Select(a => $"{a.Agency} +{N(a.Excess)}"))
                }), emptyText: "Nothing – every item is fully allocated, worked only by allocated agencies and within its quantity.");
        });
    }

    public static void Excel(Stream output, Project? project, DrawingRegister reg, string title)
    {
        using var wb = new XLWorkbook();
        Sheet(wb, "Drawings", project?.Name ?? "", title,
            ["Drawing", "Title", "Revision", "Discipline", "Status", "WBS", "Quantity", "Allocated %", "Completed %", "Agencies", "Work status",
             "BOQ value", "Completed value", "Unallocated value", "Allocated value (work orders)", "Payable for work done"],
            reg.Drawings.Select(d => new object?[]
            {
                d.Drawing.Number, d.Drawing.Title, d.Drawing.Revision, d.Drawing.Discipline, d.Drawing.Status.ToString(), d.WbsPath, d.TotalsText,
                d.AllocatedPercent / 100, d.Percent / 100, d.Agencies, StateText(d.State),
                d.Value, d.CompletedValue, d.UnallocatedValue, d.AllocatedValue, d.PayableValue
            }), percent: [8, 9]);
        Sheet(wb, "Items", project?.Name ?? "", title,
            ["Drawing", "Item", "Description", "Type", "Unit", "Qty", "Allocated", "Unallocated", "Over-allocated", "Completed", "Done without allocation", "Balance", "% complete", "Status", "Done beyond drawing qty",
             "BOQ rate", "BOQ value", "Completed value", "Unallocated value", "Allocated value (work orders)", "Payable for work done"],
            reg.Drawings.SelectMany(d => d.Items.Select(i => new object?[]
            {
                d.Drawing.Number, i.Item.ItemNo, i.Item.Description, i.Item.Kind.ToString(), i.Item.Unit, i.Qty, i.Allocated, i.Unallocated, i.OverAllocated,
                i.Completed, i.CompletedWithoutAllocation, i.Balance, i.Percent / 100, StateText(i.State), i.Excess,
                i.Item.Rate, i.Value, i.CompletedValue, i.UnallocatedValue, i.AllocatedValue, i.PayableValue
            })), percent: [13]);
        Sheet(wb, "Allocations", project?.Name ?? "", title,
            ["Drawing", "Item", "Description", "Unit", "Agency", "Work order", "Allocated", "Done", "Balance", "% done", "First worked", "Last worked", "Note",
             "Unit price payable", "Allocated value", "Payable for work done", "Balance value"],
            reg.Drawings.SelectMany(d => d.Items.SelectMany(i => i.Agencies.Select(a => new object?[]
            {
                d.Drawing.Number, i.Item.ItemNo, i.Item.Description, i.Item.Unit, a.Agency, a.WorkOrders, a.Allocated, a.Done, a.Balance, a.Percent / 100,
                a.FirstWorked, a.LastWorked, a.Unallocated ? "Worked without allocation" : a.Excess > 0 ? $"Done {a.Excess:0.##} beyond allocation" : "",
                a.Rate, a.AllocatedValue, a.PayableValue, a.BalanceValue
            }))), percent: [10]);
        Sheet(wb, "Subcontractors", project?.Name ?? "", title,
            ["Agency", "Unit", "Allocated", "Done", "Balance", "% done", "Drawings", "Items", "Done without allocation", "Allocated value", "Payable for work done", "Balance value", "Work orders"],
            reg.ByAgency.Select(a => new object?[] { a.Agency, a.Unit, a.Allocated, a.Done, a.Balance, a.Percent / 100, a.Drawings, a.Items, a.WithoutAllocation, a.AllocatedValue, a.PayableValue, a.BalanceValue, a.WorkOrders }), percent: [6]);
        wb.SaveAs(output);
    }

    private static void Sheet(XLWorkbook wb, string name, string title, string sub, string[] headers, IEnumerable<object?[]> rows, int[] percent)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = title;
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(14);
        ws.Cell(2, 1).Value = $"Drawing register · {name} · {sub}";
        for (var c = 0; c < headers.Length; c++) ws.Cell(4, c + 1).Value = headers[c];
        ws.Range(4, 1, 4, headers.Length).Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(XLColor.FromHtml("#1F4E79")).Alignment.SetWrapText();
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
                    case double v: cell.Value = v; cell.Style.NumberFormat.Format = "#,##0.###"; break;
                    case decimal m: cell.Value = m; cell.Style.NumberFormat.Format = "#,##0.00"; break;
                    case int i: cell.Value = i; break;
                    default: cell.Value = row[c]!.ToString(); break;
                }
            }
        }
        foreach (var pc in percent) ws.Range(5, pc, Math.Max(5, r), pc).Style.NumberFormat.Format = "0.0%";
        if (r > 4) ws.Range(4, 1, r, headers.Length).SetAutoFilter();
        ws.SheetView.FreezeRows(4);
        ws.Columns(1, headers.Length).AdjustToContents(4, Math.Min(r, 300), 8, 50);
    }
}
