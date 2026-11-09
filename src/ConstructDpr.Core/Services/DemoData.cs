using ConstructDpr.Core.Data;
using ConstructDpr.Shared.Model;
using ConstructDpr.Shared.Security;

namespace ConstructDpr.Core.Services;

/// <summary>Creates a realistic sample project, scheduled, baselined and with a week of approved reports.</summary>
public static class DemoData
{
    public static Project Create(AppDbContext db, DateOnly today, string user = "demo.engineer")
    {
        var start = today.AddDays(-28);
        // Project codes are unique on a shared server: DEMO-BF2, DEMO-BF2-2, DEMO-BF2-3 …
        var code = "DEMO-BF2";
        for (var n = 2; db.Projects.Any(x => x.Code == code); n++) code = $"DEMO-BF2-{n}";
        var p = new Project
        {
            Code = code, Name = "Blast Furnace #2 — Skip Bridge & Stockhouse", Client = "Demo Steel Ltd",
            Contractor = "Demo Industrial Constructions", WorkOrderNo = "WO/BF2/2026/017", SiteAddress = "Plant Road, Industrial Area",
            StartDate = start, CompletionDate = start.AddDays(150), AllowSelfApproval = true,
        };
        db.Projects.Add(p);
        db.SaveChanges();
        var defs = new (string Code, string Name, string Wbs, string Unit, decimal Qty, decimal Rate, decimal W, int Dur, bool Ms)[]
        {
            ("A1000", "Excavation — skip bridge foundations", "1 Civil", "m³", 1200, 350, 4, 12, false),
            ("A1010", "RCC foundations — skip bridge", "1 Civil", "m³", 800, 7800, 12, 22, false),
            ("A2000", "Fabrication — skip bridge trusses", "2 Structural", "MT", 300, 85000, 25, 35, false),
            ("A2010", "Erection — skip bridge structure", "2 Structural", "MT", 300, 18000, 15, 30, false),
            ("A2020", "Erection — stockhouse structure", "2 Structural", "MT", 150, 18000, 8, 25, false),
            ("A3000", "Vibrating screens & feeders", "3 Mechanical", "MT", 120, 22000, 10, 25, false),
            ("A4000", "Refractory lining — cast house runners", "4 Refractory", "m³", 300, 45000, 18, 30, false),
            ("A5000", "No-load trials — stockhouse", "5 Commissioning", null!, 0, 0, 6, 8, false),
            ("M9000", "Mechanical completion", "5 Commissioning", null!, 0, 0, 0, 0, true),
        };
        var acts = defs.Select((d, i) => new Activity
        {
            ProjectId = p.Id, Code = d.Code, Name = d.Name, Wbs = d.Wbs, Unit = d.Unit, TotalQty = d.Qty, Rate = d.Rate,
            Weight = d.W, Duration = d.Dur, IsMilestone = d.Ms, SortOrder = i, Responsible = d.Wbs.Split(' ')[1] + " engineer",
        }).ToList();
        db.Activities.AddRange(acts);
        db.SaveChanges();
        int Id(string c) => acts.First(a => a.Code == c).Id;
        db.Dependencies.AddRange(
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A1000"), SuccessorId = Id("A1010"), Type = DependencyType.SS, Lag = 6 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A1010"), SuccessorId = Id("A2010"), Type = DependencyType.SS, Lag = 8 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A2000"), SuccessorId = Id("A2010"), Type = DependencyType.SS, Lag = 12 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A2010"), SuccessorId = Id("A2020"), Type = DependencyType.SS, Lag = 15 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A2020"), SuccessorId = Id("A3000"), Type = DependencyType.FS, Lag = -5 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A3000"), SuccessorId = Id("A4000"), Type = DependencyType.SS, Lag = 10 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A4000"), SuccessorId = Id("A5000"), Type = DependencyType.FS, Lag = 0 },
            new Dependency { ProjectId = p.Id, PredecessorId = Id("A5000"), SuccessorId = Id("M9000"), Type = DependencyType.FS, Lag = 0 });
        db.Holidays.Add(new Holiday { ProjectId = p.Id, Date = start.AddDays(20), Name = "Festival holiday" });
        db.SaveChanges();

        var schedule = new ScheduleService(db);
        schedule.Run(p.Id);
        schedule.ApplyToPlan(p.Id);
        schedule.FreezeBaseline(p.Id, "Contract baseline");

        var service = new DprService(db, user);
        for (var i = 0; i < 7; i++)
        {
            var date = today.AddDays(i - 7);
            var dpr = service.Create(p.Id, date, Shift.Day);
            dpr.Weather = i == 3 ? "Rain 10:00–12:00" : "Clear, 34 °C";
            dpr.SiteConditions = i == 3 ? "Wet ground near grid C; work stopped 2 h" : "Normal";
            dpr.WorkCompleted = "Excavation, RCC and steel fabrication progressed as listed.";
            dpr.TomorrowPlan = "Continue truss erection T3–T4; pour footing F6.";
            dpr.DelayReasons = i == 3 ? "Rain stoppage" : null;
            dpr.SafetyNotes = "Toolbox talk: work at height. No incidents.";
            dpr.Activities.Add(new DprActivityLine { ActivityId = Id("A1000"), QtyToday = i == 3 ? 40 : 85, DelayReason = i == 3 ? "Rain" : null });
            dpr.Activities.Add(new DprActivityLine { ActivityId = Id("A1010"), QtyToday = i < 2 ? 0 : 32 });
            dpr.Activities.Add(new DprActivityLine { ActivityId = Id("A2000"), QtyToday = 7.5m });
            dpr.Manpower.Add(new ManpowerLine { Agency = "Bharat Civil Works", Trade = "Civil", Skilled = 18, SemiSkilled = 12, Unskilled = 30, Supervisors = 2 });
            dpr.Manpower.Add(new ManpowerLine { Agency = "Sterling Steel Erectors", Trade = "Structural", Skilled = 20 + i, SemiSkilled = 8, Unskilled = 10, Supervisors = 2 });
            dpr.Equipment.Add(new EquipmentLine { Code = "CR-01", Type = "Mobile crane 100 T", WorkingHours = 7, IdleHours = 2 });
            dpr.Equipment.Add(new EquipmentLine { Code = "EX-02", Type = "Excavator", WorkingHours = i == 5 ? 3 : 8, BreakdownHours = i == 5 ? 4 : 0, BreakdownReason = i == 5 ? "Hydraulic hose failure" : null });
            dpr.Materials.Add(new MaterialLine { Code = "CEM-OPC53", Description = "Cement OPC 53", Unit = "bags", Opening = 400 + i * 20, Received = i % 2 == 0 ? 600 : 0, Issued = 220, ChallanNo = $"CH-{5100 + i}" });
            service.Save(dpr);
            service.Submit(dpr);
            service.Approve(dpr);
        }
        // Today's report from the site supervisor, waiting for the planning engineer's review.
        var site = new DprService(db, "site.supervisor");
        var pending = site.Create(p.Id, today, Shift.Day);
        pending.Weather = "Clear, 33 °C";
        pending.SiteConditions = "Normal";
        pending.WorkCompleted = "Footing F6 poured; trusses T3–T4 erected with 100 T crane.";
        pending.TomorrowPlan = "Start purlins bay 1–3; backfill F1–F4.";
        pending.SafetyNotes = "Lifting permit LP-112 closed. No incidents.";
        pending.Activities.Add(new DprActivityLine { ActivityId = Id("A1000"), QtyToday = 90 });
        pending.Activities.Add(new DprActivityLine { ActivityId = Id("A1010"), QtyToday = 36 });
        pending.Activities.Add(new DprActivityLine { ActivityId = Id("A2000"), QtyToday = 9 });
        pending.Manpower.Add(new ManpowerLine { Agency = "Bharat Civil Works", Trade = "Civil", Skilled = 20, SemiSkilled = 12, Unskilled = 28, Supervisors = 2 });
        pending.Manpower.Add(new ManpowerLine { Agency = "Sterling Steel Erectors", Trade = "Structural", Skilled = 27, SemiSkilled = 8, Unskilled = 10, Supervisors = 2 });
        pending.Equipment.Add(new EquipmentLine { Code = "CR-01", Type = "Mobile crane 100 T", WorkingHours = 8, IdleHours = 1 });
        site.Save(pending);
        site.Submit(pending);

        schedule.Run(p.Id, today);
        return p;
    }
}
