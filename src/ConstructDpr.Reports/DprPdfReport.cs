using ConstructDpr.Shared.Model;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ConstructDpr.Reports;

/// <summary>Printable daily progress report (A4) built with QuestPDF.</summary>
public static class DprPdfReport
{
    static DprPdfReport() => PdfSettings.Apply();

    private static readonly string Brand = "#0F3D5E";

    public static byte[] Generate(Dpr d, Project p)
    {
        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(24);
            page.DefaultTextStyle(t => t.FontSize(8.5f));
            page.Header().Element(h => Header(h, d, p));
            page.Content().PaddingVertical(8).Column(col =>
            {
                col.Spacing(10);
                col.Item().Element(c => Section(c, "1. Report details", t =>
                    Grid(t, new[] { "DPR no.", "Revision", "Date", "Shift", "Weather", "Site conditions" },
                        new[] { new[] { d.Number, d.Revision.ToString(), d.ReportDate.ToString("dd MMM yyyy"), d.Shift.ToString(), d.Weather ?? "—", d.SiteConditions ?? "—" } })));
                col.Item().Element(c => Section(c, "2. Activity progress", t =>
                    Grid(t, new[] { "Activity", "Unit", "Approved", "Previous", "Today", "Accepted", "Cum.", "Balance", "Plan %", "Actual %" },
                        d.Activities.Select(l => new[]
                        {
                            $"{l.Activity?.Code} {l.Activity?.Name}", l.Activity?.Unit ?? "", N(l.Activity?.TotalQty), N(l.PrevCum), N(l.QtyToday), N(l.QtyAccepted),
                            N(l.CumQty), N(l.Balance), N(l.PlannedPct, 1), N(l.ActualPct, 1),
                        }).ToArray(), firstWide: true)));
                col.Item().Element(c => Section(c, $"3. Manpower — {d.Manpower.Sum(m => m.Headcount)} people, {d.Manpower.Sum(m => m.ManHours):0} man-hours", t =>
                    Grid(t, new[] { "Agency", "Trade", "Skilled", "Semi-skilled", "Unskilled", "Supervisors", "Total", "Man-hours" },
                        d.Manpower.Select(m => new[] { m.Agency, m.Trade, m.Skilled.ToString(), m.SemiSkilled.ToString(), m.Unskilled.ToString(), m.Supervisors.ToString(), m.Headcount.ToString(), N(m.ManHours, 0) }).ToArray())));
                col.Item().Element(c => Section(c, "4. Equipment", t =>
                    Grid(t, new[] { "Code", "Type", "Working h", "Idle h", "Breakdown h", "Utilisation", "Breakdown reason" },
                        d.Equipment.Select(e => new[] { e.Code, e.Type, N(e.WorkingHours, 1), N(e.IdleHours, 1), N(e.BreakdownHours, 1), e.UtilisationPct is { } u ? $"{u:0.0}%" : "—", e.BreakdownReason ?? "" }).ToArray())));
                col.Item().Element(c => Section(c, "5. Materials", t =>
                    Grid(t, new[] { "Code", "Description", "Unit", "Opening", "Received", "Issued", "Closing", "Challan" },
                        d.Materials.Select(m => new[] { m.Code, m.Description, m.Unit, N(m.Opening), N(m.Received), N(m.Issued), N(m.Closing), m.ChallanNo ?? "" }).ToArray())));
                col.Item().Element(c => Section(c, "6. Narrative", t => t.Column(n =>
                {
                    void Para(string label, string? text) { if (!string.IsNullOrWhiteSpace(text)) n.Item().Text(x => { x.Span(label + ": ").Bold(); x.Span(text); }); }
                    Para("Work completed", d.WorkCompleted);
                    Para("Delays", d.DelayReasons);
                    Para("Safety", d.SafetyNotes);
                    Para("Tomorrow's plan", d.TomorrowPlan);
                    Para("Review comment", d.ReviewComment);
                })));
                col.Item().PaddingTop(16).Row(r =>
                {
                    void Sign(string role, string? name, DateTime? at) => r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Medium).Padding(6).Column(s =>
                    {
                        s.Item().Text(role).FontColor(Colors.Grey.Darken1);
                        s.Item().PaddingTop(14).Text(name ?? "—").Bold();
                        s.Item().Text(at?.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? "");
                    });
                    Sign("Prepared by", d.PreparedBy, d.CreatedAt);
                    r.ConstantItem(8);
                    Sign("Approved by", d.ApprovedBy, d.ApprovedAt);
                });
            });
            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"{p.Code} · {d.Number} R{d.Revision} · {d.Status}" + (d.SnapshotHash is null ? "" : $" · SHA-256 {d.SnapshotHash[..16]}…")).FontColor(Colors.Grey.Darken1);
                r.ConstantItem(80).AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
            });
        })).GeneratePdf();
    }

    private static void Header(IContainer c, Dpr d, Project p) => c.Background(Brand).Padding(10).Row(r =>
    {
        r.RelativeItem().Column(col =>
        {
            col.Item().Text("DAILY PROGRESS REPORT").FontSize(14).Bold().FontColor(Colors.White);
            col.Item().Text($"{p.Code} — {p.Name}").FontColor(Colors.White);
            col.Item().Text($"Client: {p.Client ?? "—"} · Work order: {p.WorkOrderNo ?? "—"}").FontColor(Colors.Grey.Lighten2);
        });
        r.ConstantItem(150).AlignRight().Column(col =>
        {
            col.Item().AlignRight().Text(d.Number).FontSize(12).Bold().FontColor(Colors.White);
            col.Item().AlignRight().Text(d.ReportDate.ToString("dddd, dd MMM yyyy")).FontColor(Colors.White);
            col.Item().AlignRight().Text(d.Status.ToString().ToUpperInvariant()).Bold().FontColor(d.Status == DprStatus.Approved ? "#7CE0A3" : "#F5A524");
        });
    });

    private static void Section(IContainer c, string title, Action<IContainer> body) => c.Column(col =>
    {
        col.Item().PaddingBottom(3).Text(title).FontSize(10).Bold().FontColor(Brand);
        col.Item().Element(body);
    });

    internal static void Grid(IContainer c, string[] head, string[][] rows, bool firstWide = false)
    {
        if (rows.Length == 0) { c.Text("None recorded.").Italic().FontColor(Colors.Grey.Darken1); return; }
        c.Table(t =>
        {
            t.ColumnsDefinition(cd => { for (var i = 0; i < head.Length; i++) { if (i == 0 && firstWide) cd.RelativeColumn(3.2f); else cd.RelativeColumn(); } });
            t.Header(h => { foreach (var x in head) h.Cell().Background("#E6EEF4").Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(3).Text(x).Bold(); });
            foreach (var row in rows)
                for (var i = 0; i < row.Length; i++)
                {
                    var cell = t.Cell().Border(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(3);
                    if (i > 0 && decimal.TryParse(row[i].TrimEnd('%'), out _)) cell.AlignRight().Text(row[i]); else cell.Text(row[i]);
                }
        });
    }

    internal static string N(decimal? v, int dp = 3) => v is null ? "—" : Math.Round(v.Value, dp).ToString(dp == 0 ? "#,##0" : "#,##0." + new string('#', dp));
}
