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
                   TransmittalNo AS [Transmittal], ReceivedDate AS [Received], IssuedToCode AS [Issued To], IssuedDate AS [Issued On],
                   FileCount AS [Files], Remarks
            FROM dbo.vw_DrawingRegister ORDER BY PackageCode, DrawingNo, SheetNo, Revision
            """, "Package", true, "Every sheet / revision with transmittal and issue status."),
        new("Drawings & BOM", "Drawings without uploaded file", """
            SELECT PackageCode AS [Package], DrawingNo AS [Drawing No], Title, CurrentRevision AS [Rev], Status, OriginatorCode AS [Originator],
                   BomLines AS [BOM Lines], BomWeightTon AS [BOM Wt (MT)]
            FROM dbo.vw_DrawingMaster WHERE ISNULL(FileCount, 0) = 0 ORDER BY PackageCode, DrawingNo
            """, "Package", true, "Drawings in the master that have no uploaded PDF / DWG yet."),
        new("Documents", "Uploaded files register", """
            SELECT DocType AS [Type], EntityName AS [Screen], RecordRef AS [Record], FileName AS [File], Revision AS [Rev],
                   FileSizeKb AS [Size KB], Description, UploadedBy AS [Uploaded By], UploadedOn AS [Uploaded On]
            FROM dbo.vw_Attachment ORDER BY DocType, RecordRef, FileName
            """, "Type", true, "Every uploaded drawing and document."),
        new("Documents", "Notes register", """
            SELECT EntityName AS [Screen], RecordId AS [Record Id], NoteDate AS [Date], Category,
                   CASE WHEN IsImportant = 1 THEN N'YES' ELSE N'' END AS [Important], NoteText AS [Note], CreatedBy AS [By]
            FROM dbo.vw_RecordNote ORDER BY EntityName, NoteDate DESC
            """, "Screen", true, "All notes written on any record."),
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

        // ----------------------------------------------------------- Activities
        new("Activities", "Activity summary (scope, executed, internal vs external)", """
            SELECT ServiceType AS [Service], ActivityCode AS [Code], Name AS [Activity], Unit, ApprovedContractors AS [Contractors],
                   WorkOrders AS [WOs], BoqItems AS [BOQ Items], ScopeTon AS [Scope (MT)], ExecutedTon AS [Executed (MT)],
                   DefaultInternalRate AS [Default Internal Rate], DefaultExternalRate AS [Default External Rate],
                   AvgInternalRate AS [Avg Internal Rate], AvgExternalRate AS [Avg External Rate],
                   InternalAmount AS [Internal Amount], ExternalAmount AS [External Amount], Margin
            FROM dbo.vw_Activity WHERE IsActive = 1 ORDER BY SortOrder
            """, "Service", true, "Every activity with its contractors, work orders, scope, rates and margin."),
        new("Activities", "Contractor – activity matrix", """
            SELECT ContractorCode AS [Agency], ContractorName AS [Contractor], ActivityCode AS [Activity Code], ActivityName AS [Activity],
                   CASE WHEN IsApproved = 1 THEN N'Approved' ELSE N'Not approved' END AS [Status], Capacity,
                   InternalRate AS [Quoted Internal Rate], DefaultInternalRate AS [Default Internal Rate], DefaultExternalRate AS [Default External Rate],
                   WorkOrders AS [WOs], ScopeTon AS [Scope (MT)]
            FROM dbo.vw_ContractorActivity ORDER BY ContractorCode, ActivityCode
            """, "Agency", true, "Which agency is approved for which activity, with rates."),
        new("Activities", "Work order activity rates (internal / external)", """
            SELECT ContractorCode AS [Agency], UniqueNo AS [WO], ActivityCode AS [Activity Code], ActivityName AS [Activity], Unit,
                   InternalRate AS [Internal Rate], ExternalRate AS [External Rate], Margin AS [Margin per unit], MarginPct AS [Margin Pct],
                   DefaultInternalRate AS [Default Internal Rate], DefaultExternalRate AS [Default External Rate],
                   CASE WHEN ContractorApproved = 1 THEN N'Yes' ELSE N'No' END AS [Approved], Remarks
            FROM dbo.vw_RateMaster ORDER BY ContractorCode, UniqueNo, ActivityCode
            """, "Agency", true, "Rate per work order and activity, compared with the activity default."),
        new("Activities", "BOQ items to check (activity / rate / approval)", """
            SELECT ISNULL(AgencyCode, ISNULL(AgencyRef, N'(none)')) AS [Agency], UniqueNo AS [WO], ActivityCode AS [Activity],
                   Description, R1WeightTon AS [R1 (MT)], InternalRate AS [Internal Rate], ExternalRate AS [External Rate],
                   SuggestedInternalRate AS [Suggested Internal Rate], SuggestedExternalRate AS [Suggested External Rate],
                   CASE WHEN ActivityId IS NULL THEN N'No activity'
                        WHEN ActivityApproved = 0 THEN N'Agency not approved for activity'
                        WHEN InternalRate IS NULL OR ExternalRate IS NULL THEN N'Rate missing'
                        WHEN ExternalRate < InternalRate THEN N'External rate below internal rate' END AS [Issue]
            FROM dbo.vw_ContractItem
            WHERE IsGroupHeader = 0 AND (ActivityId IS NULL OR ActivityApproved = 0 OR InternalRate IS NULL OR ExternalRate IS NULL OR ExternalRate < InternalRate)
            ORDER BY 1, SortOrder
            """, "Issue", true, "Items without activity, with a missing rate, a negative margin or an agency not approved for the activity."),

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
