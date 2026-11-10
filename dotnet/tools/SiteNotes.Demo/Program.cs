using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;
using SiteNotes.Core;
using SiteNotes.Core.Data;
using SiteNotes.Core.Imaging;
using SiteNotes.Core.Models;
using SiteNotes.Core.Reports;
using SiteNotes.Core.Sync;
using SkiaSharp;

// ============================================================================================
// SiteNotes heavy demo & self-check
// ============================================================================================

var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "demo-output");
var days = args.Length > 1 && int.TryParse(args[1], out var d) ? d : 180;
if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
Directory.CreateDirectory(outDir);
var work = Path.Combine(outDir, "_devices");
var log = new Log(Path.Combine(outDir, "demo-log.txt"));
log.Line($"SiteNotes DPR – heavy demo & verification · {DateTime.Now:dd-MMM-yyyy HH:mm} · {days} days of reports for the big project");

// ---------- the sync server (real ASP.NET Core app, in-process, SQLite) ----------
Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_SITENOTES_SERVER", AppContext.BaseDirectory);
var factory = new WebApplicationFactory<SiteNotes.Server.SyncStore>().WithWebHostBuilder(b =>
{
    b.UseSetting("SiteNotes:Provider", "Sqlite");
    b.UseSetting("ConnectionStrings:Sqlite", $"Data Source={Path.Combine(work, "server.db")}");
    b.UseSetting("SiteNotes:MediaFolder", Path.Combine(work, "server-media"));
    b.UseSetting("SiteNotes:ApiKey", "demo-key");
});
Directory.CreateDirectory(work);

async Task<(LocalDatabase Db, SyncClient Sync)> Device(string name)
{
    var db = new LocalDatabase(Path.Combine(work, name)) { UserName = name };
    await db.InitAsync();
    var http = factory.CreateClient();
    return (db, new SyncClient(db, new SyncSettings { ServerUrl = http.BaseAddress!.ToString(), ApiKey = "demo-key", DeviceId = name }, http));
}

var (office, officeSync) = await Device("office-pc");
var (phoneA, syncA) = await Device("site-phone-A");
var (phoneB, syncB) = await Device("site-phone-B");

var today = DateTime.Today;
var specs = new[]
{
    new Spec("Metro Depot – Civil & Track Works", "MD-01", "Metro Rail Corp", "Larsen Infra", days, 3, 3, 2, 7, 101, "MDD-", 6),
    new Spec("Hospital Block G+8", "HB-08", "State Health Dept", "Prime Builders", Math.Max(30, days * 2 / 3), 2, 2, 2, 13, 1, "HB-", 4),
};

// ---------- 1. build data on three devices at the same time ----------
var sw = Stopwatch.StartNew();
var big = await Task.Run(() => Demo.BuildAsync(office, specs[0], today, log));
var hosp = await Task.Run(() => Demo.BuildAsync(phoneA, specs[1], today, log));
var sample = await SampleData.CreateAsync(phoneB, today);
log.Line($"Sample project created on site-phone-B: {sample.Name}");
log.Time("Build 3 projects on 3 devices", sw);

// edits & deletes after the fact (what really happens on site)
sw.Restart();
await Demo.EditHistoryAsync(office, big, log);
await Demo.EditHistoryAsync(phoneA, hosp, log);
log.Time("Edit / delete earlier reports", sw);

// concurrency: writes and reads on the same database at the same time (like a background sync while a screen loads)
await Verify.ConcurrencyAsync(office, big, log);

// ---------- 2. verify every project on its own device ----------
foreach (var (db, p) in new[] { (office, big), (phoneA, hosp), (phoneB, sample) })
    await Verify.ProjectAsync(db, p, log);

// ---------- 3. sync all devices through the server ----------
sw.Restart();
foreach (var _ in Enumerable.Range(0, 2))
{
    log.Check("Sync office", await officeSync.SyncNowAsync(), officeSync.LastMessage);
    log.Check("Sync phone A", await syncA.SyncNowAsync(), syncA.LastMessage);
    log.Check("Sync phone B", await syncB.SyncNowAsync(), syncB.LastMessage);
}
log.Time("Sync 3 devices (2 rounds)", sw);
await Verify.ConvergedAsync(log, ("office-pc", office), ("site-phone-A", phoneA), ("site-phone-B", phoneB));

// conflicting edits: office edits later → office wins everywhere
var target = (await office.ListForProjectAsync<DailyReport>(hosp.Id)).OrderBy(r => r.Date).First();
var onPhone = await phoneA.LoadReportAsync(target.Id);
onPhone.Report.Remarks = "edited on phone";
await phoneA.SaveReportAsync(onPhone);
await Task.Delay(20);
var onOffice = await office.LoadReportAsync(target.Id);
onOffice.Report.Remarks = "edited in office (later)";
await office.SaveReportAsync(onOffice);
await syncA.SyncNowAsync(); await officeSync.SyncNowAsync(); await syncA.SyncNowAsync(); await syncB.SyncNowAsync();
log.Check("Conflict: newest edit wins on every device",
    (await phoneA.GetAsync<DailyReport>(target.Id))!.Remarks == "edited in office (later)" && (await phoneB.GetAsync<DailyReport>(target.Id))!.Remarks == "edited in office (later)");

// all three projects verified again on the office PC (data that travelled through the server)
foreach (var p in new[] { big, hosp, sample }) await Verify.ProjectAsync(office, p, log, "after sync, on office PC");
await Verify.IsolationAsync(office, log, big, hosp, sample);

// ---------- 4. every report, from the office PC ----------
sw.Restart();
await Reports.AllAsync(office, outDir, log, big, hosp, sample);
log.Time("Generate all reports", sw);

// ---------- 5. summary ----------
log.Line("");
log.Line($"RESULT: {log.Passed} checks passed, {log.Failed} failed.");
Reports.VerificationPdf(Path.Combine(outDir, "00-SiteNotes-Demo-Verification.pdf"), log);
await officeSync.DisposeAsync(); await syncA.DisposeAsync(); await syncB.DisposeAsync();
factory.Dispose();
Console.WriteLine($"\n{log.Passed} passed, {log.Failed} failed. Output: {outDir}");
return log.Failed == 0 ? 0 : 1;

// ============================================================================================

record Spec(string Name, string Code, string Client, string Contractor, int Days, int Plants, int UnitsPerPlant, int AreasPerUnit,
    int Seed, int FirstNo, string Prefix, int Subcontractors);

class Log(string path)
{
    private readonly StringBuilder _sb = new();
    public int Passed, Failed;
    public List<(string Name, bool Ok, string Detail)> Checks { get; } = [];
    public List<(string Name, double Ms)> Timings { get; } = [];
    public List<string> Facts { get; } = [];

    public void Line(string s)
    {
        Console.WriteLine(s);
        _sb.AppendLine(s);
        File.WriteAllText(path, _sb.ToString());
    }

    public void Fact(string s) { Facts.Add(s); Line("  · " + s); }

    public bool Check(string name, bool ok, string detail = "")
    {
        if (ok) Passed++; else Failed++;
        Checks.Add((name, ok, detail));
        Line($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " – " + detail)}");
        return ok;
    }

    public void Time(string name, Stopwatch sw)
    {
        Timings.Add((name, sw.Elapsed.TotalMilliseconds));
        Line($"  ⏱ {name}: {sw.Elapsed.TotalSeconds:0.00} s");
    }
}

static class Demo
{
    private static readonly (string Name, string Unit, double Qty, int Days, string Trade, string Method, string Material)[] Templates =
    [
        ("Excavation", "m³", 900, 14, "Helper", "Mechanised", ""),
        ("PCC below footings", "m³", 70, 5, "Mason", "Manual", "RMC M15"),
        ("Footing reinforcement", "MT", 28, 10, "Bar bender", "Conventional", "TMT steel"),
        ("Footing concrete", "m³", 180, 8, "Mason", "Pump placement", "RMC M30"),
        ("Column shuttering", "m²", 650, 15, "Carpenter", "Conventional shuttering", ""),
        ("Slab concrete", "m³", 240, 12, "Mason", "Pump placement", "RMC M30"),
        ("Brick masonry", "m³", 320, 25, "Mason", "Manual", "Bricks"),
        ("Internal plaster", "m²", 2600, 30, "Mason", "Manual", "Cement"),
        ("Electrical conduiting", "Rm", 3200, 20, "Electrician", "Manual", ""),
        ("Structural steel erection", "MT", 45, 18, "Welder", "Crane & bucket", "Structural steel"),
    ];

