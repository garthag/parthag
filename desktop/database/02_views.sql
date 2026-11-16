/* =====================================================================
   BSL Project Control – read views used by grids, forms, formulas and
   reports. Every view exposes the base table columns plus joined detail
   (contractor, work order, package, rates) so user formulas can use them.
   Requires SQL Server 2016 SP1+ (CREATE OR ALTER).
   ===================================================================== */

CREATE OR ALTER VIEW dbo.vw_Contractor AS
SELECT c.*,
       (SELECT COUNT(*)            FROM dbo.WorkOrder w WHERE w.ContractorId = c.Id) AS WorkOrderCount,
       (SELECT SUM(w.WOValue)      FROM dbo.WorkOrder w WHERE w.ContractorId = c.Id) AS TotalWOValue,
       (SELECT SUM(w.ActualPaid)   FROM dbo.WorkOrder w WHERE w.ContractorId = c.Id) AS TotalPaid
FROM dbo.Contractor c;
GO

CREATE OR ALTER VIEW dbo.vw_ContractItemProgress AS
SELECT ci.Id AS ContractItemId,
       SUM(l.QtyTon)                                         AS ExecutedTon,
       SUM(l.QtyTon * ISNULL(l.Rate, ci.InternalRate))       AS ExecutedInternalAmount,
       SUM(l.QtyTon * ci.ExternalRate)                       AS ExecutedExternalAmount
FROM dbo.ContractItem ci
JOIN dbo.RaBillLine l ON l.ContractItemId = ci.Id
GROUP BY ci.Id;
GO

CREATE OR ALTER VIEW dbo.vw_WorkOrder AS
SELECT w.*,
       c.ContractorCode, c.Name AS ContractorName, c.ContractorType, c.ContactPerson, c.ContactNo,
       c.Email, c.PAN, c.GSTIN,
       a.ActivityCode, a.Name AS ActivityName,
       s.ScopeTon, s.ScopeInternalAmount, s.ScopeExternalAmount,
       p.ExecutedTon, p.ExecutedInternalAmount, p.ExecutedExternalAmount,
       ISNULL(s.ScopeTon,0) - ISNULL(p.ExecutedTon,0)        AS BalanceTon,
       CASE WHEN ISNULL(w.WOValue,0) = 0 THEN NULL
            ELSE CAST(ISNULL(p.ExecutedInternalAmount,0) * 100.0 / w.WOValue AS DECIMAL(9,2)) END AS WOUtilisedPct
FROM dbo.WorkOrder w
JOIN dbo.Contractor c ON c.Id = w.ContractorId
LEFT JOIN dbo.Activity a ON a.Id = w.ActivityId
OUTER APPLY (SELECT SUM(ci.R1WeightTon) AS ScopeTon, SUM(ci.InternalAmount) AS ScopeInternalAmount,
                    SUM(ci.ExternalAmount) AS ScopeExternalAmount
             FROM dbo.ContractItem ci WHERE ci.WorkOrderId = w.Id AND ci.IsGroupHeader = 0) s
OUTER APPLY (SELECT SUM(x.ExecutedTon) AS ExecutedTon, SUM(x.ExecutedInternalAmount) AS ExecutedInternalAmount,
                    SUM(x.ExecutedExternalAmount) AS ExecutedExternalAmount
             FROM dbo.ContractItem ci JOIN dbo.vw_ContractItemProgress x ON x.ContractItemId = ci.Id
             WHERE ci.WorkOrderId = w.Id) p;
GO

CREATE OR ALTER VIEW dbo.vw_RateMaster AS
SELECT r.*, w.UniqueNo, w.WorkOrderNo, c.ContractorCode, c.Name AS ContractorName,
       a.ActivityCode, a.Name AS ActivityName, a.DefaultInternalRate, a.DefaultExternalRate,
       CAST(CASE WHEN ca.Id IS NULL THEN 0 ELSE ca.IsApproved END AS BIT) AS ContractorApproved
