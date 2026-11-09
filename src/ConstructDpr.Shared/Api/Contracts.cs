using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Shared.Api;

public sealed record CurvePoint(DateOnly Date, double Planned, double? Actual);

public sealed record DashboardStats(double PlannedPct, double ActualPct, int Activities, int Critical, int Delayed,
    int PendingApprovals, int ApprovedReports, DateOnly? ForecastFinish, decimal ContractValue, decimal ExecutedValue,
    int HeadcountToday, IReadOnlyList<CurvePoint> SCurve);

public sealed record UserInfo(string Id, string Email, string DisplayName);

/// <summary>A project as listed for the signed-in person, with their role on it.</summary>
public sealed record ProjectSummary(int Id, string Code, string Name, string? Client, ProjectRole MyRole);

public sealed record DprSummary(int Id, string Number, int Revision, DateOnly ReportDate, Shift Shift, DprStatus Status,
    string PreparedBy, string? ApprovedBy, DateTime UpdatedAt);

/// <summary>
/// One save of the activities screen: changed or new activities (Id 0 = new), activities to delete,
/// and the complete predecessor list of every activity named in <see cref="LinksFor"/>.
/// </summary>
public sealed class ActivityChanges
{
    public List<Activity> Upserts { get; set; } = new();
    public List<int> Deletes { get; set; } = new();
    public List<int> LinksFor { get; set; } = new();
    public List<Dependency> Links { get; set; } = new();
}

public sealed record ScheduleSummary(DateOnly ProjectFinish, int CriticalCount, DateOnly DataDate);

public sealed record ImportResult(int Created, int Updated, IReadOnlyList<string> Errors);

public enum ReportKind { DprPdf, DprExcel, SchedulePdf, ScheduleExcel, ActivityTemplate }

public sealed record FileDownload(string FileName, string ContentType, byte[] Content);

public sealed record AppFeatures(bool AdminPortal, bool Registration, string ServerVersion);

/// <summary>A change committed on the server; every app showing that project refreshes.</summary>
public sealed record LiveChange(int ProjectId, string Entity, int? Id, string Action, string By, DateTime At);

/// <summary>
/// Everything the apps can do. Implemented in-process over SQLite/PostgreSQL (<c>LocalApi</c>, used by the server and the
/// offline Windows app) and over HTTPS (<c>RemoteApi</c>, used by the web, Android and connected Windows apps).
/// Every method enforces the caller's project role and the workflow rules.
/// </summary>
public interface IConstructDprApi
{
    Task<UserInfo> MeAsync();
    Task<AppFeatures> FeaturesAsync();

    Task<List<ProjectSummary>> ProjectsAsync();
    Task<Project> ProjectAsync(int projectId);
    Task<Project> SaveProjectAsync(Project project);
    Task<ProjectSummary> CreateProjectAsync(Project project);
    Task<ProjectSummary> CreateDemoProjectAsync();

    Task<List<ProjectMember>> MembersAsync(int projectId);
    Task<ProjectMember> AddMemberAsync(int projectId, string email, ProjectRole role);
    Task RemoveMemberAsync(int projectId, int memberId);

    Task<List<Activity>> ActivitiesAsync(int projectId);
    Task<List<Dependency>> DependenciesAsync(int projectId);
    Task SaveActivitiesAsync(int projectId, ActivityChanges changes);
    Task<ScheduleSummary> RunScheduleAsync(int projectId);
    Task<Baseline> FreezeBaselineAsync(int projectId, string name);
    Task<ImportResult> ImportActivitiesAsync(int projectId, byte[] xlsx);

    Task<DashboardStats> DashboardAsync(int projectId);

    Task<List<DprSummary>> DprsAsync(int projectId);
    Task<Dpr> DprAsync(int projectId, int dprId);
    Task<Dpr> CreateDprAsync(int projectId, DateOnly date, Shift shift);
    /// <summary>Computes previous / cumulative / balance / % for an edited report without saving it.</summary>
    Task<Dpr> RecalculateDprAsync(int projectId, Dpr dpr);
    Task<Dpr> SaveDprAsync(int projectId, Dpr dpr);
    Task<Dpr> SubmitDprAsync(int projectId, Dpr dpr);
    /// <summary>Approves; <paramref name="accepted"/> maps activity id → accepted quantity where the reviewer changed it.</summary>
    Task<Dpr> ApproveDprAsync(int projectId, int dprId, Dictionary<int, decimal> accepted);
    Task<Dpr> RejectDprAsync(int projectId, int dprId, string reason);
    Task<Dpr> ReviseDprAsync(int projectId, int dprId, string reason);
    Task<bool> VerifyDprAsync(int projectId, int dprId);

    Task<List<AuditEntry>> AuditAsync(int projectId, int take = 50);
    Task<FileDownload> ReportAsync(int projectId, ReportKind kind, int? dprId = null);
}

/// <summary>Push notifications of committed changes (SignalR for the server, in-process events offline).</summary>
public interface ILiveUpdates
{
    event Action<LiveChange>? Changed;
    /// <summary>Connected, reconnecting or offline — shown in the apps' status bar.</summary>
    event Action<string>? StatusChanged;
    string Status { get; }
    Task WatchProjectAsync(int projectId);
}
