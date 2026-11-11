using SiteNotes.Core.Data;
using SiteNotes.Core.Models;

namespace SiteNotes.Core;

public enum DrawingState { NotAllocated, PartlyAllocated, Allocated, InProgress, Completed }

/// <summary>One agency's share of a drawing item and how much of it is done.</summary>
public class AllocationProgress
{
    public string SubcontractorId { get; init; } = "";
    public string Agency { get; init; } = "";
    public double Allocated { get; set; }
    public double Done { get; set; }
    /// <summary>Unit price payable (work-order rate; quantity-weighted if the agency has several allocations on the item).</summary>
    public decimal Rate { get; set; }
    public decimal AllocatedValue { get; set; }
    /// <summary>Payable for work done, up to the allocated quantity.</summary>
    public decimal PayableValue => CostCalculator.Round((decimal)Math.Min(Done, Allocated) * Rate);
    /// <summary>Value of work done beyond the allocation (to be approved before paying).</summary>
    public decimal ExcessValue => CostCalculator.Round((decimal)Excess * Rate);
    public decimal BalanceValue => CostCalculator.Round((decimal)Balance * Rate);
    public double Balance => Math.Max(0, Allocated - Done);
    public double Percent => Allocated <= 0 ? 0 : Math.Min(100, Math.Round(Done / Allocated * 100, 2));
    public DateTime? FirstWorked { get; set; }
    public DateTime? LastWorked { get; set; }
    public string WorkOrders { get; set; } = "";
    /// <summary>True when this agency reported work on the item without an allocation.</summary>
    public bool Unallocated => Allocated <= 0;
    /// <summary>Done beyond this agency's allocation (to be checked / re-allocated).</summary>
    public double Excess => Allocated > 0 ? Math.Max(0, Math.Round(Done - Allocated, 4)) : 0;
}

public class DrawingItemProgress
{
    public DrawingItem Item { get; init; } = new();
    public double Qty => Item.Qty;
    public double Allocated { get; set; }
    public double Unallocated => Math.Max(0, Qty - Allocated);
    public double OverAllocated => Math.Max(0, Allocated - Qty);
    public double Completed { get; set; }
    /// <summary>Work reported by agencies that have no allocation on this item.</summary>
    public double CompletedWithoutAllocation { get; set; }
    public double Balance => Math.Max(0, Qty - Completed);
    /// <summary>Reported beyond the drawing quantity (wrong entry, revision, or extra work to be claimed).</summary>
    public double Excess => Math.Max(0, Math.Round(Completed - Qty, 4));
    public double Percent => Qty <= 0 ? 0 : Math.Min(100, Math.Round(Completed / Qty * 100, 2));
    public double AllocatedPercent => Qty <= 0 ? 0 : Math.Min(100, Math.Round(Allocated / Qty * 100, 2));
    public List<AllocationProgress> Agencies { get; } = [];
    /// <summary>BOQ value = drawing qty × BOQ rate.</summary>
    public decimal Value => CostCalculator.Round((decimal)Qty * Item.Rate);
    /// <summary>Value of the unallocated quantity at the BOQ rate.</summary>
    public decimal UnallocatedValue => CostCalculator.Round((decimal)Unallocated * Item.Rate);
    /// <summary>Value of completed work at the BOQ rate (capped at the drawing qty).</summary>
    public decimal CompletedValue => CostCalculator.Round((decimal)Math.Min(Completed, Qty) * Item.Rate);
    /// <summary>Σ agencies' allocated qty × their unit price payable.</summary>
    public decimal AllocatedValue => Agencies.Sum(a => a.AllocatedValue);
    /// <summary>Σ agencies' payable for work done.</summary>
    public decimal PayableValue => Agencies.Sum(a => a.PayableValue);
    public DrawingState State => Completed >= Qty && Qty > 0 ? DrawingState.Completed
        : Completed > 0 ? DrawingState.InProgress
        : Allocated <= 0 ? DrawingState.NotAllocated
        : Allocated + 1e-9 < Qty ? DrawingState.PartlyAllocated
        : DrawingState.Allocated;
}

/// <summary>Totals of a drawing for one unit (a drawing can mix MT, Nos, m³…).</summary>
public record UnitTotal(string Unit, double Qty, double Allocated, double Unallocated, double Completed, double Balance)
{
    public double Percent => Qty <= 0 ? 0 : Math.Min(100, Math.Round(Completed / Qty * 100, 2));
}

