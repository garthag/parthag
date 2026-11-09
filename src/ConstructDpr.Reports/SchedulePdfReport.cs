using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ConstructDpr.Reports;

/// <summary>A3 landscape schedule report: activity table plus the Gantt chart drawn from GanttLayout.</summary>
public static class SchedulePdfReport
{
    static SchedulePdfReport() => PdfSettings.Apply();

    public static byte[] Generate(Project p, IReadOnlyList<Activity> acts, IReadOnlyList<Dependency> deps, DateOnly today)
    {
        var model = GanttLayout.Build(acts, deps, today, dayWidth: 4.2, rowHeight: 16);
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A3.Landscape());
            page.Margin(20);
            page.DefaultTextStyle(t => t.FontSize(7.5f));
            page.Header().PaddingBottom(6).Row(r =>
            {
                r.RelativeItem().Text($"{p.Code} — {p.Name} · Schedule (CPM)").FontSize(13).Bold().FontColor("#0F3D5E");
                r.ConstantItem(260).AlignRight().Text($"Data date {today:dd MMM yyyy} · forecast finish {acts.Max(a => a.EarlyFinish):dd MMM yyyy}");
            });
            page.Content().Row(row =>
            {
                row.ConstantItem(330).Table(t =>
                {
                    t.ColumnsDefinition(c => { c.ConstantColumn(44); c.RelativeColumn(); c.ConstantColumn(46); c.ConstantColumn(46); c.ConstantColumn(22); });
                    t.Header(h => { foreach (var x in new[] { "ID", "Activity", "Start", "Finish", "TF" }) h.Cell().Height(28).Background("#E6EEF4").Padding(2).AlignBottom().Text(x).Bold(); });
                    foreach (var r in model.Rows)
                    {
                        string color = r.Critical ? "#B91C1C" : "#000000";
                        foreach (var v in new[] { r.Code, r.Name, r.Start?.ToString("dd-MMM-yy") ?? "", r.Finish?.ToString("dd-MMM-yy") ?? "", r.TotalFloat?.ToString() ?? "" })
                            t.Cell().Height((float)model.RowHeight).BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten2).PaddingHorizontal(2).AlignMiddle().Text(v).FontColor(color).ClampLines(1);
                    }
                });
                row.RelativeItem().Height((float)(28 + model.Rows.Count * model.RowHeight + 2)).Svg(size => GanttSvg(model, size.Width));
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text("Blue = forecast, red = critical path, grey = baseline, dark fill = progress, diamond = milestone, orange line = data date.").FontColor(Colors.Grey.Darken1);
                r.ConstantItem(80).AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
        })).GeneratePdf();
    }

    /// <summary>The Gantt as SVG, scaled to the available width.</summary>
    private static string GanttSvg(GanttModel m, float width)
    {
        var scale = width / m.TotalWidth;
        var h = 28 + m.Rows.Count * m.RowHeight;
        var sb = new System.Text.StringBuilder();
        string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{F(width)}' height='{F(h)}' viewBox='0 0 {F(width)} {F(h)}' font-family='Arial' font-size='7'>");
        sb.Append($"<rect x='0' y='0' width='{F(width)}' height='28' fill='#E6EEF4'/>");
        foreach (var t in m.Ticks)
        {
            var x = t.X * scale;
            sb.Append($"<line x1='{F(x)}' y1='{(t.Major ? 0 : 14)}' x2='{F(x)}' y2='{F(h)}' stroke='{(t.Major ? "#9AA5B1" : "#E2E8F0")}' stroke-width='0.5'/>");
            if (t.Major) sb.Append($"<text x='{F(x + 2)}' y='10' font-weight='bold'>{t.Label}</text>");
            else if (scale * m.DayWidth * 7 > 14) sb.Append($"<text x='{F(x + 1)}' y='24' fill='#555'>{t.Label}</text>");
        }
        foreach (var b in m.Bars)
        {
            var y = 28 + b.Row * m.RowHeight;
            if (b.BaselineX is { } bx && b.BaselineWidth is { } bw)
                sb.Append($"<rect x='{F(bx * scale)}' y='{F(y + m.RowHeight - 5)}' width='{F(bw * scale)}' height='2.5' fill='#94A3B8'/>");
            if (b.Milestone)
            {
                var cx = b.X * scale; var cy = y + m.RowHeight / 2;
                sb.Append($"<polygon points='{F(cx)},{F(cy - 5)} {F(cx + 5)},{F(cy)} {F(cx)},{F(cy + 5)} {F(cx - 5)},{F(cy)}' fill='#111827'/>");
            }
            else
            {
                var fill = b.Critical ? "#DC2626" : "#2A78D6";
                sb.Append($"<rect x='{F(b.X * scale)}' y='{F(y + 3)}' width='{F(b.Width * scale)}' height='{F(m.RowHeight - 9)}' rx='1.5' fill='{fill}' fill-opacity='0.55'/>");
                if (b.ProgressFraction > 0)
                    sb.Append($"<rect x='{F(b.X * scale)}' y='{F(y + 3)}' width='{F(b.Width * scale * Math.Min(1, b.ProgressFraction))}' height='{F(m.RowHeight - 9)}' rx='1.5' fill='{fill}'/>");
            }
        }
        foreach (var l in m.Links)
            sb.Append($"<path d='M{F(l.X1 * scale)},{F(28 + l.Y1)} H{F(Math.Max(l.X1 * scale + 3, l.X2 * scale - 3))} V{F(28 + l.Y2)} H{F(l.X2 * scale)}' fill='none' stroke='{(l.Critical ? "#DC2626" : "#64748B")}' stroke-width='0.6'/>");
        if (m.TodayX is { } tx) sb.Append($"<line x1='{F(tx * scale)}' y1='0' x2='{F(tx * scale)}' y2='{F(h)}' stroke='#F59E0B' stroke-width='1'/>");
        sb.Append("</svg>");
        return sb.ToString();
    }
}
