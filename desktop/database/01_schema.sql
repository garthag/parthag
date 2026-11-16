/* =====================================================================
   BSL Project Control – SQL Server schema (idempotent, safe to re-run)
   Target: SQL Server 2016+ / Express / LocalDB
   Batches are separated by "GO" lines; the app runs them one by one.
   ===================================================================== */

/* ---------- Lookups (drop-down lists editable by the user) ---------- */
IF OBJECT_ID(N'dbo.LookupValue', N'U') IS NULL
CREATE TABLE dbo.LookupValue (
    Id          INT IDENTITY(1,1) CONSTRAINT PK_LookupValue PRIMARY KEY,
    Category    NVARCHAR(50)  NOT NULL,
    Value       NVARCHAR(100) NOT NULL,
    SortOrder   INT           NOT NULL CONSTRAINT DF_LookupValue_Sort DEFAULT (0),
    CONSTRAINT UQ_LookupValue UNIQUE (Category, Value)
);
GO

/* ---------- Contractor master (one row per firm) ---------- */
IF OBJECT_ID(N'dbo.Contractor', N'U') IS NULL
CREATE TABLE dbo.Contractor (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_Contractor PRIMARY KEY,
    ContractorCode  NVARCHAR(20)  NOT NULL CONSTRAINT UQ_Contractor_Code UNIQUE,   -- GRE, MDE, SEW, ERC ...
    Name            NVARCHAR(200) NOT NULL,
    ContractorType  NVARCHAR(50)  NOT NULL CONSTRAINT DF_Contractor_Type DEFAULT (N'Executing Agency'),
    ContactPerson   NVARCHAR(100) NULL,
    ContactNo       NVARCHAR(30)  NULL,
    Email           NVARCHAR(150) NULL,
    PAN             NVARCHAR(10)  NULL,
    GSTIN           NVARCHAR(15)  NULL,
    Address         NVARCHAR(400) NULL,
    State           NVARCHAR(50)  NULL,
    BankName        NVARCHAR(100) NULL,
    BankAccountNo   NVARCHAR(30)  NULL,
    IFSC            NVARCHAR(11)  NULL,
    IsActive        BIT           NOT NULL CONSTRAINT DF_Contractor_Active DEFAULT (1),
    Remarks         NVARCHAR(400) NULL,
    CreatedOn       DATETIME2(0)  NOT NULL CONSTRAINT DF_Contractor_Created DEFAULT (SYSDATETIME()),
    ModifiedOn      DATETIME2(0)  NULL
);
GO

/* ---------- Work orders (the old "Contractor_Master" rows: BM01, BM02 ...) ---------- */
IF OBJECT_ID(N'dbo.WorkOrder', N'U') IS NULL
CREATE TABLE dbo.WorkOrder (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_WorkOrder PRIMARY KEY,
    UniqueNo        NVARCHAR(20)  NOT NULL CONSTRAINT UQ_WorkOrder_UniqueNo UNIQUE,
    ContractorId    INT           NOT NULL CONSTRAINT FK_WorkOrder_Contractor REFERENCES dbo.Contractor(Id),
    ServiceType     NVARCHAR(50)  NULL,      -- DISMANTLING / ERECTION / MODIFICATION/FABRICATION / SUPPLY & SERVICE
    WorkCategory    NVARCHAR(100) NULL,      -- BSL_MECH_ERE_EQUIPMENT ...
    WorkOrderNo     NVARCHAR(100) NULL,
    WODate          DATE          NULL,
    WOValue         DECIMAL(18,2) NULL,
    ActualWorkDone  DECIMAL(18,2) NULL,
    ActualPaid      DECIMAL(18,2) NULL,
    Difference      AS (ISNULL(ActualWorkDone,0) - ISNULL(ActualPaid,0)),
    Status          NVARCHAR(30)  NOT NULL CONSTRAINT DF_WorkOrder_Status DEFAULT (N'Open'),
    Remarks         NVARCHAR(400) NULL,
    CreatedOn       DATETIME2(0)  NOT NULL CONSTRAINT DF_WorkOrder_Created DEFAULT (SYSDATETIME()),
    ModifiedOn      DATETIME2(0)  NULL
);
GO

