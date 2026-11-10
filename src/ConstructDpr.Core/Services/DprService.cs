using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Calculations;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Core.Services;


/// <summary>
/// Daily progress report workflow: Draft → Submitted → Approved (frozen) or Rejected (with reason) → resubmitted.
/// Corrections to an approved report are new revisions; the approved revision is never changed.
/// </summary>
public sealed class DprService(AppDbContext db, string currentUser)
{
    public Dpr Load(int id) =>
        db.Dprs.Include(d => d.Activities).ThenInclude(l => l.Activity)
            .Include(d => d.Manpower).Include(d => d.Equipment).Include(d => d.Materials)
            .Include(d => d.Project).AsSplitQuery().First(d => d.Id == id);

    public List<Dpr> List(int projectId) =>
        db.Dprs.Where(d => d.ProjectId == projectId).OrderByDescending(d => d.ReportDate).ThenByDescending(d => d.Revision).ToList();

    public Dpr Create(int projectId, DateOnly date, Shift shift)
    {
        if (date > DateOnly.FromDateTime(DateTime.Today).AddDays(1)) throw new WorkflowException("The report date cannot be in the future.");
        if (db.Dprs.Any(d => d.ProjectId == projectId && d.ReportDate == date && d.Shift == shift && d.Revision == 0))
            throw new WorkflowException($"A report for {date:dd MMM yyyy} ({shift} shift) already exists. Open it instead.");
        var project = db.Projects.First(p => p.Id == projectId);
        var seq = db.Dprs.Where(d => d.ProjectId == projectId && d.Revision == 0).Count() + 1;
        var dpr = new Dpr { ProjectId = projectId, ReportDate = date, Shift = shift, Number = $"{project.Code}-DPR-{seq:0000}", PreparedBy = currentUser };
        db.Dprs.Add(dpr);
        db.SaveChanges();
        Audit(dpr, "create");
        return dpr;
    }

    /// <summary>Accepted cumulative quantity per activity from the latest approved revision of earlier reports.</summary>
    public Dictionary<int, decimal> PriorAccepted(int projectId, DateOnly date, string? excludeNumber)
    {
        var approved = db.Dprs.Where(d => d.ProjectId == projectId && d.Status == DprStatus.Approved && d.Number != excludeNumber && d.ReportDate < date)
            .ToList().GroupBy(d => d.Number).Select(g => g.OrderByDescending(x => x.Revision).First().Id).ToList();
        return db.DprActivityLines.Where(l => approved.Contains(l.DprId)).ToList()
            .GroupBy(l => l.ActivityId).ToDictionary(g => g.Key, g => g.Sum(l => l.QtyAccepted ?? l.QtyToday));
    }

    /// <summary>Recomputes previous / cumulative / balance / % for every line (server-side truth).</summary>
    public void Recalculate(Dpr dpr, bool useAccepted)
    {
        var prior = PriorAccepted(dpr.ProjectId, dpr.ReportDate, dpr.Number);
        foreach (var l in dpr.Activities)
        {
            var a = l.Activity ??= db.Activities.First(x => x.Id == l.ActivityId);
            Progress.ApplyLine(l, a, prior.GetValueOrDefault(l.ActivityId), dpr.ReportDate, useAccepted);
        }
    }

    public static decimal? PlannedPct(Activity a, DateOnly on) => Progress.PlannedPct(a, on);

    public List<string> Validate(Dpr dpr, bool approving)
    {
        var errors = new List<string>();
        if (dpr.Activities.Count == 0 && string.IsNullOrWhiteSpace(dpr.WorkCompleted))
            errors.Add("Add at least one activity or describe the work completed.");
        Recalculate(dpr, approving);
        foreach (var l in dpr.Activities)
        {
            var a = l.Activity ?? db.Activities.First(x => x.Id == l.ActivityId);
            if (l.QtyToday < 0 || l.QtyAccepted < 0) errors.Add($"{a.Code}: quantities cannot be negative.");
            if (a.TotalQty > 0 && l.CumQty > a.TotalQty)
                errors.Add($"{a.Code}: cumulative {l.CumQty:0.###} exceeds the approved quantity {a.TotalQty:0.###} {a.Unit}.");
        }
        foreach (var e in dpr.Equipment)
        {
            if (e.WorkingHours + e.IdleHours + e.BreakdownHours > 24) errors.Add($"{e.Code}: hours exceed 24.");
            if (e.BreakdownHours > 0 && string.IsNullOrWhiteSpace(e.BreakdownReason)) errors.Add($"{e.Code}: enter the breakdown reason.");
        }
        foreach (var m in dpr.Materials)
            if (m.Closing < 0) errors.Add($"{m.Code}: issue exceeds stock (closing {m.Closing:0.###}).");
        return errors;
    }

