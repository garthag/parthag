using System.Globalization;
using SiteNotes.Core.Models;
using SkiaSharp;

namespace SiteNotes.Core.Reports;

/// <summary>All PDF reports. <c>mediaPath</c> turns a media file name into a full local path.</summary>
public static class PdfReports
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string N(double v) => v == 0 ? "0" : v.ToString(v % 1 == 0 ? "#,0" : "#,0.##", Inv);
    public static string M(decimal v) => v.ToString("#,0.00", Inv);
    public static string P(double v) => v.ToString("0.##", Inv);
    public static string D(DateTime? d) => d?.ToString("dd-MMM-yyyy", Inv) ?? "-";

    // ================= single daily progress report =================

    public static string ReportLabel(DprBundle b) =>
        $"{(string.IsNullOrWhiteSpace(b.Project?.ReportPrefix) ? "DPR-" : b.Project!.ReportPrefix)}{b.Report.ReportNo:000}";

    public static void DailyReport(Stream output, DprBundle b, Func<string, string> mediaPath, WorkCalendar? cal = null, ReportOptions? opt = null)
    {
        opt ??= new ReportOptions();
        PdfDoc.Render(output, b.Project?.Name ?? "Daily Progress Report", $"{ReportLabel(b)} · {D(b.Report.Date)}", opt.Landscape, opt.Layout,
            pdf => WriteDailyReport(pdf, b, mediaPath, cal, opt));
    }

    public static void WriteDailyReport(PdfDoc pdf, DprBundle b, Func<string, string> mediaPath, WorkCalendar? cal = null, ReportOptions? opt = null)
    {
        opt ??= new ReportOptions();
        cal ??= new WorkCalendar();
        var cost = CostCalculator.ForReport(b, cal);
        var showCost = opt.ShowCost && cost.Total > 0;
        var r = b.Report;
        var p = b.Project;
        pdf.HeaderTitle = p?.Name ?? "Daily Progress Report";
        pdf.HeaderSubtitle = $"{ReportLabel(b)} · {D(r.Date)}";
        pdf.NewPage();

        pdf.Text(string.IsNullOrWhiteSpace(opt.ReportTitle) ? "DAILY PROGRESS REPORT" : opt.ReportTitle, 15, bold: true, color: PdfDoc.Accent, align: TextAlign.Center, spacingAfter: 2);
        pdf.Text($"{r.Date:dddd, dd MMMM yyyy} · Report No. {ReportLabel(b)} · {r.Status}", 9, color: PdfDoc.Muted,
            align: TextAlign.Center, spacingAfter: 8);

        var sub = DprCalculator.Total(b.Manpower, ManpowerSource.Subcontractor);
        var dept = DprCalculator.Total(b.Manpower, ManpowerSource.Department);
        var no = 0;
        string Head(string t) => $"{++no}. {t}";
        var activities = opt.Sort(b.Activities).ToList();
        var manpower = opt.Sort(b.Manpower).ToList();

        foreach (var section in opt.Sections.Distinct())
        {
            switch (section)
            {
                case DprSection.ProjectDetails:
                    pdf.KeyValues([
                        ("Project", p?.ToString()), ("Client", p?.Client), ("Contractor", p?.Contractor),
                        ("Consultant / PMC", p?.Consultant), ("Location", p?.Location), ("Shift", r.Shift.ToString()),
                        ("Weather (AM / PM)", $"{r.WeatherMorning} / {r.WeatherEvening}"),
                        ("Temp / Rain", $"{(r.TemperatureC is { } t ? N(t) + " °C" : "-")} / {(r.RainfallMm is { } mm ? N(mm) + " mm" : "-")}"),
                        ("Working hours", N(r.WorkingHours)),
                    ]);
                    break;

                case DprSection.Summary:
                    var tiles = new List<(string, string)>
                    {
                        ("Total manpower", N(sub + dept)), ("Subcontractor", N(sub)), ("Department", N(dept)),
                        ("Man-hours", N(b.Manpower.Sum(m => m.ManHours))), ("Machines", N(b.Machines.Sum(m => m.Count))),
                    };
                    if (showCost) tiles.Add(($"Cost ({cal.Currency})", M(cost.Total)));
                    else tiles.Add(("Open hindrances", N(b.Hindrances.Count(h => h.Status == HindranceStatus.Open))));
                    pdf.Tiles(tiles);
                    break;

                case DprSection.Activities when activities.Count > 0 || !opt.HideEmptySections:
                    pdf.Section(Head("Work progress (activities)"), $"{activities.Count(a => a.TodayQty > 0)} of {activities.Count} worked today");
                    pdf.Table([
                            new("#", 0.55f), new("Activity", 2.8f), new("Location", 1.2f), new("Agency", 1.4f), new("Unit", 0.6f),
                            new("Plan qty", 1.1f, true), new("Today", 1, true), new("To date", 1.1f, true), new("Balance", 1.1f, true),
                            new("Progress", 1.4f, false, true), new("Remarks", 1.3f)
                        ],
                        activities.Select((a, i) => new[]
                        {
                            (i + 1).ToString(), a.ActivityName, a.Location, a.SubcontractorName, a.Unit, a.PlannedQty > 0 ? N(a.PlannedQty) : "-",
                            N(a.TodayQty), N(a.CumulativeQty), a.PlannedQty > 0 ? N(Math.Max(0, a.PlannedQty - a.CumulativeQty)) : "-",
                            a.PlannedQty > 0 ? P(Math.Min(100, a.PercentComplete)) : "-", a.Remarks
                        }));
                    break;

                case DprSection.Manpower when manpower.Count > 0 || !opt.HideEmptySections:
                    pdf.Section(Head("Manpower"), $"Total {sub + dept} nos · {N(b.Manpower.Sum(m => m.ManHours))} man-hrs");
                    pdf.Table(showCost
                            ? [new("#", 0.55f), new("Type", 1f), new("Subcontractor / Department", 2.4f), new("Trade", 1.4f), new("Activity", 2f),
                               new("Nos", 0.7f, true), new("Hrs", 0.6f, true), new("Man-hrs", 0.9f, true), new("Cost", 1.2f, true)]
                            : [new("#", 0.55f), new("Type", 1f), new("Subcontractor / Department", 2.6f), new("Trade", 1.6f), new("Activity", 2.2f),
                               new("Nos", 0.7f, true), new("Hrs", 0.6f, true), new("Man-hrs", 0.9f, true)],
                        manpower.Select((m, i) => new[]
                        {
                            (i + 1).ToString(), m.Source == ManpowerSource.Subcontractor ? "Subcon." : "Dept.", m.PartyName, m.Trade, m.ActivityName,
                            N(m.Count), N(m.Hours), N(m.ManHours), m.Rate > 0 ? M(CostCalculator.Cost(m, cal)) : "-"
                        }),
                        ["", "", "TOTAL", "", "", N(sub + dept), "", N(b.Manpower.Sum(m => m.ManHours)), showCost ? M(cost.Manpower) : ""]);
                    break;

                case DprSection.ManpowerSummary:
                    var bySub = DprCalculator.ByParty(b.Manpower, ManpowerSource.Subcontractor);
                    var byDept = DprCalculator.ByParty(b.Manpower, ManpowerSource.Department);
                    if (bySub.Count + byDept.Count == 0) break;
                    pdf.Table([new("Manpower by agency / department", 3), new("Type", 1.2f), new("Nos", 0.8f, true), new("Man-hrs", 1, true)],
                        bySub.Select(g => new[] { g.Name, "Subcontractor", N(g.Count), N(g.ManHours) })
                            .Concat(byDept.Select(g => new[] { g.Name, "Department", N(g.Count), N(g.ManHours) })),
                        ["TOTAL", "", N(sub + dept), N(b.Manpower.Sum(m => m.ManHours))]);
                    break;

                case DprSection.Machinery when b.Machines.Count > 0 || !opt.HideEmptySections:
                    pdf.Section(Head("Machinery & equipment"), $"{b.Machines.Sum(m => m.Count)} nos");
                    pdf.Table([
                            new("#", 0.55f), new("Machine / equipment", 2.2f), new("Own / Hired", 0.9f), new("Activity", 1.8f), new("Nos", 0.6f, true),
                            new("Working hrs", 0.9f, true), new("Idle hrs", 0.8f, true), new("Fuel (L)", 0.8f, true), new("Cost", showCost ? 1.1f : 0.01f, true)
                        ],
                        b.Machines.Select((m, i) => new[]
                        {
                            (i + 1).ToString(), m.Name, m.Ownership.ToString(), m.ActivityName, N(m.Count), N(m.WorkingHours), N(m.IdleHours),
                            N(m.FuelLitres), showCost && m.Rate > 0 ? M(CostCalculator.Cost(m, cal)) : ""
                        }),
                        ["", "TOTAL", "", "", N(b.Machines.Sum(m => m.Count)), N(b.Machines.Sum(m => m.WorkingHours)),
                         N(b.Machines.Sum(m => m.IdleHours)), N(b.Machines.Sum(m => m.FuelLitres)), showCost ? M(cost.Machinery) : ""]);
                    break;

                case DprSection.Materials when b.Materials.Count > 0 || !opt.HideEmptySections:
                    pdf.Section(Head("Materials & resources used"));
                    pdf.Table([
                            new("#", 0.55f), new("Material / resource", 2.4f), new("Type", 0.9f), new("Activity", 1.8f), new("Unit", 0.6f),
                            new("Received", 0.9f, true), new("Consumed", 0.9f, true), new("Cost", showCost ? 1.1f : 0.01f, true)
                        ],
                        b.Materials.Select((m, i) => new[]
                        {
                            (i + 1).ToString(), m.Material + (string.IsNullOrWhiteSpace(m.Remarks) ? "" : $" ({m.Remarks})"),
                            m.Kind == ResourceType.Other ? "Non-material" : "Material", m.ActivityName, m.Unit, N(m.Received), N(m.Consumed),
                            showCost && m.Rate > 0 ? M(CostCalculator.Cost(m)) : ""
                        }),
                        showCost ? ["", "TOTAL", "", "", "", "", "", M(cost.Material + cost.Other)] : null);
                    break;

                case DprSection.Cost when showCost:
                    pdf.Section(Head("Cost of the day"), $"{cal.Currency} {M(cost.Total)}");
                    pdf.Tiles([
                        ($"Manpower ({cal.Currency})", M(cost.Manpower)), ($"Machinery ({cal.Currency})", M(cost.Machinery)),
                        ($"Material ({cal.Currency})", M(cost.Material)), ($"Non-material ({cal.Currency})", M(cost.Other)),
                        ($"Total ({cal.Currency})", M(cost.Total)),
                    ]);
                    pdf.Table([new("Activity", 3), new("Man-hrs", 0.9f, true), new("Done today", 1.2f, true), new("Cost", 1.2f, true),
                        new("Cost / unit", 1.1f, true), new("Cost / hour", 1.1f, true)], CostByActivity(b, cal));
                    break;

                case DprSection.Hindrances when b.Hindrances.Count > 0 || !opt.HideEmptySections:
                    pdf.Section(Head("Hindrances"), $"{N(b.Hindrances.Sum(h => h.HoursLost))} hrs lost");
                    HindranceTable(pdf, b.Hindrances);
                    break;

                case DprSection.Requirements when b.Requirements.Count > 0 || !opt.HideEmptySections:
                    pdf.Section(Head("Requirements"));
                    RequirementTable(pdf, b.Requirements);
                    break;

                case DprSection.Safety when !opt.HideEmptySections || r.ToolboxTalkTopic != "" || r.SafetyRemarks != "" || r.Visitors != "":
                    pdf.Section(Head("Safety & toolbox talk"));
                    pdf.KeyValues([
                        ("Toolbox talk topic", r.ToolboxTalkTopic), ("Attendees", r.ToolboxTalkAttendees.ToString()),
                        ("Visitors", r.Visitors),
                    ]);
                    pdf.Paragraph(r.SafetyRemarks);
                    break;

                case DprSection.PlanRemarks when !opt.HideEmptySections || r.PlanForTomorrow != "" || r.Remarks != "" || opt.ExtraRemarks != "":
                    pdf.Section(Head("Plan for tomorrow & remarks"));
                    if (!string.IsNullOrWhiteSpace(r.PlanForTomorrow))
                    {
                        pdf.Text("Plan for tomorrow", 9, bold: true, spacingAfter: 1);
                        pdf.Paragraph(r.PlanForTomorrow);
                    }
                    if (!string.IsNullOrWhiteSpace(r.Remarks))
                    {
                        pdf.Text("Remarks", 9, bold: true, spacingAfter: 1);
                        pdf.Paragraph(r.Remarks);
                    }
                    if (!string.IsNullOrWhiteSpace(opt.ExtraRemarks))
                    {
                        pdf.Text("Note", 9, bold: true, spacingAfter: 1);
                        pdf.Paragraph(opt.ExtraRemarks);
                    }
                    break;

                case DprSection.Photos:
                    var photos = b.Media.Where(m => m.Kind is MediaKind.Photo or MediaKind.ScanPage).ToList();
                    if (opt.MaxPhotos > 0) photos = photos.Take(opt.MaxPhotos).ToList();
                    var videos = b.Media.Where(m => m.Kind == MediaKind.Video).ToList();
                    if (photos.Count > 0)
                    {
                        pdf.Section(Head("Site photographs"), $"{photos.Count} photos");
                        var per = Math.Clamp(opt.PhotosPerRow, 1, 4);
                        pdf.ImageGrid(photos.Select(m => (mediaPath(m.FileName), Caption(m))), per, per switch { 1 => 300, 2 => 170, 3 => 120, _ => 90 });
                    }
                    if (videos.Count > 0)
                    {
                        pdf.Section("Videos");
                        pdf.Table([new("#", 0.55f), new("File", 2.5f), new("Caption", 3), new("Recorded", 1.5f)],
                            videos.Select((v, i) => new[] { (i + 1).ToString(), v.FileName, v.Caption, v.CapturedAtUtc.ToLocalTime().ToString("dd-MMM HH:mm") }));
                    }
                    break;

                case DprSection.Signatures:
                    pdf.Signatures(("Prepared by", r.PreparedBy), ("Checked by", r.CheckedBy), ("Approved by", r.ApprovedBy));
                    break;
            }
        }
    }

    /// <summary>Cost of the day per activity (rows booked to no activity = General / overheads).</summary>
    public static IEnumerable<string[]> CostByActivity(DprBundle b, WorkCalendar cal)
    {
        string Key(string id, string name) => id != "" ? id : string.IsNullOrWhiteSpace(name) ? "" : "name:" + name;
        var names = b.Activities.GroupBy(a => Key(a.ActivityId, a.ActivityName)).ToDictionary(g => g.Key, g => g.First());
        return b.Manpower.Select(m => (K: Key(m.ActivityId, m.ActivityName), N: m.ActivityName, Cost: CostCalculator.Cost(m, cal), Mh: m.ManHours))
            .Concat(b.Machines.Select(m => (K: Key(m.ActivityId, m.ActivityName), N: m.ActivityName, Cost: CostCalculator.Cost(m, cal), Mh: 0.0)))
            .Concat(b.Materials.Select(m => (K: Key(m.ActivityId, m.ActivityName), N: m.ActivityName, Cost: CostCalculator.Cost(m), Mh: 0.0)))
            .GroupBy(x => x.K).OrderBy(g => g.Key == "")
            .Select(g =>
            {
                var act = names.GetValueOrDefault(g.Key);
                var label = g.Key == "" ? "General / overheads" : act?.ActivityName ?? g.FirstOrDefault(x => x.N != "").N ?? "Activity";
                var done = act?.TodayQty ?? 0;
                var unit = act?.Unit ?? "";
                var total = g.Sum(x => x.Cost);
                return new[] { label, N(g.Sum(x => x.Mh)), done > 0 ? $"{N(done)} {unit}" : "-", M(total),
                    done > 0 ? M(CostCalculator.Round(total / (decimal)done)) : "-",
                    cal.HoursPerDay > 0 ? M(CostCalculator.Round(total / (decimal)cal.HoursPerDay)) : "-" };
            });
    }

    private static string Caption(MediaItem m)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Caption)) parts.Add(m.Caption);
        parts.Add(m.CapturedAtUtc.ToLocalTime().ToString("dd-MMM-yyyy HH:mm"));
        if (m.Latitude is { } la && m.Longitude is { } lo) parts.Add($"{la:0.00000}, {lo:0.00000}");
        if (!string.IsNullOrWhiteSpace(m.Remarks)) parts.Add(m.Remarks);
        return string.Join(" · ", parts);
    }

    private static void HindranceTable(PdfDoc pdf, IEnumerable<Hindrance> list) =>
        pdf.Table([
                new("No.", 0.5f), new("From", 1.35f), new("To", 1.35f), new("Category", 1.3f), new("Description", 2.8f),
                new("Responsible", 1.3f), new("Hrs lost", 0.8f, true), new("Status", 0.9f), new("Action taken", 2.2f)
            ],
            list.OrderBy(h => h.SerialNo).Select(h => new[]
            {
                h.SerialNo.ToString(), D(h.FromDate), D(h.ToDate), h.Category, h.Description +
                (string.IsNullOrWhiteSpace(h.AffectedActivity) ? "" : $" (Affects: {h.AffectedActivity})"),
                h.ResponsibleParty, N(h.HoursLost), h.Status.ToString(), h.ActionTaken
            }));

    private static void RequirementTable(PdfDoc pdf, IEnumerable<Requirement> list) =>
        pdf.Table([
                new("No.", 0.5f), new("Raised", 1.35f), new("Category", 1.1f), new("Item / description", 2.8f),
                new("Qty", 0.7f, true), new("Unit", 0.6f), new("Required by", 1.35f), new("Priority", 0.9f),
                new("Status", 1), new("Remarks", 1.8f)
            ],
            list.OrderBy(q => q.SerialNo).Select(q => new[]
            {
                q.SerialNo.ToString(), D(q.RaisedDate), q.Category, q.Item, N(q.Quantity), q.Unit,
                D(q.RequiredBy) + (q.IsOverdue ? " (overdue)" : ""), q.Priority.ToString(), q.Status.ToString(), q.Remarks
            }));

    // ================= multi-report register =================

    /// <summary>Summary table of the filtered reports, optionally followed by every full report.</summary>
    public static void ReportRegister(Stream output, Project? project, IReadOnlyList<DprBundle> bundles,
        string filterText, bool includeFullReports, Func<string, string> mediaPath, WorkCalendar? cal = null, ReportOptions? opt = null)
    {
        opt ??= new ReportOptions();
        PdfDoc.Render(output, project?.Name ?? "Daily Progress Reports", $"DPR register · {filterText}", true, opt.Layout,
            pdf => WriteRegister(pdf, project, bundles, filterText, includeFullReports, mediaPath, cal, opt));
    }

    private static void WriteRegister(PdfDoc pdf, Project? project, IReadOnlyList<DprBundle> bundles,
        string filterText, bool includeFullReports, Func<string, string> mediaPath, WorkCalendar? cal, ReportOptions opt)
    {
        cal ??= new WorkCalendar();
        var showCost = opt.ShowCost && bundles.Any(b => b.Report.TotalCost > 0);
        pdf.NewPage();
        pdf.Text("DAILY PROGRESS REPORT REGISTER", 14, bold: true, color: PdfDoc.Accent, spacingAfter: 2);
        pdf.Text(filterText, 9, color: PdfDoc.Muted, spacingAfter: 8);

        var all = bundles.SelectMany(b => b.Manpower).ToList();
        pdf.Tiles([
            ("Reports", bundles.Count.ToString()),
            ("Man-days", N(all.Sum(m => m.Count))),
            ("Man-hours", N(all.Sum(m => m.ManHours))),
            ("Avg manpower / day", bundles.Count == 0 ? "0" : N(Math.Round(all.Sum(m => m.Count) / (double)bundles.Count, 1))),
            ("Machine-hours", N(bundles.SelectMany(b => b.Machines).Sum(m => m.WorkingHours * Math.Max(1, m.Count)))),
            ("Hours lost", N(bundles.SelectMany(b => b.Hindrances).Sum(h => h.HoursLost))),
        ]);

        pdf.Table([
                new("No.", 0.8f), new("Date", 1.1f), new("Status", 0.9f), new("Weather", 1.1f),
                new("Subcon.", 0.8f, true), new("Dept.", 0.8f, true), new("Total", 0.8f, true), new("Man-hrs", 0.9f, true),
                new("Machines", 0.9f, true), new("Activities", 0.9f, true), new("Hindr.", 0.7f, true),
                new("Prepared by", 1.2f), new($"Cost ({cal.Currency})", showCost ? 1.2f : 0.01f, true), new("Remarks", showCost ? 2.2f : 3)
            ],
            bundles.Select(b => new[]
            {
                ReportLabel(b), D(b.Report.Date), b.Report.Status.ToString(), b.Report.WeatherMorning,
                N(DprCalculator.Total(b.Manpower, ManpowerSource.Subcontractor)),
                N(DprCalculator.Total(b.Manpower, ManpowerSource.Department)),
                N(b.Manpower.Sum(m => m.Count)), N(b.Manpower.Sum(m => m.ManHours)), N(b.Machines.Sum(m => m.Count)),
                b.Activities.Count.ToString(), b.Hindrances.Count.ToString(), b.Report.PreparedBy,
                showCost ? M(CostCalculator.ForReport(b, cal).Total) : "", b.Report.Remarks
            }),
            ["", "TOTAL", "", "",
             N(bundles.Sum(b => DprCalculator.Total(b.Manpower, ManpowerSource.Subcontractor))),
             N(bundles.Sum(b => DprCalculator.Total(b.Manpower, ManpowerSource.Department))),
             N(all.Sum(m => m.Count)), N(all.Sum(m => m.ManHours)), "", "", "", "",
             showCost ? M(bundles.Sum(b => CostCalculator.ForReport(b, cal).Total)) : "", ""]);

        ManpowerSummaryTables(pdf, bundles);

        if (!includeFullReports) return;
        if (!string.IsNullOrWhiteSpace(opt.ExtraRemarks)) { pdf.Section("Note"); pdf.Paragraph(opt.ExtraRemarks); }
        foreach (var b in bundles) WriteDailyReport(pdf, b, mediaPath, cal, opt);
    }

    /// <summary>Manpower by agency, department and trade for a period.</summary>
    public static void ManpowerSummary(Stream output, Project? project, IReadOnlyList<DprBundle> bundles, string filterText, ReportOptions? opt = null)
    {
        PdfDoc.Render(output, project?.Name ?? "Manpower", $"Manpower summary · {filterText}", false, opt?.Layout, pdf =>
        {
            pdf.NewPage();
            pdf.Text("MANPOWER SUMMARY", 14, bold: true, color: PdfDoc.Accent, spacingAfter: 2);
            pdf.Text(filterText, 9, color: PdfDoc.Muted, spacingAfter: 8);

            pdf.Section("Day-wise manpower");
            pdf.Table([new("Date", 1.2f), new("DPR", 0.6f, true), new("Subcontractor", 1, true), new("Department", 1, true),
                    new("Total", 1, true), new("Man-hours", 1, true)],
                bundles.OrderBy(b => b.Report.Date).Select(b => new[]
                {
                    D(b.Report.Date), b.Report.ReportNo.ToString(),
                    N(DprCalculator.Total(b.Manpower, ManpowerSource.Subcontractor)),
                    N(DprCalculator.Total(b.Manpower, ManpowerSource.Department)),
                    N(b.Manpower.Sum(m => m.Count)), N(b.Manpower.Sum(m => m.ManHours))
                }),
                ["TOTAL", "", N(bundles.Sum(b => DprCalculator.Total(b.Manpower, ManpowerSource.Subcontractor))),
                 N(bundles.Sum(b => DprCalculator.Total(b.Manpower, ManpowerSource.Department))),
                 N(bundles.Sum(b => b.Manpower.Sum(m => m.Count))), N(bundles.Sum(b => b.Manpower.Sum(m => m.ManHours)))]);
            ManpowerSummaryTables(pdf, bundles);
        });
    }

    private static void ManpowerSummaryTables(PdfDoc pdf, IReadOnlyList<DprBundle> bundles)
    {
        var all = bundles.SelectMany(b => b.Manpower).ToList();
        var days = Math.Max(1, bundles.Count);
        pdf.Section("Subcontractor-wise man-days");
        pdf.Table([new("Subcontractor", 3), new("Man-days", 1, true), new("Man-hours", 1, true), new("Avg / day", 1, true)],
            DprCalculator.ByParty(all, ManpowerSource.Subcontractor)
                .Select(g => new[] { g.Name, N(g.Count), N(g.ManHours), N(Math.Round(g.Count / (double)days, 1)) }));
        pdf.Section("Department-wise man-days");
        pdf.Table([new("Department", 3), new("Man-days", 1, true), new("Man-hours", 1, true), new("Avg / day", 1, true)],
            DprCalculator.ByParty(all, ManpowerSource.Department)
                .Select(g => new[] { g.Name, N(g.Count), N(g.ManHours), N(Math.Round(g.Count / (double)days, 1)) }));
        pdf.Section("Trade-wise man-days");
        pdf.Table([new("Trade", 3), new("Man-days", 1, true), new("Man-hours", 1, true), new("Avg / day", 1, true)],
            DprCalculator.ByTrade(all).Select(g => new[] { g.Name, N(g.Count), N(g.ManHours), N(Math.Round(g.Count / (double)days, 1)) }));
    }

    // ================= registers =================

    public static void HindranceRegister(Stream output, Project? project, IReadOnlyList<Hindrance> list, string filterText, ReportOptions? opt = null)
    {
        PdfDoc.Render(output, project?.Name ?? "Hindrance register", $"Hindrance register · {filterText}", true, opt?.Layout, pdf =>
        {
            pdf.NewPage();
            pdf.Text("HINDRANCE REGISTER", 14, bold: true, color: PdfDoc.Accent, spacingAfter: 2);
            pdf.Text(filterText, 9, color: PdfDoc.Muted, spacingAfter: 8);
            pdf.Tiles([
                ("Total", list.Count.ToString()), ("Open", list.Count(h => h.Status == HindranceStatus.Open).ToString()),
                ("Resolved", list.Count(h => h.Status == HindranceStatus.Resolved).ToString()),
                ("Hours lost", N(list.Sum(h => h.HoursLost))),
            ]);
            HindranceTable(pdf, list);
            pdf.Section("Hours lost by responsibility");
            pdf.Table([new("Responsible party", 3), new("Entries", 1, true), new("Hours lost", 1, true)],
                list.GroupBy(h => h.ResponsibleParty).OrderByDescending(g => g.Sum(h => h.HoursLost))
                    .Select(g => new[] { g.Key, g.Count().ToString(), N(g.Sum(h => h.HoursLost)) }));
        });
    }

    public static void RequirementRegister(Stream output, Project? project, IReadOnlyList<Requirement> list, string filterText, ReportOptions? opt = null)
    {
        PdfDoc.Render(output, project?.Name ?? "Requirement register", $"Requirement register · {filterText}", true, opt?.Layout, pdf =>
        {
            pdf.NewPage();
            pdf.Text("REQUIREMENT REGISTER", 14, bold: true, color: PdfDoc.Accent, spacingAfter: 2);
            pdf.Text(filterText, 9, color: PdfDoc.Muted, spacingAfter: 8);
            pdf.Tiles([
                ("Total", list.Count.ToString()),
                ("Pending", list.Count(q => q.Status is RequirementStatus.Pending or RequirementStatus.Approved).ToString()),
                ("Ordered", list.Count(q => q.Status == RequirementStatus.Ordered).ToString()),
                ("Received", list.Count(q => q.Status == RequirementStatus.Received).ToString()),
                ("Overdue", list.Count(q => q.IsOverdue).ToString()),
            ]);
            RequirementTable(pdf, list);
        });
    }

    // ================= notes & scans =================

    public static void Note(Stream output, Note note, IReadOnlyList<NoteBlock> blocks, string folderName,
        Func<string, MediaItem?> media, Func<string, string> mediaPath, ReportOptions? opt = null)
    {
        PdfDoc.Render(output, string.IsNullOrWhiteSpace(note.Title) ? "Note" : note.Title,
            $"{folderName} · {note.UpdatedAtUtc.ToLocalTime():dd-MMM-yyyy HH:mm}", false, opt?.Layout, pdf =>
        {
            pdf.NewPage();
            pdf.Text(string.IsNullOrWhiteSpace(note.Title) ? "Untitled note" : note.Title, 18, bold: true, spacingAfter: 2);
            var meta = $"Updated {note.UpdatedAtUtc.ToLocalTime():dd-MMM-yyyy HH:mm}" +
                       (string.IsNullOrWhiteSpace(note.UpdatedBy) ? "" : $" by {note.UpdatedBy}") +
                       (string.IsNullOrWhiteSpace(note.Tags) ? "" : $" · Tags: {note.Tags}");
            pdf.Text(meta, 8.5f, color: PdfDoc.Muted, spacingAfter: 6);
            pdf.Divider();

            for (var i = 0; i < blocks.Count; i++)
            {
                var b = blocks[i];
                var s = b.Style;
                var indent = b.Indent * 16f;
                switch (b.Type)
                {
                    case BlockType.Divider:
                        pdf.Divider();
                        continue;
                    case BlockType.Image:
                    case BlockType.Scan:
                    {
                        var m = media(b.MediaId);
                        if (m != null) pdf.Image(mediaPath(m.FileName), 320, string.IsNullOrWhiteSpace(b.Text) ? m.Caption : b.Text);
                        else pdf.Placeholder($"[{b.Type}: {b.Text}]");
                        continue;
                    }
                    case BlockType.Video:
                    {
                        var m = media(b.MediaId);
                        pdf.Placeholder($"▶ Video: {(string.IsNullOrWhiteSpace(b.Text) ? m?.FileName : b.Text)}");
                        continue;
                    }
                }

                var size = (float)b.EffectiveSize * 0.72f; // screen px → pt
                var bold = s.Bold || b.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3;
                var color = ParseColor(s.Color) ?? (b.Type == BlockType.Quote ? PdfDoc.Muted : PdfDoc.Ink);
                var strike = s.Strikethrough || (b.Type == BlockType.Checklist && b.Checked);
                if (b.Type == BlockType.Checklist && b.Checked) color = PdfDoc.Muted;
                var marker = b.Type == BlockType.Checklist ? (b.Checked ? "[x]" : "[  ]") : NoteDocument.Marker(blocks, i);
                if (b.Type == BlockType.Arrow) marker = "→";
                if (b.Type is BlockType.Heading1 or BlockType.Heading2) pdf.Space(4);
                pdf.Text(b.Text, size, bold, s.Italic || b.Type == BlockType.Quote, color, s.FontFamily, s.Underline, strike,
                    s.Align, indent + (b.Type == BlockType.Quote ? 10 : 0), marker, ParseColor(s.Highlight),
                    b.Type is BlockType.Heading1 or BlockType.Heading2 ? 6 : 3);
            }
        });
    }

    /// <summary>Scanned pages, one image per A4 page.</summary>
    public static void ScanPages(Stream output, string title, IEnumerable<string> pagePaths)
    {
        using var pdf = new PdfDoc(output, title);
        foreach (var p in pagePaths) pdf.FullPageImage(p);
    }

    private static SKColor? ParseColor(string? hex) =>
        !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var c) ? c : null;
}
