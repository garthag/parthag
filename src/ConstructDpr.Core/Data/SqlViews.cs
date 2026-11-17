using System.Text;

namespace ConstructDpr.Core.Data;

/// <summary>
/// Hand-written SQL: reporting views for SQL Server Management Studio, Excel and Power BI.
/// They are read-only windows on the tables; the apps never depend on them. The same SQL runs on
/// SQL Server, SQLite and PostgreSQL ({s} is the schema prefix: "cdpr." or nothing on SQLite).
/// Bump <see cref="Version"/> whenever a view changes: every database then recreates them on the next start.
/// </summary>
public static class SqlViews
{
    public const int Version = 1;

    /// <summary>Name and SELECT of every view, in creation order (later views use earlier ones).</summary>
    public static readonly (string Name, string Select)[] All =
    [
        // The approved daily reports that count: the latest approved revision of each report number.
        ("vw_ApprovedReports", """
            SELECT d."Id" AS "ReportId", d."ProjectId", p."Code" AS "ProjectCode", d."Number" AS "ReportNumber", d."Revision",
                   d."ReportDate", CASE d."Shift" WHEN 0 THEN 'Day' WHEN 1 THEN 'Night' ELSE 'General' END AS "Shift",
                   d."PreparedBy", d."ApprovedBy", d."ApprovedAt", d."Weather"
            FROM {s}"Dprs" d
            JOIN {s}"Projects" p ON p."Id" = d."ProjectId"
            WHERE d."Status" = 2
              AND NOT EXISTS (SELECT 1 FROM {s}"Dprs" n
                              WHERE n."ProjectId" = d."ProjectId" AND n."Number" = d."Number" AND n."Status" = 2 AND n."Revision" > d."Revision")
            """),

        // One row per activity: BOQ, approved quantity, value and schedule dates.
        ("vw_ActivityProgress", """
            SELECT p."Code" AS "ProjectCode", a."Code" AS "ActivityCode", a."Name" AS "Activity", a."Wbs", a."Area", a."Unit",
                   CAST(a."TotalQty" AS decimal(20,6)) AS "BoqQty",
                   CAST(a."AcceptedQty" AS decimal(20,6)) AS "ApprovedQty",
                   CAST(a."TotalQty" AS decimal(20,6)) - CAST(a."AcceptedQty" AS decimal(20,6)) AS "BalanceQty",
                   CASE WHEN CAST(a."TotalQty" AS decimal(20,6)) > 0
                        THEN CAST(a."AcceptedQty" AS decimal(20,6)) * 100 / CAST(a."TotalQty" AS decimal(20,6)) END AS "ProgressPct",
                   CAST(a."Rate" AS decimal(20,6)) AS "Rate",
                   CAST(a."TotalQty" AS decimal(20,6)) * CAST(a."Rate" AS decimal(20,6)) AS "BoqValue",
                   CAST(a."AcceptedQty" AS decimal(20,6)) * CAST(a."Rate" AS decimal(20,6)) AS "ApprovedValue",
                   a."BaselineStart", a."BaselineFinish", a."EarlyStart" AS "ForecastStart", a."EarlyFinish" AS "ForecastFinish",
                   a."ActualStart", a."ActualFinish", a."TotalFloat", a."Critical",
                   CASE a."Status" WHEN 0 THEN 'Not started' WHEN 1 THEN 'In progress' WHEN 2 THEN 'Completed' ELSE 'Cancelled' END AS "Status"
            FROM {s}"Activities" a
            JOIN {s}"Projects" p ON p."Id" = a."ProjectId"
            """),

        // One row per activity per approved report: what was done on which day.
        ("vw_DailyQuantities", """
            SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", a."Code" AS "ActivityCode", a."Name" AS "Activity", a."Unit",
                   CAST(l."QtyToday" AS decimal(20,6)) AS "ReportedQty",
                   CAST(COALESCE(l."QtyAccepted", l."QtyToday") AS decimal(20,6)) AS "AcceptedQty",
                   CAST(COALESCE(l."QtyAccepted", l."QtyToday") AS decimal(20,6)) * CAST(a."Rate" AS decimal(20,6)) AS "Value",
                   l."DelayReason"
            FROM {s}"vw_ApprovedReports" r
            JOIN {s}"DprActivityLines" l ON l."DprId" = r."ReportId"
            JOIN {s}"Activities" a ON a."Id" = l."ActivityId"
            """),

        // Manpower per day, agency and trade (approved reports).
        ("vw_DailyManpower", """
            SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", m."Agency", m."Trade",
                   m."Skilled", m."SemiSkilled", m."Unskilled", m."Supervisors",
                   m."Skilled" + m."SemiSkilled" + m."Unskilled" + m."Supervisors" AS "Headcount",
                   (m."Skilled" + m."SemiSkilled" + m."Unskilled" + m."Supervisors") * CAST(m."Hours" AS decimal(20,6)) AS "ManHours"
            FROM {s}"vw_ApprovedReports" r
            JOIN {s}"ManpowerLines" m ON m."DprId" = r."ReportId"
            """),

        // Equipment hours per day (approved reports).
        ("vw_DailyEquipment", """
            SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", e."Code" AS "EquipmentCode", e."Type",
                   CAST(e."WorkingHours" AS decimal(20,6)) AS "WorkingHours", CAST(e."IdleHours" AS decimal(20,6)) AS "IdleHours",
                   CAST(e."BreakdownHours" AS decimal(20,6)) AS "BreakdownHours", e."BreakdownReason"
            FROM {s}"vw_ApprovedReports" r
            JOIN {s}"EquipmentLines" e ON e."DprId" = r."ReportId"
            """),

        // Every report in every state: the register.
        ("vw_ReportRegister", """
            SELECT p."Code" AS "ProjectCode", d."Number" AS "ReportNumber", d."Revision", d."ReportDate",
                   CASE d."Shift" WHEN 0 THEN 'Day' WHEN 1 THEN 'Night' ELSE 'General' END AS "Shift",
                   CASE d."Status" WHEN 0 THEN 'Draft' WHEN 1 THEN 'Submitted' WHEN 2 THEN 'Approved' ELSE 'Rejected' END AS "Status",
                   d."PreparedBy", d."ApprovedBy", d."ApprovedAt", d."ReviewComment"
            FROM {s}"Dprs" d
            JOIN {s}"Projects" p ON p."Id" = d."ProjectId"
            """),

        // Marker read by the apps to know whether the views are up to date.
        ("vw_SchemaInfo", "SELECT " + "{v}" + """ AS "ViewsVersion" """),
    ];

    /// <summary>The statements that (re)create all views for one kind of database, one statement per entry.</summary>
    public static List<string> Statements(DbProvider provider)
    {
        var s = provider == DbProvider.Sqlite ? "" : AppDbContext.Schema + ".";
        var list = new List<string>();
        foreach (var (name, _) in All.Reverse()) list.Add($"DROP VIEW IF EXISTS {s}\"{name}\"");
        foreach (var (name, select) in All)
            list.Add($"CREATE VIEW {s}\"{name}\" AS\n" + select.Replace("{s}", s).Replace("{v}", Version.ToString()).TrimEnd());
        return list;
    }

    /// <summary>The same as one readable script (SQL Server uses GO between statements).</summary>
    public static string Script(DbProvider provider)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- CONSTRUCT DPR reporting views (version " + Version + "). Read-only; safe to run again at any time.");
        sb.AppendLine("-- Query them from SQL Server Management Studio, Excel (Data > Get Data > From SQL Server) or Power BI.");
        sb.AppendLine();
        foreach (var st in Statements(provider))
        {
            sb.AppendLine(st + ";");
            if (provider == DbProvider.SqlServer) sb.AppendLine("GO");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
