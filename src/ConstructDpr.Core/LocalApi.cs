using ConstructDpr.Core.Data;
using ConstructDpr.Core.Services;
using ConstructDpr.Reports;
using ConstructDpr.Shared.Api;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Scheduling;
using ConstructDpr.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Core;

/// <summary>Who is calling. <see cref="IsLocalOwner"/> is the single user of the offline Windows app (administrator of everything).</summary>
public sealed record Caller(string UserId, string Email, string DisplayName, bool IsLocalOwner = false)
{
    /// <summary>The name recorded on reports and in the audit trail (e-mail on the server: unique per person).</summary>
    public string Name => IsLocalOwner ? DisplayName : Email;
}

/// <summary>
/// The rules of CONSTRUCT DPR in one place: permissions, validation and workflow, over a database.
/// The server exposes it over HTTPS for the web and Android apps; the Windows app can also use it directly (offline).
/// Every call opens its own short database context, so it is safe for many simultaneous users.
/// </summary>
public sealed class LocalApi(Func<AppDbContext> dbFactory, Caller caller, AppFeatures features,
    Action<LiveChange>? publish = null, Func<DateOnly>? today = null) : IConstructDprApi
{
    private DateOnly Today => today?.Invoke() ?? DateOnly.FromDateTime(DateTime.Today);

    // ───────────────────────────── helpers ─────────────────────────────

    private ProjectRole RoleOn(AppDbContext db, int projectId)
    {
        if (!db.Projects.Any(p => p.Id == projectId)) throw new NotFoundException("Project not found.");
        if (caller.IsLocalOwner) return ProjectRole.ProjectAdmin;
        var m = db.Members.AsNoTracking().FirstOrDefault(x => x.ProjectId == projectId && x.UserId == caller.UserId);
        return m?.Role ?? throw new NotFoundException("Project not found."); // non-members cannot even see that it exists
    }

    private ProjectRole Require(AppDbContext db, int projectId, Module module, Act action)
    {
        var role = RoleOn(db, projectId);
        if (!Permissions.Can(role, module, action))
            throw new ForbiddenException($"Your role ({Permissions.Label(role)}) does not allow this ({action} on {module}).");
        return role;
    }

    private static void CheckVersion(Entity current, int incoming, string what)
    {
        if (current.Version != incoming)
            throw new ConflictException($"{what} was changed on another device since you opened it. Reload to see the latest version, then make your change again.");
    }

    private void Audit(AppDbContext db, int projectId, string entity, int id, string action, string? details)
    {
        db.Audit.Add(new AuditEntry { ProjectId = projectId, User = caller.Name, Entity = entity, EntityId = id, Action = action, Details = details });
        db.SaveChanges();
    }

    private void Publish(int projectId, string entity, int? id, string action) =>
        publish?.Invoke(new LiveChange(projectId, entity, id, action, caller.Name, DateTime.UtcNow));

    private static Task<T> Done<T>(T value) => Task.FromResult(value);

    // ───────────────────────────── user & projects ─────────────────────────────

    public Task<UserInfo> MeAsync() => Done(new UserInfo(caller.UserId, caller.Email, caller.DisplayName));

    public Task<AppFeatures> FeaturesAsync() => Done(features);

    public Task<List<ProjectSummary>> ProjectsAsync()
    {
        using var db = dbFactory();
        if (caller.IsLocalOwner)
            return Done(db.Projects.AsNoTracking().OrderBy(p => p.Code).ToList()
                .Select(p => new ProjectSummary(p.Id, p.Code, p.Name, p.Client, ProjectRole.ProjectAdmin)).ToList());
        var mine = db.Members.AsNoTracking().Where(m => m.UserId == caller.UserId).ToList();
        var ids = mine.Select(m => m.ProjectId).ToList();
        return Done(db.Projects.AsNoTracking().Where(p => ids.Contains(p.Id)).OrderBy(p => p.Code).ToList()
            .Select(p => new ProjectSummary(p.Id, p.Code, p.Name, p.Client, mine.First(m => m.ProjectId == p.Id).Role)).ToList());
    }

    public Task<Project> ProjectAsync(int projectId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Project, Act.View);
        return Done(db.Projects.AsNoTracking().Include(p => p.Holidays).First(p => p.Id == projectId));
    }

    public Task<Project> SaveProjectAsync(Project dto)
    {
        using (var db = dbFactory())
        {
            Require(db, dto.Id, Module.Project, Act.Edit);
            var p = db.Projects.Include(x => x.Holidays).First(x => x.Id == dto.Id);
            CheckVersion(p, dto.Version, "The project");
            if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) throw new WorkflowException("Project code and name are required.");
            if (!WorkCalendar.ParseWorkDays(dto.WorkDays).Any()) throw new WorkflowException("Enter at least one working day, e.g. 1,2,3,4,5,6 (0 = Sunday).");
            if (dto.CompletionDate is { } c && c < dto.StartDate) throw new WorkflowException("Completion cannot be before the start.");
            var code = dto.Code.Trim().ToUpperInvariant();
            if (code != p.Code && db.Projects.Any(x => x.Code == code)) throw new WorkflowException($"Project code {code} is already used.");
            (p.Code, p.Name, p.Client, p.Contractor, p.WorkOrderNo, p.SiteAddress) = (code, dto.Name.Trim(), dto.Client, dto.Contractor, dto.WorkOrderNo, dto.SiteAddress);
            (p.StartDate, p.CompletionDate, p.Currency, p.WorkDays, p.AllowSelfApproval) =
                (dto.StartDate, dto.CompletionDate, string.IsNullOrWhiteSpace(dto.Currency) ? "INR" : dto.Currency, string.Join(",", WorkCalendar.ParseWorkDays(dto.WorkDays).Select(d => (int)d)), dto.AllowSelfApproval);
            db.Holidays.RemoveRange(p.Holidays);
            p.Holidays = dto.Holidays.Where(h => !string.IsNullOrWhiteSpace(h.Name)).DistinctBy(h => h.Date)
                .Select(h => new Holiday { ProjectId = p.Id, Date = h.Date, Name = h.Name.Trim() }).ToList();
            db.SaveChanges();
            Audit(db, p.Id, "Project", p.Id, "edit", p.Code);
            TryReschedule(db, p.Id);
        }
        Publish(dto.Id, "project", dto.Id, "edit");
        return ProjectAsync(dto.Id);
    }

    public Task<ProjectSummary> CreateProjectAsync(Project dto)
    {
        if (!features.AdminPortal && !caller.IsLocalOwner) throw new ForbiddenException("Creating projects is switched off on this server (admin portal).");
        using var db = dbFactory();
        var code = (dto.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0 || string.IsNullOrWhiteSpace(dto.Name)) throw new WorkflowException("Project code and name are required.");
        if (db.Projects.Any(x => x.Code == code)) throw new WorkflowException($"Project code {code} is already used.");
        var p = new Project
        {
            Code = code, Name = dto.Name.Trim(), Client = dto.Client, Contractor = dto.Contractor, WorkOrderNo = dto.WorkOrderNo,
            SiteAddress = dto.SiteAddress, StartDate = dto.StartDate == default ? Today : dto.StartDate, CompletionDate = dto.CompletionDate,
            WorkDays = string.IsNullOrWhiteSpace(dto.WorkDays) ? "1,2,3,4,5,6" : dto.WorkDays, AllowSelfApproval = dto.AllowSelfApproval,
        };
        db.Projects.Add(p);
        db.SaveChanges();
        AddCallerAsAdmin(db, p.Id);
        Audit(db, p.Id, "Project", p.Id, "create", p.Code);
        return Done(new ProjectSummary(p.Id, p.Code, p.Name, p.Client, ProjectRole.ProjectAdmin));
    }

    public Task<ProjectSummary> CreateDemoProjectAsync()
    {
        using var db = dbFactory();
        var p = DemoData.Create(db, Today, caller.Name);
        AddCallerAsAdmin(db, p.Id);
        Audit(db, p.Id, "Project", p.Id, "create", "Demo project " + p.Code);
        return Done(new ProjectSummary(p.Id, p.Code, p.Name, p.Client, ProjectRole.ProjectAdmin));
    }

    private void AddCallerAsAdmin(AppDbContext db, int projectId)
    {
        if (caller.IsLocalOwner) return;
        db.Members.Add(new ProjectMember { ProjectId = projectId, UserId = caller.UserId, Email = caller.Email, DisplayName = caller.DisplayName, Role = ProjectRole.ProjectAdmin });
        db.SaveChanges();
    }

    // ───────────────────────────── members ─────────────────────────────

    public Task<List<ProjectMember>> MembersAsync(int projectId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Members, Act.View);
        return Done(db.Members.AsNoTracking().Where(m => m.ProjectId == projectId).OrderBy(m => m.Role).ThenBy(m => m.Email).ToList());
    }

    public Task<ProjectMember> AddMemberAsync(int projectId, string email, ProjectRole role)
    {
        ProjectMember member;
        using (var db = dbFactory())
        {
            var myRole = Require(db, projectId, Module.Members, Act.Create);
            if (role == ProjectRole.ProjectAdmin && myRole != ProjectRole.ProjectAdmin) throw new ForbiddenException("Only a project administrator can appoint another administrator.");
            var normal = (email ?? "").Trim().ToUpperInvariant();
            var user = db.Users.AsNoTracking().FirstOrDefault(u => u.NormalizedEmail == normal)
                ?? throw new WorkflowException($"No account uses {email}. Ask the person to open the app and create an account first, then add them.");
            member = db.Members.FirstOrDefault(m => m.ProjectId == projectId && m.UserId == user.Id)
                     ?? db.Members.Add(new ProjectMember { ProjectId = projectId, UserId = user.Id }).Entity;
            (member.Email, member.DisplayName, member.Role) = (user.Email ?? email!, user.DisplayName, role);
            db.SaveChanges();
            Audit(db, projectId, "Member", member.Id, "set-role", $"{member.Email} → {Permissions.Label(role)}");
        }
        Publish(projectId, "members", member.Id, "edit");
        return Done(member);
    }

    public Task RemoveMemberAsync(int projectId, int memberId)
    {
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Members, Act.Delete);
            var m = db.Members.FirstOrDefault(x => x.Id == memberId && x.ProjectId == projectId) ?? throw new NotFoundException("Member not found.");
            if (m.Role == ProjectRole.ProjectAdmin && db.Members.Count(x => x.ProjectId == projectId && x.Role == ProjectRole.ProjectAdmin) == 1)
                throw new WorkflowException("A project needs at least one administrator.");
            db.Members.Remove(m);
            db.SaveChanges();
            Audit(db, projectId, "Member", memberId, "remove", m.Email);
        }
        Publish(projectId, "members", memberId, "delete");
        return Task.CompletedTask;
    }

    public Task<string> ResetPasswordAsync(int projectId, int memberId)
    {
        string temp;
        using (var db = dbFactory())
        {
            var myRole = Require(db, projectId, Module.Members, Act.Delete);
            var m = db.Members.FirstOrDefault(x => x.Id == memberId && x.ProjectId == projectId) ?? throw new NotFoundException("Member not found.");
            if (m.UserId == caller.UserId) throw new WorkflowException("Change your own password in Settings.");
            if (m.Role == ProjectRole.ProjectAdmin && myRole != ProjectRole.ProjectAdmin)
                throw new ForbiddenException("Only a project administrator can reset another administrator's password.");
            var user = db.Users.First(u => u.Id == m.UserId);
            temp = DirectAccounts.NewTemporaryPassword();
            DirectAccounts.SetPassword(user, temp);
            db.SaveChanges();
            Audit(db, projectId, "Member", memberId, "reset-password", m.Email);
        }
        return Done(temp);
    }

    // ───────────────────────────── activities & schedule ─────────────────────────────

    public Task<List<Activity>> ActivitiesAsync(int projectId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Masters, Act.View);
        return Done(db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.SortOrder).ThenBy(a => a.Code).ToList());
    }

    public Task<List<Dependency>> DependenciesAsync(int projectId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Masters, Act.View);
        return Done(db.Dependencies.AsNoTracking().Where(d => d.ProjectId == projectId).ToList());
    }

    public Task SaveActivitiesAsync(int projectId, ActivityChanges changes)
    {
        using (var db = dbFactory())
        {
            var role = RoleOn(db, projectId);
            void Need(Module m, Act a)
            {
                if (!Permissions.Can(role, m, a)) throw new ForbiddenException($"Your role ({Permissions.Label(role)}) does not allow this ({a} on {m}).");
            }
            using var tx = db.Database.BeginTransaction();
            var all = db.Activities.Where(a => a.ProjectId == projectId).ToList();
            var problems = new List<string>();

            foreach (var id in changes.Deletes.Distinct())
            {
                Need(Module.Masters, Act.Delete);
                var a = all.FirstOrDefault(x => x.Id == id) ?? throw new NotFoundException("Activity not found.");
                if (db.DprActivityLines.Any(l => l.ActivityId == id)) { problems.Add($"{a.Code} is used in daily reports and cannot be deleted (its progress history must be kept)."); continue; }
                db.Dependencies.RemoveRange(db.Dependencies.Where(d => d.PredecessorId == id || d.SuccessorId == id));
                db.Activities.Remove(a);
                all.Remove(a);
            }

            foreach (var dto in changes.Upserts)
            {
                Activity a;
                if (dto.Id == 0)
                {
                    Need(Module.Masters, Act.Create);
                    a = new Activity { ProjectId = projectId };
                    db.Activities.Add(a);
                    all.Add(a);
                }
                else
                {
                    Need(Module.Masters, Act.Edit);
                    a = all.FirstOrDefault(x => x.Id == dto.Id) ?? throw new NotFoundException("Activity not found.");
                    CheckVersion(a, dto.Version, $"Activity {a.Code}");
                    if ((a.Rate != dto.Rate || a.TotalQty != dto.TotalQty) && !Permissions.Can(role, Module.Boq, Act.Edit) && !Permissions.Can(role, Module.Masters, Act.Delete))
                        throw new ForbiddenException($"Your role ({Permissions.Label(role)}) cannot change BOQ quantities or rates.");
                }
                var code = (dto.Code ?? "").Trim();
                if (code.Length == 0 || string.IsNullOrWhiteSpace(dto.Name)) problems.Add("Every activity needs a code and a name.");
                if (dto.Duration < 0 || dto.TotalQty < 0 || dto.Rate < 0 || dto.Weight < 0) problems.Add($"{code}: duration, quantity, rate and weight cannot be negative.");
                if (dto.TotalQty > 0 && dto.TotalQty < a.AcceptedQty) problems.Add($"{code}: BOQ quantity {dto.TotalQty:0.###} is below the {a.AcceptedQty:0.###} already approved.");
                if (dto.Status == ActivityStatus.Cancelled && a.AcceptedQty > 0) problems.Add($"{code}: has approved progress and cannot be cancelled.");
                (a.Code, a.Name, a.Wbs, a.Area, a.Location, a.Unit) = (code, dto.Name?.Trim() ?? "", dto.Wbs, dto.Area, dto.Location, dto.Unit);
                (a.TotalQty, a.Rate, a.Weight, a.Duration, a.IsMilestone, a.Responsible, a.SortOrder) =
                    (dto.TotalQty, dto.Rate, dto.Weight, dto.Duration, dto.IsMilestone || dto.Duration == 0, dto.Responsible, dto.SortOrder);
                if (dto.Status == ActivityStatus.Cancelled || (a.Status == ActivityStatus.Cancelled && dto.Status == ActivityStatus.NotStarted)) a.Status = dto.Status;
            }
            foreach (var dup in all.GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                problems.Add($"Code {dup.Key} is used more than once.");

            if (changes.LinksFor.Count > 0)
            {
                Need(Module.Schedule, Act.Edit);
                var ids = all.Select(a => a.Id).ToHashSet();
                foreach (var succ in changes.LinksFor.Distinct())
                {
                    if (!ids.Contains(succ)) throw new NotFoundException("Activity not found.");
                    db.Dependencies.RemoveRange(db.Dependencies.Where(d => d.SuccessorId == succ));
                    foreach (var l in changes.Links.Where(l => l.SuccessorId == succ).DistinctBy(l => l.PredecessorId))
                    {
                        if (l.PredecessorId == succ) { problems.Add("An activity cannot be its own predecessor."); continue; }
                        if (!ids.Contains(l.PredecessorId)) { problems.Add("A predecessor belongs to another project."); continue; }
                        db.Dependencies.Add(new Dependency { ProjectId = projectId, PredecessorId = l.PredecessorId, SuccessorId = succ, Type = l.Type, Lag = l.Lag });
                    }
                }
            }
            if (problems.Count > 0) throw new WorkflowException(string.Join("\n", problems.Distinct()));
            db.SaveChanges();
            new ScheduleService(db).Run(projectId, Today); // throws on circular logic → nothing is committed
            Audit(db, projectId, "Activity", 0, "save", $"{changes.Upserts.Count} changed, {changes.Deletes.Count} deleted, links of {changes.LinksFor.Count}");
            tx.Commit();
        }
        Publish(projectId, "activities", null, "edit");
        return Task.CompletedTask;
    }

    private void TryReschedule(AppDbContext db, int projectId)
    {
        try { new ScheduleService(db).Run(projectId, Today); }
        catch (ScheduleException) { /* shown when the planner next runs the schedule */ }
    }

    public Task<ScheduleSummary> RunScheduleAsync(int projectId)
    {
        ScheduleSummary result;
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Schedule, Act.Edit);
            var r = new ScheduleService(db).Run(projectId, Today);
            result = new ScheduleSummary(r.ProjectFinish, r.Activities.Values.Count(x => x.Critical), Today);
            Audit(db, projectId, "Schedule", projectId, "calculate", $"Finish {r.ProjectFinish:dd MMM yyyy}");
        }
        Publish(projectId, "activities", null, "schedule");
        return Done(result);
    }

    public Task<Baseline> FreezeBaselineAsync(int projectId, string name)
    {
        Baseline b;
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Schedule, Act.Edit | Act.Submit);
            if (string.IsNullOrWhiteSpace(name)) throw new WorkflowException("Give the baseline a name.");
            var s = new ScheduleService(db);
            s.Run(projectId, Today);
            s.ApplyToPlan(projectId);
            b = s.FreezeBaseline(projectId, name.Trim());
            Audit(db, projectId, "Baseline", b.Id, "freeze", $"R{b.Revision} {b.Name}");
        }
        Publish(projectId, "activities", null, "baseline");
        return Done(new Baseline { Id = b.Id, ProjectId = b.ProjectId, Revision = b.Revision, Name = b.Name, CreatedAt = b.CreatedAt });
    }

    public Task<ImportResult> ImportActivitiesAsync(int projectId, byte[] xlsx)
    {
        ImportResult result;
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Masters, Act.Create | Act.Edit);
            List<Activity> rows; List<string> errors;
            try { (rows, errors) = ExcelService.ReadActivities(new MemoryStream(xlsx)); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or FormatException)
            {
                return Done(new ImportResult(0, 0, new[] { "The file is not an Excel workbook (.xlsx): " + ex.Message }));
            }
            var existing = db.Activities.Where(a => a.ProjectId == projectId).ToList().ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
                if (existing.TryGetValue(r.Code, out var a) && r.TotalQty > 0 && r.TotalQty < a.AcceptedQty)
                    errors.Add($"{r.Code}: total qty {r.TotalQty} is below the {a.AcceptedQty} already approved.");
            if (errors.Count > 0) return Done(new ImportResult(0, 0, errors));
            int created = 0, updated = 0;
            var order = existing.Values.Select(a => a.SortOrder).DefaultIfEmpty().Max();
            foreach (var r in rows)
            {
                if (existing.TryGetValue(r.Code, out var a))
                {
                    (a.Name, a.Wbs, a.Area, a.Unit, a.TotalQty, a.Rate, a.Weight, a.Duration, a.IsMilestone, a.Responsible) =
                        (r.Name, r.Wbs, r.Area, r.Unit, r.TotalQty, r.Rate, r.Weight, r.Duration, r.IsMilestone, r.Responsible);
                    updated++;
                }
                else
                {
                    r.ProjectId = projectId;
                    r.SortOrder = order += 10;
                    db.Activities.Add(r);
                    created++;
                }
            }
            db.SaveChanges();
            TryReschedule(db, projectId);
            Audit(db, projectId, "Activity", 0, "import", $"{created} new, {updated} updated");
            result = new ImportResult(created, updated, Array.Empty<string>());
        }
        Publish(projectId, "activities", null, "import");
        return Done(result);
    }

    public Task<DashboardStats> DashboardAsync(int projectId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Project, Act.View);
        return Done(new DashboardService(db).Build(projectId, Today));
    }

    // ───────────────────────────── daily reports ─────────────────────────────

    public Task<List<DprSummary>> DprsAsync(int projectId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Dpr, Act.View);
        return Done(db.Dprs.AsNoTracking().Where(d => d.ProjectId == projectId)
            .OrderByDescending(d => d.ReportDate).ThenByDescending(d => d.Revision).ToList()
            .Select(d => new DprSummary(d.Id, d.Number, d.Revision, d.ReportDate, d.Shift, d.Status, d.PreparedBy, d.ApprovedBy, d.UpdatedAt)).ToList());
    }

    public Task<Dpr> DprAsync(int projectId, int dprId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Dpr, Act.View);
        return Done(LoadForView(db, projectId, dprId));
    }

    private static Dpr LoadForView(AppDbContext db, int projectId, int dprId)
    {
        var d = db.Dprs.AsNoTracking().Include(x => x.Activities).ThenInclude(l => l.Activity)
            .Include(x => x.Manpower).Include(x => x.Equipment).Include(x => x.Materials).AsSplitQuery()
            .FirstOrDefault(x => x.Id == dprId && x.ProjectId == projectId) ?? throw new NotFoundException("Report not found.");
        if (d.Status != DprStatus.Approved) new DprService(db, "").Recalculate(d, d.Status == DprStatus.Submitted); // approved figures are frozen
        foreach (var l in d.Activities) if (l.Activity is not null) l.Activity.Project = null;
        return d;
    }

    private DprService Service(AppDbContext db) => new(db, caller.Name);

    private Dpr Tracked(AppDbContext db, int projectId, int dprId) =>
        db.Dprs.Include(x => x.Activities).ThenInclude(l => l.Activity)
            .Include(x => x.Manpower).Include(x => x.Equipment).Include(x => x.Materials).AsSplitQuery()
            .FirstOrDefault(x => x.Id == dprId && x.ProjectId == projectId) ?? throw new NotFoundException("Report not found.");

    public Task<Dpr> CreateDprAsync(int projectId, DateOnly date, Shift shift)
    {
        int id;
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Dpr, Act.Create);
            if (date > Today) throw new WorkflowException("The report date cannot be in the future.");
            var svc = Service(db);
            var d = svc.Create(projectId, date, shift);
            // Start from the latest earlier report's crews and equipment: on site these change little from day to day.
            var last = db.Dprs.Where(x => x.ProjectId == projectId && x.Id != d.Id && x.ReportDate <= date)
                .OrderByDescending(x => x.ReportDate).ThenByDescending(x => x.Revision).Select(x => x.Id).FirstOrDefault();
            if (last > 0)
            {
                var prev = svc.Load(last);
                d.Manpower.AddRange(prev.Manpower.Select(m => new ManpowerLine { Agency = m.Agency, Trade = m.Trade, Skilled = m.Skilled, SemiSkilled = m.SemiSkilled, Unskilled = m.Unskilled, Supervisors = m.Supervisors, Hours = m.Hours }));
                d.Equipment.AddRange(prev.Equipment.Select(q => new EquipmentLine { Code = q.Code, Type = q.Type }));
                d.Materials.AddRange(prev.Materials.Select(m => new MaterialLine { Code = m.Code, Description = m.Description, Unit = m.Unit, Opening = m.Closing }));
                // Yesterday's activities that are still open, with quantity 0: the engineer only types today's numbers.
                var open = db.Activities.Where(a => a.ProjectId == projectId && a.Status != ActivityStatus.Completed && a.Status != ActivityStatus.Cancelled)
                    .Select(a => a.Id).ToHashSet();
                d.Activities.AddRange(prev.Activities.Where(l => open.Contains(l.ActivityId)).Select(l => new DprActivityLine { ActivityId = l.ActivityId }));
                db.SaveChanges();
            }
            id = d.Id;
        }
        Publish(projectId, "dpr", id, "create");
        return DprAsync(projectId, id);
    }

    public Task<Dpr> RecalculateDprAsync(int projectId, Dpr dto)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Dpr, Act.View);
        dto.ProjectId = projectId;
        var acts = db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId).ToDictionary(a => a.Id);
        dto.Activities.RemoveAll(l => !acts.ContainsKey(l.ActivityId));
        foreach (var l in dto.Activities) l.Activity = acts[l.ActivityId];
        new DprService(db, "").Recalculate(dto, dto.Status == DprStatus.Submitted);
        return Done(dto);
    }

    public Task<Dpr> SaveDprAsync(int projectId, Dpr dto)
    {
        using (var db = dbFactory())
        {
            SaveInto(db, projectId, dto, Require(db, projectId, Module.Dpr, Act.View));
        }
        Publish(projectId, "dpr", dto.Id, "edit");
        return DprAsync(projectId, dto.Id);
    }

    private Dpr SaveInto(AppDbContext db, int projectId, Dpr dto, ProjectRole role)
    {
        var d = Tracked(db, projectId, dto.Id);
        // Editors may change any draft; people who may only create reports may change their own drafts.
        var own = string.Equals(d.PreparedBy, caller.Name, StringComparison.OrdinalIgnoreCase);
        if (!Permissions.Can(role, Module.Dpr, Act.Edit) && !(own && Permissions.Can(role, Module.Dpr, Act.Create)))
            throw new ForbiddenException($"Your role ({Permissions.Label(role)}) cannot change reports prepared by others.");
        CheckVersion(d, dto.Version, $"Report {d.Number}");
        if (!d.IsEditable) throw new WorkflowException($"{d.Number} is {d.Status} and can no longer be edited.");
        (d.Weather, d.SiteConditions, d.WorkCompleted, d.DelayReasons, d.TomorrowPlan, d.SafetyNotes) =
            (dto.Weather, dto.SiteConditions, dto.WorkCompleted, dto.DelayReasons, dto.TomorrowPlan, dto.SafetyNotes);

        var actIds = db.Activities.Where(a => a.ProjectId == projectId).Select(a => a.Id).ToHashSet();
        var wanted = dto.Activities.Where(l => actIds.Contains(l.ActivityId)).GroupBy(l => l.ActivityId).Select(g => g.Last()).ToList(); // a repeated activity: the latest entry counts
        foreach (var gone in d.Activities.Where(l => wanted.All(w => w.ActivityId != l.ActivityId)).ToList()) d.Activities.Remove(gone);
        foreach (var w in wanted)
        {
            var line = d.Activities.FirstOrDefault(l => l.ActivityId == w.ActivityId);
            if (line is null) d.Activities.Add(line = new DprActivityLine { ActivityId = w.ActivityId, Activity = db.Activities.Find(w.ActivityId) });
            (line.QtyToday, line.DelayReason, line.Remarks, line.QtyAccepted) = (w.QtyToday, w.DelayReason, w.Remarks, null);
        }
        d.Manpower.Clear(); d.Manpower.AddRange(dto.Manpower.Select(m => new ManpowerLine { Agency = m.Agency ?? "", Trade = m.Trade ?? "", Skilled = m.Skilled, SemiSkilled = m.SemiSkilled, Unskilled = m.Unskilled, Supervisors = m.Supervisors, Hours = m.Hours }));
        d.Equipment.Clear(); d.Equipment.AddRange(dto.Equipment.Select(e => new EquipmentLine { Code = e.Code ?? "", Type = e.Type ?? "", WorkingHours = e.WorkingHours, IdleHours = e.IdleHours, BreakdownHours = e.BreakdownHours, BreakdownReason = e.BreakdownReason }));
        d.Materials.Clear(); d.Materials.AddRange(dto.Materials.Select(m => new MaterialLine { Code = m.Code ?? "", Description = m.Description ?? "", Unit = m.Unit ?? "", Opening = m.Opening, Received = m.Received, Issued = m.Issued, ChallanNo = m.ChallanNo }));
        foreach (var m in d.Manpower) if (m.Skilled < 0 || m.SemiSkilled < 0 || m.Unskilled < 0 || m.Supervisors < 0 || m.Hours < 0 || m.Hours > 24) throw new WorkflowException($"{m.Agency}: headcount cannot be negative and hours must be 0–24.");
        db.Entry(d).State = EntityState.Modified; // bumps the version even when only lines changed
        Service(db).Save(d);
        return d;
    }

    public Task<Dpr> SubmitDprAsync(int projectId, Dpr dto)
    {
        using (var db = dbFactory())
        {
            var role = Require(db, projectId, Module.Dpr, Act.Submit);
            using var tx = db.Database.BeginTransaction();
            var d = SaveInto(db, projectId, dto, role);
            Service(db).Submit(d);
            tx.Commit();
        }
        Publish(projectId, "dpr", dto.Id, "submit");
        return DprAsync(projectId, dto.Id);
    }

    public Task<Dpr> ApproveDprAsync(int projectId, int dprId, Dictionary<int, decimal> accepted)
    {
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Dpr, Act.Approve);
            using var tx = db.Database.BeginTransaction();
            var d = Tracked(db, projectId, dprId);
            if (d.Status != DprStatus.Submitted) throw new WorkflowException("Only submitted reports can be approved.");
            foreach (var l in d.Activities)
                if (accepted.TryGetValue(l.ActivityId, out var q))
                {
                    if (q < 0) throw new WorkflowException("Accepted quantities cannot be negative.");
                    l.QtyAccepted = q;
                }
            Service(db).Approve(d);
            TryReschedule(db, projectId); // actual dates and progress move the forecast
            tx.Commit();
        }
        Publish(projectId, "dpr", dprId, "approve");
        return DprAsync(projectId, dprId);
    }

    public Task<Dpr> RejectDprAsync(int projectId, int dprId, string reason)
    {
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Dpr, Act.Reject);
            Service(db).Reject(Tracked(db, projectId, dprId), reason);
        }
        Publish(projectId, "dpr", dprId, "reject");
        return DprAsync(projectId, dprId);
    }

    public Task<Dpr> ReviseDprAsync(int projectId, int dprId, string reason)
    {
        int id;
        using (var db = dbFactory())
        {
            Require(db, projectId, Module.Dpr, Act.Create | Act.Edit);
            id = Service(db).StartRevision(Tracked(db, projectId, dprId), reason).Id;
            foreach (var p in db.Photos.AsNoTracking().Where(p => p.DprId == dprId).ToList())
                db.Photos.Add(new DprPhoto { ProjectId = projectId, DprId = id, FileName = p.FileName, ContentType = p.ContentType, Caption = p.Caption, TakenBy = p.TakenBy, Size = p.Size, Sha256 = p.Sha256, Content = p.Content });
            db.SaveChanges();
        }
        Publish(projectId, "dpr", id, "revise");
        return DprAsync(projectId, id);
    }

    public Task<bool> VerifyDprAsync(int projectId, int dprId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Dpr, Act.View);
        return Done(new DprService(db, "").VerifyIntegrity(Tracked(db, projectId, dprId)));
    }

    // ───────────────────────────── photos ─────────────────────────────

    private const int MaxPhotoBytes = 8 * 1024 * 1024, MaxPhotosPerReport = 40;

    private static PhotoInfo Info(DprPhoto p) => new(p.Id, p.DprId, p.FileName, p.ContentType, p.Caption, p.TakenBy, p.Size, p.CreatedAt);

    public Task<List<PhotoInfo>> PhotosAsync(int projectId, int dprId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Dpr, Act.View);
        return Done(db.Photos.AsNoTracking().Where(p => p.ProjectId == projectId && p.DprId == dprId).OrderBy(p => p.Id)
            .Select(p => new PhotoInfo(p.Id, p.DprId, p.FileName, p.ContentType, p.Caption, p.TakenBy, p.Size, p.CreatedAt)).ToList());
    }

    /// <summary>Recognises JPEG, PNG and WebP by their first bytes (the file name is not trusted).</summary>
    public static string? ImageType(byte[] b) =>
        b.Length > 12 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
        : b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b.Length > 12 && b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' && b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P' ? "image/webp"
        : null;

    private void RequireEditable(AppDbContext db, int projectId, Dpr d)
    {
        var role = RoleOn(db, projectId);
        var own = string.Equals(d.PreparedBy, caller.Name, StringComparison.OrdinalIgnoreCase);
        if (!Permissions.Can(role, Module.Dpr, Act.Edit) && !(own && Permissions.Can(role, Module.Dpr, Act.Create)))
            throw new ForbiddenException($"Your role ({Permissions.Label(role)}) cannot change this report.");
        if (!d.IsEditable) throw new WorkflowException($"{d.Number} is {d.Status}: photos can only be added or removed while it is a draft (start a revision to correct it).");
    }

    public Task<PhotoInfo> AddPhotoAsync(int projectId, int dprId, string fileName, byte[] content, string? caption)
    {
        DprPhoto photo;
        using (var db = dbFactory())
        {
            var d = db.Dprs.FirstOrDefault(x => x.Id == dprId && x.ProjectId == projectId) ?? throw new NotFoundException("Report not found.");
            RequireEditable(db, projectId, d);
            if (content is null || content.Length == 0) throw new WorkflowException("The photo is empty.");
            if (content.Length > MaxPhotoBytes) throw new WorkflowException($"The photo is {content.Length / 1048576.0:0.0} MB; the limit is 8 MB.");
            var type = ImageType(content) ?? throw new WorkflowException("Only JPEG, PNG or WebP photos can be attached.");
            if (db.Photos.Count(p => p.DprId == dprId) >= MaxPhotosPerReport) throw new WorkflowException($"A report can hold at most {MaxPhotosPerReport} photos.");
            var raw = string.IsNullOrWhiteSpace(fileName) ? "photo" : fileName.Trim();
            var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..]; // Windows and Android paths, whatever the server's OS
            if (name.Length == 0) name = "photo";
            photo = new DprPhoto
            {
                ProjectId = projectId, DprId = dprId, FileName = name.Length > 120 ? name[..120] : name, ContentType = type,
                Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim(), TakenBy = caller.Name, Size = content.Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant(), Content = content,
            };
            db.Photos.Add(photo);
            db.SaveChanges();
            Audit(db, projectId, "Photo", photo.Id, "add", $"{d.Number}: {photo.FileName}");
        }
        Publish(projectId, "dpr", dprId, "photo");
        return Done(Info(photo));
    }

    public Task DeletePhotoAsync(int projectId, int photoId)
    {
        int dprId;
        using (var db = dbFactory())
        {
            var p = db.Photos.FirstOrDefault(x => x.Id == photoId && x.ProjectId == projectId) ?? throw new NotFoundException("Photo not found.");
            var d = db.Dprs.First(x => x.Id == p.DprId);
            RequireEditable(db, projectId, d);
            dprId = d.Id;
            db.Photos.Remove(p);
            db.SaveChanges();
            Audit(db, projectId, "Photo", photoId, "delete", $"{d.Number}: {p.FileName}");
        }
        Publish(projectId, "dpr", dprId, "photo");
        return Task.CompletedTask;
    }

    public Task<FileDownload> PhotoAsync(int projectId, int photoId)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Dpr, Act.View);
        var p = db.Photos.AsNoTracking().FirstOrDefault(x => x.Id == photoId && x.ProjectId == projectId) ?? throw new NotFoundException("Photo not found.");
        return Done(new FileDownload(p.FileName, p.ContentType, p.Content));
    }

    /// <summary>
    /// Latest change number of a project (the newest audit entry). Apps working straight on a shared SQL database
    /// poll this every few seconds and reload when it moves: live updates without a server.
    /// </summary>
    public Task<int> ChangeStampAsync(int projectId)
    {
        using var db = dbFactory();
        RoleOn(db, projectId);
        return Done(db.Audit.Where(a => a.ProjectId == projectId).Select(a => (int?)a.Id).Max() ?? 0);
    }

    // ───────────────────────────── audit & reports ─────────────────────────────

    public Task<FileDownload> PeriodReportAsync(int projectId, DateOnly from, DateOnly to, bool excel)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Reports, Act.Export);
        var summary = new PeriodService(db).Build(projectId, from, to);
        var name = $"{summary.ProjectCode}-progress-{from:yyyyMMdd}-{to:yyyyMMdd}";
        return Done(excel
            ? new FileDownload(name + ".xlsx", Xlsx, ExcelService.ExportPeriod(summary))
            : new FileDownload(name + ".pdf", Pdf, PeriodPdfReport.Generate(summary)));
    }

    public Task<List<AuditEntry>> AuditAsync(int projectId, int take = 50)
    {
        using var db = dbFactory();
        Require(db, projectId, Module.Reports, Act.View);
        return Done(db.Audit.AsNoTracking().Where(a => a.ProjectId == projectId).OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToList());
    }

    public Task<FileDownload> ReportAsync(int projectId, ReportKind kind, int? dprId = null)
    {
        using var db = dbFactory();
        if (kind == ReportKind.ActivityTemplate)
        {
            Require(db, projectId, Module.Masters, Act.View);
            return Done(new FileDownload("activity-import-template.xlsx", Xlsx, ExcelService.ActivityTemplate()));
        }
        Require(db, projectId, Module.Reports, Act.Export);
        var p = db.Projects.AsNoTracking().First(x => x.Id == projectId);
        if (kind is ReportKind.DprPdf or ReportKind.DprExcel)
        {
            var d = LoadForView(db, projectId, dprId ?? throw new WorkflowException("Choose a report."));
            var name = $"{d.Number}-R{d.Revision}";
            return Done(kind == ReportKind.DprPdf
                ? new FileDownload(name + ".pdf", Pdf, DprPdfReport.Generate(d, p,
                    db.Photos.AsNoTracking().Where(x => x.DprId == d.Id).OrderBy(x => x.Id).ToList()
                        .Select(x => (x.Caption ?? x.FileName, x.Content)).ToList()))
                : new FileDownload(name + ".xlsx", Xlsx, ExcelService.ExportDpr(d, p)));
        }
        var acts = db.Activities.AsNoTracking().Where(a => a.ProjectId == projectId).OrderBy(a => a.SortOrder).ToList();
        var stem = $"{p.Code}-schedule-{Today:yyyyMMdd}";
        return Done(kind == ReportKind.SchedulePdf
            ? new FileDownload(stem + ".pdf", Pdf, SchedulePdfReport.Generate(p, acts, db.Dependencies.AsNoTracking().Where(d => d.ProjectId == projectId).ToList(), Today))
            : new FileDownload(stem + ".xlsx", Xlsx, ExcelService.ExportSchedule(p, acts)));
    }

    private const string Pdf = "application/pdf";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}
