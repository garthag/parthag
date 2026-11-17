using System.Data;
using Dapper;

namespace Bf3Billing.Core;

/// <summary>
/// Data loaded into a new data file: activities, the BF#3 contractors and their
/// work orders, and default rates. Nothing is billed.
/// <see cref="LoadDemo"/> adds a few BOM drawings and sample bills for trying the app.
/// </summary>
public static class Seed
{
    public static readonly (string Name, bool IsBom, string Unit)[] Activities =
    {
        ("Fabrication", true, "t"),
        ("Erection", true, "t"),
        ("Painting", true, "t"),
        ("Supply / transport", true, "t"),
        ("Dismantling", false, "t"),
        ("Utility", false, "LS"),
        ("Other", false, "nr"),
    };

    public static readonly (string Code, string Name)[] Contractors =
    {
        ("GRE", "G.R ENTERPRISES"),
        ("MDE", "M.D. ESTHAPAN"),
        ("SEW", "SURAJ ENGINEERING WORKS"),
        ("CHC", "CHOUDHARY CONSTRUCTION"),
        ("RAZ", "RAZZAVUDDIN"),
    };

    public static readonly (string WoNo, string Contractor, string Service, decimal Value)[] WorkOrders =
    {
        ("RAZZAVUDDIN · DISMANTLING", "RAZZAVUDDIN", "DISMANTLING", 0m),
        ("MCK/WO-MECH/BSL/2023-24/002", "G.R ENTERPRISES", "DISMANTLING", 2_30_00_000m),
        ("MCK/WO-MECH/BSL/2023-24/003", "SURAJ ENGINEERING WORKS", "DISMANTLING", 42_00_000m),
        ("MCK/WO-MECH/BSL/2023-24/004", "M.D. ESTHAPAN", "DISMANTLING", 20_80_000m),
        ("MCK/WO-MECH/BSL/2024-25/006", "G.R ENTERPRISES", "ERECTION", 1_93_50_070m),
        ("MCK/WO-MECH/BSL/2024-25/007", "G.R ENTERPRISES", "ERECTION", 91_94_160m),
        ("MCK/WO-MECH/BSL/2024-25/008", "M.D. ESTHAPAN", "ERECTION", 1_97_69_050m),
        ("MCK/WO-MECH/BSL/2024-25/009", "M.D. ESTHAPAN", "ERECTION", 74_55_375m),
        ("MCK/WO-MECH/BSL/2024-25/010", "SURAJ ENGINEERING WORKS", "ERECTION", 74_83_400m),
        ("MCK/WO-MECH/BSL/2024-25/012", "G.R ENTERPRISES", "ERECTION", 2_39_52_700m),
        ("MCK/WO-MECH/BSL/2024-25/013", "SURAJ ENGINEERING WORKS", "ERECTION", 3_44_853m),
        ("MCK/WO-MECH/BSL/2024-25/014", "SURAJ ENGINEERING WORKS", "ERECTION", 51_10_606m),
        ("MCK/BSL/24-25/01", "CHOUDHARY CONSTRUCTION", "", 32_50_000m),
        ("G.R ENTERPRISES · MODIFICATION/FABRICATION", "G.R ENTERPRISES", "MODIFICATION/FABRICATION", 0m),
    };

    public static void Masters(IDbConnection c)
    {
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM Activity", transaction: tx) == 0)
        {
            var i = 0;
            foreach (var a in Activities)
                c.Execute("INSERT INTO Activity(Name, IsBom, DefaultUnit, SortOrder) VALUES (@Name, @IsBom, @Unit, @Sort)",
                    new { a.Name, a.IsBom, a.Unit, Sort = ++i * 10 }, tx);
        }

        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM Contractor", transaction: tx) == 0)
        {
            foreach (var k in Contractors)
                c.Execute("INSERT INTO Contractor(Code, Name) VALUES (@Code, @Name)", new { k.Code, k.Name }, tx);
            foreach (var w in WorkOrders)
                c.Execute("""
                    INSERT INTO WorkOrder(WoNo, ContractorId, Service, Value, Status)
                    VALUES (@WoNo, (SELECT Id FROM Contractor WHERE Name = @Contractor), @Service, @Value, 'Open')
                    """, new { w.WoNo, w.Contractor, w.Service, w.Value }, tx);
        }

        foreach (var (k, v) in new[] { ("DefaultGstPct", "18"), ("DefaultRetentionPct", "0"), ("DefaultTdsPct", "0"), ("ProjectName", "BF#3") })
            c.Execute("INSERT OR IGNORE INTO Setting(Key, Value) VALUES (@k, @v)", new { k, v }, tx);
        tx.Commit();
    }

