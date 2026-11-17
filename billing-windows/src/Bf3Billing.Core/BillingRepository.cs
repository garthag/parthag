using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Bf3Billing.Core;

/// <summary>A rule the user broke (shown as a message, not a crash).</summary>
public sealed class BillingException(string message) : Exception(message);

public sealed class BillingRepository(BillingDb db)
{
    public BillingDb Db { get; } = db;

    SqliteConnection Open() => Db.Open();

    // ───────────────────────── Settings ─────────────────────────

    public string? GetSetting(string key)
    {
        using var c = Open();
        return c.ExecuteScalar<string?>("SELECT Value FROM Setting WHERE Key = @key", new { key });
    }

    public void SetSetting(string key, string? value)
    {
        using var c = Open();
        c.Execute("INSERT INTO Setting(Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value", new { key, value });
    }

    public decimal GetSettingDecimal(string key, decimal fallback) =>
        decimal.TryParse(GetSetting(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public void SetSettingDecimal(string key, decimal value) => SetSetting(key, value.ToString(CultureInfo.InvariantCulture));

    // ───────────────────────── Contractors ─────────────────────────

    public List<Contractor> Contractors()
    {
        using var c = Open();
        return c.Query<Contractor>("SELECT * FROM Contractor ORDER BY Name").ToList();
    }

    public long SaveContractor(Contractor k)
    {
        k.Name = k.Name.Trim();
        if (k.Name.Length == 0) throw new BillingException("Enter the contractor's name.");
        using var c = Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM Contractor WHERE Name = @Name AND Id <> @Id", k) > 0)
            throw new BillingException($"A contractor named {k.Name} already exists.");
        if (k.Id == 0)
            return k.Id = c.ExecuteScalar<long>("""
                INSERT INTO Contractor(Code, Name, Gstin, Pan, Phone, Address, Remarks, Extra)
                VALUES (@Code, @Name, @Gstin, @Pan, @Phone, @Address, @Remarks, @Extra) RETURNING Id
                """, k);
        c.Execute("""
            UPDATE Contractor SET Code=@Code, Name=@Name, Gstin=@Gstin, Pan=@Pan, Phone=@Phone,
              Address=@Address, Remarks=@Remarks, Extra=@Extra WHERE Id=@Id
            """, k);
        return k.Id;
    }

    public void DeleteContractor(long id)
    {
        using var c = Open();
        var n = c.ExecuteScalar<long>("SELECT COUNT(*) FROM WorkOrder WHERE ContractorId = @id", new { id });
        if (n > 0) throw new BillingException($"This contractor has {n} work order(s). Delete or move them first.");
        c.Execute("DELETE FROM Contractor WHERE Id = @id", new { id });
    }

    // ───────────────────────── Work orders ─────────────────────────

    public List<WorkOrder> WorkOrders()
    {
        using var c = Open();
        return c.Query<WorkOrder>("""
            SELECT w.*, k.Name AS ContractorName FROM WorkOrder w JOIN Contractor k ON k.Id = w.ContractorId
            ORDER BY w.Id
            """).ToList();
    }

    public long SaveWorkOrder(WorkOrder w)
    {
        w.WoNo = w.WoNo.Trim();
        if (w.WoNo.Length == 0) throw new BillingException("Enter the work order number.");
        if (w.ContractorId == 0) throw new BillingException("Choose the contractor.");
        if (w.Value < 0) throw new BillingException("The work order value cannot be negative.");
        using var c = Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM WorkOrder WHERE WoNo = @WoNo AND Id <> @Id", w) > 0)
            throw new BillingException($"Work order {w.WoNo} already exists.");
        if (w.Id == 0)
            return w.Id = c.ExecuteScalar<long>("""
                INSERT INTO WorkOrder(WoNo, ContractorId, Service, Value, WoDate, Status, Remarks, Extra)
                VALUES (@WoNo, @ContractorId, @Service, @Value, @WoDate, @Status, @Remarks, @Extra) RETURNING Id
                """, w);
        c.Execute("""
            UPDATE WorkOrder SET WoNo=@WoNo, ContractorId=@ContractorId, Service=@Service, Value=@Value,
              WoDate=@WoDate, Status=@Status, Remarks=@Remarks, Extra=@Extra WHERE Id=@Id
            """, w);
        return w.Id;
    }

    public void DeleteWorkOrder(long id)
    {
        using var c = Open();
        var n = c.ExecuteScalar<long>("SELECT COUNT(*) FROM Bill WHERE WorkOrderId = @id", new { id });
        if (n > 0) throw new BillingException($"This work order has {n} bill(s). Delete them first.");
        c.Execute("DELETE FROM Rate WHERE WorkOrderId = @id; DELETE FROM WorkOrder WHERE Id = @id", new { id });
    }

    // ───────────────────────── Activities ─────────────────────────

    public List<Activity> Activities()
    {
        using var c = Open();
        return c.Query<Activity>("SELECT * FROM Activity ORDER BY SortOrder, Name").ToList();
    }

    public long SaveActivity(Activity a)
    {
        a.Name = a.Name.Trim();
        if (a.Name.Length == 0) throw new BillingException("Enter the activity name.");
        using var c = Open();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM Activity WHERE Name = @Name AND Id <> @Id", a) > 0)
            throw new BillingException($"Activity {a.Name} already exists.");
        if (a.Id == 0)
        {
            if (a.SortOrder == 0) a.SortOrder = c.ExecuteScalar<int>("SELECT COALESCE(MAX(SortOrder), 0) + 10 FROM Activity");
            return a.Id = c.ExecuteScalar<long>(
                "INSERT INTO Activity(Name, IsBom, DefaultUnit, SortOrder) VALUES (@Name, @IsBom, @DefaultUnit, @SortOrder) RETURNING Id", a);
        }
        c.Execute("UPDATE Activity SET Name=@Name, IsBom=@IsBom, DefaultUnit=@DefaultUnit, SortOrder=@SortOrder WHERE Id=@Id", a);
        return a.Id;
    }

    public void DeleteActivity(long id)
    {
        using var c = Open();
        var n = c.ExecuteScalar<long>("SELECT COUNT(*) FROM BillLine WHERE ActivityId = @id", new { id });
        if (n > 0) throw new BillingException($"{n} bill line(s) use this activity. It cannot be deleted.");
        c.Execute("DELETE FROM Activity WHERE Id = @id", new { id });
    }

    // ───────────────────────── Rates ─────────────────────────

    public List<Rate> Rates()
    {
        using var c = Open();
        return c.Query<Rate>("""
            SELECT r.*, COALESCE(w.WoNo, '(all work orders)') AS WorkOrderNo, a.Name AS ActivityName
            FROM Rate r JOIN Activity a ON a.Id = r.ActivityId LEFT JOIN WorkOrder w ON w.Id = r.WorkOrderId
            ORDER BY r.WorkOrderId, a.SortOrder
            """).ToList();
    }

    public void SaveRate(Rate r)
    {
        if (r.ActivityId == 0) throw new BillingException("Choose the activity.");
        if (r.Amount < 0) throw new BillingException("The rate cannot be negative.");
        using var c = Open();
        if (r.Id == 0)
            c.Execute("""
                INSERT INTO Rate(WorkOrderId, ActivityId, Unit, Amount) VALUES (@WorkOrderId, @ActivityId, @Unit, @Amount)
                ON CONFLICT(WorkOrderId, ActivityId) DO UPDATE SET Unit = excluded.Unit, Amount = excluded.Amount
                """, r);
        else
            c.Execute("UPDATE Rate SET WorkOrderId=@WorkOrderId, ActivityId=@ActivityId, Unit=@Unit, Amount=@Amount WHERE Id=@Id", r);
    }

    public void DeleteRate(long id)
    {
        using var c = Open();
        c.Execute("DELETE FROM Rate WHERE Id = @id", new { id });
    }

    /// <summary>The work order's own rate for the activity, else the default rate, else null.</summary>
    public Rate? FindRate(long workOrderId, long activityId)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<Rate>("""
            SELECT * FROM Rate WHERE ActivityId = @activityId AND WorkOrderId IN (@workOrderId, 0)
            ORDER BY WorkOrderId DESC LIMIT 1
            """, new { workOrderId, activityId });
    }

    // ───────────────────────── BOM ─────────────────────────

    public List<BomItem> BomItems()
    {
        using var c = Open();
        return c.Query<BomItem>("SELECT * FROM BomItem ORDER BY Id").ToList();
    }

    public BomItem? GetBomItem(long id)
    {
        using var c = Open();
        return c.QueryFirstOrDefault<BomItem>("SELECT * FROM BomItem WHERE Id = @id", new { id });
    }

    public int BomCount()
    {
        using var c = Open();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM BomItem");
    }

    public List<string> BomSystems()
    {
        using var c = Open();
        return c.Query<string>("SELECT System FROM BomItem GROUP BY System ORDER BY MIN(Id)").ToList();
    }

    /// <summary>Adds new BOM rows and updates the tonnes of rows that already exist (same system, drawing, sheet, rev and item).</summary>
    public (int Added, int Updated) UpsertBom(IEnumerable<BomItem> items)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        int added = 0, updated = 0;
        foreach (var b in items)
        {
            b.System = b.System.Trim(); b.DrawingNo = b.DrawingNo.Trim();
            b.Sheet = b.Sheet.Trim(); b.Rev = b.Rev.Trim(); b.Item = b.Item.Trim();
            if (b.System.Length == 0 || b.DrawingNo.Length == 0) continue;
            var found = c.QueryFirstOrDefault<BomItem>("""
                SELECT Id, Extra FROM BomItem WHERE System=@System AND DrawingNo=@DrawingNo AND Sheet=@Sheet AND Rev=@Rev AND Item=@Item
                """, b, tx);
            var id = found?.Id;
            if (id == null)
            {
                b.Id = c.ExecuteScalar<long>("""
                    INSERT INTO BomItem(System, DrawingNo, Sheet, Rev, Item, Tonnes, Extra)
                    VALUES (@System, @DrawingNo, @Sheet, @Rev, @Item, @Tonnes, @Extra) RETURNING Id
                    """, b, tx);
                added++;
            }
            else
            {
                b.Id = id.Value;
                b.Extra = Extras.Merge(found!.Extra, Extras.Parse(b.Extra));
                updated += c.Execute("UPDATE BomItem SET Tonnes=@Tonnes, Extra=@Extra WHERE Id=@Id AND (Tonnes<>@Tonnes OR Extra<>@Extra)", b, tx);
            }
        }
        tx.Commit();
        return (added, updated);
    }