public class DrawingProgress
{
    public Drawing Drawing { get; init; } = new();
    public string WbsPath { get; set; } = "";
    public List<DrawingItemProgress> Items { get; } = [];
    public List<UnitTotal> Totals { get; } = [];
    /// <summary>Average of item % weighted by quantity within each unit, then across units.</summary>
    public double Percent { get; set; }
    public double AllocatedPercent { get; set; }
    public DrawingState State { get; set; }
    public int Agencies => Items.SelectMany(i => i.Agencies).Select(a => a.SubcontractorId).Distinct().Count();
    public string TotalsText => string.Join(" · ", Totals.Select(t => $"{t.Qty:#,0.##} {t.Unit}"));
    public decimal Value => Items.Sum(i => i.Value);
    public decimal AllocatedValue => Items.Sum(i => i.AllocatedValue);
    public decimal UnallocatedValue => Items.Sum(i => i.UnallocatedValue);
    public decimal CompletedValue => Items.Sum(i => i.CompletedValue);
    public decimal PayableValue => Items.Sum(i => i.PayableValue);
    public string? FileName { get; set; }
}

/// <summary>Subcontractor-wise drawing work: allocated vs done, per unit.</summary>
public record AgencyDrawingSummary(string Agency, string Unit, double Allocated, double Done, double Balance, int Drawings, int Items, double WithoutAllocation,
    decimal AllocatedValue = 0, decimal PayableValue = 0, decimal BalanceValue = 0, string WorkOrders = "")
{
    public double Percent => Allocated <= 0 ? 0 : Math.Min(100, Math.Round(Done / Allocated * 100, 2));
}

public class DrawingRegister
{
    public List<DrawingProgress> Drawings { get; } = [];
    public List<AgencyDrawingSummary> ByAgency { get; } = [];
    public IEnumerable<DrawingItemProgress> AllItems => Drawings.SelectMany(d => d.Items);
}