FROM dbo.RateMaster r
LEFT JOIN dbo.WorkOrder w ON w.Id = r.WorkOrderId
LEFT JOIN dbo.Contractor c ON c.Id = w.ContractorId
LEFT JOIN dbo.Activity a ON a.Id = r.ActivityId
LEFT JOIN dbo.ContractorActivity ca ON ca.ContractorId = w.ContractorId AND ca.ActivityId = r.ActivityId;
GO

CREATE OR ALTER VIEW dbo.vw_ContractItem AS
SELECT ci.*,
       w.UniqueNo, w.WorkOrderNo, c.ContractorCode AS AgencyCode, c.Name AS AgencyName,
       a.ActivityCode, a.Name AS ActivityName,
       COALESCE(rm.InternalRate, a.DefaultInternalRate)      AS SuggestedInternalRate,
       COALESCE(rm.ExternalRate, a.DefaultExternalRate)      AS SuggestedExternalRate,
       CAST(CASE WHEN ci.ActivityId IS NULL OR w.Id IS NULL THEN NULL
                 WHEN EXISTS (SELECT 1 FROM dbo.ContractorActivity ca WHERE ca.ContractorId = w.ContractorId
                              AND ca.ActivityId = ci.ActivityId AND ca.IsApproved = 1) THEN 1 ELSE 0 END AS BIT) AS ActivityApproved,
       p.ExecutedTon, p.ExecutedInternalAmount, p.ExecutedExternalAmount,
       ISNULL(ci.R1WeightTon,0) - ISNULL(p.ExecutedTon,0)    AS BalanceTon,
       CASE WHEN ISNULL(ci.R1WeightTon,0) = 0 THEN NULL
            ELSE CAST(ISNULL(p.ExecutedTon,0) * 100.0 / ci.R1WeightTon AS DECIMAL(9,2)) END AS ProgressPct
FROM dbo.ContractItem ci
LEFT JOIN dbo.WorkOrder w  ON w.Id = ci.WorkOrderId
LEFT JOIN dbo.Contractor c ON c.Id = w.ContractorId
LEFT JOIN dbo.Activity a   ON a.Id = ci.ActivityId
OUTER APPLY (SELECT TOP 1 r.InternalRate, r.ExternalRate FROM dbo.RateMaster r
             WHERE r.WorkOrderId = ci.WorkOrderId AND r.ActivityId = ci.ActivityId
             ORDER BY r.EffectiveFrom DESC, r.Id DESC) rm
LEFT JOIN dbo.vw_ContractItemProgress p ON p.ContractItemId = ci.Id;
GO

CREATE OR ALTER VIEW dbo.vw_RaBill AS
SELECT b.*, w.UniqueNo, w.WorkOrderNo, c.ContractorCode AS AgencyCode, c.Name AS AgencyName,
       t.LineCount, t.TotalTon, t.TotalAmount
FROM dbo.RaBill b
LEFT JOIN dbo.WorkOrder w  ON w.Id = b.WorkOrderId
LEFT JOIN dbo.Contractor c ON c.Id = w.ContractorId
OUTER APPLY (SELECT COUNT(*) AS LineCount, SUM(l.QtyTon) AS TotalTon,
                    SUM(l.QtyTon * ISNULL(l.Rate, ci.InternalRate)) AS TotalAmount
             FROM dbo.RaBillLine l JOIN dbo.ContractItem ci ON ci.Id = l.ContractItemId
             WHERE l.RaBillId = b.Id) t;
GO

CREATE OR ALTER VIEW dbo.vw_RaBillLine AS
SELECT l.*, b.RaNo, b.BillDate, w.UniqueNo, c.ContractorCode AS AgencyCode,
       ci.TechSpecItem, ci.Description AS ItemDescription, ci.Unit, ci.R1WeightTon,
       ci.InternalRate, ci.ExternalRate,
       ISNULL(l.Rate, ci.InternalRate)                       AS EffectiveRate,
       l.QtyTon * ISNULL(l.Rate, ci.InternalRate)            AS Amount,
       l.QtyTon * ci.ExternalRate                            AS ExternalAmount
