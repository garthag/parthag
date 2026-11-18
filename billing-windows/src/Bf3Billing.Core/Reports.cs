using System.Data;

namespace Bf3Billing.Core;

/// <summary>Reports as plain tables, so the app can show, export and print them the same way.</summary>
public static class Reports
{
    public static readonly string[] Names =
    {
        "Contractor-wise abstract",
        "Work order-wise abstract",
        "Activity-wise abstract",
        "Bill register",
        "Payment register",
        "Outstanding bills",
        "Bill lines (all)",
        "Lines above BOM scope",
    };

    public static DataTable Run(BillingRepository repo, string name, DateTime? from = null, DateTime? to = null)
    {
        var bills = repo.BillSummaries(new BillFilter { From = from, To = to })
            .Where(b => b.Status != BillStatus.Cancelled).OrderBy(b => b.BillDate).ThenBy(b => b.Id).ToList();
        var t = new DataTable(name);
        switch (name)
        {
            case "Contractor-wise abstract":
            {
                Cols(t, ("Contractor", typeof(string)), ("Work orders", typeof(int)), ("WO value ₹", typeof(decimal)), ("Bills", typeof(int)),
                    ("Gross ₹", typeof(decimal)), ("Net payable ₹", typeof(decimal)), ("Paid ₹", typeof(decimal)), ("Outstanding ₹", typeof(decimal)));
                foreach (var g in repo.WorkOrders().GroupBy(w => w.ContractorName).OrderBy(g => g.Key))
                {
                    var bs = bills.Where(b => b.Contractor == g.Key).ToList();
                    t.Rows.Add(g.Key, g.Count(), g.Sum(w => w.Value), bs.Count, bs.Sum(b => b.Gross), bs.Sum(b => b.Net), bs.Sum(b => b.Paid), bs.Sum(b => b.Due));
                }
                TotalRow(t);
                break;
            }
            case "Work order-wise abstract":
            {
                Cols(t, ("Work order", typeof(string)), ("Contractor", typeof(string)), ("Service", typeof(string)), ("WO value ₹", typeof(decimal)),
                    ("Bills", typeof(int)), ("Gross ₹", typeof(decimal)), ("Balance ₹", typeof(decimal)), ("Net payable ₹", typeof(decimal)),
                    ("Paid ₹", typeof(decimal)), ("Outstanding ₹", typeof(decimal)));
                foreach (var w in repo.WorkOrders())
                {
                    var bs = bills.Where(b => b.WorkOrderId == w.Id).ToList();
                    t.Rows.Add(w.WoNo, w.ContractorName, w.Service, w.Value, bs.Count, bs.Sum(b => b.Gross), w.Value - bs.Sum(b => b.Gross),
                        bs.Sum(b => b.Net), bs.Sum(b => b.Paid), bs.Sum(b => b.Due));
                }
                TotalRow(t);
                break;
            }
            case "Activity-wise abstract":
            {
                Cols(t, ("Activity", typeof(string)), ("Unit", typeof(string)), ("Lines", typeof(int)), ("Quantity", typeof(decimal)), ("Amount ₹", typeof(decimal)));
                var lines = repo.LineDetails(null, from, to).Where(l => l.Status != BillStatus.Cancelled);
                foreach (var g in lines.GroupBy(l => (l.Activity, l.Unit)).OrderBy(g => g.Key.Activity))
                    t.Rows.Add(g.Key.Activity == "" ? "(none)" : g.Key.Activity, g.Key.Unit, g.Count(), g.Sum(l => l.Qty), g.Sum(l => l.Amount));
                TotalRow(t, "Quantity");
                break;
            }
            case "Bill register":
            case "Payment register":
            case "Outstanding bills":
            {
                Cols(t, ("Date", typeof(string)), ("Bill no.", typeof(string)), ("Contractor", typeof(string)), ("Work order", typeof(string)),
                    ("Status", typeof(string)), ("Gross ₹", typeof(decimal)), ("Net payable ₹", typeof(decimal)), ("Paid ₹", typeof(decimal)), ("Due ₹", typeof(decimal)));
                IEnumerable<BillSummary> q = bills;
                if (name == "Payment register") q = q.Where(b => b.Paid > 0);
                if (name == "Outstanding bills") q = q.Where(b => b.Due > 0.005m);
                foreach (var b in q)
                    t.Rows.Add(Fmt.Date(b.BillDate), b.BillNo, b.Contractor, b.WorkOrder, b.Status, b.Gross, b.Net, b.Paid, b.Due);
                TotalRow(t);
                break;
            }
            case "Bill lines (all)":
            case "Lines above BOM scope":
            {
                Cols(t, ("Date", typeof(string)), ("Bill no.", typeof(string)), ("Contractor", typeof(string)), ("Work order", typeof(string)),
                    ("#", typeof(int)), ("Item", typeof(string)), ("Activity", typeof(string)), ("Unit", typeof(string)),
                    ("Qty", typeof(decimal)), ("Rate ₹", typeof(decimal)), ("Amount ₹", typeof(decimal)), ("Remarks", typeof(string)));
                var lines = repo.LineDetails(null, from, to).Where(l => l.Status != BillStatus.Cancelled).ToList();
                if (name == "Lines above BOM scope")
                {
                    var over = OverScopeKeys(repo);
                    lines = lines.Where(l => over.Contains((l.BillId, l.LineNo))).ToList();
                }
                foreach (var l in lines)
                    t.Rows.Add(Fmt.Date(l.BillDate), l.BillNo, l.Contractor, l.WorkOrder, l.LineNo, l.Item, l.Activity, l.Unit, l.Qty, l.Rate, l.Amount, l.Remarks);
                if (name == "Bill lines (all)") TotalRow(t, "Qty", "Rate ₹", "#");
                break;
            }
            default:
                throw new BillingException($"Unknown report {name}.");
        }
        return t;
    }

    static HashSet<(long BillId, int LineNo)> OverScopeKeys(BillingRepository repo)
    {
        var ids = repo.OverScopeLineIds();
        var set = new HashSet<(long, int)>();
        foreach (var b in repo.BillSummaries().Where(b => b.OverScopeLines > 0))
        {
            var bill = repo.GetBill(b.Id);
            if (bill == null) continue;
            foreach (var l in bill.Value.Lines.Where(l => ids.Contains(l.Id))) set.Add((b.Id, l.LineNo));
        }
        return set;
    }

    static void Cols(DataTable t, params (string Name, Type Type)[] cols)
    {
        foreach (var (n, ty) in cols) t.Columns.Add(n, ty);
    }

    /// <summary>Adds a "Total" row that sums the decimal columns (except the ones named).</summary>
    static void TotalRow(DataTable t, params string[] skip)
    {
        if (t.Rows.Count == 0) return;
        var r = t.NewRow();
        r[0] = "Total";
        foreach (DataColumn c in t.Columns)
        {
            if (skip.Contains(c.ColumnName) || c.Ordinal == 0) continue;
            if (c.DataType == typeof(decimal)) r[c] = t.AsEnumerable().Sum(x => x.IsNull(c) ? 0m : (decimal)x[c]);
            else if (c.DataType == typeof(int) && c.ColumnName is "Bills" or "Lines" or "Work orders") r[c] = t.AsEnumerable().Sum(x => x.IsNull(c) ? 0 : (int)x[c]);
        }
        t.Rows.Add(r);
    }
}