/// <summary>
/// Drawing register with work completion: for every drawing item the quantity, how much is allocated to which
/// agency, how much is still unallocated, how much each agency has completed (from the daily reports) and the balance.
/// </summary>
public class DrawingService(LocalDatabase db)
{
    public async Task<DrawingRegister> ComputeAsync(string projectId, DateTime? asOf = null)
    {
        var until = (asOf ?? DateTime.MaxValue).Date;
        var drawings = await db.ListForProjectAsync<Drawing>(projectId);
        var items = (await db.ListForProjectAsync<DrawingItem>(projectId)).ToLookup(i => i.DrawingId);
        var allocs = (await db.ListForProjectAsync<DrawingAllocation>(projectId)).ToLookup(a => a.DrawingItemId);
        var subs = (await db.ListForProjectAsync<Subcontractor>(projectId)).ToDictionary(s => s.Id, s => s.Name);
        var wbs = (await db.ListForProjectAsync<WbsNode>(projectId)).ToDictionary(w => w.Id);
        var reports = (await db.ListForProjectAsync<DailyReport>(projectId)).Where(r => r.Date.Date <= until).ToDictionary(r => r.Id, r => r.Date.Date);
        var rows = (await db.ListForProjectAsync<ReportActivity>(projectId))
            .Where(r => r.DrawingItemId != "" && reports.ContainsKey(r.ReportId)).ToLookup(r => r.DrawingItemId);
        string Name(string id, string fallback = "") => subs.GetValueOrDefault(id, fallback == "" ? (id == "" ? "(own)" : "(unknown agency)") : fallback);

        var reg = new DrawingRegister();
        foreach (var d in drawings.OrderBy(d => d.Number))
        {
            var dp = new DrawingProgress { Drawing = d, WbsPath = ProgressService.WbsPath(d.WbsId, wbs) };
            if (d.FileMediaId != "" && await db.GetAsync<MediaItem>(d.FileMediaId) is { IsDeleted: false } f) dp.FileName = f.FileName;
            foreach (var it in items[d.Id].OrderBy(i => i.SortOrder).ThenBy(i => i.ItemNo))
            {
                var ip = new DrawingItemProgress { Item = it };
                var work = rows[it.Id].ToList();
                var byAgency = new Dictionary<string, AllocationProgress>();
                foreach (var a in allocs[it.Id])
                {
                    if (!byAgency.TryGetValue(a.SubcontractorId, out var ap))
                        byAgency[a.SubcontractorId] = ap = new AllocationProgress { SubcontractorId = a.SubcontractorId, Agency = Name(a.SubcontractorId, a.SubcontractorName) };
                    ap.Allocated += a.AllocatedQty;
                    ap.AllocatedValue += CostCalculator.Round((decimal)a.AllocatedQty * a.Rate);
                    if (a.WorkOrderNo != "") ap.WorkOrders = ap.WorkOrders == "" ? a.WorkOrderNo : ap.WorkOrders + ", " + a.WorkOrderNo;
                }
                foreach (var g in work.GroupBy(w => w.SubcontractorId))
                {
                    if (!byAgency.TryGetValue(g.Key, out var ap))
                        byAgency[g.Key] = ap = new AllocationProgress { SubcontractorId = g.Key, Agency = Name(g.Key, g.First().SubcontractorName) };
                    ap.Done = Math.Round(g.Sum(w => w.TodayQty), 4);
                    var dates = g.Where(w => w.TodayQty > 0).Select(w => reports[w.ReportId]).ToList();
                    if (dates.Count > 0) { ap.FirstWorked = dates.Min(); ap.LastWorked = dates.Max(); }
                }
                foreach (var ap in byAgency.Values) ap.Rate = ap.Allocated > 0 ? Math.Round(ap.AllocatedValue / (decimal)ap.Allocated, 4) : 0;
                ip.Agencies.AddRange(byAgency.Values.OrderByDescending(a => a.Allocated).ThenBy(a => a.Agency));
                ip.Allocated = Math.Round(ip.Agencies.Sum(a => a.Allocated), 4);
                ip.Completed = Math.Round(work.Sum(w => w.TodayQty), 4);
                ip.CompletedWithoutAllocation = Math.Round(ip.Agencies.Where(a => a.Unallocated).Sum(a => a.Done), 4);
                dp.Items.Add(ip);
            }
            foreach (var g in dp.Items.GroupBy(i => i.Item.Unit))
                dp.Totals.Add(new UnitTotal(g.Key, g.Sum(i => i.Qty), g.Sum(i => i.Allocated), g.Sum(i => i.Unallocated), g.Sum(i => i.Completed), g.Sum(i => i.Balance)));
            dp.Percent = Weighted(dp, i => i.Percent);
            dp.AllocatedPercent = Weighted(dp, i => i.AllocatedPercent);
            dp.State = dp.Items.Count == 0 ? DrawingState.NotAllocated
                : dp.Items.All(i => i.State == DrawingState.Completed) ? DrawingState.Completed
                : dp.Items.Any(i => i.Completed > 0) ? DrawingState.InProgress
                : dp.Items.All(i => i.State == DrawingState.NotAllocated) ? DrawingState.NotAllocated
                : dp.Items.All(i => i.State == DrawingState.Allocated) ? DrawingState.Allocated
                : DrawingState.PartlyAllocated;
            reg.Drawings.Add(dp);
        }

        // subcontractor-wise, per unit
        foreach (var g in reg.Drawings.SelectMany(d => d.Items.SelectMany(i => i.Agencies.Select(a => (d, i, a))))
                     .GroupBy(x => (x.a.Agency, x.i.Item.Unit)).OrderBy(g => g.Key.Agency).ThenBy(g => g.Key.Unit))
        {
            var allocated = g.Sum(x => x.a.Allocated);
            var done = g.Sum(x => x.a.Done);
            reg.ByAgency.Add(new AgencyDrawingSummary(g.Key.Agency, g.Key.Unit, Math.Round(allocated, 4), Math.Round(done, 4),
                Math.Round(g.Sum(x => x.a.Balance), 4), g.Select(x => x.d.Drawing.Id).Distinct().Count(), g.Select(x => x.i.Item.Id).Distinct().Count(),
                Math.Round(g.Where(x => x.a.Unallocated).Sum(x => x.a.Done), 4),
                g.Sum(x => x.a.AllocatedValue), g.Sum(x => x.a.PayableValue), g.Sum(x => x.a.BalanceValue),
                string.Join(", ", g.Select(x => x.a.WorkOrders).Where(w => w != "").Distinct())));
        }
        return reg;
    }

    private static double Weighted(DrawingProgress d, Func<DrawingItemProgress, double> pct)
    {
        // quantities in different units cannot be added: weight items by quantity within a unit, units equally
        var perUnit = d.Items.Where(i => i.Qty > 0).GroupBy(i => i.Item.Unit)
            .Select(g => g.Sum(i => i.Qty * pct(i)) / g.Sum(i => i.Qty)).ToList();
        return perUnit.Count == 0 ? 0 : Math.Round(perUnit.Average(), 2);
    }
}
