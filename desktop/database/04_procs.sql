/* =====================================================================
   BSL Project Control – activity linking and rate procedures.
   Both procedures only FILL BLANKS; values entered by users are kept.
   ===================================================================== */

/* usp_LinkActivities: analyses the data and links activities to
   work orders, BOQ items, contractors, rates, packages and drawings. */
CREATE OR ALTER PROCEDURE dbo.usp_LinkActivities
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @wo INT, @items INT, @conact INT, @rates INT, @defaults INT, @pkgs INT, @drgs INT;

    /* 1. work order → activity, from the work category (BSL_MECH_ERE_EQUIPMENT → ERE-EQP ...) */
    UPDATE w SET ActivityId = a.Id
    FROM dbo.WorkOrder w
    JOIN dbo.Activity a ON a.WorkCategory = w.WorkCategory
    WHERE w.ActivityId IS NULL;
    SET @wo = @@ROWCOUNT;

    /* 2. BOQ item → activity: the work order's activity; without a work order, by the item's activity type */
    UPDATE ci SET ActivityId = COALESCE(w.ActivityId,
        (SELECT TOP 1 a.Id FROM dbo.Activity a WHERE a.ServiceType = ci.ActivityType AND a.WorkCategory IS NOT NULL ORDER BY a.SortOrder))
    FROM dbo.ContractItem ci
    LEFT JOIN dbo.WorkOrder w ON w.Id = ci.WorkOrderId
    WHERE ci.ActivityId IS NULL AND ci.IsGroupHeader = 0
      AND (w.ActivityId IS NOT NULL OR ci.ActivityType IS NOT NULL);
    SET @items = @@ROWCOUNT;

    /* 3. contractor ↔ activity, from the work orders each contractor holds */
    INSERT dbo.ContractorActivity (ContractorId, ActivityId, IsApproved, Remarks)
    SELECT w.ContractorId, w.ActivityId, 1, N'From work order(s) ' + STRING_AGG(w.UniqueNo, N', ')
    FROM dbo.WorkOrder w
    WHERE w.ActivityId IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dbo.ContractorActivity x WHERE x.ContractorId = w.ContractorId AND x.ActivityId = w.ActivityId)
    GROUP BY w.ContractorId, w.ActivityId;
    SET @conact = @@ROWCOUNT;

    /* 4. work-order activity rates: weighted average (by R1 weight) of the BOQ item rates */
    INSERT dbo.RateMaster (WorkOrderId, ActivityId, ActivityType, ItemCode, Description, Unit, InternalRate, ExternalRate, Remarks)
    SELECT ci.WorkOrderId, ci.ActivityId, MAX(a.ServiceType), MAX(a.ActivityCode),
           MAX(a.Name) + N' – ' + MAX(w.UniqueNo), MAX(a.Unit),
           CAST(SUM(CASE WHEN ci.InternalRate > 0 THEN ci.InternalRate * ISNULL(NULLIF(ci.R1WeightTon,0),1) END)
                / NULLIF(SUM(CASE WHEN ci.InternalRate > 0 THEN ISNULL(NULLIF(ci.R1WeightTon,0),1) END), 0) AS DECIMAL(18,2)),
           CAST(SUM(CASE WHEN ci.ExternalRate > 0 THEN ci.ExternalRate * ISNULL(NULLIF(ci.R1WeightTon,0),1) END)
                / NULLIF(SUM(CASE WHEN ci.ExternalRate > 0 THEN ISNULL(NULLIF(ci.R1WeightTon,0),1) END), 0) AS DECIMAL(18,2)),
           N'Weighted average of ' + CAST(COUNT(*) AS NVARCHAR(10)) + N' BOQ items (by R1 weight)'
    FROM dbo.ContractItem ci
    JOIN dbo.WorkOrder w ON w.Id = ci.WorkOrderId
    JOIN dbo.Activity a  ON a.Id = ci.ActivityId
    WHERE ci.IsGroupHeader = 0 AND (ci.InternalRate > 0 OR ci.ExternalRate > 0)
      AND NOT EXISTS (SELECT 1 FROM dbo.RateMaster r WHERE r.WorkOrderId = ci.WorkOrderId AND r.ActivityId = ci.ActivityId)
    GROUP BY ci.WorkOrderId, ci.ActivityId;
    SET @rates = @@ROWCOUNT;

    /* 5. activity default rates (blank only): weighted average over the whole BOQ */
    UPDATE a SET
        DefaultInternalRate = ISNULL(a.DefaultInternalRate, s.IntRate),
        DefaultExternalRate = ISNULL(a.DefaultExternalRate, s.ExtRate)
    FROM dbo.Activity a
    CROSS APPLY (SELECT
        CAST(SUM(CASE WHEN ci.InternalRate > 0 THEN ci.InternalRate * ISNULL(NULLIF(ci.R1WeightTon,0),1) END)
             / NULLIF(SUM(CASE WHEN ci.InternalRate > 0 THEN ISNULL(NULLIF(ci.R1WeightTon,0),1) END), 0) AS DECIMAL(18,2)) AS IntRate,
        CAST(SUM(CASE WHEN ci.ExternalRate > 0 THEN ci.ExternalRate * ISNULL(NULLIF(ci.R1WeightTon,0),1) END)
             / NULLIF(SUM(CASE WHEN ci.ExternalRate > 0 THEN ISNULL(NULLIF(ci.R1WeightTon,0),1) END), 0) AS DECIMAL(18,2)) AS ExtRate
        FROM dbo.ContractItem ci WHERE ci.ActivityId = a.Id AND ci.IsGroupHeader = 0) s
    WHERE (a.DefaultInternalRate IS NULL AND s.IntRate IS NOT NULL) OR (a.DefaultExternalRate IS NULL AND s.ExtRate IS NOT NULL);
    SET @defaults = @@ROWCOUNT;

    /* 6. package → activity from its name (ducts / mains / pipes vs structures vs stove shell) */
    UPDATE p SET ActivityId = a.Id
    FROM dbo.Package p
    CROSS APPLY (SELECT UPPER(p.PackageCode + N' ' + ISNULL(p.PackageName, N'')) AS T) n
    JOIN dbo.Activity a ON a.ActivityCode =
        CASE WHEN n.T LIKE N'%DUCT%' OR n.T LIKE N'%MAIN%' OR n.T LIKE N'%LINE%' OR n.T LIKE N'%PIPE%' OR n.T LIKE N'%STACK%'
                  OR n.T LIKE N'%NOZZLE%' OR n.T LIKE N'%TRAP%' OR n.T LIKE N'%BLAST%' OR n.T LIKE N'%DIFFUS%' THEN N'ERE-DUCT'
             WHEN n.T LIKE N'%STR%' OR n.T LIKE N'%HOUSE%' OR n.T LIKE N'%PLT%' OR n.T LIKE N'%PLATFORM%' OR n.T LIKE N'%SUPP%' THEN N'ERE-STR'
             WHEN n.T LIKE N'%STOVE%' THEN N'ERE-EQP' END
    WHERE p.ActivityId IS NULL;
    SET @pkgs = @@ROWCOUNT;

    /* 7. drawing → activity: BOQ item's activity, else the package's */
    UPDATE d SET ActivityId = COALESCE(ci.ActivityId, p.ActivityId)
    FROM dbo.DrawingMaster d
    LEFT JOIN dbo.ContractItem ci ON ci.Id = d.ContractItemId
    LEFT JOIN dbo.Package p       ON p.Id = d.PackageId
    WHERE d.ActivityId IS NULL AND COALESCE(ci.ActivityId, p.ActivityId) IS NOT NULL;
    SET @drgs = @@ROWCOUNT;

    SELECT @wo AS WorkOrders, @items AS BoqItems, @conact AS ContractorActivities, @rates AS WorkOrderRates,
           @defaults AS ActivityDefaults, @pkgs AS Packages, @drgs AS Drawings;
