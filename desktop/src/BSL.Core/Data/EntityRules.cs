using System.Data;

namespace BSL.Core.Data;

/// <summary>
/// Smart defaults while editing: activity → service type / category / rates, work order → activity,
/// next work-order number. Rules only fill blank fields, so typed values are never overwritten.
/// </summary>
public sealed class EntityRules(Database db)
{
    bool _busy;

    /// <summary>Default values for a brand-new record.</summary>
    public void OnNew(string entity, DataRow row)
    {
        if (entity == "WorkOrder" && row.Table.Columns.Contains("UniqueNo") && row["UniqueNo"] is DBNull)
            row["UniqueNo"] = NextWorkOrderNo();
    }

    /// <summary>Next "BMnn" number after the highest existing one.</summary>
    public string NextWorkOrderNo()
    {
        var max = db.Scalar("SELECT MAX(TRY_CAST(SUBSTRING(UniqueNo, 3, 10) AS INT)) FROM dbo.WorkOrder WHERE UniqueNo LIKE N'BM%'");
        var n = max is null ? 1 : Convert.ToInt32(max) + 1;
        return "BM" + n.ToString("00");
    }

    /// <summary>Called after a column value changed in the grid or form.</summary>
    public void OnChanged(string entity, DataRow row, string column)
    {
        if (_busy || row.RowState == DataRowState.Deleted) return;
        _busy = true;
        try
        {
            switch (entity, column)
            {
                case ("WorkOrder", "ActivityId"):
                    if (Activity(row["ActivityId"]) is { } a)
                    {
                        SetIfBlank(row, "ServiceType", a["ServiceType"]);
                        SetIfBlank(row, "WorkCategory", a["WorkCategory"]);
                    }
                    break;

                case ("ContractItem", "WorkOrderId"):
                    if (row["ActivityId"] is DBNull && row["WorkOrderId"] is int wo)
                        SetIfBlank(row, "ActivityId", db.Scalar("SELECT ActivityId FROM dbo.WorkOrder WHERE Id = @w", ("@w", wo)));
                    if (Activity(row["ActivityId"]) is { } woa) SetIfBlank(row, "ActivityType", woa["ServiceType"]);
                    FillItemRates(row);
                    break;

                case ("ContractItem", "ActivityId"):
                    if (Activity(row["ActivityId"]) is { } ia) SetIfBlank(row, "ActivityType", ia["ServiceType"]);
                    FillItemRates(row);
                    break;

                case ("RateMaster", "ActivityId"):
                    if (Activity(row["ActivityId"]) is { } ra)
                    {
                        SetIfBlank(row, "ActivityType", ra["ServiceType"]);
                        SetIfBlank(row, "ItemCode", ra["ActivityCode"]);
                        SetIfBlank(row, "Unit", ra["Unit"]);
                        SetIfBlank(row, "InternalRate", ra["DefaultInternalRate"]);
                        SetIfBlank(row, "ExternalRate", ra["DefaultExternalRate"]);
                        var wo2 = row["WorkOrderId"] is int w2 ? db.Scalar("SELECT UniqueNo FROM dbo.WorkOrder WHERE Id = @w", ("@w", w2)) : null;
                        SetIfBlank(row, "Description", ra["Name"] + (wo2 != null ? " – " + wo2 : ""));
                    }
                    break;

                case ("ContractorActivity", "ActivityId"):
                    if (Activity(row["ActivityId"]) is { } ca) SetIfBlank(row, "InternalRate", ca["DefaultInternalRate"]);
                    break;
            }
        }
        finally { _busy = false; }
    }

    void FillItemRates(DataRow row)
    {
        if (row["ActivityId"] is not int act) return;
        if (row["InternalRate"] is not DBNull && row["ExternalRate"] is not DBNull) return;
        var t = db.Query("""
            SELECT COALESCE(rm.InternalRate, a.DefaultInternalRate) AS IntRate, COALESCE(rm.ExternalRate, a.DefaultExternalRate) AS ExtRate
            FROM dbo.Activity a
            OUTER APPLY (SELECT TOP 1 r.InternalRate, r.ExternalRate FROM dbo.RateMaster r
                         WHERE r.ActivityId = a.Id AND r.WorkOrderId = @w ORDER BY r.EffectiveFrom DESC, r.Id DESC) rm
            WHERE a.Id = @a
            """, ("@a", act), ("@w", row["WorkOrderId"] is int w ? w : null));
        if (t.Rows.Count == 0) return;
        SetIfBlank(row, "InternalRate", t.Rows[0]["IntRate"]);
        SetIfBlank(row, "ExternalRate", t.Rows[0]["ExtRate"]);
    }

    readonly Dictionary<int, DataRow> _activities = [];

    DataRow? Activity(object id)
    {
        if (id is not int i) return null;
        if (_activities.TryGetValue(i, out var r)) return r;
        var t = db.Query("SELECT * FROM dbo.Activity WHERE Id = @i", ("@i", i));
        return t.Rows.Count == 0 ? null : _activities[i] = t.Rows[0];
    }

    static void SetIfBlank(DataRow row, string col, object? value)
    {
        if (!row.Table.Columns.Contains(col) || value is null or DBNull) return;
        var c = row.Table.Columns[col]!;
        if (c.ReadOnly || c.Expression.Length > 0) return;
        if (row[col] is DBNull || (row[col] is string s && s.Length == 0))
            row[col] = c.DataType == typeof(string) ? Convert.ToString(value) ?? "" : Convert.ChangeType(value, c.DataType);
    }
}
