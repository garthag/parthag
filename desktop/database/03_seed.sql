/* =====================================================================
   BSL Project Control – seed data (lookups, standard parties, example
   user defined fields). Idempotent: rows are only inserted if missing.
   ===================================================================== */

MERGE dbo.LookupValue AS t
USING (VALUES
    (N'ContractorType', N'Executing Agency', 1), (N'ContractorType', N'Design Consultant', 2),
    (N'ContractorType', N'Owner / Client', 3),   (N'ContractorType', N'Main Contractor', 4),
    (N'ContractorType', N'OEM / Supplier', 5),
    (N'ServiceType', N'DISMANTLING', 1), (N'ServiceType', N'ERECTION', 2),
    (N'ServiceType', N'MODIFICATION/FABRICATION', 3), (N'ServiceType', N'SUPPLY & SERVICE', 4),
    (N'WorkCategory', N'BSL_MECH_DIS_EQUIPMENT', 1), (N'WorkCategory', N'BSL_MECH_ERE_EQUIPMENT', 2),
    (N'WorkCategory', N'BSL_MECH_ERE_UTILITY', 3),   (N'WorkCategory', N'BSL_MECH_ERE_HYDRAULICS', 4),
    (N'WorkCategory', N'BSL_MECH_SUP&SERVICE', 5),   (N'WorkCategory', N'BSL_MECH_MOD_FABRICATION', 6),
    (N'Unit', N'nr', 1), (N'Unit', N'set', 2), (N'Unit', N'm', 3), (N'Unit', N'MT', 4), (N'Unit', N'kg', 5), (N'Unit', N'LS', 6),
    (N'WOStatus', N'Open', 1), (N'WOStatus', N'Closed', 2), (N'WOStatus', N'Short Closed', 3), (N'WOStatus', N'On Hold', 4),
    (N'RaStatus', N'Draft', 1), (N'RaStatus', N'Submitted', 2), (N'RaStatus', N'Certified', 3), (N'RaStatus', N'Paid', 4),
    (N'DrawingStatus', N'IFC', 1), (N'DrawingStatus', N'IFA', 2), (N'DrawingStatus', N'IFI', 3),
    (N'DrawingStatus', N'As-Built', 4), (N'DrawingStatus', N'Superseded', 5), (N'DrawingStatus', N'Scope (no drg no.)', 6),
    (N'RegisterStatus', N'Current', 1), (N'RegisterStatus', N'Superseded', 2), (N'RegisterStatus', N'Void', 3),
    (N'Purpose', N'IFC', 1), (N'Purpose', N'IFA', 2), (N'Purpose', N'IFI', 3), (N'Purpose', N'As-Built', 4),
    (N'Discipline', N'Structural', 1), (N'Discipline', N'Mechanical', 2), (N'Discipline', N'Piping', 3),
    (N'Discipline', N'Refractory', 4), (N'Discipline', N'Electrical', 5),
    (N'DrawingType', N'GA', 1), (N'DrawingType', N'Fabrication', 2), (N'DrawingType', N'Erection / Marking Plan', 3),
    (N'DrawingType', N'Detail', 4), (N'DrawingType', N'Scope item', 5),
    (N'RowType', N'PART', 1), (N'RowType', N'ASSEMBLY', 2),
    (N'NoteCategory', N'GENERAL', 1), (N'NoteCategory', N'INSTRUCTION', 2), (N'NoteCategory', N'HINDRANCE', 3),
    (N'NoteCategory', N'QUERY', 4), (N'NoteCategory', N'DECISION', 5), (N'NoteCategory', N'FOLLOW-UP', 6),
    (N'DocType', N'DRAWING', 1), (N'DocType', N'TRANSMITTAL', 2), (N'DocType', N'WORK ORDER', 3), (N'DocType', N'RA BILL', 4),
    (N'DocType', N'PHOTO', 5), (N'DocType', N'LETTER', 6), (N'DocType', N'DOCUMENT', 7)
) AS s (Category, Value, SortOrder)
ON t.Category = s.Category AND t.Value = s.Value
WHEN NOT MATCHED THEN INSERT (Category, Value, SortOrder) VALUES (s.Category, s.Value, s.SortOrder);
GO

/* Standard parties found in the drawing numbers and the contractor sheet */
MERGE dbo.Contractor AS t
USING (VALUES
    (N'BSL', N'Bokaro Steel Limited (SAIL)', N'Owner / Client'),
    (N'ERC', N'ERC (Design Consultant)',       N'Design Consultant'),
    (N'SMS', N'SMS group (OEM Designer)',      N'OEM / Supplier')
) AS s (ContractorCode, Name, ContractorType)
ON t.ContractorCode = s.ContractorCode
WHEN NOT MATCHED THEN INSERT (ContractorCode, Name, ContractorType) VALUES (s.ContractorCode, s.Name, s.ContractorType);
GO

/* Example user defined fields – shows Text/List/Date and Formula fields that
   use contractor detail and internal/external rates. Users can edit or delete. */