    public static async Task<Project> BuildAsync(LocalDatabase db, Spec s, DateTime today, Log log)
    {
        var rnd = new Random(s.Seed);
        var start = today.AddDays(-s.Days);
        var p = new Project { Name = s.Name, Code = s.Code, Client = s.Client, Contractor = s.Contractor, Location = "Site", StartDate = start, ReportPrefix = s.Prefix, FirstReportNo = s.FirstNo };
        await db.SaveAsync(p);
        var cal = new WorkCalendar { ProjectId = p.Id, HoursPerDay = 8, WorkingDays = "123456", OvertimeFactor = 1.5m, Currency = "₹",
            Holidays = $"{start.AddDays(26):yyyy-MM-dd},{start.AddDays(71):yyyy-MM-dd}" };
        await db.SaveAsync(cal);
        var depts = Lists.DefaultDepartments.Take(5).Select(n => new Department { ProjectId = p.Id, Name = n }).ToList();
        await db.SaveAllAsync(depts);
        string[] subNames = ["Shree Formwork", "Om Rebar Works", "Sai Concrete", "Patil Masonry", "Volt Electricals", "Steelfab Erectors", "Green Earthmovers", "Aqua Plumbing"];
        var subs = subNames.Take(s.Subcontractors).Select(n => new Subcontractor { ProjectId = p.Id, Name = n, Trade = "Civil" }).ToList();
        await db.SaveAllAsync(subs);

        // resources with rates
        var resources = new List<SiteResource>();
        foreach (var t in new[] { ("Mason", 950m), ("Carpenter", 1000m), ("Helper", 650m), ("Bar bender", 900m), ("Electrician", 1100m), ("Welder", 1200m) })
            resources.Add(new SiteResource { ProjectId = p.Id, Name = t.Item1, Type = ResourceType.Manpower, Rate = t.Item2, Basis = RateBasis.PerDay, SubcontractorId = subs[rnd.Next(subs.Count)].Id });
        resources.Add(new SiteResource { ProjectId = p.Id, Name = "JCB", Type = ResourceType.Machinery, Rate = 1250.50m, Basis = RateBasis.PerHour });
        resources.Add(new SiteResource { ProjectId = p.Id, Name = "Concrete pump", Type = ResourceType.Machinery, Rate = 2200m, Basis = RateBasis.PerHour });
        resources.Add(new SiteResource { ProjectId = p.Id, Name = "Tower crane", Type = ResourceType.Machinery, Rate = 18000m, Basis = RateBasis.PerDay });
        foreach (var m in new[] { ("RMC M15", 4800m, "m³"), ("RMC M30", 5632.75m, "m³"), ("TMT steel", 61850m, "MT"), ("Bricks", 7.35m, "Nos"), ("Cement", 385m, "Bags"), ("Structural steel", 72000m, "MT") })
            resources.Add(new SiteResource { ProjectId = p.Id, Name = m.Item1, Type = ResourceType.Material, Rate = m.Item2, Basis = RateBasis.PerUnit, Unit = m.Item3 });
        resources.Add(new SiteResource { ProjectId = p.Id, Name = "Scaffolding hire", Type = ResourceType.Other, Rate = 3500m, Basis = RateBasis.LumpSum, Unit = "LS" });
        await db.SaveAllAsync(resources);
        SiteResource R(string n) => resources.First(r => r.Name == n);

        // WBS + activities
        var wbs = new List<WbsNode>();
        var activities = new List<Activity>();
        var plan = new List<ActivityResource>();
        // areas start one after another across ~130 % of the reported period, so at "today" some work is done,
        // some running (on time or late) and some not started yet
        var areaCount = s.Plants * s.UnitsPerPlant * s.AreasPerUnit;
        var spacing = s.Days * 1.3 / areaCount;
        var scale = Math.Max(0.6, s.Days / 120.0);
        var areaIndex = 0;
        for (var pl = 1; pl <= s.Plants; pl++)
        {
            var plant = new WbsNode { ProjectId = p.Id, Code = $"P{pl}", Name = $"Plant {pl}", Level = WbsLevel.Plant };
            wbs.Add(plant);
            for (var u = 1; u <= s.UnitsPerPlant; u++)
            {
                var unit = new WbsNode { ProjectId = p.Id, ParentId = plant.Id, Code = $"P{pl}-U{u}", Name = $"Unit {u}", Level = WbsLevel.Unit };
                wbs.Add(unit);
                for (var a = 1; a <= s.AreasPerUnit; a++)
                {
                    var area = new WbsNode { ProjectId = p.Id, ParentId = unit.Id, Code = $"P{pl}-U{u}-A{a}", Name = $"Area {a}", Level = WbsLevel.Area };
                    wbs.Add(area);
                    var areaStart = (int)(areaIndex++ * spacing);
                    var step = 0;
                    foreach (var t in Templates.Where(_ => rnd.NextDouble() < 0.75))
                    {
                        var qty = Math.Round(t.Qty * scale * (0.7 + rnd.NextDouble() * 0.6), 1);
                        var dur = Math.Max(3, (int)(t.Days * scale * (0.8 + rnd.NextDouble() * 0.5)));
                        var ps = start.AddDays(areaStart + step);
                        var act = new Activity
                        {
                            ProjectId = p.Id, Code = $"{area.Code}-{activities.Count + 1:000}", Name = t.Name, Unit = t.Unit, PlannedQty = qty,
                            WbsId = area.Id, Method = t.Method, SubcontractorId = subs[rnd.Next(subs.Count)].Id, DepartmentId = depts[0].Id,
                            PlannedStart = ps, PlannedFinish = ps.AddDays(dur), PlannedRatePerDay = Math.Round(qty / (dur * 6 / 7.0), 2),
                            Weightage = rnd.Next(1, 10), Location = $"{plant.Name} / {unit.Name} / {area.Name}",
                        };
                        activities.Add(act);
                        plan.Add(new ActivityResource { ProjectId = p.Id, ActivityId = act.Id, ResourceName = t.Trade, Type = ResourceType.Manpower, PlannedQty = 6, Rate = R(t.Trade).Rate, Basis = RateBasis.PerDay });
                        if (t.Material != "") plan.Add(new ActivityResource { ProjectId = p.Id, ActivityId = act.Id, ResourceName = t.Material, Type = ResourceType.Material, PlannedQty = qty, Rate = R(t.Material).Rate, Basis = RateBasis.PerUnit });
                        step += (int)(dur * 0.6);
                    }
                }
            }
        }
        await db.SaveAllAsync(wbs);
        await db.SaveAllAsync(activities);
        await db.SaveAllAsync(plan);
        // drawing register: each area has a GFC drawing with BOQ items / equipment / parts; items are allocated
        // to one agency, split between two or three agencies, or only partly allocated
        var drawings = new List<Drawing>();
        var dItems = new List<DrawingItem>();
        var dAllocs = new List<DrawingAllocation>();
        foreach (var area in wbs.Where(w => w.Level == WbsLevel.Area).Take(Math.Max(4, s.Plants * 2)))
        {
            var dwg = new Drawing { ProjectId = p.Id, Number = $"DWG-{area.Code}-S-{drawings.Count + 101}", Title = $"Structural steel & equipment – {area.Name}", Revision = "R1", WbsId = area.Id, IssueDate = start.AddDays(drawings.Count * 9), Discipline = "Structural" };
            drawings.Add(dwg);
            var its = new (string No, string Desc, DrawingItemKind Kind, string Unit, double Qty)[]
            {
                ($"{1450 + drawings.Count}", "Main steel – columns & beams", DrawingItemKind.BoqItem, "MT", 100),
                ($"{1460 + drawings.Count}", "Secondary steel – bracings", DrawingItemKind.Part, "MT", 35),
                ($"{1470 + drawings.Count}", "Grating & handrail", DrawingItemKind.BoqItem, "m²", 420),
                ($"EQ-{drawings.Count:00}", "Pump set with motor", DrawingItemKind.Equipment, "Nos", 4),
            };
            var order = 0;
            foreach (var (no, desc, kind, unit, qty) in its)
            {
                var item = new DrawingItem { ProjectId = p.Id, DrawingId = dwg.Id, ItemNo = no, Description = desc, Kind = kind, Unit = unit, Qty = qty, SortOrder = order++ };
                dItems.Add(item);
                var a = subs[rnd.Next(subs.Count)];
                var b2 = subs[(subs.IndexOf(a) + 1) % subs.Count];
                var c3 = subs[(subs.IndexOf(a) + 2) % subs.Count];
                switch (order % 4)
                {
                    case 1: // split: 60 % / 40 %
                        dAllocs.Add(new DrawingAllocation { ProjectId = p.Id, DrawingId = dwg.Id, DrawingItemId = item.Id, SubcontractorId = a.Id, SubcontractorName = a.Name, AllocatedQty = Math.Round(qty * 0.6, 2), WorkOrderNo = $"WO-{rnd.Next(100, 999)}" });
                        dAllocs.Add(new DrawingAllocation { ProjectId = p.Id, DrawingId = dwg.Id, DrawingItemId = item.Id, SubcontractorId = b2.Id, SubcontractorName = b2.Name, AllocatedQty = Math.Round(qty * 0.4, 2), WorkOrderNo = $"WO-{rnd.Next(100, 999)}" });
                        break;
                    case 2: // single agency
                        dAllocs.Add(new DrawingAllocation { ProjectId = p.Id, DrawingId = dwg.Id, DrawingItemId = item.Id, SubcontractorId = a.Id, SubcontractorName = a.Name, AllocatedQty = qty });
                        break;
                    case 3: // three agencies, 20 % not yet allocated
                        foreach (var (x, f) in new[] { (a, 0.3), (b2, 0.3), (c3, 0.2) })
                            dAllocs.Add(new DrawingAllocation { ProjectId = p.Id, DrawingId = dwg.Id, DrawingItemId = item.Id, SubcontractorId = x.Id, SubcontractorName = x.Name, AllocatedQty = Math.Round(qty * f, 2) });
                        break;
                    default: // equipment: one per agency of two, rest unallocated
                        dAllocs.Add(new DrawingAllocation { ProjectId = p.Id, DrawingId = dwg.Id, DrawingItemId = item.Id, SubcontractorId = a.Id, SubcontractorName = a.Name, AllocatedQty = 2 });
                        break;
                }
            }
        }
        await db.SaveAllAsync(drawings);
        await db.SaveAllAsync(dItems);
        await db.SaveAllAsync(dAllocs);
        var drawingWindow = drawings.ToDictionary(x => x.Id, x => x.IssueDate!.Value);
        var drawingDone = new Dictionary<string, double>();

        var speed = activities.ToDictionary(a => a.Id, _ => 0.6 + rnd.NextDouble() * 0.7);
        var overshoot = activities.Take(2).Select(a => a.Id).ToHashSet();
        var templateOf = activities.ToDictionary(a => a.Id, a => Templates.First(t => t.Name == a.Name));

        // daily reports
        var hol = cal.HolidaySet;
        var reports = 0;
        for (var day = 0; day < s.Days; day++)
        {
            var date = start.AddDays(day);
            if (!cal.IsWorkingDay(date, hol)) continue;
            var shifts = day % 30 == 15 ? 2 : 1; // a night shift now and then (two reports on one date)
            for (var shift = 0; shift < shifts; shift++)
            {
                var b = await db.NewReportAsync(p.Id, date);
                b.Report.Shift = shift == 0 ? Shift.Day : Shift.Night;
                b.Report.PreparedBy = "Site engineer";
                b.Report.WeatherMorning = rnd.NextDouble() < 0.1 ? "Light rain" : "Sunny";
                b.Report.ToolboxTalkTopic = Lists.Weather.Length > 0 ? "PPE" : "";
                b.Report.ToolboxTalkAttendees = rnd.Next(20, 60);
                var age = (today - date).TotalDays;
                b.Report.Status = age > 7 ? ReportStatus.Approved : age > 2 ? ReportStatus.Submitted : ReportStatus.Draft;

                // activities active on this date (planned window stretched by each activity's speed)
                foreach (var a in activities.Where(a => a.PlannedStart <= date && date <= a.PlannedFinish!.Value.AddDays((a.PlannedFinish.Value - a.PlannedStart!.Value).TotalDays / speed[a.Id] - (a.PlannedFinish.Value - a.PlannedStart.Value).TotalDays + 2)))
                    if (b.Activities.All(x => x.ActivityId != a.Id))
                        b.Activities.Add(new ReportActivity { ActivityId = a.Id, ActivityName = a.ToString(), Unit = a.Unit, PlannedQty = a.PlannedQty, SubcontractorId = a.SubcontractorId, SubcontractorName = subs.First(x => x.Id == a.SubcontractorId).Name });
                b.Manpower.Clear(); b.Machines.Clear(); b.Materials.Clear();
                b.Activities.RemoveAll(x => x.DrawingItemId != "");   // drawing rows are added fresh below
                foreach (var row in b.Activities.Where(x => x.ActivityId != ""))
                {
                    var a = activities.First(x => x.Id == row.ActivityId);
                    var t = templateOf[a.Id];
                    var factor = shift == 1 ? 0.3 : 1;
                    var qty = Math.Round(a.PlannedRatePerDay * speed[a.Id] * (0.5 + rnd.NextDouble() * 0.8) * factor, 2);
                    if (overshoot.Contains(a.Id)) qty *= 1.6;
                    row.TodayQty = qty;
                    if (qty <= 0) continue;
                    var sub = subs.First(x => x.Id == a.SubcontractorId);
                    var res = R(t.Trade);
                    var hours = rnd.NextDouble() < 0.2 ? 10 : 8;
                    b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = sub.Id, PartyName = sub.Name, Trade = t.Trade, Count = rnd.Next(4, 14), Hours = hours, Rate = res.Rate, Basis = res.Basis, ResourceId = res.Id, ActivityId = a.Id, ActivityName = a.ToString() });
                    b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Subcontractor, PartyId = sub.Id, PartyName = sub.Name, Trade = "Helper", Count = rnd.Next(4, 16), Hours = hours, Rate = R("Helper").Rate, Basis = RateBasis.PerDay, ActivityId = a.Id, ActivityName = a.ToString() });
                    if (t.Name == "Excavation") b.Machines.Add(new ReportMachine { Name = "JCB", Ownership = MachineOwnership.Hired, Count = 2, WorkingHours = 7, IdleHours = rnd.NextDouble() < 0.3 ? 1.5 : 0, Rate = R("JCB").Rate, Basis = RateBasis.PerHour, FuelLitres = 60, ActivityId = a.Id, ActivityName = a.ToString() });
                    if (t.Method == "Pump placement") b.Machines.Add(new ReportMachine { Name = "Concrete pump", Ownership = MachineOwnership.Hired, Count = 1, WorkingHours = 6, Rate = R("Concrete pump").Rate, Basis = RateBasis.PerHour, ActivityId = a.Id, ActivityName = a.ToString() });
                    if (t.Material != "")
                    {
                        var mat = R(t.Material);
                        var consumed = t.Material switch { "Bricks" => Math.Round(qty * 500), "Cement" => Math.Round(qty * 0.08, 1), _ => qty };
                        b.Materials.Add(new ReportMaterial { Material = mat.Name, Unit = mat.Unit, Consumed = consumed, Received = rnd.NextDouble() < 0.3 ? consumed * 2 : 0, Rate = mat.Rate, Basis = RateBasis.PerUnit, ActivityId = a.Id, ActivityName = a.ToString() });
                    }
                }
                // drawing work: every allocated agency reports its share; now and then an agency works without allocation
                if (shift == 0)
                    foreach (var al in dAllocs.Where(al => date >= drawingWindow[al.DrawingId] && rnd.NextDouble() < 0.35))
                    {
                        var item = dItems.First(i => i.Id == al.DrawingItemId);
                        var doneSoFar = drawingDone.GetValueOrDefault(al.Id);
                        var excessAllowed = al.DrawingItemId == dItems[0].Id; // one item gets deliberate excess, to prove it is flagged
                        if (doneSoFar >= al.AllocatedQty && !excessAllowed) continue;
                        var rate = item.Kind == DrawingItemKind.Equipment ? 1 : Math.Round(al.AllocatedQty / 25 * (0.6 + rnd.NextDouble()), 2);
                        if (!excessAllowed) rate = Math.Min(rate, Math.Round(al.AllocatedQty - doneSoFar, 2));
                        if (rate <= 0) continue;
                        drawingDone[al.Id] = doneSoFar + rate;
                        b.Activities.Add(new ReportActivity { DrawingItemId = item.Id, ActivityName = $"{drawings.First(x => x.Id == al.DrawingId).Number} / {item}", Unit = item.Unit, SubcontractorId = al.SubcontractorId, SubcontractorName = al.SubcontractorName, TodayQty = rate });
                    }
                if (shift == 0 && rnd.NextDouble() < 0.03 && dItems.Count > 0)
                {
                    var item = dItems[rnd.Next(dItems.Count)];
                    var stranger = subs.Last();
                    if (dAllocs.All(al => al.DrawingItemId != item.Id || al.SubcontractorId != stranger.Id) && date >= drawingWindow[item.DrawingId])
                        b.Activities.Add(new ReportActivity { DrawingItemId = item.Id, ActivityName = item.ToString(), Unit = item.Unit, SubcontractorId = stranger.Id, SubcontractorName = stranger.Name, TodayQty = 0.5 });
                }
                // own staff (not booked to an activity → overheads) and a crane by the day
                b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Department, PartyId = depts[0].Id, PartyName = depts[0].Name, Trade = "Site engineer", Count = 3, Hours = 9, Rate = 2500, Basis = RateBasis.PerDay });
                b.Manpower.Add(new ReportManpower { Source = ManpowerSource.Department, PartyId = depts[4].Id, PartyName = depts[4].Name, Trade = "Safety officer", Count = 1, Hours = 8, Rate = 16.5m * 8, Basis = RateBasis.PerDay });
                if (shift == 0) b.Machines.Add(new ReportMachine { Name = "Tower crane", Ownership = MachineOwnership.Own, Count = 1, WorkingHours = 8, IdleHours = 1, Rate = 18000, Basis = RateBasis.PerDay });
                if (date.DayOfWeek == DayOfWeek.Monday && shift == 0) b.Materials.Add(new ReportMaterial { Kind = ResourceType.Other, Material = "Scaffolding hire", Unit = "LS", Consumed = 1, Rate = 3500, Basis = RateBasis.LumpSum });
                if (rnd.NextDouble() < 0.05)
                    b.Hindrances.Add(new Hindrance { ProjectId = p.Id, FromDate = date, Category = Lists.HindranceCategories[rnd.Next(Lists.HindranceCategories.Length)], Description = "Work stopped – " + (rnd.NextDouble() < 0.5 ? "drawing not released" : "material delayed"), ResponsibleParty = Lists.ResponsibleParties[rnd.Next(3)], HoursLost = rnd.Next(1, 6), Status = age > 20 ? HindranceStatus.Resolved : HindranceStatus.Open, ToDate = age > 20 ? date.AddDays(2) : null });
                if (rnd.NextDouble() < 0.06)
                    b.Requirements.Add(new Requirement { ProjectId = p.Id, RaisedDate = date, Item = "TMT 12 mm Fe500D", Quantity = rnd.Next(2, 20), Unit = "MT", RequiredBy = date.AddDays(rnd.Next(2, 20)), Priority = (Priority)rnd.Next(4), Status = age > 25 ? RequirementStatus.Received : RequirementStatus.Pending });
                b.Report.Remarks = $"Day {day + 1} – work as per plan";
                await db.SaveReportAsync(b);
                if (reports % 40 == 3) await AddPhotosAsync(db, p, b.Report, rnd);
                reports++;
            }
        }
        log.Line($"Built {s.Name}: {wbs.Count} WBS nodes, {activities.Count} activities, {drawings.Count} drawings with {dItems.Count} items / {dAllocs.Count} allocations, {resources.Count} resources, {reports} daily reports");
        return p;
    }

    private static async Task AddPhotosAsync(LocalDatabase db, Project p, DailyReport r, Random rnd)
    {
        for (var i = 0; i < 2; i++)
        {
            var name = Guid.NewGuid().ToString("N") + ".jpg";
            using var bmp = new SKBitmap(640, 480);
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(new SKColor((byte)rnd.Next(40, 120), (byte)rnd.Next(60, 140), (byte)rnd.Next(80, 180)));
                using var paint = new SKPaint { Color = SKColors.White.WithAlpha(120) };
                for (var k = 0; k < 6; k++) c.DrawRect(rnd.Next(0, 500), rnd.Next(0, 380), rnd.Next(40, 140), rnd.Next(20, 100), paint);
            }
            ImageTools.SaveJpeg(bmp, db.MediaPath(name));
            await db.SaveAsync(new MediaItem { ProjectId = p.Id, OwnerType = MediaOwner.Report, OwnerId = r.Id, Kind = MediaKind.Photo, FileName = name, Caption = $"Progress photo {i + 1}", CapturedAtUtc = r.Date.AddHours(10 + i) });
        }
    }

    /// <summary>Edits today's quantities and manpower of earlier reports and deletes one report, like real corrections.</summary>
    public static async Task EditHistoryAsync(LocalDatabase db, Project p, Log log)
    {
        var rnd = new Random(99);
        var reports = (await db.ListForProjectAsync<DailyReport>(p.Id)).OrderBy(r => r.Date).ToList();
        for (var i = 0; i < 6; i++)
        {
            var r = reports[rnd.Next(reports.Count / 4, reports.Count * 3 / 4)];
            var b = await db.LoadReportAsync(r.Id);
            var row = b.Activities.FirstOrDefault(a => a.TodayQty > 0);
            if (row != null) row.TodayQty = Math.Round(row.TodayQty + 7.25, 2);
            if (b.Manpower.Count > 0) b.Manpower[0].Count += 3;
            await db.SaveReportAsync(b);
        }
        var victim = await db.LoadReportAsync(reports[reports.Count / 2].Id);
        victim.Activities.Clear(); victim.Manpower.Clear(); victim.Machines.Clear(); victim.Materials.Clear(); victim.Hindrances.Clear(); victim.Requirements.Clear();
        await db.SaveReportAsync(victim);
        await db.DeleteAsync(victim.Report);
        log.Line($"Edited 6 earlier reports and deleted report no. {victim.Report.ReportNo} in {p.Name}");
    }
}