    /// <summary>A few real BF#3 drawings (not the full 458-drawing BOM).</summary>
    public static readonly (string System, string Drawing, string Sheet, string Rev, decimal Tonnes)[] DemoBom =
    {
        ("1 STOVE", "16193442", "2/6", "A", 132.273m),
        ("1 STOVE", "16193442", "3/6", "A", 309.714m),
        ("1 STOVE", "16193442", "4/6", "A", 58.200m),
        ("1 STOVE", "1623628", "", "", 1.155m),
        ("1 STOVE NOZZLES", "16193442", "5/6", "", 61.209m),
        ("1 STOVE NOZZLES", "16135904", "", "B", 0.016m),
        ("1 STOVE HOUSE", "ERC/MCK/BSL/STH/F-001", "", "3", 0m),
        ("1 STOVE HOUSE", "ERC/MCK/BSL/STH/F-002", "", "2", 0m),
        ("1 STOVE HOUSE", "ERC/MCK/BSL/STH/F-003", "", "3", 0m),
        ("1 STOVE HOUSE", "ERC/MCK/BSL/STH/F-004", "", "1", 32.618m),
    };

    /// <summary>Loads demo BOM drawings and six made-up bills (the figures in the design preview).</summary>
    public static void LoadDemo(BillingRepository repo)
    {
        if (repo.BomCount() == 0)
            repo.UpsertBom(DemoBom.Select(b => new BomItem { System = b.System, DrawingNo = b.Drawing, Sheet = b.Sheet, Rev = b.Rev, Tonnes = b.Tonnes }));

        var acts = repo.Activities().ToDictionary(a => a.Name, a => a.Id);
        var bom = repo.BomItems();
        long Bom(string sheet) => bom.First(b => b.DrawingNo == "16193442" && b.Sheet == sheet).Id;
        long Wo(string no) => repo.WorkOrders().First(w => w.WoNo == no).Id;
        BillLine BomLine(string sheet, string act, decimal qty, decimal rate) =>
            new() { BomItemId = Bom(sheet), ActivityId = acts[act], Unit = "t", Qty = qty, Rate = rate };
        BillLine Free(string desc, string act, string unit, decimal qty, decimal rate, string remarks = "") =>
            new() { Description = desc, ActivityId = acts[act], Unit = unit, Qty = qty, Rate = rate, Remarks = remarks };

        void Add(string wo, string date, string status, decimal paid, string? paidOn, params BillLine[] lines)
        {
            var b = new Bill
            {
                WorkOrderId = Wo(wo), BillNo = "RA-1", BillType = "RA bill",
                BillDate = DateTime.ParseExact(date, "dd-MM-yyyy", null), Status = status, GstPct = 18,
                PaidAmount = paid, PaidOn = paidOn == null ? null : DateTime.ParseExact(paidOn, "dd-MM-yyyy", null),
                Remarks = "Sample bill",
            };
            repo.SaveBill(b, lines);
        }

        Add("MCK/WO-MECH/BSL/2024-25/006", "18-08-2025", BillStatus.Paid, 7_54_881.40m, "15-09-2025",
            BomLine("2/6", "Erection", 38.420m, 6500), BomLine("3/6", "Erection", 60m, 6500));
        Add("MCK/WO-MECH/BSL/2024-25/008", "30-09-2025", BillStatus.Certified, 0, null,
            BomLine("3/6", "Erection", 60m, 6500), BomLine("4/6", "Erection", 40m, 7250));
        Add("MCK/WO-MECH/BSL/2024-25/013", "11-12-2025", BillStatus.Submitted, 0, null,
            Free("Erection of service platforms", "Erection", "LS", 1, 3_25_000));
        Add("MCK/BSL/24-25/01", "20-01-2026", BillStatus.Submitted, 0, null,
            Free("Site levelling near stove house", "Utility", "LS", 1, 69_000));
        Add("MCK/WO-MECH/BSL/2023-24/002", "04-02-2026", BillStatus.Paid, 9_66_420m, "28-02-2026",
            BomLine("3/6", "Supply / transport", 60m, 13_650));
        Add("MCK/WO-MECH/BSL/2024-25/012", "09-03-2026", BillStatus.Draft, 0, null,
            BomLine("3/6", "Erection", 25m, 6500), BomLine("4/6", "Erection", 25m, 6500),
            Free("Temporary scaffolding for dome lift", "Utility", "LS", 1, 85_000, "Per site measurement"));
    }
}