/* ---------- Rate master: internal (paid to agency) vs external (billed to client) ---------- */
IF OBJECT_ID(N'dbo.RateMaster', N'U') IS NULL
CREATE TABLE dbo.RateMaster (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_RateMaster PRIMARY KEY,
    WorkOrderId     INT           NULL CONSTRAINT FK_RateMaster_WorkOrder REFERENCES dbo.WorkOrder(Id),
    ActivityType    NVARCHAR(50)  NULL,
    ItemCode        NVARCHAR(50)  NULL,
    Description     NVARCHAR(300) NOT NULL,
    Unit            NVARCHAR(20)  NULL,
    InternalRate    DECIMAL(18,2) NULL,
    ExternalRate    DECIMAL(18,2) NULL,
    Margin          AS (ExternalRate - InternalRate),                                   -- NULL when a rate is missing
    MarginPct       AS (CASE WHEN ExternalRate > 0 AND InternalRate IS NOT NULL
                             THEN CAST((ExternalRate - InternalRate) * 100.0 / ExternalRate AS DECIMAL(9,2)) END),
    EffectiveFrom   DATE          NULL,
    Remarks         NVARCHAR(400) NULL
);
GO

/* ---------- Package = one BOM sheet / plant area (STOVE, HOT BLAST ...) ---------- */
IF OBJECT_ID(N'dbo.Package', N'U') IS NULL
CREATE TABLE dbo.Package (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_Package PRIMARY KEY,
    PackageCode     NVARCHAR(60)  NOT NULL CONSTRAINT UQ_Package_Code UNIQUE,
    PackageName     NVARCHAR(200) NULL,
    Area            NVARCHAR(100) NULL,
    NoOfSets        INT           NOT NULL CONSTRAINT DF_Package_Sets DEFAULT (1),  -- e.g. 3 stoves, 4 traps
    WorkOrderId     INT           NULL CONSTRAINT FK_Package_WorkOrder REFERENCES dbo.WorkOrder(Id),
    SourceSheet     NVARCHAR(100) NULL,
    Remarks         NVARCHAR(400) NULL
);
GO

/* ---------- Contract BOQ (old "Sheet2") ---------- */
IF OBJECT_ID(N'dbo.ContractItem', N'U') IS NULL
CREATE TABLE dbo.ContractItem (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_ContractItem PRIMARY KEY,
    TechSpecItem    NVARCHAR(30)  NULL,
    GroupName       NVARCHAR(200) NULL,
    IsGroupHeader   BIT           NOT NULL CONSTRAINT DF_ContractItem_Group DEFAULT (0),
    Description     NVARCHAR(400) NOT NULL,
    Unit            NVARCHAR(20)  NULL,
    Qty             DECIMAL(18,3) NULL,
    UnitWeightKg    DECIMAL(18,3) NULL,
    TotalWeightTon  AS (CAST(ISNULL(Qty,0) * ISNULL(UnitWeightKg,0) / 1000.0 AS DECIMAL(18,3))),
    R1WeightTon     DECIMAL(18,3) NULL,
    ExternalRate    DECIMAL(18,2) NULL,        -- "Supply Unit Rate" (client -> MCK)
    InternalRate    DECIMAL(18,2) NULL,        -- "Agency Rate"      (MCK -> agency)
    ExternalAmount  AS (CAST(ISNULL(R1WeightTon,0) * ISNULL(ExternalRate,0) AS DECIMAL(18,2))),
    InternalAmount  AS (CAST(ISNULL(R1WeightTon,0) * ISNULL(InternalRate,0) AS DECIMAL(18,2))),
    Margin          AS (CAST(ISNULL(R1WeightTon,0) * (ExternalRate - InternalRate) AS DECIMAL(18,2))),   -- NULL when a rate is missing
    WorkOrderId     INT           NULL CONSTRAINT FK_ContractItem_WorkOrder REFERENCES dbo.WorkOrder(Id),
    AgencyRef       NVARCHAR(50)  NULL,        -- raw value from sheet when it is not a work order (BSL, SN, NA ...)
    ActivityType    NVARCHAR(50)  NULL,
    DrawingQty      NVARCHAR(50)  NULL,
    Remarks         NVARCHAR(400) NULL,
    SortOrder       INT           NOT NULL CONSTRAINT DF_ContractItem_Sort DEFAULT (0)
);
GO

