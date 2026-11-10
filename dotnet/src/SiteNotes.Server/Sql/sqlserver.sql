-- SiteNotes DPR – SQL Server schema (SQL Server 2016+ / Express / Azure SQL)
-- The server creates this automatically on first start. You can also run it yourself in SSMS:
-- create an empty database called SiteNotes first, then run this script against it.
-- Batches are separated by GO.

IF OBJECT_ID('dbo.SyncRecords') IS NULL
BEGIN
    CREATE TABLE dbo.SyncRecords (
        Id            NVARCHAR(64)  NOT NULL,
        EntityType    NVARCHAR(64)  NOT NULL,
        ProjectId     NVARCHAR(64)  NOT NULL,
        Json          NVARCHAR(MAX) NOT NULL,
        UpdatedAtUtc  DATETIME2(3)  NOT NULL,
        IsDeleted     BIT           NOT NULL DEFAULT 0,
        Version       BIGINT        NOT NULL,
        DeviceId      NVARCHAR(64)  NOT NULL DEFAULT '',
        ReceivedAtUtc DATETIME2(3)  NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SyncRecords PRIMARY KEY (EntityType, Id)
    );
    CREATE INDEX IX_SyncRecords_Version ON dbo.SyncRecords (Version);
    CREATE INDEX IX_SyncRecords_Project ON dbo.SyncRecords (ProjectId, EntityType);
END
GO

IF OBJECT_ID('dbo.SyncCounter') IS NULL
BEGIN
    CREATE TABLE dbo.SyncCounter (Id INT NOT NULL PRIMARY KEY, Value BIGINT NOT NULL);
    INSERT INTO dbo.SyncCounter (Id, Value) VALUES (1, 0);
END
GO

-- ---------------------------------------------------------------------------
-- Reporting views: read the synced JSON as normal tables, for Power BI, Excel
-- (Data > Get Data > SQL Server) or your own SQL queries.
-- ---------------------------------------------------------------------------

CREATE OR ALTER VIEW dbo.vw_Projects AS
SELECT Id, JSON_VALUE(Json, '$.name') AS Name, JSON_VALUE(Json, '$.code') AS Code,
       JSON_VALUE(Json, '$.client') AS Client, JSON_VALUE(Json, '$.contractor') AS Contractor,
       JSON_VALUE(Json, '$.consultant') AS Consultant, JSON_VALUE(Json, '$.location') AS Location,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.startDate')) AS StartDate,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.endDate')) AS EndDate
FROM dbo.SyncRecords WHERE EntityType = 'Project' AND IsDeleted = 0;
GO

CREATE OR ALTER VIEW dbo.vw_DailyReports AS
SELECT Id, ProjectId,
       TRY_CONVERT(int, JSON_VALUE(Json, '$.reportNo')) AS ReportNo,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.date')) AS ReportDate,
       JSON_VALUE(Json, '$.status') AS Status, JSON_VALUE(Json, '$.shift') AS Shift,
       JSON_VALUE(Json, '$.weatherMorning') AS WeatherMorning, JSON_VALUE(Json, '$.weatherEvening') AS WeatherEvening,
       TRY_CONVERT(int, JSON_VALUE(Json, '$.totalManpower')) AS TotalManpower,
       TRY_CONVERT(float, JSON_VALUE(Json, '$.totalManHours')) AS TotalManHours,
       TRY_CONVERT(int, JSON_VALUE(Json, '$.totalMachines')) AS TotalMachines,
       JSON_VALUE(Json, '$.toolboxTalkTopic') AS ToolboxTalkTopic,
       JSON_VALUE(Json, '$.preparedBy') AS PreparedBy, JSON_VALUE(Json, '$.remarks') AS Remarks,
       UpdatedAtUtc
FROM dbo.SyncRecords WHERE EntityType = 'DailyReport' AND IsDeleted = 0;
GO

CREATE OR ALTER VIEW dbo.vw_Manpower AS
SELECT m.Id, m.ProjectId, JSON_VALUE(m.Json, '$.reportId') AS ReportId, r.ReportDate, r.ReportNo,
       JSON_VALUE(m.Json, '$.source') AS Source, JSON_VALUE(m.Json, '$.partyName') AS PartyName,
       JSON_VALUE(m.Json, '$.trade') AS Trade,
       TRY_CONVERT(int, JSON_VALUE(m.Json, '$.count')) AS Nos,
       TRY_CONVERT(float, JSON_VALUE(m.Json, '$.hours')) AS Hours
