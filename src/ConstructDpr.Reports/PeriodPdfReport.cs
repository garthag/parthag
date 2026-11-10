using System.Globalization;
using ConstructDpr.Shared.Api;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ConstructDpr.Reports;

/// <summary>Progress report for a period (A4): headline figures, quantities, manpower, equipment, materials, delays, reports.</summary>
public static class PeriodPdfReport
{
    static PeriodPdfReport() => PdfSettings.Apply();
    private const string Brand = "#0F3D5E";
    private static readonly CultureInfo In = CultureInfo.GetCultureInfo("en-IN");

    public static byte[] Generate(PeriodSummary s) => Document.Create(doc => doc.Page(page =>
    {
        page.Size(PageSizes.A4);
        page.Margin(24);
        page.DefaultTextStyle(t => t.FontSize(8.5f));
        page.Header().Background(Brand).Padding(10).Row(r =>
        {
            r.RelativeItem().Column(c =>
            {
                c.Item().Text("PROGRESS REPORT").FontSize(15).Bold().FontColor(Colors.White);
                c.Item().Text($"{s.ProjectCode} — {s.ProjectName}").FontColor(Colors.White);
                c.Item().Text($"{s.Client}{(s.Contractor is null ? "" : " · " + s.Contractor)}").FontColor("#C9D7E3");
            });
            r.ConstantItem(190).AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text($"{s.From:dd MMM yyyy} – {s.To:dd MMM yyyy}").FontSize(11).Bold().FontColor(Colors.White);
                c.Item().AlignRight().Text($"{s.ApprovedReports} approved daily reports").FontColor("#C9D7E3");
                if (s.NotYetApproved > 0) c.Item().AlignRight().Text($"{s.NotYetApproved} not yet approved (not counted)").FontColor("#F5A524");
            });
        });
        page.Content().PaddingVertical(8).Column(col =>
        {
            col.Spacing(10);
            col.Item().Row(r =>
            {
                void Kpi(string label, string value, string note)
                {
                    r.RelativeItem().Border(0.5f).BorderColor(Colors.Grey.Lighten1).Padding(6).Column(c =>
                    {
                        c.Item().Text(label).FontColor(Colors.Grey.Darken1);
                        c.Item().Text(value).FontSize(14).Bold().FontColor(Brand);
                        c.Item().Text(note).FontSize(7.5f).FontColor(Colors.Grey.Darken1);
                    });
                    r.ConstantItem(6);
                }
                var variance = s.ActualPct - s.PlannedPct;
                Kpi("Physical progress", $"{s.ActualPct:0.0}%", $"plan {s.PlannedPct:0.0}% · variance {variance:+0.0;-0.0;0.0}%");
                Kpi("Work done in period", "₹ " + Math.Round(s.ValueInPeriod).ToString("#,##0", In), "at BOQ rates");
                Kpi("Work done to date", "₹ " + Math.Round(s.ValueToDate).ToString("#,##0", In), "at BOQ rates");
                Kpi("Manpower", s.Crews.Sum(c => c.ManDays).ToString("#,##0", In) + " man-days", s.Crews.Sum(c => c.ManHours).ToString("#,##0", In) + " man-hours");
            });
            col.Item().Element(c => Section(c, "1. Quantities", b => DprPdfReport.Grid(b,
                new[] { "Activity", "Unit", "BOQ", "This period", "Cumulative", "Actual %", "Plan %", "Days worked" },
                s.Activities.Select(a => new[] { $"{a.Code} {a.Name}", a.Unit ?? "", N(a.TotalQty), N(a.InPeriod), N(a.Cumulative), N(a.ActualPct, 1), N(a.PlannedPct, 1), a.WorkingDays.ToString() }).ToArray(), firstWide: true)));
            col.Item().Element(c => Section(c, "2. Manpower", b => DprPdfReport.Grid(b,
                new[] { "Agency", "Trade", "Man-days", "Man-hours", "Peak headcount" },
                s.Crews.Select(m => new[] { m.Agency, m.Trade, m.ManDays.ToString("#,##0", In), N(m.ManHours, 0), m.PeakHeadcount.ToString() }).ToArray())));
            col.Item().Element(c => Section(c, "3. Equipment", b => DprPdfReport.Grid(b,
                new[] { "Code", "Type", "Working h", "Idle h", "Breakdown h", "Utilisation" },
                s.Equipment.Select(e => new[] { e.Code, e.Type, N(e.Working, 1), N(e.Idle, 1), N(e.Breakdown, 1), e.UtilisationPct is { } u ? $"{u:0.0}%" : "—" }).ToArray())));
            col.Item().Element(c => Section(c, "4. Materials", b => DprPdfReport.Grid(b,
                new[] { "Code", "Description", "Unit", "Received", "Issued", "Latest closing stock" },
                s.Materials.Select(m => new[] { m.Code, m.Description, m.Unit, N(m.Received), N(m.Issued), N(m.LatestClosing) }).ToArray())));
            col.Item().Element(c => Section(c, "5. Delays, breakdowns and safety", b => DprPdfReport.Grid(b,
                new[] { "Date", "Type", "Report", "Note" },
                s.Notes.Select(n => new[] { n.Date.ToString("dd MMM", CultureInfo.InvariantCulture), n.Kind, n.Number, n.Text }).ToArray())));
            col.Item().Element(c => Section(c, "6. Daily reports included", b => DprPdfReport.Grid(b,
                new[] { "Report", "Date", "Shift", "Prepared by", "Approved by" },
                s.Reports.Select(d => new[] { d.Number + (d.Revision > 0 ? $" R{d.Revision}" : ""), d.ReportDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture), d.Shift.ToString(), d.PreparedBy, d.ApprovedBy ?? "" }).ToArray())));
        });
        page.Footer().Row(r =>
        {
            r.RelativeItem().Text($"{s.ProjectCode} · progress report {s.From:dd MMM yyyy} – {s.To:dd MMM yyyy} · from approved daily reports only").FontColor(Colors.Grey.Darken1);
            r.ConstantItem(80).AlignRight().Text(t => { t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages(); });
        });
    })).GeneratePdf();

    private static void Section(IContainer c, string title, Action<IContainer> body) => c.Column(col =>
    {
        col.Item().PaddingBottom(3).Text(title).FontSize(10).Bold().FontColor(Brand);
        col.Item().Element(body);
    });

    private static string N(decimal? v, int dp = 2) => DprPdfReport.N(v, dp);
}