/* ---------- RA bills (old RA-1 ... RA-42 columns, now rows) ---------- */
IF OBJECT_ID(N'dbo.RaBill', N'U') IS NULL
CREATE TABLE dbo.RaBill (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_RaBill PRIMARY KEY,
    RaNo            INT           NOT NULL,
    WorkOrderId     INT           NULL CONSTRAINT FK_RaBill_WorkOrder REFERENCES dbo.WorkOrder(Id),
    BillDate        DATE          NULL,
    Status          NVARCHAR(30)  NOT NULL CONSTRAINT DF_RaBill_Status DEFAULT (N'Draft'),
    Remarks         NVARCHAR(400) NULL
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_RaBill_No_WO')
    CREATE UNIQUE INDEX UX_RaBill_No_WO ON dbo.RaBill(RaNo, WorkOrderId) WHERE WorkOrderId IS NOT NULL;
GO

IF OBJECT_ID(N'dbo.RaBillLine', N'U') IS NULL
CREATE TABLE dbo.RaBillLine (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_RaBillLine PRIMARY KEY,
    RaBillId        INT           NOT NULL CONSTRAINT FK_RaBillLine_RaBill REFERENCES dbo.RaBill(Id) ON DELETE CASCADE,
    ContractItemId  INT           NOT NULL CONSTRAINT FK_RaBillLine_Item REFERENCES dbo.ContractItem(Id) ON DELETE CASCADE,
    QtyTon          DECIMAL(18,3) NOT NULL,
    Rate            DECIMAL(18,2) NULL,        -- blank = use item internal rate
    Remarks         NVARCHAR(400) NULL,
    CONSTRAINT UQ_RaBillLine UNIQUE (RaBillId, ContractItemId)
);
GO

/* ---------- Drawing master (one row per drawing number) ---------- */
IF OBJECT_ID(N'dbo.DrawingMaster', N'U') IS NULL
CREATE TABLE dbo.DrawingMaster (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_DrawingMaster PRIMARY KEY,
    DrawingNo       NVARCHAR(100) NOT NULL CONSTRAINT UQ_DrawingMaster_No UNIQUE,
    Title           NVARCHAR(300) NULL,
    PackageId       INT           NULL CONSTRAINT FK_Drawing_Package REFERENCES dbo.Package(Id),
    Area            NVARCHAR(100) NULL,
    SubArea         NVARCHAR(150) NULL,
    OriginatorId    INT           NULL CONSTRAINT FK_Drawing_Originator REFERENCES dbo.Contractor(Id),  -- ERC / SMS / MCK
    Discipline      NVARCHAR(50)  NULL,
    DrawingType     NVARCHAR(50)  NULL,
    TotalSheets     INT           NULL,
    CurrentRevision NVARCHAR(10)  NULL,
    Status          NVARCHAR(30)  NOT NULL CONSTRAINT DF_Drawing_Status DEFAULT (N'IFC'),
    ContractItemId  INT           NULL CONSTRAINT FK_Drawing_ContractItem REFERENCES dbo.ContractItem(Id),
    AgencyWorkOrderId INT         NULL CONSTRAINT FK_Drawing_WorkOrder REFERENCES dbo.WorkOrder(Id),
    EstQty          NVARCHAR(30)  NULL,
    EstWeightKg     DECIMAL(18,3) NULL,
    Remarks         NVARCHAR(400) NULL
);
GO

/* ---------- Drawing register (sheet / revision / transmittal log) ---------- */
IF OBJECT_ID(N'dbo.DrawingRegister', N'U') IS NULL
CREATE TABLE dbo.DrawingRegister (
    Id              INT IDENTITY(1,1) CONSTRAINT PK_DrawingRegister PRIMARY KEY,
    DrawingId       INT           NOT NULL CONSTRAINT FK_DrgReg_Drawing REFERENCES dbo.DrawingMaster(Id) ON DELETE CASCADE,
    SheetNo         NVARCHAR(20)  NULL,
    Revision        NVARCHAR(10)  NULL,
    RevisionDate    DATE          NULL,
    TransmittalNo   NVARCHAR(100) NULL,
    ReceivedDate    DATE          NULL,
    Purpose         NVARCHAR(30)  NULL,      -- IFC / IFA / IFI / As-Built
    IssuedToId      INT           NULL CONSTRAINT FK_DrgReg_Contractor REFERENCES dbo.Contractor(Id),
    IssuedDate      DATE          NULL,
    Status          NVARCHAR(20)  NOT NULL CONSTRAINT DF_DrgReg_Status DEFAULT (N'Current'),
    SourceRef       NVARCHAR(150) NULL,      -- drawing text exactly as found in the BOM sheet
    Remarks         NVARCHAR(400) NULL
);
GO

/* ---------- Import batches ---------- */
IF OBJECT_ID(N'dbo.ImportBatch', N'U') IS NULL
CREATE TABLE dbo.ImportBatch (
    Id          INT IDENTITY(1,1) CONSTRAINT PK_ImportBatch PRIMARY KEY,
    ImportType  NVARCHAR(50)  NOT NULL,
    FileName    NVARCHAR(260) NOT NULL,
    ImportedOn  DATETIME2(0)  NOT NULL CONSTRAINT DF_ImportBatch_On DEFAULT (SYSDATETIME()),
    ImportedBy  NVARCHAR(128) NOT NULL CONSTRAINT DF_ImportBatch_By DEFAULT (SUSER_SNAME()),
    RowsRead    INT           NOT NULL CONSTRAINT DF_ImportBatch_Rows DEFAULT (0),
    Notes       NVARCHAR(MAX) NULL
);
GO

/* ---------- BOM line items (all 21 sheets in one standard structure) ---------- */
IF OBJECT_ID(N'dbo.BomItem', N'U') IS NULL
CREATE TABLE dbo.BomItem (
    Id                 INT IDENTITY(1,1) CONSTRAINT PK_BomItem PRIMARY KEY,
    PackageId          INT           NOT NULL CONSTRAINT FK_BomItem_Package REFERENCES dbo.Package(Id),
    DrawingId          INT           NULL CONSTRAINT FK_BomItem_Drawing REFERENCES dbo.DrawingMaster(Id),
    SheetNo            NVARCHAR(20)  NULL,
    Revision           NVARCHAR(10)  NULL,
    DrawingDescription NVARCHAR(300) NULL,
    ItemDescription    NVARCHAR(300) NULL,
    AssemblyMark       NVARCHAR(100) NULL,
    PartMark           NVARCHAR(100) NULL,
    ItemNo             NVARCHAR(50)  NULL,
    PartQty            DECIMAL(18,3) NULL,
    SectionDrg         NVARCHAR(150) NULL,      -- section exactly as written on drawing
    SectionStd         NVARCHAR(100) NULL,      -- standardised section (PL-16, ISMC200 ...)
    LengthMm           DECIMAL(18,3) NULL,
    WidthMm            DECIMAL(18,3) NULL,
    ThicknessMm        DECIMAL(18,3) NULL,
    Qty                DECIMAL(18,3) NULL,
    QtyText            NVARCHAR(30)  NULL,      -- original text when not numeric (e.g. "9/12")
    SourceUnitWt       DECIMAL(18,4) NULL,      -- unit column as found (kg/pc, kg/m, density ...)
    UnitWeightKg       DECIMAL(18,4) NULL,      -- standard: kg per piece = Total / Qty
    TotalWeightKg      DECIMAL(18,3) NULL,
    MaterialGrade      NVARCHAR(100) NULL,
    Remarks            NVARCHAR(400) NULL,
    SectionChange      NVARCHAR(100) NULL,
    DimensionStd       NVARCHAR(100) NULL,
    RowType            NVARCHAR(20)  NOT NULL CONSTRAINT DF_BomItem_RowType DEFAULT (N'PART'),  -- PART / ASSEMBLY
    IsExcluded         BIT           NOT NULL CONSTRAINT DF_BomItem_Excluded DEFAULT (0),      -- not counted in totals
    QualityFlags       NVARCHAR(400) NULL,
    SourceSheet        NVARCHAR(100) NULL,
    SourceRow          INT           NULL,
    ImportBatchId      INT           NULL CONSTRAINT FK_BomItem_Batch REFERENCES dbo.ImportBatch(Id)
);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BomItem_Package')
    CREATE INDEX IX_BomItem_Package ON dbo.BomItem(PackageId) INCLUDE (TotalWeightKg, IsExcluded);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BomItem_Drawing')
    CREATE INDEX IX_BomItem_Drawing ON dbo.BomItem(DrawingId) INCLUDE (TotalWeightKg, IsExcluded);
GO

/* ---------- User defined fields (any entity) ---------- */
IF OBJECT_ID(N'dbo.UdfDefinition', N'U') IS NULL
CREATE TABLE dbo.UdfDefinition (
    Id            INT IDENTITY(1,1) CONSTRAINT PK_UdfDefinition PRIMARY KEY,
    EntityName    NVARCHAR(50)   NOT NULL,
    FieldKey      NVARCHAR(50)   NOT NULL,       -- column name used in formulas: [U_Margin]
    Caption       NVARCHAR(100)  NOT NULL,
    DataType      NVARCHAR(20)   NOT NULL,       -- Text / Number / Date / YesNo / List / Formula
    ListValues    NVARCHAR(1000) NULL,           -- for List: values separated by ;
    Formula       NVARCHAR(2000) NULL,           -- for Formula: expression, e.g. ([ExternalRate]-[InternalRate])*[R1WeightTon]
    DecimalPlaces INT            NOT NULL CONSTRAINT DF_Udf_Dec DEFAULT (2),
    IsRequired    BIT            NOT NULL CONSTRAINT DF_Udf_Req DEFAULT (0),
    ShowInGrid    BIT            NOT NULL CONSTRAINT DF_Udf_Grid DEFAULT (1),
    SortOrder     INT            NOT NULL CONSTRAINT DF_Udf_Sort DEFAULT (0),
    IsActive      BIT            NOT NULL CONSTRAINT DF_Udf_Active DEFAULT (1),
    CONSTRAINT UQ_UdfDefinition UNIQUE (EntityName, FieldKey)
);
GO

IF OBJECT_ID(N'dbo.UdfValue', N'U') IS NULL
CREATE TABLE dbo.UdfValue (
    Id         INT IDENTITY(1,1) CONSTRAINT PK_UdfValue PRIMARY KEY,
    UdfId      INT            NOT NULL CONSTRAINT FK_UdfValue_Def REFERENCES dbo.UdfDefinition(Id) ON DELETE CASCADE,
    RecordId   INT            NOT NULL,
    ValueText  NVARCHAR(2000) NULL,
    CONSTRAINT UQ_UdfValue UNIQUE (UdfId, RecordId)
);
GO

/* ---------- Audit trail ---------- */
IF OBJECT_ID(N'dbo.AuditLog', N'U') IS NULL
CREATE TABLE dbo.AuditLog (
    Id          BIGINT IDENTITY(1,1) CONSTRAINT PK_AuditLog PRIMARY KEY,
    EntityName  NVARCHAR(50)  NOT NULL,
    RecordId    INT           NULL,
    Action      NVARCHAR(20)  NOT NULL,
    Detail      NVARCHAR(MAX) NULL,
    ChangedOn   DATETIME2(0)  NOT NULL CONSTRAINT DF_Audit_On DEFAULT (SYSDATETIME()),
    ChangedBy   NVARCHAR(128) NOT NULL CONSTRAINT DF_Audit_By DEFAULT (SUSER_SNAME())
);
GO

/* =====================================================================
   ACTIVITIES – what work is done (dismantling, erection, fabrication …),
   linked to contractors, work orders, rates, BOQ items, packages, drawings
   ===================================================================== */
IF OBJECT_ID(N'dbo.Activity', N'U') IS NULL
CREATE TABLE dbo.Activity (
    Id                  INT IDENTITY(1,1) CONSTRAINT PK_Activity PRIMARY KEY,
    ActivityCode        NVARCHAR(20)  NOT NULL CONSTRAINT UQ_Activity_Code UNIQUE,   -- DIS-EQP, ERE-EQP ...
    Name                NVARCHAR(200) NOT NULL,
    ServiceType         NVARCHAR(50)  NULL,      -- DISMANTLING / ERECTION / MODIFICATION/FABRICATION / SUPPLY & SERVICE
    WorkCategory        NVARCHAR(100) NULL,      -- matching work-order category (BSL_MECH_ERE_EQUIPMENT ...)
    Unit                NVARCHAR(20)  NOT NULL CONSTRAINT DF_Activity_Unit DEFAULT (N'MT'),
    DefaultInternalRate DECIMAL(18,2) NULL,      -- paid to agency
    DefaultExternalRate DECIMAL(18,2) NULL,      -- billed to client
    DefaultMargin       AS (DefaultExternalRate - DefaultInternalRate),
    Description         NVARCHAR(400) NULL,
    IsActive            BIT           NOT NULL CONSTRAINT DF_Activity_Active DEFAULT (1),
    SortOrder           INT           NOT NULL CONSTRAINT DF_Activity_Sort DEFAULT (0)
);
GO

/* Contractor ↔ activity: which agency is approved / capable for which activity */
IF OBJECT_ID(N'dbo.ContractorActivity', N'U') IS NULL
CREATE TABLE dbo.ContractorActivity (
    Id            INT IDENTITY(1,1) CONSTRAINT PK_ContractorActivity PRIMARY KEY,
    ContractorId  INT           NOT NULL CONSTRAINT FK_ConAct_Contractor REFERENCES dbo.Contractor(Id) ON DELETE CASCADE,
    ActivityId    INT           NOT NULL CONSTRAINT FK_ConAct_Activity REFERENCES dbo.Activity(Id) ON DELETE CASCADE,
    IsApproved    BIT           NOT NULL CONSTRAINT DF_ConAct_Approved DEFAULT (1),
    InternalRate  DECIMAL(18,2) NULL,        -- contractor's quoted rate for this activity (optional)
    Capacity      NVARCHAR(100) NULL,        -- e.g. "300 MT / month", "2 cranes"
    Remarks       NVARCHAR(400) NULL,
    CONSTRAINT UQ_ContractorActivity UNIQUE (ContractorId, ActivityId)
);
GO

/* activity link on existing tables (safe on an existing database) */
IF COL_LENGTH(N'dbo.WorkOrder', N'ActivityId') IS NULL
    ALTER TABLE dbo.WorkOrder ADD ActivityId INT NULL CONSTRAINT FK_WorkOrder_Activity REFERENCES dbo.Activity(Id);
GO
IF COL_LENGTH(N'dbo.RateMaster', N'ActivityId') IS NULL
    ALTER TABLE dbo.RateMaster ADD ActivityId INT NULL CONSTRAINT FK_RateMaster_Activity REFERENCES dbo.Activity(Id);
GO
IF COL_LENGTH(N'dbo.ContractItem', N'ActivityId') IS NULL
    ALTER TABLE dbo.ContractItem ADD ActivityId INT NULL CONSTRAINT FK_ContractItem_Activity REFERENCES dbo.Activity(Id);
GO
IF COL_LENGTH(N'dbo.Package', N'ActivityId') IS NULL
    ALTER TABLE dbo.Package ADD ActivityId INT NULL CONSTRAINT FK_Package_Activity REFERENCES dbo.Activity(Id);
GO
IF COL_LENGTH(N'dbo.DrawingMaster', N'ActivityId') IS NULL
    ALTER TABLE dbo.DrawingMaster ADD ActivityId INT NULL CONSTRAINT FK_Drawing_Activity REFERENCES dbo.Activity(Id);
GO

/* ---------- migration: margins are NULL (not misleading) when one of the two rates is missing ---------- */
IF EXISTS (SELECT 1 FROM sys.computed_columns WHERE object_id = OBJECT_ID(N'dbo.RateMaster') AND name = N'Margin' AND definition LIKE N'%isnull%')
BEGIN
    ALTER TABLE dbo.RateMaster DROP COLUMN MarginPct, Margin;
    ALTER TABLE dbo.RateMaster ADD Margin AS (ExternalRate - InternalRate),
        MarginPct AS (CASE WHEN ExternalRate > 0 AND InternalRate IS NOT NULL
                           THEN CAST((ExternalRate - InternalRate) * 100.0 / ExternalRate AS DECIMAL(9,2)) END);
END
GO
IF EXISTS (SELECT 1 FROM sys.computed_columns WHERE object_id = OBJECT_ID(N'dbo.ContractItem') AND name = N'Margin' AND definition LIKE N'%isnull(\[InternalRate\]%' ESCAPE N'\')
BEGIN
    ALTER TABLE dbo.ContractItem DROP COLUMN Margin;
    ALTER TABLE dbo.ContractItem ADD Margin AS (CAST(ISNULL(R1WeightTon,0) * (ExternalRate - InternalRate) AS DECIMAL(18,2)));
END
GO
IF EXISTS (SELECT 1 FROM sys.computed_columns WHERE object_id = OBJECT_ID(N'dbo.Activity') AND name = N'DefaultMargin' AND definition LIKE N'%isnull%')
BEGIN
    ALTER TABLE dbo.Activity DROP COLUMN DefaultMargin;
    ALTER TABLE dbo.Activity ADD DefaultMargin AS (DefaultExternalRate - DefaultInternalRate);
END
GO