    /// <summary>Deletes BOM rows that no bill line uses. Returns how many were deleted.</summary>
    public int DeleteUnusedBom()
    {
        using var c = Open();
        return c.Execute("DELETE FROM BomItem WHERE Id NOT IN (SELECT BomItemId FROM BillLine WHERE BomItemId IS NOT NULL)");
    }

    // ───────────────────────── User-added fields ─────────────────────────

    static string TableOf(string entity) => Entities.All.Contains(entity) ? entity : throw new BillingException($"Unknown record type {entity}.");

    public List<CustomField> CustomFields(string? entity = null)
    {
        using var c = Open();
        return c.Query<CustomField>("SELECT * FROM CustomField WHERE @entity IS NULL OR Entity = @entity ORDER BY Entity, SortOrder, Id", new { entity }).ToList();
    }

    static string CheckFieldName(string name)
    {
        name = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) throw new BillingException("Enter a name for the field.");
        if (name.Length > 60) throw new BillingException("Keep field names to 60 characters.");
        if (name.IndexOfAny(new[] { '"', '\\', '\'' }) >= 0) throw new BillingException("Field names cannot contain quotes or backslashes.");
        return name;
    }

    /// <summary>Adds a field (or returns the existing one with the same name).</summary>
    public CustomField AddCustomField(string entity, string name)
    {
        TableOf(entity);
        name = CheckFieldName(name);
        using var c = Open();
        var existing = c.QueryFirstOrDefault<CustomField>("SELECT * FROM CustomField WHERE Entity=@entity AND Name=@name COLLATE NOCASE", new { entity, name });
        if (existing != null) return existing;
        var sort = c.ExecuteScalar<int>("SELECT COALESCE(MAX(SortOrder), 0) + 10 FROM CustomField WHERE Entity=@entity", new { entity });
        var id = c.ExecuteScalar<long>("INSERT INTO CustomField(Entity, Name, SortOrder) VALUES (@entity, @name, @sort) RETURNING Id", new { entity, name, sort });
        return new CustomField { Id = id, Entity = entity, Name = name, SortOrder = sort };
    }

    public void RenameCustomField(long id, string newName)
    {
        newName = CheckFieldName(newName);
        using var c = Open();
        using var tx = c.BeginTransaction();
        var f = c.QueryFirstOrDefault<CustomField>("SELECT * FROM CustomField WHERE Id=@id", new { id }, tx) ?? throw new BillingException("The field no longer exists.");
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM CustomField WHERE Entity=@Entity AND Name=@newName COLLATE NOCASE AND Id<>@Id", new { f.Entity, newName, f.Id }, tx) > 0)
            throw new BillingException($"There is already a field named {newName}.");
        var table = TableOf(f.Entity);
        foreach (var (rid, extra) in c.Query<(long, string)>($"SELECT Id, Extra FROM {table}", transaction: tx).ToList())
        {
            var d = Extras.Parse(extra);
            if (!d.Remove(f.Name, out var v)) continue;
            d[newName] = v;
            c.Execute($"UPDATE {table} SET Extra=@x WHERE Id=@rid", new { x = Extras.Write(d), rid }, tx);
        }
        c.Execute("UPDATE CustomField SET Name=@newName WHERE Id=@id", new { newName, id }, tx);
        tx.Commit();
    }

    /// <summary>Deletes a field and every value stored in it.</summary>
    public void DeleteCustomField(long id)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var f = c.QueryFirstOrDefault<CustomField>("SELECT * FROM CustomField WHERE Id=@id", new { id }, tx);
        if (f == null) return;
        var table = TableOf(f.Entity);
        foreach (var (rid, extra) in c.Query<(long, string)>($"SELECT Id, Extra FROM {table}", transaction: tx).ToList())
        {
            var d = Extras.Parse(extra);
            if (d.Remove(f.Name)) c.Execute($"UPDATE {table} SET Extra=@x WHERE Id=@rid", new { x = Extras.Write(d), rid }, tx);
        }
        c.Execute("DELETE FROM CustomField WHERE Id=@id", new { id }, tx);
        tx.Commit();
    }

    // ───────────────────────── Bills ─────────────────────────

    public Bill NewBill(long workOrderId)
    {
        return new Bill
        {
            WorkOrderId = workOrderId,
            BillNo = workOrderId == 0 ? "RA-1" : NextBillNo(workOrderId),
            BillDate = DateTime.Today,
            GstPct = GetSettingDecimal("DefaultGstPct", 18),
            RetentionPct = GetSettingDecimal("DefaultRetentionPct", 0),
            TdsPct = GetSettingDecimal("DefaultTdsPct", 0),
        };
    }

    /// <summary>RA-1, RA-2 … the next free running-account number for the work order.</summary>
    public string NextBillNo(long workOrderId)
    {
        using var c = Open();
        var nos = c.Query<string>("SELECT BillNo FROM Bill WHERE WorkOrderId = @workOrderId", new { workOrderId });
        var max = 0;
        foreach (var no in nos)
        {
            var t = no.Trim().ToUpperInvariant();
            if (t.StartsWith("RA-") && int.TryParse(t[3..], out var n) && n > max) max = n;
        }
        return "RA-" + (max + 1).ToString(CultureInfo.InvariantCulture);
    }

    public (Bill Bill, List<BillLine> Lines)? GetBill(long id)
    {
        using var c = Open();
        var b = c.QueryFirstOrDefault<Bill>("SELECT * FROM Bill WHERE Id = @id", new { id });
        if (b == null) return null;
        var lines = c.Query<BillLine>("SELECT * FROM BillLine WHERE BillId = @id ORDER BY LineNo", new { id }).ToList();
        return (b, lines);
    }

    public long SaveBill(Bill b, IEnumerable<BillLine> lines)
    {
        var list = lines.ToList();
        b.BillNo = b.BillNo.Trim();
        if (b.WorkOrderId == 0) throw new BillingException("Choose the work order.");
        if (b.BillNo.Length == 0) throw new BillingException("Enter the bill number.");
        if (!BillStatus.All.Contains(b.Status)) throw new BillingException($"Unknown status {b.Status}.");
        if (b.PeriodFrom.HasValue && b.PeriodTo.HasValue && b.PeriodFrom > b.PeriodTo)
            throw new BillingException("'Period from' is after 'Period to'.");
        foreach (var p in new[] { b.GstPct, b.RetentionPct, b.TdsPct })
            if (p < 0 || p > 100) throw new BillingException("Percentages must be between 0 and 100.");
        if (b.OtherDeduction < 0 || b.PaidAmount < 0) throw new BillingException("Amounts cannot be negative.");
        for (var i = 0; i < list.Count; i++)
        {
            var l = list[i];
            if (!l.IsBom && string.IsNullOrWhiteSpace(l.Description))
                throw new BillingException($"Line {i + 1}: enter a description.");
            if (l.Qty < 0 || l.Rate < 0) throw new BillingException($"Line {i + 1}: quantity and rate cannot be negative.");
        }

        using var c = Open();
        using var tx = c.BeginTransaction();
        if (c.ExecuteScalar<long>("SELECT COUNT(*) FROM Bill WHERE WorkOrderId=@WorkOrderId AND BillNo=@BillNo COLLATE NOCASE AND Id<>@Id", b, tx) > 0)
            throw new BillingException($"Bill {b.BillNo} already exists for this work order.");
        b.UpdatedAt = DateTime.Now.AddTicks(-(DateTime.Now.Ticks % TimeSpan.TicksPerSecond));
        if (b.Id == 0)
        {
            b.CreatedAt = b.UpdatedAt;
            b.Id = c.ExecuteScalar<long>("""
                INSERT INTO Bill(WorkOrderId, BillNo, BillType, BillDate, PeriodFrom, PeriodTo, Status, GstPct, RetentionPct,
                  TdsPct, OtherDeduction, PaidAmount, PaidOn, Remarks, CreatedAt, UpdatedAt)
                VALUES (@WorkOrderId, @BillNo, @BillType, @BillDate, @PeriodFrom, @PeriodTo, @Status, @GstPct, @RetentionPct,
                  @TdsPct, @OtherDeduction, @PaidAmount, @PaidOn, @Remarks, @CreatedAt, @UpdatedAt) RETURNING Id
                """, b, tx);
        }
        else
        {
            c.Execute("""
                UPDATE Bill SET WorkOrderId=@WorkOrderId, BillNo=@BillNo, BillType=@BillType, BillDate=@BillDate,
                  PeriodFrom=@PeriodFrom, PeriodTo=@PeriodTo, Status=@Status, GstPct=@GstPct, RetentionPct=@RetentionPct,
                  TdsPct=@TdsPct, OtherDeduction=@OtherDeduction, PaidAmount=@PaidAmount, PaidOn=@PaidOn,
                  Remarks=@Remarks, UpdatedAt=@UpdatedAt WHERE Id=@Id
                """, b, tx);
            c.Execute("DELETE FROM BillLine WHERE BillId = @Id", b, tx);
        }
        var no = 0;
        foreach (var l in list)
        {
            l.BillId = b.Id;
            l.LineNo = ++no;
            l.Id = c.ExecuteScalar<long>("""
                INSERT INTO BillLine(BillId, LineNo, BomItemId, Description, Remarks, ActivityId, Unit, Qty, Rate, Extra)
                VALUES (@BillId, @LineNo, @BomItemId, @Description, @Remarks, @ActivityId, @Unit, @Qty, @Rate, @Extra) RETURNING Id
                """, l, tx);
        }
        tx.Commit();
        return b.Id;
    }

    public void DeleteBill(long id)
    {
        using var c = Open();
        c.Execute("DELETE FROM Bill WHERE Id = @id", new { id });
    }

    /// <summary>Copies a bill as a new Draft with the next bill number, unpaid.</summary>
    public long DuplicateBill(long id)
    {
        var src = GetBill(id) ?? throw new BillingException("The bill no longer exists.");
        var b = src.Bill;
        b.Id = 0;
        b.BillNo = NextBillNo(b.WorkOrderId);
        b.Status = BillStatus.Draft;
        b.BillDate = DateTime.Today;
        b.PaidAmount = 0;
        b.PaidOn = null;
        var lines = src.Lines.Select(l => { var n = l.Clone(); n.Id = 0; return n; });
        return SaveBill(b, lines);
    }

    sealed class LineRow
    {
        public long Id { get; set; }
        public long BillId { get; set; }
        public long? BomItemId { get; set; }
        public long? ActivityId { get; set; }
        public decimal Qty { get; set; }
        public decimal Rate { get; set; }
    }

    /// <summary>
    /// Bill lines that take the cumulative billing of a BOM item (per activity) above its BOM tonnes,
    /// walking the bills in date order. Cancelled bills are left out.
    /// </summary>
    public HashSet<long> OverScopeLineIds()
    {
        using var c = Open();
        var rows = c.Query<(long LineId, long BomItemId, long ActivityId, decimal Qty, decimal Tonnes)>("""
            SELECT l.Id, l.BomItemId, COALESCE(l.ActivityId, 0), l.Qty, i.Tonnes
            FROM BillLine l JOIN Bill b ON b.Id = l.BillId JOIN BomItem i ON i.Id = l.BomItemId
            WHERE b.Status <> 'Cancelled'
            ORDER BY b.BillDate, b.Id, l.LineNo
            """);
        var running = new Dictionary<(long, long), decimal>();
        var over = new HashSet<long>();
        foreach (var r in rows)
        {
            var key = (r.BomItemId, r.ActivityId);
            var total = running.GetValueOrDefault(key) + r.Qty;
            running[key] = total;
            if (total > r.Tonnes + 0.0005m) over.Add(r.LineId);
        }
        return over;
    }

    /// <summary>
    /// For each (BOM item, activity) pair: the BOM tonnes and the quantity billed in all other
    /// (not cancelled) bills. <paramref name="billId"/> is the bill being edited (0 for a new one).
    /// </summary>
    public Dictionary<(long BomItemId, long ActivityId), (decimal Scope, decimal Earlier)> ScopeFor(long billId, IEnumerable<(long BomItemId, long ActivityId)> keys)
    {
        var result = new Dictionary<(long, long), (decimal, decimal)>();
        using var c = Open();
        foreach (var k in keys.Distinct())
        {
            var scope = c.ExecuteScalar<decimal?>("SELECT Tonnes FROM BomItem WHERE Id = @BomItemId", new { k.BomItemId }) ?? 0m;
            var earlier = c.ExecuteScalar<decimal?>("""
                SELECT SUM(l.Qty) FROM BillLine l JOIN Bill b ON b.Id = l.BillId
                WHERE l.BomItemId = @BomItemId AND COALESCE(l.ActivityId, 0) = @ActivityId AND b.Id <> @billId AND b.Status <> 'Cancelled'
                """, new { k.BomItemId, k.ActivityId, billId }) ?? 0m;
            result[k] = (scope, earlier);
        }
        return result;
    }

    public List<BillSummary> BillSummaries(BillFilter? f = null)
    {
        f ??= new BillFilter();
        using var c = Open();
        var bills = c.Query<Bill>("SELECT * FROM Bill").ToDictionary(b => b.Id);
        var lines = c.Query<LineRow>("SELECT Id, BillId, BomItemId, ActivityId, Qty, Rate FROM BillLine").ToList();
        var wos = c.Query<WorkOrder>("SELECT w.*, k.Name AS ContractorName FROM WorkOrder w JOIN Contractor k ON k.Id = w.ContractorId").ToDictionary(w => w.Id);
        var over = OverScopeLineIds();
        var byBill = lines.GroupBy(l => l.BillId).ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<BillSummary>();
        foreach (var b in bills.Values)
        {
            var bl = byBill.GetValueOrDefault(b.Id) ?? new List<LineRow>();
            var t = BillMath.Totals(b, bl.Select(l => new BillLine { Qty = l.Qty, Rate = l.Rate }));
            var wo = wos[b.WorkOrderId];
            result.Add(new BillSummary
            {
                Id = b.Id, BillNo = b.BillNo, BillDate = b.BillDate, BillType = b.BillType, Status = b.Status,
                ContractorId = wo.ContractorId, Contractor = wo.ContractorName, WorkOrderId = wo.Id, WorkOrder = wo.WoNo,
                Lines = bl.Count, Gross = t.Gross, Net = t.Net, Paid = t.Paid, Due = t.Due, Remarks = b.Remarks,
                OverScopeLines = bl.Count(l => over.Contains(l.Id)),
            });
        }

        IEnumerable<BillSummary> q = result;
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var s = f.Search.Trim();
            q = q.Where(r => Has(r.BillNo, s) || Has(r.Contractor, s) || Has(r.WorkOrder, s) || Has(r.Remarks, s) || Has(r.Status, s));
        }
        if (!string.IsNullOrEmpty(f.Status)) q = q.Where(r => r.Status == f.Status);
        if (f.ContractorId.HasValue) q = q.Where(r => r.ContractorId == f.ContractorId);
        if (f.WorkOrderId.HasValue) q = q.Where(r => r.WorkOrderId == f.WorkOrderId);
        if (f.From.HasValue) q = q.Where(r => r.BillDate >= f.From.Value.Date);
        if (f.To.HasValue) q = q.Where(r => r.BillDate <= f.To.Value.Date);
        if (f.OnlyOverScope) q = q.Where(r => r.OverScopeLines > 0);
        return q.OrderByDescending(r => r.BillDate).ThenByDescending(r => r.Id).ToList();
    }

    static bool Has(string? s, string part) => s != null && s.Contains(part, StringComparison.OrdinalIgnoreCase);

    // ───────────────────────── Dashboard and progress ─────────────────────────

    public Dashboard GetDashboard()
    {
        var bills = BillSummaries().Where(b => b.Status != BillStatus.Cancelled).ToList();
        var wos = WorkOrders();
        var d = new Dashboard
        {
            BillCount = bills.Count,
            Gross = bills.Sum(b => b.Gross),
            Net = bills.Sum(b => b.Net),
            Paid = bills.Sum(b => b.Paid),
            Outstanding = bills.Sum(b => b.Due),
            OverScopeLines = bills.Sum(b => b.OverScopeLines),
            DraftBills = bills.Count(b => b.Status == BillStatus.Draft),
        };

        using (var c = Open())
        {
            d.BomDrawings = c.ExecuteScalar<int>("SELECT COUNT(*) FROM (SELECT 1 FROM BomItem GROUP BY System, DrawingNo, Sheet, Rev)");
            d.BomTonnes = c.ExecuteScalar<decimal?>("SELECT SUM(Tonnes) FROM BomItem") ?? 0m;
            var billed = c.Query<(long ActivityId, decimal Qty)>("""
                SELECT l.ActivityId, SUM(l.Qty) FROM BillLine l JOIN Bill b ON b.Id = l.BillId
                WHERE l.BomItemId IS NOT NULL AND b.Status <> 'Cancelled' GROUP BY l.ActivityId
                """).Where(r => r.ActivityId != 0).ToDictionary(r => r.ActivityId, r => r.Qty);
            foreach (var a in c.Query<Activity>("SELECT * FROM Activity WHERE IsBom = 1 ORDER BY SortOrder, Name"))
            {
                var t = billed.GetValueOrDefault(a.Id);
                d.Progress.Add(new ActivityProgress
                {
                    ActivityId = a.Id, Activity = a.Name, Tonnes = t,
                    Percent = d.BomTonnes > 0 ? t * 100m / d.BomTonnes : 0m,
                });
            }
        }

        foreach (var g in wos.GroupBy(w => w.ContractorName))
        {
            var bs = bills.Where(b => b.Contractor == g.Key).ToList();
            if (g.Sum(w => w.Value) == 0 && bs.Count == 0) continue;
            d.ByContractor.Add(new ContractorSummary
            {
                Contractor = g.Key, WoValue = g.Sum(w => w.Value), Bills = bs.Count,
                Gross = bs.Sum(b => b.Gross), Net = bs.Sum(b => b.Net), Paid = bs.Sum(b => b.Paid), Outstanding = bs.Sum(b => b.Due),
            });
        }
        d.ByContractor = d.ByContractor.OrderByDescending(x => x.WoValue).ToList();

        foreach (var w in wos)
            d.WorkOrders.Add(new WorkOrderUse
            {
                WorkOrderId = w.Id, WoNo = w.WoNo, Contractor = w.ContractorName, Service = w.Service, Value = w.Value,
                Gross = bills.Where(b => b.WorkOrderId == w.Id).Sum(b => b.Gross),
            });
        return d;
    }

    public List<BomProgressRow> BomProgress(BomLevel level, string? system = null, string? search = null, bool onlyBilled = false)
    {
        using var c = Open();
        var items = c.Query<BomItem>("SELECT * FROM BomItem ORDER BY Id").ToList();
        var billed = c.Query<(long BomItemId, long ActivityId, decimal Qty)>("""
            SELECT l.BomItemId, COALESCE(l.ActivityId, 0), SUM(l.Qty) FROM BillLine l JOIN Bill b ON b.Id = l.BillId
            WHERE l.BomItemId IS NOT NULL AND b.Status <> 'Cancelled' GROUP BY l.BomItemId, l.ActivityId
            """).ToList();
        var byItem = billed.GroupBy(r => r.BomItemId).ToDictionary(g => g.Key, g => g.ToList());

        if (!string.IsNullOrEmpty(system)) items = items.Where(i => i.System == system).ToList();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            items = items.Where(i => Has(i.System, s) || Has(i.DrawingLabel, s) || Has(i.Item, s)).ToList();
        }

        string Key(BomItem i) => level switch
        {
            BomLevel.Systems => i.System,
            BomLevel.Drawings => i.System + "\u0001" + i.DrawingNo + "\u0001" + i.Sheet + "\u0001" + i.Rev,
            _ => i.Id.ToString(CultureInfo.InvariantCulture),
        };

        var rows = new List<BomProgressRow>();
        foreach (var g in items.GroupBy(Key))
        {
            var first = g.First();
            var row = new BomProgressRow
            {
                System = first.System,
                Drawing = level == BomLevel.Systems ? $"{g.Select(i => (i.DrawingNo, i.Sheet, i.Rev)).Distinct().Count()} drawing(s)" : first.DrawingLabel,
                Item = level == BomLevel.Items ? first.Item : "",
                BomTonnes = g.Sum(i => i.Tonnes),
            };
            // Over is judged per BOM row and activity, so one over-billed item flags its drawing and system.
            foreach (var i in g)
            {
                row.BomItemIds.Add(i.Id);
                foreach (var b in byItem.GetValueOrDefault(i.Id) ?? new())
                {
                    row.Billed[b.ActivityId] = row.Billed.GetValueOrDefault(b.ActivityId) + b.Qty;
                    if (b.Qty > i.Tonnes + 0.0005m) row.Over = true;
                }
            }
            if (onlyBilled && row.Billed.Values.All(v => v == 0)) continue;
            rows.Add(row);
        }
        return rows;
    }

    public List<BillLineDetail> LineDetails(IEnumerable<long>? bomItemIds = null, DateTime? from = null, DateTime? to = null)
    {
        using var c = Open();
        var sql = """
            SELECT b.Id AS BillId, b.BillNo, b.BillDate, b.Status, k.Name AS Contractor, w.WoNo AS WorkOrder, l.LineNo,
              CASE WHEN l.BomItemId IS NULL THEN l.Description
                   ELSE i.System || ' › ' || i.DrawingNo
                        || CASE WHEN i.Sheet <> '' THEN ' · Sht ' || i.Sheet ELSE '' END
                        || CASE WHEN i.Rev <> '' THEN ' · Rev ' || i.Rev ELSE '' END
                        || CASE WHEN i.Item <> '' THEN ' › ' || i.Item ELSE '' END END AS Item,
              COALESCE(a.Name, '') AS Activity, l.Unit, l.Qty, l.Rate, ROUND(l.Qty * l.Rate, 2) AS Amount, l.Remarks
            FROM BillLine l JOIN Bill b ON b.Id = l.BillId JOIN WorkOrder w ON w.Id = b.WorkOrderId
            JOIN Contractor k ON k.Id = w.ContractorId LEFT JOIN BomItem i ON i.Id = l.BomItemId
            LEFT JOIN Activity a ON a.Id = l.ActivityId
            WHERE 1 = 1
            """;
        var p = new DynamicParameters();
        if (bomItemIds != null)
        {
            sql += " AND l.BomItemId IN @ids";
            p.Add("ids", bomItemIds.ToArray());
        }
        if (from.HasValue) { sql += " AND b.BillDate >= @from"; p.Add("from", from.Value.Date); }
        if (to.HasValue) { sql += " AND b.BillDate <= @to"; p.Add("to", to.Value.Date); }
        sql += " ORDER BY b.BillDate, b.Id, l.LineNo";
        return c.Query<BillLineDetail>(sql, p).ToList();
    }

    public (int Contractors, int WorkOrders, int BomRows, int Bills, int Lines) Counts()
    {
        using var c = Open();
        return (c.ExecuteScalar<int>("SELECT COUNT(*) FROM Contractor"), c.ExecuteScalar<int>("SELECT COUNT(*) FROM WorkOrder"),
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM BomItem"), c.ExecuteScalar<int>("SELECT COUNT(*) FROM Bill"),
                c.ExecuteScalar<int>("SELECT COUNT(*) FROM BillLine"));
    }
}
