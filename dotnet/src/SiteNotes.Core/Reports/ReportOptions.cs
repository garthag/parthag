using System.Text.Json;
using System.Text.Json.Serialization;
using SiteNotes.Core.Models;

namespace SiteNotes.Core.Reports;

public enum PageNumberStyle { None, PageN, PageNofTotal, NSlashTotal }

public enum DprSection
{
    ProjectDetails, Summary, Activities, Manpower, ManpowerSummary, Machinery, Materials, Cost,
    Hindrances, Requirements, Safety, PlanRemarks, Photos, Signatures
}

public enum ActivityRowSort { AsEntered, Name, ProgressHigh, ProgressLow, TodayQtyHigh, Agency, Location }
public enum ManpowerRowSort { AsEntered, Party, Trade, CountHigh, Activity }

/// <summary>Page header and footer: logos, company name, reference lines, footer text, page numbers.</summary>
public class PdfLayout
{
    /// <summary>Full path of the company logo (left of the header). Empty = none.</summary>
    public string LogoPath { get; set; } = "";
    /// <summary>Full path of the client logo (right of the header). Empty = none.</summary>
    public string ClientLogoPath { get; set; } = "";
    public string CompanyName { get; set; } = "";
    /// <summary>Free text lines under the title, e.g. "Project ref: WTP/2026/014" and "Contract no: 45/2026".</summary>
    public string HeaderLines { get; set; } = "";
    public TextAlign HeaderAlign { get; set; } = TextAlign.Left;
    public string FooterText { get; set; } = "";
    public PageNumberStyle PageNumbers { get; set; } = PageNumberStyle.PageNofTotal;
    public TextAlign PageNumberAlign { get; set; } = TextAlign.Right;
    public bool ShowGeneratedStamp { get; set; } = true;

    [JsonIgnore] public bool NeedsTotal => PageNumbers is PageNumberStyle.PageNofTotal or PageNumberStyle.NSlashTotal;
    [JsonIgnore] public bool IsRich => LogoPath != "" || ClientLogoPath != "" || CompanyName != "" || HeaderLines != "";
}

/// <summary>Everything the report manager lets you choose. Saved per project (see <see cref="ReportProfile"/>).</summary>
public class ReportOptions
{
    public PdfLayout Layout { get; set; } = new();
    /// <summary>Sections of the daily report, in print order. Sections not listed are left out.</summary>
    public List<DprSection> Sections { get; set; } = [.. Enum.GetValues<DprSection>()];
    public ActivityRowSort ActivitySort { get; set; } = ActivityRowSort.AsEntered;
    public ManpowerRowSort ManpowerSort { get; set; } = ManpowerRowSort.AsEntered;
    public bool ShowCost { get; set; } = true;
    /// <summary>Leave out sections that have nothing in them (keeps reports short).</summary>
    public bool HideEmptySections { get; set; } = true;
    public int PhotosPerRow { get; set; } = 2;
    /// <summary>Crop photos to fill uniform 4:3 frames (true) or fit the whole photo inside the frame (false).</summary>
    public bool PhotoCrop { get; set; } = true;
    /// <summary>0 = all photos.</summary>
    public int MaxPhotos { get; set; }
    public bool Landscape { get; set; }
    /// <summary>Extra remarks printed at the end of every generated report (e.g. "Issued for client review").</summary>
    public string ExtraRemarks { get; set; } = "";
    public string ReportTitle { get; set; } = "DAILY PROGRESS REPORT";

    // file names (in the media folder) for the logos, so they sync between devices
    public string LogoFile { get; set; } = "";
    public string ClientLogoFile { get; set; } = "";

    public bool Has(DprSection s) => Sections.Contains(s);

    public static string Label(DprSection s) => s switch
    {
        DprSection.ProjectDetails => "Project & weather details",
        DprSection.Summary => "Summary figures",
        DprSection.Activities => "Work progress (activities)",
        DprSection.Manpower => "Manpower",
        DprSection.ManpowerSummary => "Manpower by agency / department",
        DprSection.Machinery => "Machinery & equipment",
        DprSection.Materials => "Materials & non-material",
        DprSection.Cost => "Cost of the day",
        DprSection.Hindrances => "Hindrances",
        DprSection.Requirements => "Requirements",
        DprSection.Safety => "Safety & toolbox talk",
        DprSection.PlanRemarks => "Plan for tomorrow & remarks",
        DprSection.Photos => "Photos & videos",
        DprSection.Signatures => "Signatures",
        _ => s.ToString(),
    };

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ReportOptions FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new ReportOptions();
        try { return JsonSerializer.Deserialize<ReportOptions>(json, Json) ?? new ReportOptions(); }
        catch (JsonException) { return new ReportOptions(); }
    }

    /// <summary>Copy with logo paths resolved to this device's media folder.</summary>
    public ReportOptions Resolve(Func<string, string> mediaPath)
    {
        var o = FromJson(ToJson());
        o.Layout.LogoPath = o.LogoFile == "" ? "" : mediaPath(o.LogoFile);
        o.Layout.ClientLogoPath = o.ClientLogoFile == "" ? "" : mediaPath(o.ClientLogoFile);
        if (!File.Exists(o.Layout.LogoPath)) o.Layout.LogoPath = "";
        if (!File.Exists(o.Layout.ClientLogoPath)) o.Layout.ClientLogoPath = "";
        return o;
    }

    public IEnumerable<ReportActivity> Sort(IEnumerable<ReportActivity> rows) => ActivitySort switch
    {
        ActivityRowSort.Name => rows.OrderBy(a => a.ActivityName),
        ActivityRowSort.ProgressHigh => rows.OrderByDescending(a => a.PercentComplete),
        ActivityRowSort.ProgressLow => rows.OrderBy(a => a.PercentComplete),
        ActivityRowSort.TodayQtyHigh => rows.OrderByDescending(a => a.TodayQty),
        ActivityRowSort.Agency => rows.OrderBy(a => a.SubcontractorName).ThenBy(a => a.ActivityName),
        ActivityRowSort.Location => rows.OrderBy(a => a.Location).ThenBy(a => a.ActivityName),
        _ => rows,
    };

    public IEnumerable<ReportManpower> Sort(IEnumerable<ReportManpower> rows) => ManpowerSort switch
    {
        ManpowerRowSort.Party => rows.OrderBy(m => m.Source).ThenBy(m => m.PartyName).ThenBy(m => m.Trade),
        ManpowerRowSort.Trade => rows.OrderBy(m => m.Trade),
        ManpowerRowSort.CountHigh => rows.OrderByDescending(m => m.Count),
        ManpowerRowSort.Activity => rows.OrderBy(m => m.ActivityName),
        _ => rows,
    };
}