    public void Save(Dpr dpr)
    {
        if (!dpr.IsEditable) throw new WorkflowException($"{dpr.Number} is {dpr.Status} and can no longer be edited.");
        Recalculate(dpr, false);
        db.SaveChanges();
    }

    public void Submit(Dpr dpr)
    {
        if (!dpr.IsEditable) throw new WorkflowException($"{dpr.Number} is already {dpr.Status}.");
        var errors = Validate(dpr, false);
        if (errors.Count > 0) throw new WorkflowException(string.Join(Environment.NewLine, errors));
        dpr.Status = DprStatus.Submitted;
        dpr.ReviewComment = null;
        db.SaveChanges();
        Audit(dpr, "submit");
    }

    public void Reject(Dpr dpr, string reason)
    {
        if (dpr.Status != DprStatus.Submitted) throw new WorkflowException("Only submitted reports can be rejected.");
        if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("Enter a reason for rejection.");
        dpr.Status = DprStatus.Rejected;
        dpr.ReviewComment = reason.Trim();
        db.SaveChanges();
        Audit(dpr, "reject", reason);
    }

    public void Approve(Dpr dpr)
    {
        if (dpr.Status != DprStatus.Submitted) throw new WorkflowException("Only submitted reports can be approved.");
        var project = db.Projects.First(p => p.Id == dpr.ProjectId);
        if (string.Equals(dpr.PreparedBy, currentUser, StringComparison.OrdinalIgnoreCase) && !project.AllowSelfApproval)
            throw new WorkflowException("You cannot approve a report you prepared (project policy).");
        foreach (var l in dpr.Activities) l.QtyAccepted ??= l.QtyToday;
        var errors = Validate(dpr, true);
        if (errors.Count > 0) throw new WorkflowException(string.Join(Environment.NewLine, errors));
        dpr.Status = DprStatus.Approved;
        dpr.ApprovedBy = currentUser;
        dpr.ApprovedAt = DateTime.UtcNow;
        dpr.SnapshotHash = Hash(dpr, PhotoHashes(dpr.Id));
        foreach (var l in dpr.Activities)
        {
            var a = l.Activity ?? db.Activities.First(x => x.Id == l.ActivityId);
            if ((l.QtyAccepted ?? 0) > 0 && a.ActualStart is null) a.ActualStart = dpr.ReportDate;
            if (a.TotalQty > 0 && l.CumQty >= a.TotalQty) { a.Status = ActivityStatus.Completed; a.ActualFinish ??= dpr.ReportDate; }
            else if ((l.QtyAccepted ?? 0) > 0 && a.Status == ActivityStatus.NotStarted) a.Status = ActivityStatus.InProgress;
        }
        db.SaveChanges();
        RefreshAcceptedQuantities(dpr.ProjectId);
        Audit(dpr, "approve");
    }