FROM dbo.SyncRecords m
JOIN dbo.vw_DailyReports r ON r.Id = JSON_VALUE(m.Json, '$.reportId')
WHERE m.EntityType = 'ReportManpower' AND m.IsDeleted = 0;
GO

CREATE OR ALTER VIEW dbo.vw_Activities AS
SELECT a.Id, a.ProjectId, JSON_VALUE(a.Json, '$.reportId') AS ReportId, r.ReportDate, r.ReportNo,
       JSON_VALUE(a.Json, '$.activityName') AS Activity, JSON_VALUE(a.Json, '$.location') AS Location,
       JSON_VALUE(a.Json, '$.subcontractorName') AS Agency, JSON_VALUE(a.Json, '$.unit') AS Unit,
       TRY_CONVERT(float, JSON_VALUE(a.Json, '$.plannedQty')) AS PlannedQty,
       TRY_CONVERT(float, JSON_VALUE(a.Json, '$.todayQty')) AS TodayQty,
       TRY_CONVERT(float, JSON_VALUE(a.Json, '$.cumulativeQty')) AS CumulativeQty,
       JSON_VALUE(a.Json, '$.remarks') AS Remarks
FROM dbo.SyncRecords a
JOIN dbo.vw_DailyReports r ON r.Id = JSON_VALUE(a.Json, '$.reportId')
WHERE a.EntityType = 'ReportActivity' AND a.IsDeleted = 0;
GO

CREATE OR ALTER VIEW dbo.vw_Machinery AS
SELECT m.Id, m.ProjectId, r.ReportDate, r.ReportNo,
       JSON_VALUE(m.Json, '$.name') AS Machine, JSON_VALUE(m.Json, '$.ownership') AS Ownership,
       TRY_CONVERT(int, JSON_VALUE(m.Json, '$.count')) AS Nos,
       TRY_CONVERT(float, JSON_VALUE(m.Json, '$.workingHours')) AS WorkingHours,
       TRY_CONVERT(float, JSON_VALUE(m.Json, '$.idleHours')) AS IdleHours,
       TRY_CONVERT(float, JSON_VALUE(m.Json, '$.fuelLitres')) AS FuelLitres
FROM dbo.SyncRecords m
JOIN dbo.vw_DailyReports r ON r.Id = JSON_VALUE(m.Json, '$.reportId')
WHERE m.EntityType = 'ReportMachine' AND m.IsDeleted = 0;
GO

CREATE OR ALTER VIEW dbo.vw_Hindrances AS
SELECT Id, ProjectId, TRY_CONVERT(int, JSON_VALUE(Json, '$.serialNo')) AS SerialNo,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.fromDate')) AS FromDate,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.toDate')) AS ToDate,
       JSON_VALUE(Json, '$.category') AS Category, JSON_VALUE(Json, '$.description') AS Description,
       JSON_VALUE(Json, '$.responsibleParty') AS ResponsibleParty,
       TRY_CONVERT(float, JSON_VALUE(Json, '$.hoursLost')) AS HoursLost,
       JSON_VALUE(Json, '$.status') AS Status, JSON_VALUE(Json, '$.actionTaken') AS ActionTaken
FROM dbo.SyncRecords WHERE EntityType = 'Hindrance' AND IsDeleted = 0;
GO

CREATE OR ALTER VIEW dbo.vw_Requirements AS
SELECT Id, ProjectId, TRY_CONVERT(int, JSON_VALUE(Json, '$.serialNo')) AS SerialNo,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.raisedDate')) AS RaisedDate,
       JSON_VALUE(Json, '$.category') AS Category, JSON_VALUE(Json, '$.item') AS Item,
       TRY_CONVERT(float, JSON_VALUE(Json, '$.quantity')) AS Quantity, JSON_VALUE(Json, '$.unit') AS Unit,
       TRY_CONVERT(date, JSON_VALUE(Json, '$.requiredBy')) AS RequiredBy,
       JSON_VALUE(Json, '$.priority') AS Priority, JSON_VALUE(Json, '$.status') AS Status,
       JSON_VALUE(Json, '$.raisedBy') AS RaisedBy, JSON_VALUE(Json, '$.remarks') AS Remarks
FROM dbo.SyncRecords WHERE EntityType = 'Requirement' AND IsDeleted = 0;
GO

-- Example: monthly man-days by subcontractor
-- SELECT FORMAT(ReportDate, 'yyyy-MM') AS Month, PartyName, SUM(Nos) AS ManDays
-- FROM dbo.vw_Manpower WHERE Source = 'Subcontractor' GROUP BY FORMAT(ReportDate, 'yyyy-MM'), PartyName ORDER BY 1, 3 DESC;