static class Verify
{
    /// <summary>Recalculates everything independently from the raw rows and compares with the app's results.</summary>
    public static async Task ProjectAsync(LocalDatabase db, Project p, Log log, string where = "")
    {
        log.Line($"\n▶ Verify {p.Name}{(where == "" ? "" : " (" + where + ")")}");
        var sw = Stopwatch.StartNew();
        var cal = await db.CalendarAsync(p.Id);
        var reports = (await db.ListForProjectAsync<DailyReport>(p.Id)).OrderBy(r => r.Date).ThenBy(r => r.ReportNo).ToList();
        var alive = reports.Select(r => r.Id).ToHashSet();
        var acts = (await db.ListForProjectAsync<ReportActivity>(p.Id)).Where(a => alive.Contains(a.ReportId)).ToList();
        var mp = (await db.ListForProjectAsync<ReportManpower>(p.Id)).Where(a => alive.Contains(a.ReportId)).ToList();
        var mc = (await db.ListForProjectAsync<ReportMachine>(p.Id)).Where(a => alive.Contains(a.ReportId)).ToList();
        var mt = (await db.ListForProjectAsync<ReportMaterial>(p.Id)).Where(a => alive.Contains(a.ReportId)).ToList();
        var activities = await db.ListForProjectAsync<Activity>(p.Id);
        var lastDate = reports.Count == 0 ? DateTime.Today : reports.Max(r => r.Date);

        // numbering & dates
        var nos = reports.Select(r => r.ReportNo).ToList();
        log.Check("Report numbers are unique", nos.Distinct().Count() == nos.Count, $"{nos.Count} reports, {nos.Min()}–{nos.Max()}");
        log.Check("Report numbers follow date order", reports.Zip(reports.Skip(1)).All(x => x.First.ReportNo < x.Second.ReportNo));
        var proj = await db.GetAsync<Project>(p.Id);
        log.Check("First report number respected", proj!.FirstReportNo <= 0 || nos.Min() == proj.FirstReportNo, $"first {nos.Min()}, configured {proj.FirstReportNo}");
        log.Check("Next report number = last + 1", await db.NextReportNoAsync(p.Id) == nos.Max() + 1);
        var nextDate = await db.NextReportDateAsync(p.Id);
        log.Check("Next report date is a working day after the last report, not in the future",
            nextDate <= DateTime.Today && (nextDate > lastDate || lastDate >= DateTime.Today) && (cal.IsWorkingDay(nextDate) || nextDate == DateTime.Today), $"last {lastDate:dd-MMM}, next {nextDate:dd-MMM}");

        // progress = Σ today's quantity, % capped
        var pp = await new ProgressService(db).ComputeAsync(p.Id, asOf: lastDate);
        var sums = acts.Where(a => a.ActivityId != "").GroupBy(a => a.ActivityId).ToDictionary(g => g.Key, g => g.Sum(a => a.TodayQty));
        var badDone = pp.Activities.Where(a => Math.Abs(a.DoneQty - Math.Round(sums.GetValueOrDefault(a.Activity.Id), 4)) > 1e-6).ToList();
        log.Check("Activity progress = Σ today's quantities", badDone.Count == 0, $"{pp.Activities.Count} activities{(badDone.Count > 0 ? ", wrong: " + string.Join(", ", badDone.Take(3).Select(a => a.Activity.Code)) : "")}");
        log.Check("% complete = done ÷ planned, capped at 100", pp.Activities.All(a => a.Activity.PlannedQty <= 0 || Math.Abs(a.Percent - Math.Min(100, Math.Round(a.DoneQty / a.Activity.PlannedQty * 100, 2))) < 0.011));
        log.Check("Over-reported activities stay at 100 % and show Completed", pp.Activities.Where(a => a.DoneQty > a.Activity.PlannedQty && a.Activity.PlannedQty > 0).All(a => a.Percent == 100 && a.State == WorkState.Completed),
            $"{pp.Activities.Count(a => a.DoneQty > a.Activity.PlannedQty)} over plan");
        log.Check("Overall % within 0–100", pp.OverallPercent is >= 0 and <= 100, $"{pp.OverallPercent:0.##} % (planned {pp.PlannedPercent:0.##} %)");
        log.Check("Forecast finish dates are working days", pp.Activities.Where(a => a.ForecastFinish != null && a.State != WorkState.Completed).All(a => cal.IsWorkingDay(a.ForecastFinish!.Value)));
        log.Check("Earned value never exceeds budget", pp.Activities.All(a => a.EarnedValue <= a.PlannedCost));

        // cumulative in reports (bulk loader and single loader)
        var bundles = await db.LoadReportsAsync(p.Id, reports);
        var running = new Dictionary<string, double>();
        var cumOk = true; var checkedRows = 0;
        foreach (var b in bundles)
        {
            foreach (var g in b.Activities.Where(a => a.ActivityId != "").GroupBy(a => a.ActivityId))
                running[g.Key] = running.GetValueOrDefault(g.Key) + g.Sum(x => x.TodayQty);
            foreach (var a in b.Activities.Where(a => a.ActivityId != ""))
            {
                checkedRows++;
                if (Math.Abs(a.CumulativeQty - Math.Round(running[a.ActivityId], 4)) > 1e-6) cumOk = false;
            }
        }
        log.Check("Cumulative 'to date' in every report = running total", cumOk, $"{checkedRows} rows in {bundles.Count} reports");
        var sample = bundles.Where((_, i) => i % Math.Max(1, bundles.Count / 12) == 0).ToList();
        var singleOk = true;
        foreach (var b in sample)
        {
            var one = await db.LoadReportAsync(b.Report.Id);
            singleOk &= one.Activities.All(a => Math.Abs(a.CumulativeQty - b.Activities.First(x => x.Id == a.Id).CumulativeQty) < 1e-6);
        }
        log.Check("Single-report loader agrees with bulk loader", singleOk, $"{sample.Count} reports compared");

        // cached totals and cost, recomputed with the formulas written out again here
        decimal Paid(double h) => (decimal)Math.Min(h, cal.HoursPerDay) + (decimal)Math.Max(0, h - cal.HoursPerDay) * cal.OvertimeFactor;
        decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
        decimal ManCost(ReportManpower m) => Round(m.Basis switch
        {
            RateBasis.PerHour => m.Count * Paid(m.Hours) * m.Rate,
            RateBasis.PerDay => m.Count * m.Rate * Paid(m.Hours) / (decimal)cal.HoursPerDay,
            RateBasis.PerUnit => m.Count * m.Rate,
            _ => m.Rate,
        });
        decimal MachCost(ReportMachine m)
        {
            var ch = (decimal)(m.WorkingHours + (m.Ownership == MachineOwnership.Hired ? m.IdleHours : 0));
            return Round(m.Basis switch { RateBasis.PerHour => m.Count * ch * m.Rate, RateBasis.PerDay => m.Count * m.Rate * ch / (decimal)cal.HoursPerDay, RateBasis.PerUnit => m.Count * m.Rate, _ => m.Rate });
        }
        decimal MatCost(ReportMaterial m) => Round(m.Basis == RateBasis.LumpSum ? m.Rate : (decimal)m.Consumed * m.Rate);
        var mpBy = mp.ToLookup(x => x.ReportId); var mcBy = mc.ToLookup(x => x.ReportId); var mtBy = mt.ToLookup(x => x.ReportId);
        var totalsOk = reports.All(r => r.TotalManpower == mpBy[r.Id].Sum(x => x.Count) && Math.Abs(r.TotalManHours - mpBy[r.Id].Sum(x => x.Count * x.Hours)) < 1e-6);
        log.Check("Report manpower & man-hour totals match rows", totalsOk);
        var costBad = reports.Where(r => r.TotalCost != mpBy[r.Id].Sum(ManCost) + mcBy[r.Id].Sum(MachCost) + mtBy[r.Id].Sum(MatCost)).ToList();
        log.Check("Cost of each day = Σ manpower + machinery + material (exact, to the paisa)", costBad.Count == 0, costBad.Count == 0 ? "" : $"{costBad.Count} reports differ, e.g. no. {costBad[0].ReportNo}");
        var projectCost = reports.Sum(r => r.TotalCost);
        log.Check("Project cost (progress engine) = Σ daily costs", pp.Actual.Total == projectCost, $"₹ {PdfReports.M(projectCost)}");
        var perActivity = mp.Where(x => x.ActivityId != "").GroupBy(x => x.ActivityId).ToDictionary(g => g.Key, g => g.Sum(ManCost));
        log.Check("Labour cost per activity matches", pp.Activities.All(a => a.Actual.Manpower == perActivity.GetValueOrDefault(a.Activity.Id)));
        var mdAll = mp.Sum(x => x.Count * x.Hours / cal.HoursPerDay);
        var mdParties = pp.BySubcontractor.Sum(x => x.ManDays) + pp.ByDepartment.Sum(x => x.ManDays);
        log.Check("Subcontractor-wise + department-wise man-days = total", Math.Abs(mdAll - mdParties) < 0.05 * (pp.BySubcontractor.Count + pp.ByDepartment.Count + 1), $"{mdAll:0.##} man-days");

        // filters
        var q = new ReportQueryService(db);
        var mid = reports[reports.Count / 2].Date;
        var f1 = await q.FindAsync(new ReportFilter { ProjectId = p.Id, From = mid, Statuses = [ReportStatus.Approved] });
        log.Check("Filter: date + status", f1.Count == reports.Count(r => r.Date >= mid && r.Status == ReportStatus.Approved), $"{f1.Count} reports");
        var anySub = mp.FirstOrDefault(x => x.Source == ManpowerSource.Subcontractor)?.PartyId ?? "";
        var f2 = await q.FindAsync(new ReportFilter { ProjectId = p.Id, SubcontractorId = anySub });
        var expect2 = mp.Where(x => x.PartyId == anySub).Select(x => x.ReportId).Concat(acts.Where(a => a.SubcontractorId == anySub).Select(a => a.ReportId)).ToHashSet();
        log.Check("Filter: subcontractor", f2.Select(r => r.Id).ToHashSet().SetEquals(expect2), $"{f2.Count} reports");
        var f3 = await q.FindAsync(new ReportFilter { ProjectId = p.Id, Sort = ReportSort.ManpowerHigh });
        log.Check("Sort: most manpower first", f3.Zip(f3.Skip(1)).All(x => x.First.TotalManpower >= x.Second.TotalManpower));
        var search = activities.FirstOrDefault()?.Name ?? "x";
        var f4 = await q.FindAsync(new ReportFilter { ProjectId = p.Id, Search = search });
        log.Check($"Search “{search}”", f4.Count > 0 && f4.All(r => acts.Any(a => a.ReportId == r.Id && a.ActivityName.Contains(search, StringComparison.OrdinalIgnoreCase)) || r.Remarks.Contains(search, StringComparison.OrdinalIgnoreCase)), $"{f4.Count} reports");

        // drawing register: completed = Σ daily work on the item; per agency = Σ its rows; allocated + unallocated = qty
        var reg = await new DrawingService(db).ComputeAsync(p.Id);
        if (reg.Drawings.Count > 0)
        {
            var dRows = acts.Where(a => a.DrawingItemId != "").ToList();
            var allocRows = await db.ListForProjectAsync<DrawingAllocation>(p.Id);
            var items = reg.AllItems.ToList();
            log.Check("Drawing items: completed = Σ daily quantities on the item", items.All(i => Math.Abs(i.Completed - Math.Round(dRows.Where(r => r.DrawingItemId == i.Item.Id).Sum(r => r.TodayQty), 4)) < 1e-6), $"{items.Count} items, {dRows.Count} daily rows");
            log.Check("Drawing items: each agency's done = Σ its own rows", items.All(i => i.Agencies.All(a => Math.Abs(a.Done - Math.Round(dRows.Where(r => r.DrawingItemId == i.Item.Id && r.SubcontractorId == a.SubcontractorId).Sum(r => r.TodayQty), 4)) < 1e-6)));
            log.Check("Drawing items: allocated = Σ allocations, allocated + unallocated = qty", items.All(i =>
                Math.Abs(i.Allocated - allocRows.Where(x => x.DrawingItemId == i.Item.Id).Sum(x => x.AllocatedQty)) < 1e-6 && (i.OverAllocated > 0 || Math.Abs(i.Allocated + i.Unallocated - i.Qty) < 1e-6)),
                $"{items.Count(i => i.Unallocated > 0)} items partly unallocated");
            log.Check("Drawing items: work by agencies without allocation is flagged", items.Where(i => i.CompletedWithoutAllocation > 0).All(i => i.Agencies.Any(a => a.Unallocated && a.Done > 0)),
                $"{items.Count(i => i.CompletedWithoutAllocation > 0)} items");
            log.Check("Drawing % and balance consistent (capped at 100)", items.All(i => i.Percent <= 100 && Math.Abs(i.Balance - Math.Max(0, i.Qty - i.Completed)) < 1e-9));
            log.Check("Work beyond drawing qty / allocation is flagged", items.All(i => Math.Abs(i.Excess - Math.Max(0, Math.Round(i.Completed - i.Qty, 4))) < 1e-9)
                && items.SelectMany(i => i.Agencies).All(a => a.Unallocated || Math.Abs(a.Excess - Math.Max(0, Math.Round(a.Done - a.Allocated, 4))) < 1e-9),
                $"{items.Count(i => i.Excess > 0)} items beyond drawing qty, {items.SelectMany(i => i.Agencies).Count(a => a.Excess > 0)} agency shares beyond allocation");
            var bySub = reg.ByAgency.GroupBy(x => x.Unit).ToDictionary(g => g.Key, g => g.Sum(x => x.Done));
            var byItem = items.GroupBy(i => i.Item.Unit).ToDictionary(g => g.Key, g => g.Sum(i => i.Completed));
            log.Check("Subcontractor-wise drawing work adds up to the item totals (per unit)", byItem.All(kv => Math.Abs(bySub.GetValueOrDefault(kv.Key) - kv.Value) < 1e-3), string.Join(", ", byItem.Select(kv => $"{kv.Value:#,0.##} {kv.Key}")));
            // the 'to date' of a drawing row in the reports = that agency's running total on that item
            var running2 = new Dictionary<string, double>();
            var dOk = true;
            foreach (var bb in bundles)
            {
                foreach (var g in bb.Activities.Where(a => a.DrawingItemId != "").GroupBy(a => a.ProgressKey)) running2[g.Key] = running2.GetValueOrDefault(g.Key) + g.Sum(x => x.TodayQty);
                foreach (var a in bb.Activities.Where(a => a.DrawingItemId != ""))
                {
                    var alloc = allocRows.Where(x => x.DrawingItemId == a.DrawingItemId && x.SubcontractorId == a.SubcontractorId).Sum(x => x.AllocatedQty);
                    var item = items.First(i => i.Item.Id == a.DrawingItemId);
                    dOk &= Math.Abs(a.CumulativeQty - Math.Round(running2[a.ProgressKey], 4)) < 1e-6 && Math.Abs(a.PlannedQty - (alloc > 0 ? alloc : item.Qty)) < 1e-9;
                }
            }
            log.Check("DPR drawing rows: to date = agency's running total, planned = its allocation", dOk);
            log.Fact($"{p.Name}: {reg.Drawings.Count} drawings · {items.Count} items · {reg.Drawings.Count(d => d.State == DrawingState.Completed)} drawings completed · " +
                     $"{items.Count(i => i.Unallocated > 0)} items partly unallocated · {items.Count(i => i.CompletedWithoutAllocation > 0)} items worked without allocation");
        }

        // registers
        var hs = await db.ListForProjectAsync<Hindrance>(p.Id);
        var rq = await db.ListForProjectAsync<Requirement>(p.Id);
        log.Check("Hindrance serial numbers unique", hs.Select(h => h.SerialNo).Distinct().Count() == hs.Count && hs.All(h => h.SerialNo > 0), $"{hs.Count} hindrances");
        log.Check("Requirement serial numbers unique", rq.Select(h => h.SerialNo).Distinct().Count() == rq.Count && rq.All(h => h.SerialNo > 0), $"{rq.Count} requirements");
        log.Time($"Verify {p.Name}", sw);

        log.Fact($"{p.Name}: {reports.Count} reports · {activities.Count} activities · overall {pp.OverallPercent:0.#} % (plan {pp.PlannedPercent:0.#} %) · " +
                 $"{pp.Activities.Count(a => a.State == WorkState.Completed)} completed, {pp.Activities.Count(a => a.State == WorkState.Delayed)} delayed · " +
                 $"{mdAll:#,0} man-days · cost ₹ {PdfReports.M(projectCost)} · {hs.Count} hindrances · {rq.Count} requirements");
    }

