using SiteNotes.Core.Models;

namespace SiteNotes.Core.Data;

/// <summary>Every table that syncs. The name is what goes over the wire and into the server's SQL.</summary>
public static class EntityRegistry
{
    public static readonly IReadOnlyDictionary<string, Type> Types = new Dictionary<string, Type>
    {
        [nameof(Project)] = typeof(Project),
        [nameof(Subcontractor)] = typeof(Subcontractor),
        [nameof(Department)] = typeof(Department),
        [nameof(Activity)] = typeof(Activity),
        [nameof(DailyReport)] = typeof(DailyReport),
        [nameof(ReportActivity)] = typeof(ReportActivity),
        [nameof(ReportManpower)] = typeof(ReportManpower),
        [nameof(ReportMachine)] = typeof(ReportMachine),
        [nameof(ReportMaterial)] = typeof(ReportMaterial),
        [nameof(Hindrance)] = typeof(Hindrance),
        [nameof(Requirement)] = typeof(Requirement),
        [nameof(Folder)] = typeof(Folder),
        [nameof(Note)] = typeof(Note),
        [nameof(ScanDocument)] = typeof(ScanDocument),
        [nameof(MediaItem)] = typeof(MediaItem),
        [nameof(SiteResource)] = typeof(SiteResource),
        [nameof(WbsNode)] = typeof(WbsNode),
        [nameof(ActivityResource)] = typeof(ActivityResource),
        [nameof(WorkCalendar)] = typeof(WorkCalendar),
        [nameof(ReportProfile)] = typeof(ReportProfile),
        [nameof(Drawing)] = typeof(Drawing),
        [nameof(DrawingItem)] = typeof(DrawingItem),
        [nameof(DrawingAllocation)] = typeof(DrawingAllocation),
    };

    public static string NameOf(Type t) => t.Name;
}
