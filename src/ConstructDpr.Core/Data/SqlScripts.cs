using Microsoft.EntityFrameworkCore;

namespace ConstructDpr.Core.Data;

/// <summary>The SQL files in the repository's database/ folder, produced from the same definitions the apps use.</summary>
public static class SqlScripts
{
    /// <summary>All tables, keys and indexes for Microsoft SQL Server, as a script to read or to run by hand.</summary>
    public static string SqlServerSchema()
    {
        using var db = new AppDbContext(AppDbContext.Options(DbProvider.SqlServer, "Server=unused;Database=ConstructDpr"));
        return """
            -- CONSTRUCT DPR: tables for Microsoft SQL Server (schema "cdpr").
            -- You normally do NOT need to run this: the Windows app and the sync server create the tables on first start.
            -- It is here for IT departments who want to review the database or create it by hand:
            --   1. CREATE DATABASE ConstructDpr;   2. USE ConstructDpr;   3. run this file, then 02-views.sql.


            """.ReplaceLineEndings("\n") + db.Database.GenerateCreateScript().ReplaceLineEndings("\n");
    }

    public static Dictionary<string, string> Files() => new()
    {
        ["sqlserver/01-tables.sql"] = SqlServerSchema(),
        ["sqlserver/02-views.sql"] = SqlViews.Script(DbProvider.SqlServer).ReplaceLineEndings("\n"),
        ["sqlite/views.sql"] = SqlViews.Script(DbProvider.Sqlite).ReplaceLineEndings("\n"),
    };
}