    public static async Task ConcurrencyAsync(LocalDatabase db, Project p, Log log)
    {
        log.Line("\n▶ Concurrency stress: 40 saves and 40 full reads at the same time");
        var sw = Stopwatch.StartNew();
        var reports = (await db.ListForProjectAsync<DailyReport>(p.Id)).OrderBy(r => r.Date).ToList();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        var writer = Task.Run(async () =>
        {
            var rnd = new Random(5);
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    var b = await db.LoadReportAsync(reports[rnd.Next(reports.Count)].Id);
                    b.Report.Remarks += ".";
                    await db.SaveReportAsync(b);
                }
                catch (Exception ex) { errors.Add("write: " + ex.Message); }
            }
        });
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    var acts = await db.ListForProjectAsync<Activity>(p.Id);
                    if (acts.Select(a => a.Id).Distinct().Count() != acts.Count) errors.Add("duplicate activity rows in a read");
                    var bundles = await db.LoadReportsAsync(p.Id, reports);
                    if (bundles.SelectMany(b => b.Manpower).GroupBy(m => m.Id).Any(g => g.Count() > 1)) errors.Add("duplicate manpower rows in a read");
                    await new ProgressService(db).ComputeAsync(p.Id);
                }
                catch (Exception ex) { errors.Add("read: " + ex.Message); }
            }
        })).ToList();
        await Task.WhenAll(readers.Append(writer));
        log.Check("Reads during writes never fail or see a row twice", errors.IsEmpty, errors.IsEmpty ? $"{sw.Elapsed.TotalSeconds:0.0} s" : string.Join("; ", errors.Distinct().Take(3)));
    }

    public static async Task ConvergedAsync(Log log, params (string Name, LocalDatabase Db)[] devices)
    {
        log.Line("\n▶ Verify all devices hold identical data after sync");
        var ok = true; var detail = "";
        foreach (var (type, t) in EntityRegistry.Types)
        {
            var sets = new List<HashSet<string>>();
            foreach (var (_, db) in devices)
            {
                var map = db.Connection.GetConnection().GetMapping(t);
                var rows = await db.Connection.QueryAsync(map, $"SELECT * FROM \"{map.TableName}\"");
                sets.Add(rows.Cast<SyncEntity>().Select(e => $"{e.Id}|{e.UpdatedAtUtc.Ticks}|{e.IsDeleted}").ToHashSet());
            }
            if (sets.Skip(1).Any(s => !s.SetEquals(sets[0]))) { ok = false; detail += $"{type} differs; "; }
            else if (type is nameof(DailyReport) or nameof(ReportManpower) or nameof(MediaItem)) detail += $"{type}: {sets[0].Count}; ";
        }
        log.Check("Same rows on office PC, phone A and phone B", ok, detail.TrimEnd(' ', ';'));
        var missing = 0;
        foreach (var (_, db) in devices)
            missing += (await db.ListAsync<MediaItem>()).Count(m => !File.Exists(db.MediaPath(m.FileName)));
        log.Check("Every photo / scan file present on every device", missing == 0, $"{missing} missing");
    }

    public static async Task IsolationAsync(LocalDatabase db, Log log, params Project[] projects)
    {
        log.Line("\n▶ Verify projects never mix");
        foreach (var p in projects)
        {
            var reportIds = (await db.ListForProjectAsync<DailyReport>(p.Id)).Select(r => r.Id).ToHashSet();
            var foreignRows = (await db.ListAsync<ReportManpower>()).Count(m => reportIds.Contains(m.ReportId) && m.ProjectId != p.Id)
                              + (await db.ListAsync<ReportActivity>()).Count(m => reportIds.Contains(m.ReportId) && m.ProjectId != p.Id);
            var bundles = await new ReportQueryService(db).LoadBundlesAsync(new ReportFilter { ProjectId = p.Id });
            var leaked = bundles.SelectMany(b => b.Manpower.Cast<SyncEntity>().Concat(b.Activities).Concat(b.Machines).Concat(b.Materials)).Count(x => x.ProjectId != p.Id);
            log.Check($"{p.Name}: no rows from other projects", foreignRows == 0 && leaked == 0);
        }
    }
}

