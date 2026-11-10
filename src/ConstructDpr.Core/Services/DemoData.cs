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

        // One approved report per working day since the work started: quantities follow the baseline at about 85 %
        // (rain on some days), so the project is realistically a little behind plan.
        var service = new DprService(db, user);
        var cal = schedule.CalendarFor(p);
        var measured = acts.Where(a => a.TotalQty > 0).ToList();
        var done = measured.ToDictionary(a => a.Id, _ => 0m);
        decimal Round(Activity a, decimal q) => a.Unit == "MT" ? Math.Round(q * 2, MidpointRounding.AwayFromZero) / 2 : Math.Round(q, MidpointRounding.AwayFromZero);
        decimal Planned(Activity a, DateOnly d)
        {
            if (a.BaselineStart is not { } bs || a.BaselineFinish is not { } bf || d < bs || d > bf) return 0;
            var days = cal.IndexOf(bf) - cal.IndexOf(bs) + 1;
            return a.TotalQty / Math.Max(1, days);
        }
        var day = 0;
        for (var date = start; date < today; date = date.AddDays(1))
        {
            if (!cal.IsWorkday(date)) continue;
            day++;
            var rain = day % 7 == 4;
            var factor = rain ? 0.4m : 0.9m + (day % 3) * 0.05m;
            var lines = new List<DprActivityLine>();
            foreach (var a in measured)
            {
                var q = Round(a, Planned(a, date) * factor);
                q = Math.Min(q, a.TotalQty - done[a.Id]);
                if (q <= 0) continue;
                done[a.Id] += q;
                lines.Add(new DprActivityLine { ActivityId = a.Id, QtyToday = q, DelayReason = rain ? "Rain stoppage 2 h" : null });
            }
            if (lines.Count == 0) continue;
            var dpr = service.Create(p.Id, date, Shift.Day);
            dpr.Weather = rain ? "Rain 10:00–12:00" : "Clear, 33 °C";
            dpr.SiteConditions = rain ? "Wet ground near grid C; work stopped 2 h" : "Normal";
            dpr.WorkCompleted = "Work progressed as listed: " + string.Join(", ", lines.Select(l => acts.First(a => a.Id == l.ActivityId).Name.Split(" — ")[0].ToLowerInvariant()).Distinct()) + ".";
            dpr.TomorrowPlan = "Continue the same fronts; next pour as per the 3-week look-ahead.";
            dpr.DelayReasons = rain ? "Rain stoppage" : null;
            dpr.SafetyNotes = day % 5 == 0 ? "Toolbox talk: work at height. Near miss reported at grid B (dropped spanner); barricading improved." : "Toolbox talk held. No incidents.";
            dpr.Activities.AddRange(lines);
            dpr.Manpower.Add(new ManpowerLine { Agency = "Bharat Civil Works", Trade = "Civil", Skilled = 16 + day % 5, SemiSkilled = 12, Unskilled = 26 + day % 6, Supervisors = 2 });
            if (lines.Any(l => acts.First(a => a.Id == l.ActivityId).Wbs!.StartsWith("2")))
                dpr.Manpower.Add(new ManpowerLine { Agency = "Sterling Steel Erectors", Trade = "Structural", Skilled = 18 + day % 8, SemiSkilled = 8, Unskilled = 10, Supervisors = 2 });
            dpr.Equipment.Add(new EquipmentLine { Code = "CR-01", Type = "Mobile crane 100 T", WorkingHours = rain ? 5 : 7, IdleHours = rain ? 4 : 2 });
            dpr.Equipment.Add(new EquipmentLine { Code = "EX-02", Type = "Excavator", WorkingHours = day % 9 == 5 ? 3 : 8, BreakdownHours = day % 9 == 5 ? 4 : 0, BreakdownReason = day % 9 == 5 ? "Hydraulic hose failure" : null });
            dpr.Materials.Add(new MaterialLine { Code = "CEM-OPC53", Description = "Cement OPC 53", Unit = "bags", Opening = 400 + day * 10, Received = day % 2 == 0 ? 600 : 0, Issued = 220, ChallanNo = $"CH-{5100 + day}" });
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
        foreach (var a in measured)
        {
            var q = Math.Min(Round(a, Planned(a, today) * 0.95m), a.TotalQty - done[a.Id]);
            if (q > 0) pending.Activities.Add(new DprActivityLine { ActivityId = a.Id, QtyToday = q });
        }
        if (pending.Activities.Count == 0) pending.Activities.Add(new DprActivityLine { ActivityId = Id("A2000"), QtyToday = 1 });
        pending.Manpower.Add(new ManpowerLine { Agency = "Bharat Civil Works", Trade = "Civil", Skilled = 20, SemiSkilled = 12, Unskilled = 28, Supervisors = 2 });
        pending.Manpower.Add(new ManpowerLine { Agency = "Sterling Steel Erectors", Trade = "Structural", Skilled = 27, SemiSkilled = 8, Unskilled = 10, Supervisors = 2 });
        pending.Equipment.Add(new EquipmentLine { Code = "CR-01", Type = "Mobile crane 100 T", WorkingHours = 8, IdleHours = 1 });
        site.Save(pending);
        site.Submit(pending);

        schedule.Run(p.Id, today);
        return p;
    }
}
