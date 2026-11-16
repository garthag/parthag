namespace BSL.Core.Reports;

/// <summary>A standard report: SQL on the views; column names are the printed captions.</summary>
public sealed record ReportDef(string Category, string Name, string Sql, string? GroupBy = null, bool Landscape = true, string? Description = null)
{
    public override string ToString() => Name;
}

public static class ReportCatalog
{
    public static readonly IReadOnlyList<ReportDef> All =
    [
        // ----------------------------------------------------------- Drawings & BOM
        new("Drawings & BOM", "Package-wise weight summary", """
            SELECT PackageCode AS [Package], PackageName AS [Name], Area, AgencyCode AS [Agency], NoOfSets AS [Sets],
                   DrawingCount AS [Drawings], BomLines AS [BOM Lines], WeightPerSetTon AS [Wt per set (MT)], WeightAllSetsTon AS [Wt all sets (MT)]
            FROM dbo.vw_Package ORDER BY PackageCode
            """, null, true, "Weight per package with the number of sets (stoves / traps) applied."),
        new("Drawings & BOM", "Section-wise weight (per package)", """
            SELECT p.PackageCode AS [Package], ISNULL(b.SectionStd, N'(blank)') AS [Section], COUNT(*) AS [Lines],
                   SUM(b.Qty) AS [Pcs], CAST(SUM(b.TotalWeightKg) / 1000 AS DECIMAL(18,3)) AS [Wt per set (MT)],
                   CAST(SUM(b.TotalWeightKg) * p.NoOfSets / 1000 AS DECIMAL(18,3)) AS [Wt all sets (MT)]
            FROM dbo.BomItem b JOIN dbo.Package p ON p.Id = b.PackageId
            WHERE b.IsExcluded = 0
            GROUP BY p.PackageCode, p.NoOfSets, b.SectionStd ORDER BY p.PackageCode, SUM(b.TotalWeightKg) DESC
            """, "Package", false, "Steel take-off by standardised section."),
        new("Drawings & BOM", "Material grade-wise weight", """
            SELECT ISNULL(b.MaterialGrade, N'(blank)') AS [Material Grade], p.PackageCode AS [Package], COUNT(*) AS [Lines],
                   CAST(SUM(b.TotalWeightKg) * p.NoOfSets / 1000 AS DECIMAL(18,3)) AS [Wt all sets (MT)]
            FROM dbo.BomItem b JOIN dbo.Package p ON p.Id = b.PackageId
            WHERE b.IsExcluded = 0
            GROUP BY b.MaterialGrade, p.PackageCode, p.NoOfSets ORDER BY b.MaterialGrade, p.PackageCode
            """, "Material Grade", false, "Procurement view: weight per grade."),
        new("Drawings & BOM", "Drawing-wise BOM weight", """
            SELECT PackageCode AS [Package], DrawingNo AS [Drawing No], Title, OriginatorCode AS [Originator], CurrentRevision AS [Rev],
                   TotalSheets AS [Sheets], Status, BomLines AS [BOM Lines], BomWeightTon AS [BOM Wt (MT)], EstWeightKg AS [Est. Wt (kg)]
            FROM dbo.vw_DrawingMaster ORDER BY PackageCode, DrawingNo
            """, "Package", true, "Drawing master with weight from the BOM."),
        new("Drawings & BOM", "Drawing register", """
            SELECT PackageCode AS [Package], DrawingNo AS [Drawing No], SheetNo AS [Sheet], Revision AS [Rev], Purpose, Status,
                   TransmittalNo AS [Transmittal], ReceivedDate AS [Received], IssuedToCode AS [Issued To], IssuedDate AS [Issued On], Remarks
            FROM dbo.vw_DrawingRegister ORDER BY PackageCode, DrawingNo, SheetNo, Revision
            """, "Package", true, "Every sheet / revision with transmittal and issue status."),
        new("Drawings & BOM", "Drawings pending (no BOM / no drawing no.)", """
            SELECT Status, DrawingNo AS [Drawing No], Title, Area, SubArea AS [Sub Area], PackageCode AS [Package], EstQty AS [Est. Qty],
                   EstWeightKg AS [Est. Wt (kg)], Remarks
            FROM dbo.vw_DrawingMaster WHERE ISNULL(BomLines, 0) = 0 ORDER BY Status, Area, DrawingNo
            """, "Status", true, "Scope items without drawing number or without BOM lines."),
        new("Drawings & BOM", "BOM data-quality flags", """
            SELECT PackageCode AS [Package], SourceRow AS [Row], DrawingNo AS [Drawing No], ItemDescription AS [Item], SectionDrg AS [Section],
                   TotalWeightKg AS [Wt (kg)], QualityFlags AS [Flags]
            FROM dbo.vw_BomItem WHERE QualityFlags IS NOT NULL AND QualityFlags NOT LIKE N'detail of assembly%'
            ORDER BY PackageCode, SourceRow
            """, "Package", true, "Lines the importer corrected or could not resolve."),

        // ----------------------------------------------------------- Contractors & contract
        new("Contract", "Contractor master list", """
            SELECT ContractorType AS [Type], ContractorCode AS [Code], Name, ContactPerson AS [Contact], ContactNo AS [Phone], Email AS [E-mail],
                   PAN, GSTIN, WorkOrderCount AS [WOs], TotalWOValue AS [Total WO Value]
            FROM dbo.vw_Contractor ORDER BY ContractorType, ContractorCode
            """, "Type", true),
        new("Contract", "Work order summary", """
            SELECT ContractorCode AS [Agency], UniqueNo AS [Unique No], ServiceType AS [Service], WorkOrderNo AS [WO No], WODate AS [WO Date],
                   WOValue AS [WO Value], ScopeTon AS [Scope (MT)], ExecutedTon AS [Executed (MT)], BalanceTon AS [Balance (MT)],
                   ExecutedInternalAmount AS [Executed Amount], WOUtilisedPct AS [WO used Pct], ActualPaid AS [Paid], Difference
            FROM dbo.vw_WorkOrder ORDER BY ContractorCode, UniqueNo
            """, "Agency", true, "Work order value against executed quantity (RA bills)."),
        new("Contract", "BOQ progress (agency-wise)", """
            SELECT ISNULL(AgencyCode, ISNULL(AgencyRef, N'(none)')) AS [Agency], UniqueNo AS [WO], TechSpecItem AS [Spec Item],
                   Description, Unit, R1WeightTon AS [R1 (MT)], ExecutedTon AS [Executed (MT)], BalanceTon AS [Balance (MT)],
                   ProgressPct AS [Progress Pct], InternalRate AS [Internal Rate], ExecutedInternalAmount AS [Executed Amount]
            FROM dbo.vw_ContractItem WHERE IsGroupHeader = 0 ORDER BY 1, SortOrder
            """, "Agency", true, "Scope vs. executed quantity for every BOQ item."),
        new("Contract", "Internal vs external rate – margin", """
            SELECT ISNULL(AgencyCode, ISNULL(AgencyRef, N'(none)')) AS [Agency], GroupName AS [Group], Description, R1WeightTon AS [R1 (MT)],
                   ExternalRate AS [External Rate], InternalRate AS [Internal Rate], ExternalAmount AS [External Amount],
                   InternalAmount AS [Internal Amount], Margin AS [Margin],
                   CAST(CASE WHEN ISNULL(ExternalRate,0) = 0 THEN NULL ELSE (ExternalRate - ISNULL(InternalRate,0)) * 100.0 / ExternalRate END AS DECIMAL(9,2)) AS [Margin Pct]
            FROM dbo.vw_ContractItem WHERE IsGroupHeader = 0 ORDER BY 1, SortOrder
            """, "Agency", true, "Client (external) rate against agency (internal) rate."),
        new("Contract", "Rate master", """
            SELECT ContractorCode AS [Agency], UniqueNo AS [WO], ActivityType AS [Activity], ItemCode AS [Code], Description, Unit,
                   InternalRate AS [Internal Rate], ExternalRate AS [External Rate], Margin AS [Margin per unit], MarginPct AS [Margin Pct], EffectiveFrom AS [From]
            FROM dbo.vw_RateMaster ORDER BY ContractorCode, ItemCode
            """, "Agency", true),
        new("Contract", "RA bill abstract", """
            SELECT ISNULL(UniqueNo, N'-') + N' / RA-' + CAST(RaNo AS NVARCHAR(10)) AS [RA Bill], AgencyCode AS [Agency], TechSpecItem AS [Spec Item],
                   ItemDescription AS [Item], QtyTon AS [Qty (MT)], EffectiveRate AS [Rate], Amount AS [Amount], ExternalAmount AS [External Amount]
            FROM dbo.vw_RaBillLine ORDER BY UniqueNo, RaNo, TechSpecItem
            """, "RA Bill", true, "Line items of every RA bill with amounts."),
        new("Contract", "RA bill summary", """
            SELECT AgencyCode AS [Agency], UniqueNo AS [WO], RaNo AS [RA No], BillDate AS [Bill Date], Status, LineCount AS [Lines],
                   TotalTon AS [Qty (MT)], TotalAmount AS [Amount]
            FROM dbo.vw_RaBill ORDER BY AgencyCode, UniqueNo, RaNo
            """, "Agency", false)
    ];
}