END
GO

/* usp_ApplyActivityRates: fills blank BOQ item rates from the work-order activity rate,
   else the activity default. @Overwrite = 1 replaces existing item rates as well. */
CREATE OR ALTER PROCEDURE dbo.usp_ApplyActivityRates @Overwrite BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE ci SET
        InternalRate = CASE WHEN @Overwrite = 1 OR ci.InternalRate IS NULL
                            THEN COALESCE(rm.InternalRate, a.DefaultInternalRate, ci.InternalRate) ELSE ci.InternalRate END,
        ExternalRate = CASE WHEN @Overwrite = 1 OR ci.ExternalRate IS NULL
                            THEN COALESCE(rm.ExternalRate, a.DefaultExternalRate, ci.ExternalRate) ELSE ci.ExternalRate END
    FROM dbo.ContractItem ci
    JOIN dbo.Activity a ON a.Id = ci.ActivityId
    OUTER APPLY (SELECT TOP 1 r.InternalRate, r.ExternalRate FROM dbo.RateMaster r
                 WHERE r.WorkOrderId = ci.WorkOrderId AND r.ActivityId = ci.ActivityId
                 ORDER BY r.EffectiveFrom DESC, r.Id DESC) rm
    WHERE ci.IsGroupHeader = 0
      AND (@Overwrite = 1 OR ci.InternalRate IS NULL OR ci.ExternalRate IS NULL)
      AND COALESCE(rm.InternalRate, a.DefaultInternalRate, rm.ExternalRate, a.DefaultExternalRate) IS NOT NULL;
    SELECT @@ROWCOUNT AS ItemsUpdated;
END
GO
