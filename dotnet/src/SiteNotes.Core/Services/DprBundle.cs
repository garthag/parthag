using SiteNotes.Core.Models;

namespace SiteNotes.Core;

/// <summary>A daily progress report with all of its rows.</summary>
public class DprBundle
{
    public DailyReport Report { get; set; } = new();
    public Project? Project { get; set; }
    public List<ReportActivity> Activities { get; set; } = [];
    public List<ReportManpower> Manpower { get; set; } = [];
    public List<ReportMachine> Machines { get; set; } = [];
    public List<ReportMaterial> Materials { get; set; } = [];
    public List<Hindrance> Hindrances { get; set; } = [];
    public List<Requirement> Requirements { get; set; } = [];
    public List<MediaItem> Media { get; set; } = [];
}

public record ManpowerGroup(string Name, int Count, double ManHours);

public static class DprCalculator
{
    public static void UpdateTotals(DprBundle b)
    {
        var r = b.Report;
        r.TotalManpower = b.Manpower.Sum(m => m.Count);
        r.TotalManHours = b.Manpower.Sum(m => m.ManHours);
        r.TotalMachines = b.Machines.Sum(m => m.Count);
        r.ActivityCount = b.Activities.Count;
        r.HindranceCount = b.Hindrances.Count;
    }

    public static int Total(IEnumerable<ReportManpower> rows, ManpowerSource? source = null) =>
        rows.Where(m => source == null || m.Source == source).Sum(m => m.Count);

    /// <summary>Manpower grouped by subcontractor or department, biggest first.</summary>
    public static List<ManpowerGroup> ByParty(IEnumerable<ReportManpower> rows, ManpowerSource source) =>
        rows.Where(m => m.Source == source)
            .GroupBy(m => string.IsNullOrWhiteSpace(m.PartyName) ? "(unassigned)" : m.PartyName)
            .Select(g => new ManpowerGroup(g.Key, g.Sum(m => m.Count), g.Sum(m => m.ManHours)))
            .OrderByDescending(g => g.Count).ToList();

    public static List<ManpowerGroup> ByTrade(IEnumerable<ReportManpower> rows) =>
        rows.GroupBy(m => string.IsNullOrWhiteSpace(m.Trade) ? "General" : m.Trade)
            .Select(g => new ManpowerGroup(g.Key, g.Sum(m => m.Count), g.Sum(m => m.ManHours)))
            .OrderByDescending(g => g.Count).ToList();

    /// <summary>Keeps cumulative = previous cumulative + today when today's quantity is edited.</summary>
    public static void ApplyTodayQty(ReportActivity a, double oldToday, double newToday)
    {
        a.CumulativeQty = Math.Max(0, a.CumulativeQty - oldToday + newToday);
        a.TodayQty = newToday;
    }
}
