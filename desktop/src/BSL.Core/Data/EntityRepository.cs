using System.Data;
using System.Globalization;
using BSL.Core.Formula;
using BSL.Core.Metadata;
using Microsoft.Data.SqlClient;

namespace BSL.Core.Data;

/// <summary>
/// Generic CRUD for every entity in <see cref="EntityCatalog"/>: loads the read view plus user defined
/// fields into one DataTable (shared by the grid view and the form view) and saves the changes back.
/// </summary>
public sealed class EntityRepository(Database db)
{
    readonly UdfRepository _udf = new(db);

    public Database Db => db;

    public List<UdfDefinition> Udfs(EntityDef e) => _udf.GetDefinitions(e.Name);

    /// <summary>Fields of the entity including the active user defined fields.</summary>
    public List<FieldDef> AllFields(EntityDef e, List<UdfDefinition>? udfs = null) =>
        e.Fields.Concat((udfs ?? Udfs(e)).Select(u => u.ToFieldDef())).ToList();

    public DataTable Load(EntityDef e, string? whereSql = null, params (string, object?)[] ps)
    {
        var sql = $"SELECT * FROM dbo.{e.View}" + (string.IsNullOrWhiteSpace(whereSql) ? "" : " WHERE " + whereSql) + $" ORDER BY {e.OrderBy}";
        var dt = db.Query(sql, ps);
        dt.TableName = e.Name;
        foreach (var f in e.Fields)
            if (dt.Columns[f.Name] is { } c) c.Caption = f.Caption;
        if (dt.Columns[e.Key] is { } key)
        {
            // new rows get temporary negative ids until saved
            key.AutoIncrement = true; key.AutoIncrementSeed = -1; key.AutoIncrementStep = -1;
            key.ReadOnly = true;
            dt.PrimaryKey = [key];
        }
        AttachUdfs(e, dt);
        dt.AcceptChanges();
        return dt;
    }

    /// <summary>Adds UDF value columns and formula columns to a loaded table.</summary>
    public void AttachUdfs(EntityDef e, DataTable dt)
    {
        var defs = Udfs(e);
        if (defs.Count == 0) return;
        var values = _udf.GetValues(e.Name);
        foreach (var d in defs.Where(d => d.DataType != UdfType.Formula))
        {
            if (dt.Columns.Contains(d.FieldKey)) continue;
            var col = dt.Columns.Add(d.FieldKey, d.ClrType);
            col.Caption = d.Caption;
            col.ExtendedProperties["udf"] = d.Id;
            foreach (DataRow r in dt.Rows)
                if (values.TryGetValue((d.Id, (int)r[e.Key]), out var text) && text != null)
                    r[col] = Parse(text, d.ClrType) ?? DBNull.Value;
        }
        // formulas last: they may use other UDFs and the joined view columns
        foreach (var d in defs.Where(d => d.DataType == UdfType.Formula && !string.IsNullOrWhiteSpace(d.Formula)))
            FormulaEngine.AddFormulaColumn(dt, d.FieldKey, d.Caption, d.Formula!);
    }

    static object? Parse(string text, Type t)
    {
        if (t == typeof(string)) return text;
        if (t == typeof(decimal)) return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (t == typeof(DateTime)) return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        if (t == typeof(bool)) return text is "1" or "True" or "true";
        return text;
    }