    /// <summary>Starts a correction of an approved report as a new draft revision.</summary>
    public Dpr StartRevision(Dpr approved, string reason)
    {
        if (approved.Status != DprStatus.Approved) throw new WorkflowException("Only approved reports are revised.");
        if (string.IsNullOrWhiteSpace(reason)) throw new WorkflowException("Enter the reason for the revision.");
        var rev = new Dpr
        {
            ProjectId = approved.ProjectId, Number = approved.Number, Revision = db.Dprs.Where(d => d.Number == approved.Number && d.ProjectId == approved.ProjectId).Max(d => d.Revision) + 1,
            ReportDate = approved.ReportDate, Shift = approved.Shift, Weather = approved.Weather, SiteConditions = approved.SiteConditions,
            PreparedBy = currentUser, WorkCompleted = approved.WorkCompleted, DelayReasons = approved.DelayReasons,
            TomorrowPlan = approved.TomorrowPlan, SafetyNotes = approved.SafetyNotes, ReviewComment = "Revision: " + reason.Trim(),
            Activities = approved.Activities.Select(l => new DprActivityLine { ActivityId = l.ActivityId, QtyToday = l.QtyToday, DelayReason = l.DelayReason, Remarks = l.Remarks }).ToList(),
            Manpower = approved.Manpower.Select(m => new ManpowerLine { Agency = m.Agency, Trade = m.Trade, Skilled = m.Skilled, SemiSkilled = m.SemiSkilled, Unskilled = m.Unskilled, Supervisors = m.Supervisors, Hours = m.Hours }).ToList(),
            Equipment = approved.Equipment.Select(e => new EquipmentLine { Code = e.Code, Type = e.Type, WorkingHours = e.WorkingHours, IdleHours = e.IdleHours, BreakdownHours = e.BreakdownHours, BreakdownReason = e.BreakdownReason }).ToList(),
            Materials = approved.Materials.Select(m => new MaterialLine { Code = m.Code, Description = m.Description, Unit = m.Unit, Opening = m.Opening, Received = m.Received, Issued = m.Issued, ChallanNo = m.ChallanNo }).ToList(),
        };
        db.Dprs.Add(rev);
        db.SaveChanges();
        Audit(rev, "revise", reason);
        return rev;
    }

    /// <summary>Activity accepted totals from the latest approved revision of every report.</summary>
    public void RefreshAcceptedQuantities(int projectId)
    {
        var latest = db.Dprs.Where(d => d.ProjectId == projectId && d.Status == DprStatus.Approved).ToList()
            .GroupBy(d => d.Number).Select(g => g.OrderByDescending(x => x.Revision).First().Id).ToList();
        var sums = db.DprActivityLines.Where(l => latest.Contains(l.DprId)).ToList()
            .GroupBy(l => l.ActivityId).ToDictionary(g => g.Key, g => g.Sum(l => l.QtyAccepted ?? l.QtyToday));
        foreach (var a in db.Activities.Where(a => a.ProjectId == projectId)) a.AcceptedQty = sums.GetValueOrDefault(a.Id);
        db.SaveChanges();
    }

    /// <summary>True when the stored approved content still matches its snapshot hash.</summary>
    public bool VerifyIntegrity(Dpr dpr) => dpr.Status != DprStatus.Approved || dpr.SnapshotHash == Hash(dpr, PhotoHashes(dpr.Id));

    private List<string> PhotoHashes(int dprId) =>
        db.Photos.Where(p => p.DprId == dprId).OrderBy(p => p.Id).Select(p => p.Sha256).ToList();

    private static string Hash(Dpr d, List<string> photos)
    {
        object content = new
        {
            d.Number, d.Revision, d.ReportDate, d.Shift, d.Weather, d.SiteConditions, d.WorkCompleted, d.DelayReasons, d.TomorrowPlan, d.SafetyNotes,
            Activities = d.Activities.OrderBy(l => l.ActivityId).Select(l => new { l.ActivityId, l.QtyToday, l.QtyAccepted, l.CumQty }),
            Manpower = d.Manpower.Select(m => new { m.Agency, m.Trade, m.Skilled, m.SemiSkilled, m.Unskilled, m.Supervisors, m.Hours }),
            Equipment = d.Equipment.Select(e => new { e.Code, e.WorkingHours, e.IdleHours, e.BreakdownHours }),
            Materials = d.Materials.Select(m => new { m.Code, m.Opening, m.Received, m.Issued }),
        };
        if (photos.Count > 0) content = new { Report = content, Photos = photos }; // reports without photos keep their original hash
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content)))).ToLowerInvariant();
    }

    private void Audit(Dpr d, string action, string? details = null)
    {
        db.Audit.Add(new AuditEntry { ProjectId = d.ProjectId, User = currentUser, Entity = "DPR", EntityId = d.Id, Action = action, Details = details ?? $"{d.Number} R{d.Revision}" });
        db.SaveChanges();
    }
}