MERGE dbo.UdfDefinition AS t
USING (VALUES
    (N'ContractItem', N'U_MarginPct',  N'Margin %',               N'Formula', NULL,
        N'IIF([ExternalRate] > 0, ([ExternalRate] - [InternalRate]) * 100 / [ExternalRate], 0)', 2, 10),
    (N'ContractItem', N'U_BalanceAmt', N'Balance Amount (Int.)',  N'Formula', NULL,
        N'ISNULL([BalanceTon],0) * ISNULL([InternalRate],0)', 2, 20),
    (N'ContractItem', N'U_AgencyTag',  N'Agency / WO tag',        N'Formula', NULL,
        N'ISNULL([AgencyCode],''-'') + '' / '' + ISNULL([WorkOrderNo],''No WO'')', 0, 30),
    (N'WorkOrder',    N'U_BalanceWO',  N'Balance WO Value',       N'Formula', NULL,
        N'ISNULL([WOValue],0) - ISNULL([ExecutedInternalAmount],0)', 2, 10),
    (N'WorkOrder',    N'U_SiteInCharge', N'Site In-charge',       N'Text', NULL, NULL, 0, 20),
    (N'BomItem',      N'U_PlateWtKg',  N'Calc. plate wt (kg)',    N'Formula', NULL,
        N'IIF([ThicknessMm] > 0 AND [WidthMm] > 0, [LengthMm] * [WidthMm] * [ThicknessMm] * ISNULL([Qty],1) * 7.85 / 1000000, 0)', 2, 10),
    (N'BomItem',      N'U_WtDiffKg',   N'Wt difference (kg)',     N'Formula', NULL,
        N'IIF([U_PlateWtKg] > 0, ISNULL([TotalWeightKg],0) - [U_PlateWtKg], 0)', 2, 20),
    (N'BomItem',      N'U_FabStatus',  N'Fabrication status',     N'List', N'Not started;Cutting;Fit-up;Welding;Painting;Dispatched;Erected', NULL, 0, 30),
    (N'DrawingMaster',N'U_ReceivedFromClient', N'Received from client', N'Date', NULL, NULL, 0, 10)
) AS s (EntityName, FieldKey, Caption, DataType, ListValues, Formula, DecimalPlaces, SortOrder)
ON t.EntityName = s.EntityName AND t.FieldKey = s.FieldKey
WHEN NOT MATCHED THEN INSERT (EntityName, FieldKey, Caption, DataType, ListValues, Formula, DecimalPlaces, SortOrder)
     VALUES (s.EntityName, s.FieldKey, s.Caption, s.DataType, s.ListValues, s.Formula, s.DecimalPlaces, s.SortOrder);
GO

/* Standard activities. The first six match the work-order categories found in the contractor workbook;
   default rates are left blank and filled from the BOQ by usp_LinkActivities (weighted average). */
MERGE dbo.Activity AS t
USING (VALUES
    (N'DIS-EQP',  N'Dismantling of mechanical equipment & structures', N'DISMANTLING',              N'BSL_MECH_DIS_EQUIPMENT',   N'MT', 10),
    (N'ERE-EQP',  N'Erection of mechanical equipment',                 N'ERECTION',                 N'BSL_MECH_ERE_EQUIPMENT',   N'MT', 20),
    (N'ERE-HYD',  N'Erection of hydraulics',                           N'ERECTION',                 N'BSL_MECH_ERE_HYDRAULICS',  N'MT', 30),
    (N'ERE-UTL',  N'Erection of utility piping',                       N'ERECTION',                 N'BSL_MECH_ERE_UTILITY',     N'MT', 40),
    (N'MOD-FAB',  N'Modification / fabrication',                       N'MODIFICATION/FABRICATION', N'BSL_MECH_MOD_FABRICATION', N'MT', 50),
    (N'SUP-SRV',  N'Supply & service',                                 N'SUPPLY & SERVICE',         N'BSL_MECH_SUP&SERVICE',     N'LS', 60),
    (N'FAB-STR',  N'Fabrication of structural steel',                  N'MODIFICATION/FABRICATION', NULL,                        N'MT', 70),
    (N'ERE-STR',  N'Erection of structural steel',                     N'ERECTION',                 NULL,                        N'MT', 80),
    (N'ERE-DUCT', N'Fabrication & erection of ducts / gas mains',      N'ERECTION',                 NULL,                        N'MT', 90)
) AS s (ActivityCode, Name, ServiceType, WorkCategory, Unit, SortOrder)
ON t.ActivityCode = s.ActivityCode
WHEN NOT MATCHED THEN INSERT (ActivityCode, Name, ServiceType, WorkCategory, Unit, SortOrder)
     VALUES (s.ActivityCode, s.Name, s.ServiceType, s.WorkCategory, s.Unit, s.SortOrder);
GO

/* house style: drop-down values in capitals (collation is case-insensitive, so no duplicates arise) */
UPDATE dbo.LookupValue SET Value = UPPER(Value) WHERE Value COLLATE Latin1_General_CS_AS <> UPPER(Value);
GO
