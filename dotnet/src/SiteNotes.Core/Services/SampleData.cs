using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core;

/// <summary>A small realistic project (WBS, resources with rates, activities, 5 days of DPRs) to explore the app.</summary>
public static class SampleData
{
    public static async Task<Project> CreateAsync(LocalDatabase db, DateTime? today = null)
    {
        var t = (today ?? DateTime.Today).Date;
        var p = new Project
        {
            Name = "Sample – Water Treatment Plant", Code = "WTP-01", Client = "City Water Board", Contractor = "BuildRight Infra",
            Consultant = "AquaTech PMC", Location = "Nashik", StartDate = t.AddDays(-30), EndDate = t.AddDays(300),
        };
        await db.SaveAsync(p);
        var pid = p.Id;
        await db.SaveAsync(new WorkCalendar { ProjectId = pid, HoursPerDay = 8, WorkingDays = "123456", OvertimeFactor = 1.5m });

        var depts = Lists.DefaultDepartments.Take(6).Select(n => new Department { ProjectId = pid, Name = n }).ToList();
        await db.SaveAllAsync(depts);
        var civil = depts[0];
        var formwork = new Subcontractor { ProjectId = pid, Name = "Shree Formwork", Trade = "Formwork", Phone = "98xxxxxx01" };
        var steel = new Subcontractor { ProjectId = pid, Name = "Om Rebar Works", Trade = "Reinforcement", Phone = "98xxxxxx02" };
        await db.SaveAllAsync([formwork, steel]);

        var plant = new WbsNode { ProjectId = pid, Code = "P1", Name = "Treatment plant", Level = WbsLevel.Plant };
        var clarifier = new WbsNode { ProjectId = pid, Code = "U1", Name = "Clarifier", Level = WbsLevel.Unit, ParentId = plant.Id };
        var filter = new WbsNode { ProjectId = pid, Code = "U2", Name = "Filter house", Level = WbsLevel.Unit, ParentId = plant.Id };
        await db.SaveAllAsync([plant, clarifier, filter]);

        Resource R(string code, string name, ResourceType type, decimal rate, RateBasis basis, string unit = "Nos", string sub = "", string dept = "") =>
            new() { ProjectId = pid, Code = code, Name = name, Type = type, Rate = rate, Basis = basis, Unit = unit, SubcontractorId = sub, DepartmentId = dept };
        var carpenter = R("L-CAR", "Carpenter", ResourceType.Manpower, 950, RateBasis.PerDay, sub: formwork.Id);
        var helper = R("L-HLP", "Helper", ResourceType.Manpower, 650, RateBasis.PerDay, sub: formwork.Id);
        var barBender = R("L-BAR", "Bar bender", ResourceType.Manpower, 900, RateBasis.PerDay, sub: steel.Id);
        var engineer = R("S-ENG", "Site engineer", ResourceType.Manpower, 2500, RateBasis.PerDay, dept: civil.Id);
        var jcb = R("M-JCB", "JCB", ResourceType.Machinery, 1200, RateBasis.PerHour);
        var pump = R("M-PMP", "Concrete pump", ResourceType.Machinery, 2200, RateBasis.PerHour);
        var rmc = R("C-RMC", "RMC M30", ResourceType.Material, 5600, RateBasis.PerUnit, "m³");
        var tmt = R("C-TMT", "TMT steel Fe500D", ResourceType.Material, 62000, RateBasis.PerUnit, "MT");
        var testing = R("O-TST", "Cube testing", ResourceType.Other, 1500, RateBasis.LumpSum, "LS");
        await db.SaveAllAsync([carpenter, helper, barBender, engineer, jcb, pump, rmc, tmt, testing]);

        Activity A(string code, string name, WbsNode w, string unit, double qty, int startOffset, int days, Subcontractor? s, string method) =>
            new()
            {
                ProjectId = pid, Code = code, Name = name, WbsId = w.Id, Unit = unit, PlannedQty = qty, SubcontractorId = s?.Id ?? "",
                DepartmentId = civil.Id, Method = method, PlannedStart = t.AddDays(startOffset), PlannedFinish = t.AddDays(startOffset + days),
                PlannedRatePerDay = Math.Round(qty / Math.Max(1, days * 6 / 7.0), 2),
            };
        var exc = A("C-100", "Excavation", clarifier, "m³", 1800, -12, 14, null, "Mechanised");
        var raft = A("C-110", "Raft reinforcement", clarifier, "MT", 42, -6, 12, steel, "Conventional");
        var shut = A("C-120", "Wall shuttering", clarifier, "m²", 950, -2, 20, formwork, "Conventional shuttering");
        var ffound = A("C-200", "Filter house footing", filter, "m³", 120, 3, 10, formwork, "Pump placement");
        await db.SaveAllAsync([exc, raft, shut, ffound]);
        await db.SaveAllAsync(new[]
        {
            new ActivityResource { ProjectId = pid, ActivityId = exc.Id, ResourceId = jcb.Id, ResourceName = jcb.Name, Type = jcb.Type, PlannedQty = 2, Rate = jcb.Rate, Basis = jcb.Basis },
            new ActivityResource { ProjectId = pid, ActivityId = raft.Id, ResourceId = barBender.Id, ResourceName = barBender.Name, Type = barBender.Type, PlannedQty = 10, Rate = barBender.Rate, Basis = barBender.Basis },
            new ActivityResource { ProjectId = pid, ActivityId = raft.Id, ResourceId = tmt.Id, ResourceName = tmt.Name, Type = tmt.Type, PlannedQty = 42, Rate = tmt.Rate, Basis = tmt.Basis },
            new ActivityResource { ProjectId = pid, ActivityId = shut.Id, ResourceId = carpenter.Id, ResourceName = carpenter.Name, Type = carpenter.Type, PlannedQty = 8, Rate = carpenter.Rate, Basis = carpenter.Basis },
            new ActivityResource { ProjectId = pid, ActivityId = shut.Id, ResourceId = helper.Id, ResourceName = helper.Name, Type = helper.Type, PlannedQty = 8, Rate = helper.Rate, Basis = helper.Basis },
        });

        var rnd = new Random(7);
        for (var d = -5; d <= -1; d++)
        {
            var date = t.AddDays(d);
            if (date.DayOfWeek == DayOfWeek.Sunday) continue;
            var b = await db.NewReportAsync(pid, date, carryForward: false);
            b.Report.WeatherMorning = d == -3 ? "Light rain" : "Sunny";
            b.Report.ToolboxTalkTopic = "Working at height";
            b.Report.ToolboxTalkAttendees = 30 + rnd.Next(10);
            b.Report.PreparedBy = "R. Patil";
            b.Report.Status = d < -2 ? ReportStatus.Approved : ReportStatus.Submitted;
            b.Activities.Add(new ReportActivity { ActivityId = exc.Id, TodayQty = 120 + rnd.Next(40) });
            b.Activities.Add(new ReportActivity { ActivityId = raft.Id, TodayQty = Math.Round(3 + rnd.NextDouble() * 2, 2), SubcontractorId = steel.Id, SubcontractorName = steel.Name });
            if (d >= -2) b.Activities.Add(new ReportActivity { ActivityId = shut.Id, TodayQty = 40 + rnd.Next(20), SubcontractorId = formwork.Id, SubcontractorName = formwork.Name });
            b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = steel.Id, PartyName = steel.Name, Trade = "Bar bender", Count = 9 + rnd.Next(3), Hours = 8, Rate = 900, ActivityId = raft.Id, ActivityName = raft.ToString(), ResourceId = barBender.Id });
            if (d >= -2)
            {
                b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = formwork.Id, PartyName = formwork.Name, Trade = "Carpenter", Count = 8, Hours = 9, Rate = 950, ActivityId = shut.Id, ActivityName = shut.ToString() });
                b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = formwork.Id, PartyName = formwork.Name, Trade = "Helper", Count = 10, Hours = 8, Rate = 650, ActivityId = shut.Id, ActivityName = shut.ToString() });
            }
            b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Department, PartyId = civil.Id, PartyName = civil.Name, Trade = "Site engineer", Count = 2, Hours = 9, Rate = 2500 });
            b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Department, PartyId = depts[5].Id, PartyName = depts[5].Name, Trade = "Safety officer", Count = 1, Hours = 8, Rate = 2000 });
            b.Machines.Add(new ReportMachine { Name = "JCB", Ownership = MachineOwnership.Hired, Count = 2, WorkingHours = 7, IdleHours = d == -3 ? 2 : 0, Rate = 1200, Basis = RateBasis.PerHour, FuelLitres = 70, ActivityId = exc.Id, ActivityName = exc.ToString() });
            b.Materials.Add(new ReportMaterial { Material = "TMT steel Fe500D", Unit = "MT", Received = d == -5 ? 20 : 0, Consumed = b.Activities[1].TodayQty, Rate = 62000, Basis = RateBasis.PerUnit, ActivityId = raft.Id, ActivityName = raft.ToString() });
            if (d == -3)
                b.Hindrances.Add(new Hindrance { ProjectId = pid, FromDate = date, ToDate = date, Category = "Weather", Description = "Rain stopped excavation for 2 hours", ResponsibleParty = "Force majeure", HoursLost = 2, Status = HindranceStatus.Resolved, AffectedActivity = exc.Name });
            if (d == -1)
                b.Requirements.Add(new Requirement { ProjectId = pid, RaisedDate = date, Item = "Plywood 12 mm for wall shuttering", Quantity = 120, Unit = "Nos", RequiredBy = t.AddDays(2), Priority = Priority.High, RaisedBy = "R. Patil" });
            b.Report.Remarks = "Work progressing as per plan.";
            b.Report.PlanForTomorrow = "Continue excavation and raft reinforcement; start wall shuttering.";
            await db.SaveReportAsync(b);
        }
        return p;
    }
}
