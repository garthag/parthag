-- CONSTRUCT DPR reporting views (version 1). Read-only; safe to run again at any time.
-- Query them from SQL Server Management Studio, Excel (Data > Get Data > From SQL Server) or Power BI.

DROP VIEW IF EXISTS "vw_SchemaInfo";

DROP VIEW IF EXISTS "vw_ReportRegister";

DROP VIEW IF EXISTS "vw_DailyEquipment";

DROP VIEW IF EXISTS "vw_DailyManpower";

DROP VIEW IF EXISTS "vw_DailyQuantities";

DROP VIEW IF EXISTS "vw_ActivityProgress";

DROP VIEW IF EXISTS "vw_ApprovedReports";

CREATE VIEW "vw_ApprovedReports" AS
SELECT d."Id" AS "ReportId", d."ProjectId", p."Code" AS "ProjectCode", d."Number" AS "ReportNumber", d."Revision",
       d."ReportDate", CASE d."Shift" WHEN 0 THEN 'Day' WHEN 1 THEN 'Night' ELSE 'General' END AS "Shift",
       d."PreparedBy", d."ApprovedBy", d."ApprovedAt", d."Weather"
FROM "Dprs" d
JOIN "Projects" p ON p."Id" = d."ProjectId"
WHERE d."Status" = 2
  AND NOT EXISTS (SELECT 1 FROM "Dprs" n
                  WHERE n."ProjectId" = d."ProjectId" AND n."Number" = d."Number" AND n."Status" = 2 AND n."Revision" > d."Revision");

CREATE VIEW "vw_ActivityProgress" AS
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
FROM "Activities" a
JOIN "Projects" p ON p."Id" = a."ProjectId";

CREATE VIEW "vw_DailyQuantities" AS
SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", a."Code" AS "ActivityCode", a."Name" AS "Activity", a."Unit",
       CAST(l."QtyToday" AS decimal(20,6)) AS "ReportedQty",
       CAST(COALESCE(l."QtyAccepted", l."QtyToday") AS decimal(20,6)) AS "AcceptedQty",
       CAST(COALESCE(l."QtyAccepted", l."QtyToday") AS decimal(20,6)) * CAST(a."Rate" AS decimal(20,6)) AS "Value",
       l."DelayReason"
FROM "vw_ApprovedReports" r
JOIN "DprActivityLines" l ON l."DprId" = r."ReportId"
JOIN "Activities" a ON a."Id" = l."ActivityId";

CREATE VIEW "vw_DailyManpower" AS
SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", m."Agency", m."Trade",
       m."Skilled", m."SemiSkilled", m."Unskilled", m."Supervisors",
       m."Skilled" + m."SemiSkilled" + m."Unskilled" + m."Supervisors" AS "Headcount",
       (m."Skilled" + m."SemiSkilled" + m."Unskilled" + m."Supervisors") * CAST(m."Hours" AS decimal(20,6)) AS "ManHours"
FROM "vw_ApprovedReports" r
JOIN "ManpowerLines" m ON m."DprId" = r."ReportId";

CREATE VIEW "vw_DailyEquipment" AS
SELECT r."ProjectCode", r."ReportDate", r."Shift", r."ReportNumber", e."Code" AS "EquipmentCode", e."Type",
       CAST(e."WorkingHours" AS decimal(20,6)) AS "WorkingHours", CAST(e."IdleHours" AS decimal(20,6)) AS "IdleHours",
       CAST(e."BreakdownHours" AS decimal(20,6)) AS "BreakdownHours", e."BreakdownReason"
FROM "vw_ApprovedReports" r
JOIN "EquipmentLines" e ON e."DprId" = r."ReportId";

CREATE VIEW "vw_ReportRegister" AS
SELECT p."Code" AS "ProjectCode", d."Number" AS "ReportNumber", d."Revision", d."ReportDate",
       CASE d."Shift" WHEN 0 THEN 'Day' WHEN 1 THEN 'Night' ELSE 'General' END AS "Shift",
       CASE d."Status" WHEN 0 THEN 'Draft' WHEN 1 THEN 'Submitted' WHEN 2 THEN 'Approved' ELSE 'Rejected' END AS "Status",
       d."PreparedBy", d."ApprovedBy", d."ApprovedAt", d."ReviewComment"
FROM "Dprs" d
JOIN "Projects" p ON p."Id" = d."ProjectId";

CREATE VIEW "vw_SchemaInfo" AS
SELECT 1 AS "ViewsVersion";

