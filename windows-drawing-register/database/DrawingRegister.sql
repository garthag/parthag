-- Drawing Register schema (SQL Server 2016+ / Express / LocalDB).
-- The app runs this script on start-up; every statement is safe to re-run.
-- Batches are separated by GO.

IF OBJECT_ID('dbo.DrawingType') IS NULL
CREATE TABLE dbo.DrawingType (
    Code        VARCHAR(4)    NOT NULL CONSTRAINT PK_DrawingType PRIMARY KEY,
    Name        NVARCHAR(60)  NOT NULL,
    SortOrder   INT           NOT NULL,
    Colour      VARCHAR(9)    NOT NULL
);
GO

IF OBJECT_ID('dbo.ImportBatch') IS NULL
CREATE TABLE dbo.ImportBatch (
    Id            INT IDENTITY CONSTRAINT PK_ImportBatch PRIMARY KEY,
    Source        NVARCHAR(1000) NOT NULL,
    DefaultPlant  NVARCHAR(150)  NOT NULL DEFAULT N'',
    ImportedBy    NVARCHAR(128)  NOT NULL DEFAULT SUSER_SNAME(),
    StartedOn     DATETIME2(0)   NOT NULL DEFAULT SYSDATETIME(),
    Added         INT NULL,
    Duplicates    INT NULL,
    Errors        INT NULL
);
GO

IF OBJECT_ID('dbo.Drawing') IS NULL
CREATE TABLE dbo.Drawing (
    Id          INT IDENTITY CONSTRAINT PK_Drawing PRIMARY KEY,
    Plant       NVARCHAR(150) NOT NULL DEFAULT N'',
    Unit        NVARCHAR(150) NOT NULL DEFAULT N'',
    TypeCode    VARCHAR(4)    NOT NULL CONSTRAINT FK_Drawing_Type REFERENCES dbo.DrawingType(Code),
    DrawingNo   NVARCHAR(150) NOT NULL,
    Title       NVARCHAR(400) NOT NULL DEFAULT N'',
    Kind        NVARCHAR(100) NOT NULL DEFAULT N'',
    Revision    NVARCHAR(10)  NOT NULL DEFAULT N'0',
    -- Lettered revisions (A, B …) rank below numbered ones (0, 1 …); set by the app.
    RevRank     INT           NOT NULL DEFAULT 0,
    Status      VARCHAR(4)    NOT NULL DEFAULT 'IFC'
                CONSTRAINT CK_Drawing_Status CHECK (Status IN ('IFR','IFA','IFC','AB','FI')),
    FileName    NVARCHAR(260) NOT NULL,
    StoredPath  NVARCHAR(600) NOT NULL DEFAULT N'',
    SourcePath  NVARCHAR(600) NOT NULL DEFAULT N'',
    SizeBytes   BIGINT        NOT NULL DEFAULT 0,
    Remarks     NVARCHAR(1000) NOT NULL DEFAULT N'',
    BatchId     INT NULL CONSTRAINT FK_Drawing_Batch REFERENCES dbo.ImportBatch(Id),
    ImportedOn  DATETIME2(0)  NOT NULL DEFAULT SYSDATETIME(),
    ModifiedOn  DATETIME2(0)  NOT NULL DEFAULT SYSDATETIME(),
    ModifiedBy  NVARCHAR(128) NOT NULL DEFAULT SUSER_SNAME()
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Drawing_Register')
CREATE INDEX IX_Drawing_Register ON dbo.Drawing (Plant, Unit, TypeCode, DrawingNo, RevRank DESC);
GO

-- Drawing types in register order.
MERGE dbo.DrawingType AS t
USING (VALUES
    ('GN', N'General / Plot Plan', 1, '#607D8B'),
    ('PR', N'Process', 2, '#00897B'),
    ('CV', N'Civil', 3, '#8D6E63'),
    ('ST', N'Structural', 4, '#5E35B1'),
    ('AR', N'Architectural', 5, '#D81B60'),
    ('ME', N'Mechanical / Equipment', 6, '#3949AB'),
    ('PP', N'Piping', 7, '#1E88E5'),
    ('EL', N'Electrical', 8, '#F9A825'),
    ('IN', N'Instrumentation & Control', 9, '#43A047'),
    ('HV', N'HVAC', 10, '#00ACC1'),
    ('FP', N'Fire Protection', 11, '#E53935'),
    ('PL', N'Plumbing / Utilities', 12, '#6D4C41'),
    ('OT', N'Other / Unclassified', 13, '#9E9E9E')
) AS s (Code, Name, SortOrder, Colour)
ON t.Code = s.Code
WHEN MATCHED THEN UPDATE SET Name = s.Name, SortOrder = s.SortOrder, Colour = s.Colour
WHEN NOT MATCHED THEN INSERT (Code, Name, SortOrder, Colour) VALUES (s.Code, s.Name, s.SortOrder, s.Colour);
GO

-- Every revision with IsCurrent = 1 on the latest one of each drawing.
-- Handy for Excel / Power BI / SSRS reports straight from the database.
CREATE OR ALTER VIEW dbo.vw_DrawingRegister AS
SELECT
    d.Id, d.Plant, d.Unit, d.TypeCode, t.Name AS TypeName, t.SortOrder AS TypeOrder,
    d.DrawingNo, d.Title, d.Kind, d.Revision, d.RevRank, d.Status,
    CASE WHEN ROW_NUMBER() OVER (PARTITION BY d.Plant, d.Unit, d.DrawingNo
                                 ORDER BY d.RevRank DESC, d.ImportedOn DESC, d.Id DESC) = 1
         THEN 1 ELSE 0 END AS IsCurrent,
    d.FileName, d.StoredPath, d.SourcePath, d.SizeBytes, d.Remarks, d.ImportedOn, d.ModifiedOn, d.ModifiedBy, d.BatchId
FROM dbo.Drawing d
JOIN dbo.DrawingType t ON t.Code = d.TypeCode;
GO

-- Count of current drawings per plant / unit / type.
CREATE OR ALTER VIEW dbo.vw_DrawingSummary AS
SELECT Plant, Unit, TypeCode, TypeName, TypeOrder, COUNT(*) AS Drawings
FROM dbo.vw_DrawingRegister
WHERE IsCurrent = 1
GROUP BY Plant, Unit, TypeCode, TypeName, TypeOrder;
GO