FROM dbo.RaBillLine l
JOIN dbo.RaBill b        ON b.Id = l.RaBillId
JOIN dbo.ContractItem ci ON ci.Id = l.ContractItemId
LEFT JOIN dbo.WorkOrder w  ON w.Id = b.WorkOrderId
LEFT JOIN dbo.Contractor c ON c.Id = w.ContractorId;
GO

CREATE OR ALTER VIEW dbo.vw_Package AS
SELECT p.*, w.UniqueNo, c.ContractorCode AS AgencyCode, c.Name AS AgencyName,
       a.ActivityCode, a.Name AS ActivityName,
       s.DrawingCount, s.BomLines, s.BomWeightKg,
       CAST(ISNULL(s.BomWeightKg,0) / 1000.0 AS DECIMAL(18,3))              AS WeightPerSetTon,
       CAST(ISNULL(s.BomWeightKg,0) * p.NoOfSets / 1000.0 AS DECIMAL(18,3)) AS WeightAllSetsTon
FROM dbo.Package p
LEFT JOIN dbo.WorkOrder w  ON w.Id = p.WorkOrderId
LEFT JOIN dbo.Contractor c ON c.Id = w.ContractorId
LEFT JOIN dbo.Activity a   ON a.Id = p.ActivityId
OUTER APPLY (SELECT COUNT(DISTINCT b.DrawingId) AS DrawingCount, COUNT(*) AS BomLines,
                    SUM(CASE WHEN b.IsExcluded = 0 THEN b.TotalWeightKg END) AS BomWeightKg
             FROM dbo.BomItem b WHERE b.PackageId = p.Id) s;
GO

CREATE OR ALTER VIEW dbo.vw_DrawingMaster AS
SELECT d.*, p.PackageCode, o.ContractorCode AS OriginatorCode,
       w.UniqueNo AS AgencyUniqueNo, ac.ContractorCode AS AgencyCode,
       ci.TechSpecItem, act.ActivityCode, act.Name AS ActivityName, r.SheetCount, r.LatestRevision, r.LastReceived,
       b.BomLines, b.BomWeightKg,
       CAST(ISNULL(b.BomWeightKg,0) / 1000.0 AS DECIMAL(18,3)) AS BomWeightTon
FROM dbo.DrawingMaster d
LEFT JOIN dbo.Package p       ON p.Id = d.PackageId
LEFT JOIN dbo.Contractor o    ON o.Id = d.OriginatorId
LEFT JOIN dbo.WorkOrder w     ON w.Id = d.AgencyWorkOrderId
LEFT JOIN dbo.Contractor ac   ON ac.Id = w.ContractorId
LEFT JOIN dbo.ContractItem ci ON ci.Id = d.ContractItemId
LEFT JOIN dbo.Activity act    ON act.Id = d.ActivityId
OUTER APPLY (SELECT COUNT(DISTINCT g.SheetNo) AS SheetCount, MAX(g.Revision) AS LatestRevision,
                    MAX(g.ReceivedDate) AS LastReceived
             FROM dbo.DrawingRegister g WHERE g.DrawingId = d.Id AND g.Status <> N'Void') r
OUTER APPLY (SELECT COUNT(*) AS BomLines,
                    SUM(CASE WHEN x.IsExcluded = 0 THEN x.TotalWeightKg END) AS BomWeightKg
             FROM dbo.BomItem x WHERE x.DrawingId = d.Id) b;
GO

CREATE OR ALTER VIEW dbo.vw_DrawingRegister AS
SELECT g.*, d.DrawingNo, d.Title, p.PackageCode, c.ContractorCode AS IssuedToCode, c.Name AS IssuedToName
FROM dbo.DrawingRegister g
JOIN dbo.DrawingMaster d   ON d.Id = g.DrawingId
LEFT JOIN dbo.Package p    ON p.Id = d.PackageId
LEFT JOIN dbo.Contractor c ON c.Id = g.IssuedToId;
GO

