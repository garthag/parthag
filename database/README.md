# The SQL database

CONSTRUCT DPR keeps everything in one SQL database: **Microsoft SQL Server** for an office (schema `cdpr`), or a **SQLite** file for one PC. The apps create and upgrade the tables themselves, so you never have to run these files. They are here to read, to review, or for an IT department that prefers to create the database by hand.

| File | What it is |
|---|---|
| `sqlserver/01-tables.sql` | All tables, keys and indexes for SQL Server. Produced from the C# model, so it always matches the app |
| `sqlserver/02-views.sql` | Hand-written reporting views for SQL Server |
| `sqlite/views.sql` | The same views for the single-PC SQLite file |

A test fails the build if these files no longer match the app. To regenerate them: `dotnet run --project tools/ConstructDpr.Preview -- --sql database`.

## Reporting views

The apps create these views automatically. They are read-only and count **approved** reports only (the latest approved revision of each report), so they show the same numbers as the app.

| View | One row per | Use it for |
|---|---|---|
| `cdpr.vw_ActivityProgress` | activity | BOQ, approved quantity, balance, %, value, baseline and forecast dates, float |
| `cdpr.vw_DailyQuantities` | activity per approved report | What was done each day, with value at BOQ rates |
| `cdpr.vw_DailyManpower` | crew per approved report | Headcount and man-hours by agency and trade |
| `cdpr.vw_DailyEquipment` | machine per approved report | Working, idle and breakdown hours |
| `cdpr.vw_ReportRegister` | report | Every report with its status, preparer and approver |
| `cdpr.vw_ApprovedReports` | approved report | The reports the other views are built on |

### Examples (SQL Server Management Studio)

```sql
-- Work done per week, in rupees
SELECT DATEPART(ISO_WEEK, ReportDate) AS Week, SUM(Value) AS ValueDone
FROM cdpr.vw_DailyQuantities
WHERE ProjectCode = 'BF2'
GROUP BY DATEPART(ISO_WEEK, ReportDate)
ORDER BY Week;

-- Man-days per agency this month
SELECT Agency, SUM(Headcount) AS ManDays
FROM cdpr.vw_DailyManpower
WHERE ReportDate >= DATEFROMPARTS(YEAR(GETDATE()), MONTH(GETDATE()), 1)
GROUP BY Agency;

-- Activities behind their baseline
SELECT ActivityCode, Activity, ProgressPct, BaselineFinish, ForecastFinish
FROM cdpr.vw_ActivityProgress
WHERE ForecastFinish > BaselineFinish
ORDER BY ForecastFinish;
```

**Excel:** *Data → Get Data → From Database → From SQL Server Database*, enter the server (e.g. `OFFICE-PC\SQLEXPRESS`) and database `ConstructDpr`, then pick a `vw_…` view. **Power BI:** *Get data → SQL Server*, same details.

Only read from the database. Change data through the apps, which check the rules (approvals, overrun, roles) and keep the audit trail.
