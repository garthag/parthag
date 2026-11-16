using System.Data;
using BSL.Core.Metadata;

namespace BSL.Core.Data;

public enum UdfType { Text, Number, Date, YesNo, List, Formula }

public sealed class UdfDefinition
{
    public int Id { get; set; }
    public string EntityName { get; set; } = "";
    public string FieldKey { get; set; } = "";
    public string Caption { get; set; } = "";
    public UdfType DataType { get; set; } = UdfType.Text;
    public string? ListValues { get; set; }
    public string? Formula { get; set; }
    public int DecimalPlaces { get; set; } = 2;
    public bool IsRequired { get; set; }
    public bool ShowInGrid { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;

    public string[] Choices => (ListValues ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public Type ClrType => DataType switch
    {
        UdfType.Number => typeof(decimal),
        UdfType.Date => typeof(DateTime),
        UdfType.YesNo => typeof(bool),
        UdfType.Formula => typeof(object),
        _ => typeof(string)
    };

    public FieldDef ToFieldDef() => new()
    {
        Name = FieldKey,
        Caption = Caption,
        IsUdf = true,
        Formula = DataType == UdfType.Formula ? Formula : null,
        ReadOnly = DataType == UdfType.Formula,
        Required = IsRequired,
        HiddenInGrid = !ShowInGrid,
        Group = "User defined fields",
        ChoiceCategory = DataType == UdfType.List ? "udf:" + ListValues : null,
        Kind = DataType switch
        {
            UdfType.Number => FieldKind.Decimal,
            UdfType.Date => FieldKind.Date,
            UdfType.YesNo => FieldKind.Bool,
            UdfType.List => FieldKind.Choice,
            UdfType.Formula => FieldKind.Decimal,
            _ => FieldKind.Text
        },
        Width = 110
    };

    /// <summary>Valid field key: starts with U_, letters/digits/underscore only.</summary>
    public static string NormaliseKey(string caption)
    {
        var k = new string(caption.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        if (k.Length == 0) k = "Field";
        return k.StartsWith("U_", StringComparison.OrdinalIgnoreCase) ? k : "U_" + k;
    }
}

public sealed class UdfRepository(Database db)
{
    public List<UdfDefinition> GetDefinitions(string entity, bool activeOnly = true)
    {
        var dt = db.Query("SELECT * FROM dbo.UdfDefinition WHERE EntityName = @e " +
                          (activeOnly ? "AND IsActive = 1 " : "") + "ORDER BY SortOrder, Id", ("@e", entity));
        return dt.Rows.Cast<DataRow>().Select(r => new UdfDefinition
        {
            Id = (int)r["Id"], EntityName = (string)r["EntityName"], FieldKey = (string)r["FieldKey"],
            Caption = (string)r["Caption"],
            DataType = Enum.TryParse<UdfType>((string)r["DataType"], true, out var t) ? t : UdfType.Text,
            ListValues = r["ListValues"] as string, Formula = r["Formula"] as string,
            DecimalPlaces = (int)r["DecimalPlaces"], IsRequired = (bool)r["IsRequired"], ShowInGrid = (bool)r["ShowInGrid"],
            SortOrder = (int)r["SortOrder"], IsActive = (bool)r["IsActive"]
        }).ToList();
    }

    public void Save(UdfDefinition d)
    {
        var ps = new (string, object?)[]
        {
            ("@Id", d.Id), ("@EntityName", d.EntityName), ("@FieldKey", d.FieldKey), ("@Caption", d.Caption),
            ("@DataType", d.DataType.ToString()), ("@ListValues", d.ListValues), ("@Formula", d.Formula),
            ("@DecimalPlaces", d.DecimalPlaces), ("@IsRequired", d.IsRequired), ("@ShowInGrid", d.ShowInGrid),
            ("@SortOrder", d.SortOrder), ("@IsActive", d.IsActive)
        };
        if (d.Id == 0)
            d.Id = Convert.ToInt32(db.Scalar(
                "INSERT dbo.UdfDefinition(EntityName,FieldKey,Caption,DataType,ListValues,Formula,DecimalPlaces,IsRequired,ShowInGrid,SortOrder,IsActive) " +
                "OUTPUT INSERTED.Id VALUES(@EntityName,@FieldKey,@Caption,@DataType,@ListValues,@Formula,@DecimalPlaces,@IsRequired,@ShowInGrid,@SortOrder,@IsActive)", ps));
        else
            db.Execute("UPDATE dbo.UdfDefinition SET FieldKey=@FieldKey, Caption=@Caption, DataType=@DataType, ListValues=@ListValues, " +
                       "Formula=@Formula, DecimalPlaces=@DecimalPlaces, IsRequired=@IsRequired, ShowInGrid=@ShowInGrid, SortOrder=@SortOrder, " +
                       "IsActive=@IsActive WHERE Id=@Id", ps);
    }

    public void Delete(int id) => db.Execute("DELETE dbo.UdfDefinition WHERE Id = @id", ("@id", id));

    /// <summary>All stored values for an entity: (UdfId, RecordId) → text.</summary>
    public Dictionary<(int, int), string?> GetValues(string entity)
    {
        var dt = db.Query("SELECT v.UdfId, v.RecordId, v.ValueText FROM dbo.UdfValue v " +
                          "JOIN dbo.UdfDefinition d ON d.Id = v.UdfId WHERE d.EntityName = @e", ("@e", entity));
        return dt.Rows.Cast<DataRow>().ToDictionary(r => ((int)r[0], (int)r[1]), r => r[2] as string);
    }
}
