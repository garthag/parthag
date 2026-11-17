-- CONSTRUCT DPR reporting views (version 1). Read-only; safe to run again at any time.
-- Query them from SQL Server Management Studio, Excel (Data > Get Data > From SQL Server) or Power BI.

DROP VIEW IF EXISTS cdpr."vw_SchemaInfo";
GO

DROP VIEW IF EXISTS cdpr."vw_ReportRegister";
GO

DROP VIEW IF EXISTS cdpr."vw_DailyEquipment";
GO

DROP VIEW IF EXISTS cdpr."vw_DailyManpower";
GO

DROP VIEW IF EXISTS cdpr."vw_DailyQuantities";
GO

DROP VIEW IF EXISTS cdpr."vw_ActivityProgress";
GO

DROP VIEW IF EXISTS cdpr."vw_ApprovedReports";
GO

CREATE VIEW cdpr."vw_ApprovedReports" AS
SELECT d."Id" AS "ReportId", d."ProjectId", p."Code" AS "ProjectCode", d."Number" AS "ReportNumber", d."Revision",
       d."ReportDate", CASE d."Shift" WHEN 0 THEN 'Day' WHEN 1 THEN 'Night' ELSE 'General' END AS "Shift",
       d."PreparedBy", d."ApprovedBy", d."ApprovedAt", d."Weather"
FROM cdpr."Dprs" d
JOIN cdpr."Projects" p ON p."Id" = d."ProjectId"
WHERE d."Status" = 2
  AND NOT EXISTS (SELECT 1 FROM cdpr."Dprs" n
                  WHERE n."ProjectId" = d."ProjectId" AND n."Number" = d."Number" AND n."Status" = 2 AND n."Revision" > d."Revision");
GO

CREATE VIEW cdpr."vw_ActivityProgress" AS
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
FROM cdpr."Activities" a
JOIN cdpr."Projects" p ON p."Id" = a."ProjectId";
GO

CREATE VIEW cdpr."vw_DailyQuantities" AS
SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", a."Code" AS "ActivityCode", a."Name" AS "Activity", a."Unit",
       CAST(l."QtyToday" AS decimal(20,6)) AS "ReportedQty",
       CAST(COALESCE(l."QtyAccepted", l."QtyToday") AS decimal(20,6)) AS "AcceptedQty",
       CAST(COALESCE(l."QtyAccepted", l."QtyToday") AS decimal(20,6)) * CAST(a."Rate" AS decimal(20,6)) AS "Value",
       l."DelayReason"
FROM cdpr."vw_ApprovedReports" r
JOIN cdpr."DprActivityLines" l ON l."DprId" = r."ReportId"
JOIN cdpr."Activities" a ON a."Id" = l."ActivityId";
GO

CREATE VIEW cdpr."vw_DailyManpower" AS
SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", m."Agency", m."Trade",
       m."Skilled", m."SemiSkilled", m."Unskilled", m."Supervisors",
       m."Skilled" + m."SemiSkilled" + m."Unskilled" + m."Supervisors" AS "Headcount",
       (m."Skilled" + m."SemiSkilled" + m."Unskilled" + m."Supervisors") * CAST(m."Hours" AS decimal(20,6)) AS "ManHours"
FROM cdpr."vw_ApprovedReports" r
JOIN cdpr."ManpowerLines" m ON m."DprId" = r."ReportId";
GO

CREATE VIEW cdpr."vw_DailyEquipment" AS
SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", e."Code" AS "EquipmentCode", e."Type",
       CAST(e."WorkingHours" AS decimal(20,6)) AS "WorkingHours", CAST(e."IdleHours" AS decimal(20,6)) AS "IdleHours",
       CAST(e."BreakdownHours" AS decimal(20,6)) AS "BreakdownHours", e."BreakdownReason"
FROM cdpr."vw_ApprovedReports" r
JOIN cdpr."EquipmentLines" e ON e."DprId" = r."ReportId";
GO

CREATE VIEW cdpr."vw_ReportRegister" AS
SELECT p."Code" AS "ProjectCode", d."Number" AS "ReportNumber", d."Revision", d."ReportDate",
       CASE d."Shift" WHEN 0 THEN 'Day' WHEN 1 THEN 'Night' ELSE 'General' END AS "Shift",
       CASE d."Status" WHEN 0 THEN 'Draft' WHEN 1 THEN 'Submitted' WHEN 2 THEN 'Approved' ELSE 'Rejected' END AS "Status",
       d."PreparedBy", d."ApprovedBy", d."ApprovedAt", d."ReviewComment"
FROM cdpr."Dprs" d
JOIN cdpr."Projects" p ON p."Id" = d."ProjectId";
GO

CREATE VIEW cdpr."vw_SchemaInfo" AS
SELECT 1 AS "ViewsVersion";
GO