static class Reports
{
    public static async Task AllAsync(LocalDatabase db, string outDir, Log log, params Project[] projects)
    {
        log.Line("\n▶ Generate every report");
        var opt = new ReportOptions
        {
            ExtraRemarks = "Generated by the SiteNotes heavy demo.",
            Layout = new PdfLayout { CompanyName = "BuildRight Infra Pvt Ltd", HeaderLines = "Project ref: DEMO/2026\nContract no: 45/2026", HeaderAlign = TextAlign.Center, FooterText = "Demo – for review", PageNumbers = PageNumberStyle.PageNofTotal },
        };
        var logo = Path.Combine(outDir, "_devices", "logo.jpg");
        using (var bmp = new SKBitmap(300, 120))
        {
            using (var c = new SKCanvas(bmp))
            {
                c.Clear(SKColor.Parse("#F59E0B"));
                using var f = new SKFont(SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold), 40);
                using var ink = new SKPaint { Color = SKColors.White };
                c.DrawText("BuildRight", 22, 76, SKTextAlign.Left, f, ink);
            }
            ImageTools.SaveJpeg(bmp, logo);
        }
        opt.Layout.LogoPath = logo;

        var n = 0;
        foreach (var p in projects)
        {
            var folder = Path.Combine(outDir, $"{++n:00}-{Regex.Replace(p.Name, "[^A-Za-z0-9]+", "_").Trim('_')}");
            Directory.CreateDirectory(folder);
            var cal = await db.CalendarAsync(p.Id);
            var q = new ReportQueryService(db);
            var all = new ReportFilter { ProjectId = p.Id, Sort = ReportSort.DateOldest };
            var sw = Stopwatch.StartNew();
            var bundles = await q.LoadBundlesAsync(all);
            log.Time($"{p.Name}: load {bundles.Count} reports with all rows", sw);
            var last = bundles[^1];
            var month = new ReportFilter { ProjectId = p.Id, From = last.Report.Date.AddDays(-29), To = last.Report.Date, Sort = ReportSort.DateOldest };
            var monthBundles = await q.LoadBundlesAsync(month);
            var pp = await new ProgressService(db).ComputeAsync(p.Id);
            var hs = await db.ListForProjectAsync<Hindrance>(p.Id);
            var rq = await db.ListForProjectAsync<Requirement>(p.Id);

            async Task Make(string file, Action<Stream> write, bool pdf)
            {
                var t = Stopwatch.StartNew();
                var path = Path.Combine(folder, file);
                await using (var fs = File.Create(path)) write(fs);
                t.Stop();
                var size = new FileInfo(path).Length;
                if (pdf)
                {
                    var text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
                    var pages = Regex.Matches(text, @"/Type\s*/Page[^s]").Count;
                    log.Check($"{p.Name}: {file}", text.StartsWith("%PDF") && pages > 0, $"{pages} pages · {size / 1024:#,0} KB · {t.Elapsed.TotalSeconds:0.0} s");
                }
                else
                {
                    using var wb = new XLWorkbook(path);
                    log.Check($"{p.Name}: {file}", wb.Worksheets.Count > 0, $"{wb.Worksheets.Count} sheets · {size / 1024:#,0} KB · {t.Elapsed.TotalSeconds:0.0} s");
                }
                log.Timings.Add(($"{p.Name}: {file}", t.Elapsed.TotalMilliseconds));
            }

            await Make("01-DPR-latest.pdf", s => PdfReports.DailyReport(s, last, db.MediaPath, cal, opt), true);
            var custom = ReportOptions.FromJson(opt.ToJson());
            custom.Layout = opt.Layout;
            custom.Sections = [DprSection.Summary, DprSection.Activities, DprSection.Manpower, DprSection.Cost, DprSection.Signatures];
            custom.ActivitySort = ActivityRowSort.ProgressHigh;
            custom.Layout.PageNumbers = PageNumberStyle.NSlashTotal;
            await Make("02-DPR-latest-short-custom-order.pdf", s => PdfReports.DailyReport(s, last, db.MediaPath, cal, custom), true);
            opt.Layout.PageNumbers = PageNumberStyle.PageNofTotal;
            await Make("03-DPR-all-reports-last-30-days.pdf", s => PdfReports.ReportRegister(s, p, monthBundles, month.Describe(), true, db.MediaPath, cal, opt), true);
            await Make("04-DPR-register-whole-project.pdf", s => PdfReports.ReportRegister(s, p, bundles, all.Describe(), false, db.MediaPath, cal, opt), true);
            await Make("05-DPR-workbook-whole-project.xlsx", s => ExcelReports.ReportWorkbook(s, p, bundles, all.Describe(), cal), false);
            await Make("06-Progress-and-cost.pdf", s => ProgressReports.Pdf(s, p, pp, "Whole project", true, opt), true);
            await Make("07-Progress-and-cost.xlsx", s => ProgressReports.Excel(s, p, pp, "Whole project"), false);
            await Make("08-Manpower-summary.pdf", s => PdfReports.ManpowerSummary(s, p, bundles, all.Describe(), opt), true);
            await Make("09-Hindrance-register.pdf", s => PdfReports.HindranceRegister(s, p, hs, "All", opt), true);
            await Make("10-Hindrance-register.xlsx", s => ExcelReports.HindranceRegister(s, p, hs, "All"), false);
            await Make("11-Requirement-register.pdf", s => PdfReports.RequirementRegister(s, p, rq, "All", opt), true);
            await Make("12-Requirement-register.xlsx", s => ExcelReports.RequirementRegister(s, p, rq, "All"), false);
            var dreg = await new DrawingService(db).ComputeAsync(p.Id);
            if (dreg.Drawings.Count > 0)
            {
                await Make("15-Drawing-register-work-completion.pdf", s => DrawingReports.Pdf(s, p, dreg, "All drawings", opt), true);
                await Make("16-Drawing-register-work-completion.xlsx", s => DrawingReports.Excel(s, p, dreg, "All drawings"), false);
            }

            // Excel totals must equal the database
            using (var wb = new XLWorkbook(Path.Combine(folder, "05-DPR-workbook-whole-project.xlsx")))
            {
                var ws = wb.Worksheet("DPR Summary");
                var rows = ws.RangeUsed()!.RowsUsed().Where(r => r.RowNumber() >= 5 && r.Cell(1).GetString() != "TOTAL").ToList();
                var manpower = rows.Sum(r => r.Cell(12).GetValue<double>());
                var cost = rows.Sum(r => r.Cell(21).GetValue<double>());
                log.Check($"{p.Name}: Excel manpower total = database", Math.Abs(manpower - bundles.Sum(b => b.Report.TotalManpower)) < 0.5, $"{manpower:#,0}");
                log.Check($"{p.Name}: Excel cost total = database", Math.Abs((decimal)cost - bundles.Sum(b => b.Report.TotalCost)) < 1m, $"₹ {cost:#,0.00}");
            }
            using (var wb = new XLWorkbook(Path.Combine(folder, "07-Progress-and-cost.xlsx")))
            {
                var ws = wb.Worksheet("Cost by activity");
                var rows = ws.RangeUsed()!.RowsUsed().Where(r => r.RowNumber() >= 5 && r.Cell(1).GetString() != "TOTAL").ToList();
                var cost = rows.Sum(r => r.Cell(8).GetValue<double>());
                log.Check($"{p.Name}: progress workbook cost = project cost", Math.Abs((decimal)cost - pp.Actual.Total) < 1m, $"₹ {cost:#,0.00}");
            }
        }

