using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core;

public enum ReportSort { DateNewest, DateOldest, ReportNo, ManpowerHigh, ManpowerLow, Status }

/// <summary>Filter and sort options shared by the report list, PDF and Excel exports.</summary>
public class ReportFilter
{
    public string ProjectId { get; set; } = "";
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public ReportStatus? Status { get; set; }
    /// <summary>Only reports where this subcontractor had manpower or activities.</summary>
    public string SubcontractorId { get; set; } = "";
    public string Search { get; set; } = "";
    public ReportSort Sort { get; set; } = ReportSort.DateNewest;

    public string Describe()
    {
        var parts = new List<string>();
        if (From != null || To != null) parts.Add($"{From:dd-MMM-yyyy} → {To:dd-MMM-yyyy}".Trim());
        if (Status != null) parts.Add(Status.ToString()!);
        if (!string.IsNullOrWhiteSpace(Search)) parts.Add($"“{Search}”");
        return parts.Count == 0 ? "All reports" : string.Join(" · ", parts);
    }

    public IEnumerable<DailyReport> ApplySort(IEnumerable<DailyReport> q) => Sort switch
    {
        ReportSort.DateOldest => q.OrderBy(r => r.Date).ThenBy(r => r.ReportNo),
        ReportSort.ReportNo => q.OrderBy(r => r.ReportNo),
        ReportSort.ManpowerHigh => q.OrderByDescending(r => r.TotalManpower).ThenByDescending(r => r.Date),
        ReportSort.ManpowerLow => q.OrderBy(r => r.TotalManpower).ThenByDescending(r => r.Date),
        ReportSort.Status => q.OrderBy(r => r.Status).ThenByDescending(r => r.Date),
        _ => q.OrderByDescending(r => r.Date).ThenByDescending(r => r.ReportNo),
    };

    public bool InRange(DateTime d) =>
        (From == null || d.Date >= From.Value.Date) && (To == null || d.Date <= To.Value.Date);
}

/// <summary>Runs a <see cref="ReportFilter"/> against the local database.</summary>
public class ReportQueryService(LocalDatabase db)
{
    public async Task<List<DailyReport>> FindAsync(ReportFilter f)
    {
        var reports = await db.ListForProjectAsync<DailyReport>(f.ProjectId);
        IEnumerable<DailyReport> q = reports.Where(r => f.InRange(r.Date));
        if (f.Status != null) q = q.Where(r => r.Status == f.Status);

        if (!string.IsNullOrWhiteSpace(f.SubcontractorId))
        {
            var mp = await db.ListForProjectAsync<ReportManpower>(f.ProjectId, m => m.PartyId == f.SubcontractorId);
            var act = await db.ListForProjectAsync<ReportActivity>(f.ProjectId, a => a.SubcontractorId == f.SubcontractorId);
            var ids = mp.Select(m => m.ReportId).Concat(act.Select(a => a.ReportId)).ToHashSet();
            q = q.Where(r => ids.Contains(r.Id));
        }

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            var acts = await db.ListForProjectAsync<ReportActivity>(f.ProjectId);
            var hitIds = acts.Where(a => Has(a.ActivityName, s) || Has(a.Location, s) || Has(a.Remarks, s))
                             .Select(a => a.ReportId).ToHashSet();
            q = q.Where(r => hitIds.Contains(r.Id) || Has(r.Remarks, s) || Has(r.PlanForTomorrow, s) ||
                             Has(r.SafetyRemarks, s) || Has(r.PreparedBy, s) || Has(r.ToolboxTalkTopic, s) ||
                             r.ReportNo.ToString() == s);
        }
        return f.ApplySort(q).ToList();
    }

    public async Task<List<DprBundle>> LoadBundlesAsync(ReportFilter f)
    {
        var list = new List<DprBundle>();
        foreach (var r in await FindAsync(f)) list.Add(await db.LoadReportAsync(r.Id));
        return list;
    }

    private static bool Has(string? text, string s) =>
        text?.Contains(s, StringComparison.OrdinalIgnoreCase) == true;
}