    static string? Format(object? v) => v switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString()
    };

    /// <summary>Validates required fields. Returns messages per row index.</summary>
    public List<string> Validate(EntityDef e, DataTable dt)
    {
        var errors = new List<string>();
        var req = AllFields(e).Where(f => f.Required && !f.ReadOnly).ToList();
        int i = 0;
        foreach (DataRow r in dt.Rows)
        {
            i++;
            if (r.RowState is DataRowState.Deleted or DataRowState.Unchanged or DataRowState.Detached) continue;
            foreach (var f in req)
                if (dt.Columns.Contains(f.Name) && (r[f.Name] is DBNull || string.IsNullOrWhiteSpace(Convert.ToString(r[f.Name]))))
                    errors.Add($"Row {i}: '{f.Caption}' is required.");
        }
        return errors;
    }

    /// <summary>Saves inserted, updated and deleted rows (base columns + UDF values) in one transaction.</summary>
    public (int Inserted, int Updated, int Deleted) Save(EntityDef e, DataTable dt)
    {
        var errs = Validate(e, dt);
        if (errs.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errs.Take(20)));

        var cols = e.WritableFields.Where(f => dt.Columns.Contains(f.Name)).ToList();
        var udfCols = dt.Columns.Cast<DataColumn>().Where(c => c.ExtendedProperties.ContainsKey("udf")).ToList();
        int ins = 0, upd = 0, del = 0;

        using var cn = db.Open();
        using var tx = cn.BeginTransaction();
        try
        {
            foreach (DataRow r in dt.Rows.Cast<DataRow>().ToList())
            {
                switch (r.RowState)
                {
                    case DataRowState.Deleted:
                        Database.Execute(cn, tx, $"DELETE dbo.{e.Table} WHERE {e.Key} = @id", ("@id", r[e.Key, DataRowVersion.Original]));
                        Database.Execute(cn, tx, "DELETE v FROM dbo.UdfValue v JOIN dbo.UdfDefinition d ON d.Id = v.UdfId WHERE d.EntityName = @e AND v.RecordId = @id",
                            ("@e", e.Name), ("@id", r[e.Key, DataRowVersion.Original]));
                        del++;
                        break;
                    case DataRowState.Added:
                    {
                        var used = cols.Where(f => r[f.Name] is not DBNull).ToList();
                        var sql = used.Count == 0
                            ? $"INSERT dbo.{e.Table} DEFAULT VALUES; SELECT CAST(SCOPE_IDENTITY() AS INT)"
                            : $"INSERT dbo.{e.Table} ({string.Join(",", used.Select(f => f.Name))}) OUTPUT INSERTED.{e.Key} " +
                              $"VALUES ({string.Join(",", used.Select(f => "@" + f.Name))})";
                        var id = Convert.ToInt32(Database.Scalar(cn, tx, sql, used.Select(f => ("@" + f.Name, Value(r[f.Name]))).ToArray()));
                        var keyCol = dt.Columns[e.Key]!;
                        var ro = keyCol.ReadOnly; keyCol.ReadOnly = false; r[keyCol] = id; keyCol.ReadOnly = ro;
                        SaveUdfs(cn, tx, e, r, id, udfCols);
                        ins++;
                        break;
                    }
                    case DataRowState.Modified:
                    {
                        var id = (int)r[e.Key];
                        var changed = cols.Where(f => !Equals(r[f.Name, DataRowVersion.Original], r[f.Name, DataRowVersion.Current])).ToList();
                        if (changed.Count > 0)
                        {
                            var set = string.Join(",", changed.Select(f => $"{f.Name}=@{f.Name}")) + (e.HasModifiedOn ? ",ModifiedOn=SYSDATETIME()" : "");
                            var ps = changed.Select(f => ("@" + f.Name, Value(r[f.Name]))).Append(("@__id", (object?)id)).ToArray();
                            Database.Execute(cn, tx, $"UPDATE dbo.{e.Table} SET {set} WHERE {e.Key}=@__id", ps);
                        }
                        SaveUdfs(cn, tx, e, r, id, udfCols, onlyChanged: true);
                        upd++;
                        break;
                    }
                }
            }
            Database.Execute(cn, tx, "INSERT dbo.AuditLog(EntityName, Action, Detail) VALUES(@e, N'SAVE', @d)",
                ("@e", e.Name), ("@d", $"inserted {ins}, updated {upd}, deleted {del}"));
            tx.Commit();
        }
        catch (SqlException ex)
        {
            tx.Rollback();
            throw new InvalidOperationException(FriendlySqlError(ex), ex);
        }
        catch { tx.Rollback(); throw; }
        dt.AcceptChanges();
        return (ins, upd, del);
    }

    static object? Value(object v) => v is string s ? (s.Trim().Length == 0 ? null : s.Trim()) : v;

    static void SaveUdfs(SqlConnection cn, SqlTransaction tx, EntityDef e, DataRow r, int id, List<DataColumn> udfCols, bool onlyChanged = false)
    {
        foreach (var c in udfCols)
        {
            if (onlyChanged && Equals(r[c, DataRowVersion.Original], r[c, DataRowVersion.Current])) continue;
            var udfId = (int)c.ExtendedProperties["udf"]!;
            var text = Format(r[c]);
            if (text == null)
                Database.Execute(cn, tx, "DELETE dbo.UdfValue WHERE UdfId=@u AND RecordId=@r", ("@u", udfId), ("@r", id));
            else
                Database.Execute(cn, tx,
                    "MERGE dbo.UdfValue t USING (SELECT @u AS UdfId, @r AS RecordId) s ON t.UdfId=s.UdfId AND t.RecordId=s.RecordId " +
                    "WHEN MATCHED THEN UPDATE SET ValueText=@v WHEN NOT MATCHED THEN INSERT(UdfId,RecordId,ValueText) VALUES(@u,@r,@v);",
                    ("@u", udfId), ("@r", id), ("@v", text));
        }
    }

    public static string FriendlySqlError(SqlException ex) => ex.Number switch
    {
        2627 or 2601 => "Duplicate value: a record with the same code / number already exists.\n" + ex.Message,
        547 => "This record is used by other records (or points to a missing record) and cannot be saved/deleted.\n" + ex.Message,
        515 => "A required value is missing.\n" + ex.Message,
        8152 or 2628 => "A value is too long for its field.\n" + ex.Message,
        _ => ex.Message
    };

    /// <summary>Id/Display pairs for a lookup column.</summary>
    public DataTable LookupData(LookupDef lk) => db.Query(lk.Sql);

    public List<string> Choices(string category)
    {
        if (category.StartsWith("udf:")) return category[4..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return db.Query("SELECT Value FROM dbo.LookupValue WHERE Category=@c ORDER BY SortOrder, Value", ("@c", category))
                 .Rows.Cast<DataRow>().Select(r => (string)r[0]).ToList();
    }
}