        // notes + scan, on the first project
        var proj = projects[0];
        var blocks = new List<NoteBlock>
        {
            new() { Type = BlockType.Heading1, Text = "Weekly coordination meeting" },
            new() { Type = BlockType.Heading2, Text = "Decisions" },
            new() { Type = BlockType.Numbered, Text = "Pour sequence for Plant 1 approved" },
            new() { Type = BlockType.Numbered, Text = "Night shift allowed on Saturdays" },
            new() { Type = BlockType.Checklist, Text = "Issue revised drawings", Checked = true },
            new() { Type = BlockType.Checklist, Text = "Mobilise second pump" },
            new() { Type = BlockType.Bullet, Text = "Safety audit on Monday", Style = new TextStyle { Bold = true, Color = "#B91C1C" } },
            new() { Type = BlockType.Arrow, Text = "Next meeting: Friday 10 am" },
            new() { Type = BlockType.Quote, Text = "Client: maintain the 30 % progress target for this month." },
        };
        var note = new Note { ProjectId = proj.Id, Title = "Weekly coordination meeting", Tags = "MOM", ContentJson = NoteDocument.Serialize(blocks), PlainText = NoteDocument.ToPlainText(blocks) };
        await db.SaveAsync(note);
        var pages = new List<ScanPageInput>();
        for (var i = 0; i < 3; i++)
        {
            var f = Path.Combine(outDir, "_devices", $"page{i}.jpg");
            using var bmp = new SKBitmap(900, 1200);
            using (var c = new SKCanvas(bmp))
            {
                using var sh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(900, 1200), [new SKColor(240, 236, 226), new SKColor(170, 160, 150)], SKShaderTileMode.Clamp);
                using var bg = new SKPaint { Shader = sh };
                c.DrawRect(0, 0, 900, 1200, bg);
                using var ink = new SKPaint { Color = new SKColor(30, 30, 30) };
                for (var y = 150; y < 1100; y += 55) c.DrawRect(90, y, 720 - (y % 3) * 60, 9, ink);
            }
            ImageTools.SaveJpeg(bmp, f);
            pages.Add(new ScanPageInput { RawPath = f, Remarks = $"page {i + 1}", Rotation = 0 });
        }
        var doc = await new ScanService(db, Path.Combine(outDir, "_devices", "scanraw")).SaveAsync(new ScanDocument { ProjectId = proj.Id, Title = "Delivery challan (3 pages)" }, pages, ScanFilter.BlackWhite);
        File.Copy(db.MediaPath(doc.PdfFileName), Path.Combine(outDir, "13-Scanned-document-black-white.pdf"), true);
        log.Check("Scan: 3 pages → one PDF in Scans › today", doc.PageCount == 3 && File.Exists(db.MediaPath(doc.PdfFileName)));
        blocks.Add(new NoteBlock { Type = BlockType.Scan, MediaId = doc.Id, Text = doc.Title });
        var media = (await db.ListAsync<MediaItem>(m => m.OwnerId == doc.Id)).OrderBy(m => m.PageNo).First();
        await using (var fs = File.Create(Path.Combine(outDir, "14-Note-with-scan.pdf")))
            PdfReports.Note(fs, note, blocks, "Meetings", id => id == doc.Id ? media : null, db.MediaPath, opt);
        log.Check("Note PDF with checklist, lists, arrow, quote and scan", new FileInfo(Path.Combine(outDir, "14-Note-with-scan.pdf")).Length > 5000);
    }

    public static void VerificationPdf(string path, Log log)
    {
        using var fs = File.Create(path);
        PdfDoc.Render(fs, "SiteNotes DPR – demo verification", $"{log.Passed} passed · {log.Failed} failed", false,
            new PdfLayout { PageNumbers = PageNumberStyle.PageNofTotal, FooterText = "Heavy demo run on the real code" }, pdf =>
            {
                pdf.NewPage();
                pdf.Text("HEAVY DEMO – VERIFICATION REPORT", 16, bold: true, color: PdfDoc.Accent, spacingAfter: 4);
                pdf.Text($"{DateTime.Now:dd MMM yyyy HH:mm} · 3 projects on 3 devices (office PC, 2 site phones) synced through the real server", 9, color: PdfDoc.Muted, spacingAfter: 8);
                pdf.Tiles([("Checks passed", log.Passed.ToString()), ("Checks failed", log.Failed.ToString()), ("Reports generated", log.Checks.Count(c => c.Name.Contains(".pdf") || c.Name.Contains(".xlsx")).ToString())]);
                pdf.Section("Projects");
                foreach (var f in log.Facts.Distinct()) pdf.Text("• " + f, 9, spacingAfter: 2);
                pdf.Section("Checks");
                pdf.Table([new("#", 0.4f), new("Check", 5), new("Result", 0.8f), new("Details", 4)],
                    log.Checks.Select((c, i) => new[] { (i + 1).ToString(), c.Name, c.Ok ? "PASS" : "FAIL", c.Detail }), fontSize: 7.5f);
                pdf.Section("Timings");
                pdf.Table([new("Step", 5), new("Seconds", 1, true)], log.Timings.Select(t => new[] { t.Name, (t.Ms / 1000).ToString("0.00") }), fontSize: 7.5f);
            });
    }
}
