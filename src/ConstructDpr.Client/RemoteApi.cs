using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Client;

/// <summary>IConstructDprApi over HTTPS. Server errors come back as the same exception types LocalApi throws.</summary>
public sealed class RemoteApi(AuthSession session) : IConstructDprApi
{
    private static readonly JsonSerializerOptions J = Json.Options;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await session.GetAccessTokenAsync(forceRefresh: attempt > 0) ?? throw new UnauthorizedAccessException("Please sign in.");
            using var req = new HttpRequestMessage(method, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: J);
            HttpResponseMessage res;
            try { res = await session.Http.SendAsync(req); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new HttpRequestException("No connection to the server. Check the internet connection and try again.", ex);
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { res.Dispose(); continue; } // token expired: refresh once
            await EnsureSuccessAsync(res);
            return res;
        }
    }

    /// <summary>Throws the exception that matches the server's answer (WorkflowException, ConflictException, …).</summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage res)
    {
        if (res.IsSuccessStatusCode) return;
        ApiError? err = null;
        try { err = await res.Content.ReadFromJsonAsync<ApiError>(J); } catch (Exception ex) when (ex is JsonException or NotSupportedException) { }
        string? identityProblem = null;
        if (err is null && res.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                var doc = await res.Content.ReadFromJsonAsync<JsonElement>(J);
                if (doc.TryGetProperty("errors", out var errors))
                    identityProblem = string.Join(" ", errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(v => v.GetString())));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { }
        }
        var msg = err?.Message ?? identityProblem ?? $"The server answered {(int)res.StatusCode} {res.ReasonPhrase}.";
        throw (err?.Kind ?? "") switch
        {
            "workflow" or "badrequest" => new WorkflowException(msg),
            "schedule" => new ScheduleException(msg),
            "conflict" => new ConflictException(msg),
            "forbidden" => new ForbiddenException(msg),
            "notfound" => new NotFoundException(msg),
            "unauthorized" => new UnauthorizedAccessException(msg),
            _ => res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new UnauthorizedAccessException("Please sign in again."),
                HttpStatusCode.Forbidden => new ForbiddenException(msg),
                HttpStatusCode.NotFound => new NotFoundException("Not found. Is the server address right, and the server up to date?"),
                HttpStatusCode.BadRequest => new WorkflowException(msg),
                _ => new HttpRequestException(msg),
            },
        };
    }

    private async Task<T> Get<T>(string path)
    {
        using var res = await SendAsync(HttpMethod.Get, path);
        return (await res.Content.ReadFromJsonAsync<T>(J))!;
    }

    private async Task<T> Send<T>(HttpMethod m, string path, object? body = null)
    {
        using var res = await SendAsync(m, path, body ?? new { });
        return (await res.Content.ReadFromJsonAsync<T>(J))!;
    }

    private async Task Send(HttpMethod m, string path, object? body = null)
    {
        using var _ = await SendAsync(m, path, body);
    }

    private static string P(int id) => $"api/projects/{id}";

    public Task<UserInfo> MeAsync() => Get<UserInfo>("api/me");
    public Task<AppFeatures> FeaturesAsync() => session.FeaturesAsync();

    public Task<List<ProjectSummary>> ProjectsAsync() => Get<List<ProjectSummary>>("api/projects");
    public Task<Project> ProjectAsync(int projectId) => Get<Project>(P(projectId));
    public Task<Project> SaveProjectAsync(Project project) => Send<Project>(HttpMethod.Put, P(project.Id), project);
    public Task<ProjectSummary> CreateProjectAsync(Project project) => Send<ProjectSummary>(HttpMethod.Post, "api/projects", project);
    public Task<ProjectSummary> CreateDemoProjectAsync() => Send<ProjectSummary>(HttpMethod.Post, "api/projects/demo");

    public Task<List<ProjectMember>> MembersAsync(int projectId) => Get<List<ProjectMember>>($"{P(projectId)}/members");
    public Task<ProjectMember> AddMemberAsync(int projectId, string email, ProjectRole role) => Send<ProjectMember>(HttpMethod.Post, $"{P(projectId)}/members", new { email, role });
    public Task RemoveMemberAsync(int projectId, int memberId) => Send(HttpMethod.Delete, $"{P(projectId)}/members/{memberId}");

    public Task<List<Activity>> ActivitiesAsync(int projectId) => Get<List<Activity>>($"{P(projectId)}/activities");
    public Task<List<Dependency>> DependenciesAsync(int projectId) => Get<List<Dependency>>($"{P(projectId)}/dependencies");
    public Task SaveActivitiesAsync(int projectId, ActivityChanges changes) => Send(HttpMethod.Post, $"{P(projectId)}/activities/save", changes);
    public Task<ScheduleSummary> RunScheduleAsync(int projectId) => Send<ScheduleSummary>(HttpMethod.Post, $"{P(projectId)}/schedule/run");
    public Task<Baseline> FreezeBaselineAsync(int projectId, string name) => Send<Baseline>(HttpMethod.Post, $"{P(projectId)}/baselines", new { name });
    public Task<ImportResult> ImportActivitiesAsync(int projectId, byte[] xlsx) => Send<ImportResult>(HttpMethod.Post, $"{P(projectId)}/activities/import", new { content = xlsx });

    public Task<DashboardStats> DashboardAsync(int projectId) => Get<DashboardStats>($"{P(projectId)}/dashboard");

    public Task<List<DprSummary>> DprsAsync(int projectId) => Get<List<DprSummary>>($"{P(projectId)}/dprs");
    public Task<Dpr> DprAsync(int projectId, int dprId) => Get<Dpr>($"{P(projectId)}/dprs/{dprId}");
    public Task<Dpr> CreateDprAsync(int projectId, DateOnly date, Shift shift) => Send<Dpr>(HttpMethod.Post, $"{P(projectId)}/dprs", new { date, shift });
    public Task<Dpr> RecalculateDprAsync(int projectId, Dpr dpr) => Send<Dpr>(HttpMethod.Post, $"{P(projectId)}/dprs/recalculate", dpr);
    public Task<Dpr> SaveDprAsync(int projectId, Dpr dpr) => Send<Dpr>(HttpMethod.Put, $"{P(projectId)}/dprs/{dpr.Id}", dpr);
    public Task<Dpr> SubmitDprAsync(int projectId, Dpr dpr) => Send<Dpr>(HttpMethod.Post, $"{P(projectId)}/dprs/{dpr.Id}/submit", dpr);
    public Task<Dpr> ApproveDprAsync(int projectId, int dprId, Dictionary<int, decimal> accepted) => Send<Dpr>(HttpMethod.Post, $"{P(projectId)}/dprs/{dprId}/approve", new { accepted });
    public Task<Dpr> RejectDprAsync(int projectId, int dprId, string reason) => Send<Dpr>(HttpMethod.Post, $"{P(projectId)}/dprs/{dprId}/reject", new { reason });
    public Task<Dpr> ReviseDprAsync(int projectId, int dprId, string reason) => Send<Dpr>(HttpMethod.Post, $"{P(projectId)}/dprs/{dprId}/revise", new { reason });
    public Task<bool> VerifyDprAsync(int projectId, int dprId) => Get<bool>($"{P(projectId)}/dprs/{dprId}/verify");

    public Task<List<PhotoInfo>> PhotosAsync(int projectId, int dprId) => Get<List<PhotoInfo>>($"{P(projectId)}/dprs/{dprId}/photos");
    public Task<PhotoInfo> AddPhotoAsync(int projectId, int dprId, string fileName, byte[] content, string? caption) =>
        Send<PhotoInfo>(HttpMethod.Post, $"{P(projectId)}/dprs/{dprId}/photos", new { fileName, content, caption });
    public Task DeletePhotoAsync(int projectId, int photoId) => Send(HttpMethod.Delete, $"{P(projectId)}/photos/{photoId}");

    public async Task<FileDownload> PhotoAsync(int projectId, int photoId)
    {
        using var res = await SendAsync(HttpMethod.Get, $"{P(projectId)}/photos/{photoId}");
        var name = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "photo";
        return new FileDownload(name, res.Content.Headers.ContentType?.MediaType ?? "image/jpeg", await res.Content.ReadAsByteArrayAsync());
    }

    public Task<List<AuditEntry>> AuditAsync(int projectId, int take = 50) => Get<List<AuditEntry>>($"{P(projectId)}/audit?take={take}");

    public async Task<FileDownload> ReportAsync(int projectId, ReportKind kind, int? dprId = null)
    {
        using var res = await SendAsync(HttpMethod.Get, $"{P(projectId)}/reports/{kind}" + (dprId is { } d ? $"?dprId={d}" : ""));
        var name = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? $"{kind}";
        return new FileDownload(name, res.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", await res.Content.ReadAsByteArrayAsync());
    }
}