CREATE OR ALTER VIEW dbo.vw_BomItem AS
SELECT b.*, p.PackageCode, p.NoOfSets, d.DrawingNo,
       CAST(ISNULL(b.TotalWeightKg,0) * p.NoOfSets AS DECIMAL(18,3)) AS TotalWeightAllSetsKg
FROM dbo.BomItem b
JOIN dbo.Package p          ON p.Id = b.PackageId
LEFT JOIN dbo.DrawingMaster d ON d.Id = b.DrawingId;
GO

CREATE OR ALTER VIEW dbo.vw_LookupValue AS SELECT * FROM dbo.LookupValue;
GO
CREATE OR ALTER VIEW dbo.vw_UdfDefinition AS SELECT * FROM dbo.UdfDefinition;
GO
CREATE OR ALTER VIEW dbo.vw_ImportBatch AS SELECT * FROM dbo.ImportBatch;
GO

/* ---------- activities ---------- */
CREATE OR ALTER VIEW dbo.vw_Activity AS
SELECT a.*,
       (SELECT COUNT(*) FROM dbo.ContractorActivity ca WHERE ca.ActivityId = a.Id AND ca.IsApproved = 1) AS ApprovedContractors,
       (SELECT COUNT(*) FROM dbo.WorkOrder w WHERE w.ActivityId = a.Id)                                  AS WorkOrders,
       s.BoqItems, s.ScopeTon, s.ExternalAmount, s.InternalAmount, s.Margin,
       s.AvgExternalRate, s.AvgInternalRate,
       (SELECT SUM(x.ExecutedTon) FROM dbo.ContractItem ci JOIN dbo.vw_ContractItemProgress x ON x.ContractItemId = ci.Id
        WHERE ci.ActivityId = a.Id)                                                                       AS ExecutedTon,
       (SELECT COUNT(*) FROM dbo.Package p WHERE p.ActivityId = a.Id)                                    AS Packages
FROM dbo.Activity a
OUTER APPLY (SELECT COUNT(*) AS BoqItems, SUM(ci.R1WeightTon) AS ScopeTon,
                    SUM(ci.ExternalAmount) AS ExternalAmount, SUM(ci.InternalAmount) AS InternalAmount, SUM(ci.Margin) AS Margin,
                    CAST(SUM(CASE WHEN ci.ExternalRate > 0 THEN ci.ExternalRate * ci.R1WeightTon END)
                         / NULLIF(SUM(CASE WHEN ci.ExternalRate > 0 THEN ci.R1WeightTon END), 0) AS DECIMAL(18,2)) AS AvgExternalRate,
                    CAST(SUM(CASE WHEN ci.InternalRate > 0 THEN ci.InternalRate * ci.R1WeightTon END)
                         / NULLIF(SUM(CASE WHEN ci.InternalRate > 0 THEN ci.R1WeightTon END), 0) AS DECIMAL(18,2)) AS AvgInternalRate
             FROM dbo.ContractItem ci WHERE ci.ActivityId = a.Id AND ci.IsGroupHeader = 0) s;
GO

CREATE OR ALTER VIEW dbo.vw_ContractorActivity AS
SELECT ca.*, c.ContractorCode, c.Name AS ContractorName, a.ActivityCode, a.Name AS ActivityName, a.ServiceType,
       a.DefaultInternalRate, a.DefaultExternalRate,
       (SELECT COUNT(*) FROM dbo.WorkOrder w WHERE w.ContractorId = ca.ContractorId AND w.ActivityId = ca.ActivityId) AS WorkOrders,
       (SELECT SUM(ci.R1WeightTon) FROM dbo.ContractItem ci JOIN dbo.WorkOrder w ON w.Id = ci.WorkOrderId
        WHERE w.ContractorId = ca.ContractorId AND ci.ActivityId = ca.ActivityId AND ci.IsGroupHeader = 0)         AS ScopeTon
FROM dbo.ContractorActivity ca
JOIN dbo.Contractor c ON c.Id = ca.ContractorId
JOIN dbo.Activity a   ON a.Id = ca.ActivityId;
GO
