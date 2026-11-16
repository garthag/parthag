/* =====================================================================
   EDMS Smart Folder - SQL Server schema
   Holds the Wrench EDMS Smart Folder tree and the documents in each
   folder, so the WPF app can show them in order (and offline).

   The WPF app and the sync console run this script automatically
   (it is idempotent). You can also run it yourself in SSMS after
   creating / selecting a database, e.g.:
       CREATE DATABASE EddmsSmartFolder;
       USE EddmsSmartFolder;
   ===================================================================== */

/* ---------- Tables ---------- */

IF OBJECT_ID(N'dbo.SmartFolder', N'U') IS NULL
CREATE TABLE dbo.SmartFolder
(
    FolderId          BIGINT          NOT NULL CONSTRAINT PK_SmartFolder PRIMARY KEY,
    ParentFolderId    BIGINT          NULL,
    FolderName        NVARCHAR(400)   NOT NULL,
    FolderType        INT             NOT NULL,
    FolderCriteriaId  INT             NOT NULL,
    FolderSubType     INT             NOT NULL,
    HasChild          BIT             NOT NULL,
    IsPublished       BIT             NOT NULL,
    Depth             INT             NOT NULL,
    SortOrder         INT             NOT NULL,   -- position among siblings, as shown on the site
    FullPath          NVARCHAR(2000)  NOT NULL,   -- e.g. "4906-11-Vendor Documents & Drawings / Package 1 / 01-Civil"
    DocumentCount     INT             NOT NULL CONSTRAINT DF_SmartFolder_DocumentCount DEFAULT (0),
    IsActive          BIT             NOT NULL CONSTRAINT DF_SmartFolder_IsActive DEFAULT (1),
    LastSyncedOn      DATETIME2(0)    NOT NULL
);
GO

IF OBJECT_ID(N'dbo.Document', N'U') IS NULL
CREATE TABLE dbo.Document
(
    DocId                             BIGINT          NOT NULL CONSTRAINT PK_Document PRIMARY KEY,
    DocNumber                         NVARCHAR(400)   NULL,
    VendorDocNumber                   NVARCHAR(400)   NULL,
    DocDescription                    NVARCHAR(MAX)   NULL,
    RevisionNumber                    NVARCHAR(50)    NULL,
    InternalRevisionNumber            INT             NULL,
    VersionNumber                     INT             NULL,
    SheetNumber                       NVARCHAR(50)    NULL,
    DocumentType                      NVARCHAR(200)   NULL,
    Discipline                        NVARCHAR(200)   NULL,
    Category                          NVARCHAR(200)   NULL,
    Package                           NVARCHAR(200)   NULL,
    PackageVendorName                 NVARCHAR(400)   NULL,
    DeptDwgCategory                   NVARCHAR(200)   NULL,
    LifeCycleStatus                   NVARCHAR(200)   NULL,
    InternalApprovalStatusCode        NVARCHAR(100)   NULL,
    InternalApprovalStatusDescription NVARCHAR(400)   NULL,
    CurrentStage                      NVARCHAR(400)   NULL,
    CurrentStageUser                  NVARCHAR(400)   NULL,
    DocStatus                         NVARCHAR(200)   NULL,
    WorkflowTeam                      NVARCHAR(200)   NULL,
    FileName                          NVARCHAR(500)   NULL,
    HasAttachment                     BIT             NOT NULL CONSTRAINT DF_Document_HasAttachment DEFAULT (0),
    Billable                          NVARCHAR(100)   NULL,
    ProjectNumber                     NVARCHAR(100)   NULL,
    ScheduledSubmissionDate           DATETIME2(0)    NULL,
    VendorSubmissionDate              DATETIME2(0)    NULL,
    RespondedDate                     DATETIME2(0)    NULL,
    CreatedOn                         DATETIME2(0)    NULL,
    LastEditedOn                      DATETIME2(0)    NULL,
    LatestTransmittalReleasedOn       DATETIME2(0)    NULL,
    FinalReleasedOn                   DATETIME2(0)    NULL,
    RawJson                           NVARCHAR(MAX)   NULL,   -- every field the site returned, untouched
    FirstSeenOn                       DATETIME2(0)    NOT NULL,
    LastSyncedOn                      DATETIME2(0)    NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Document_DocNumber' AND object_id = OBJECT_ID(N'dbo.Document'))
    CREATE INDEX IX_Document_DocNumber ON dbo.Document (DocNumber, SheetNumber, RevisionNumber);
GO

/* A document can show up in more than one Smart Folder (folders are search criteria). */
IF OBJECT_ID(N'dbo.FolderDocument', N'U') IS NULL
CREATE TABLE dbo.FolderDocument
(
    FolderId  BIGINT NOT NULL,
    DocId     BIGINT NOT NULL,
    RowOrder  INT    NOT NULL,   -- order the site listed it in
    CONSTRAINT PK_FolderDocument PRIMARY KEY (FolderId, DocId),
    CONSTRAINT FK_FolderDocument_Folder   FOREIGN KEY (FolderId) REFERENCES dbo.SmartFolder (FolderId),
    CONSTRAINT FK_FolderDocument_Document FOREIGN KEY (DocId)    REFERENCES dbo.Document (DocId)
);
GO

/* One row per drawing revision ever seen. The site only serves the latest
   revision to this account, so the app records each revision the first time it
   appears and keeps its downloaded file -> this is the revision history. */
IF OBJECT_ID(N'dbo.DocumentRevision', N'U') IS NULL
CREATE TABLE dbo.DocumentRevision
(
    DocId                             BIGINT          NOT NULL,
    InternalRevisionNumber            INT             NOT NULL,
    VersionNumber                     INT             NOT NULL,
    RevisionNumber                    NVARCHAR(50)    NULL,
    DocNumber                         NVARCHAR(400)   NULL,
    SheetNumber                       NVARCHAR(50)    NULL,
    FileName                          NVARCHAR(500)   NULL,
    LifeCycleStatus                   NVARCHAR(200)   NULL,
    DocStatus                         NVARCHAR(200)   NULL,
    InternalApprovalStatusCode        NVARCHAR(100)   NULL,
    InternalApprovalStatusDescription NVARCHAR(400)   NULL,
    CurrentStage                      NVARCHAR(400)   NULL,
    VendorSubmissionDate              DATETIME2(0)    NULL,
    RespondedDate                     DATETIME2(0)    NULL,
    FinalReleasedOn                   DATETIME2(0)    NULL,
    LocalFilePath                     NVARCHAR(1000)  NULL,   -- where the app saved the drawing
    DownloadedOn                      DATETIME2(0)    NULL,
    FirstSeenOn                       DATETIME2(0)    NOT NULL,
    LastSeenOn                        DATETIME2(0)    NOT NULL,
    CONSTRAINT PK_DocumentRevision PRIMARY KEY (DocId, InternalRevisionNumber, VersionNumber)
);
GO

/* Column layout (title + field) the site uses for each folder. */
IF OBJECT_ID(N'dbo.FolderColumn', N'U') IS NULL
CREATE TABLE dbo.FolderColumn
(
    FolderId  BIGINT        NOT NULL,
    Ordinal   INT           NOT NULL,
    Field     NVARCHAR(200) NOT NULL,
    Title     NVARCHAR(400) NOT NULL,
    CONSTRAINT PK_FolderColumn PRIMARY KEY (FolderId, Ordinal)
);
GO

IF OBJECT_ID(N'dbo.SyncRun', N'U') IS NULL
CREATE TABLE dbo.SyncRun
(
    SyncRunId      INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_SyncRun PRIMARY KEY,
    StartedOn      DATETIME2(0)   NOT NULL,
    FinishedOn     DATETIME2(0)   NULL,
    LoginName      NVARCHAR(200)  NOT NULL,
    FolderCount    INT            NULL,
    DocumentCount  INT            NULL,
    Status         NVARCHAR(20)   NOT NULL,   -- Running / Succeeded / Failed
    Message        NVARCHAR(MAX)  NULL
);
GO

/* ---------- Stored procedures ---------- */

CREATE OR ALTER PROCEDURE dbo.usp_UpsertSmartFolder
    @FolderId BIGINT, @ParentFolderId BIGINT, @FolderName NVARCHAR(400),
    @FolderType INT, @FolderCriteriaId INT, @FolderSubType INT,
    @HasChild BIT, @IsPublished BIT, @Depth INT, @SortOrder INT,
    @FullPath NVARCHAR(2000), @SyncedOn DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.SmartFolder
       SET ParentFolderId = @ParentFolderId, FolderName = @FolderName, FolderType = @FolderType,
           FolderCriteriaId = @FolderCriteriaId, FolderSubType = @FolderSubType, HasChild = @HasChild,
           IsPublished = @IsPublished, Depth = @Depth, SortOrder = @SortOrder, FullPath = @FullPath,
           IsActive = 1, LastSyncedOn = @SyncedOn
     WHERE FolderId = @FolderId;

    IF @@ROWCOUNT = 0
        INSERT dbo.SmartFolder (FolderId, ParentFolderId, FolderName, FolderType, FolderCriteriaId, FolderSubType,
                                HasChild, IsPublished, Depth, SortOrder, FullPath, IsActive, LastSyncedOn)
        VALUES (@FolderId, @ParentFolderId, @FolderName, @FolderType, @FolderCriteriaId, @FolderSubType,
                @HasChild, @IsPublished, @Depth, @SortOrder, @FullPath, 1, @SyncedOn);
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_UpsertDocument
    @DocId BIGINT, @DocNumber NVARCHAR(400), @VendorDocNumber NVARCHAR(400), @DocDescription NVARCHAR(MAX),
    @RevisionNumber NVARCHAR(50), @InternalRevisionNumber INT, @VersionNumber INT, @SheetNumber NVARCHAR(50),
    @DocumentType NVARCHAR(200), @Discipline NVARCHAR(200), @Category NVARCHAR(200), @Package NVARCHAR(200),
    @PackageVendorName NVARCHAR(400), @DeptDwgCategory NVARCHAR(200), @LifeCycleStatus NVARCHAR(200),
    @InternalApprovalStatusCode NVARCHAR(100), @InternalApprovalStatusDescription NVARCHAR(400),
    @CurrentStage NVARCHAR(400), @CurrentStageUser NVARCHAR(400), @DocStatus NVARCHAR(200),
    @WorkflowTeam NVARCHAR(200), @FileName NVARCHAR(500), @HasAttachment BIT, @Billable NVARCHAR(100),
    @ProjectNumber NVARCHAR(100), @ScheduledSubmissionDate DATETIME2(0), @VendorSubmissionDate DATETIME2(0),
    @RespondedDate DATETIME2(0), @CreatedOn DATETIME2(0), @LastEditedOn DATETIME2(0),
    @LatestTransmittalReleasedOn DATETIME2(0), @FinalReleasedOn DATETIME2(0),
    @RawJson NVARCHAR(MAX), @SyncedOn DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    /* Some folders show fewer columns than others, so never overwrite a known value with NULL. */
    UPDATE dbo.Document SET
        DocNumber                         = COALESCE(@DocNumber, DocNumber),
        VendorDocNumber                   = COALESCE(@VendorDocNumber, VendorDocNumber),
        DocDescription                    = COALESCE(@DocDescription, DocDescription),
        RevisionNumber                    = COALESCE(@RevisionNumber, RevisionNumber),
        InternalRevisionNumber            = COALESCE(@InternalRevisionNumber, InternalRevisionNumber),
        VersionNumber                     = COALESCE(@VersionNumber, VersionNumber),
        SheetNumber                       = COALESCE(@SheetNumber, SheetNumber),
        DocumentType                      = COALESCE(@DocumentType, DocumentType),
        Discipline                        = COALESCE(@Discipline, Discipline),
        Category                          = COALESCE(@Category, Category),
        Package                           = COALESCE(@Package, Package),
        PackageVendorName                 = COALESCE(@PackageVendorName, PackageVendorName),
        DeptDwgCategory                   = COALESCE(@DeptDwgCategory, DeptDwgCategory),
        LifeCycleStatus                   = COALESCE(@LifeCycleStatus, LifeCycleStatus),
        InternalApprovalStatusCode        = COALESCE(@InternalApprovalStatusCode, InternalApprovalStatusCode),
        InternalApprovalStatusDescription = COALESCE(@InternalApprovalStatusDescription, InternalApprovalStatusDescription),
        CurrentStage                      = COALESCE(@CurrentStage, CurrentStage),
        CurrentStageUser                  = COALESCE(@CurrentStageUser, CurrentStageUser),
        DocStatus                         = COALESCE(@DocStatus, DocStatus),
        WorkflowTeam                      = COALESCE(@WorkflowTeam, WorkflowTeam),
        FileName                          = COALESCE(@FileName, FileName),
        HasAttachment                     = CASE WHEN @HasAttachment = 1 THEN 1 ELSE HasAttachment END,
        Billable                          = COALESCE(@Billable, Billable),
        ProjectNumber                     = COALESCE(@ProjectNumber, ProjectNumber),
        ScheduledSubmissionDate           = COALESCE(@ScheduledSubmissionDate, ScheduledSubmissionDate),
        VendorSubmissionDate              = COALESCE(@VendorSubmissionDate, VendorSubmissionDate),
        RespondedDate                     = COALESCE(@RespondedDate, RespondedDate),
        CreatedOn                         = COALESCE(@CreatedOn, CreatedOn),
        LastEditedOn                      = COALESCE(@LastEditedOn, LastEditedOn),
        LatestTransmittalReleasedOn       = COALESCE(@LatestTransmittalReleasedOn, LatestTransmittalReleasedOn),
        FinalReleasedOn                   = COALESCE(@FinalReleasedOn, FinalReleasedOn),
        RawJson                           = @RawJson,
        LastSyncedOn                      = @SyncedOn
    WHERE DocId = @DocId;

    IF @@ROWCOUNT = 0
        INSERT dbo.Document (DocId, DocNumber, VendorDocNumber, DocDescription, RevisionNumber, InternalRevisionNumber,
            VersionNumber, SheetNumber, DocumentType, Discipline, Category, Package, PackageVendorName, DeptDwgCategory,
            LifeCycleStatus, InternalApprovalStatusCode, InternalApprovalStatusDescription, CurrentStage, CurrentStageUser,
            DocStatus, WorkflowTeam, FileName, HasAttachment, Billable, ProjectNumber, ScheduledSubmissionDate,
            VendorSubmissionDate, RespondedDate, CreatedOn, LastEditedOn, LatestTransmittalReleasedOn, FinalReleasedOn,
            RawJson, FirstSeenOn, LastSyncedOn)
        VALUES (@DocId, @DocNumber, @VendorDocNumber, @DocDescription, @RevisionNumber, @InternalRevisionNumber,
            @VersionNumber, @SheetNumber, @DocumentType, @Discipline, @Category, @Package, @PackageVendorName, @DeptDwgCategory,
            @LifeCycleStatus, @InternalApprovalStatusCode, @InternalApprovalStatusDescription, @CurrentStage, @CurrentStageUser,
            @DocStatus, @WorkflowTeam, @FileName, @HasAttachment, @Billable, @ProjectNumber, @ScheduledSubmissionDate,
            @VendorSubmissionDate, @RespondedDate, @CreatedOn, @LastEditedOn, @LatestTransmittalReleasedOn, @FinalReleasedOn,
            @RawJson, @SyncedOn, @SyncedOn);
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_UpsertDocumentRevision
    @DocId BIGINT, @InternalRevisionNumber INT, @VersionNumber INT, @RevisionNumber NVARCHAR(50),
    @DocNumber NVARCHAR(400), @SheetNumber NVARCHAR(50), @FileName NVARCHAR(500),
    @LifeCycleStatus NVARCHAR(200), @DocStatus NVARCHAR(200), @InternalApprovalStatusCode NVARCHAR(100),
    @InternalApprovalStatusDescription NVARCHAR(400), @CurrentStage NVARCHAR(400),
    @VendorSubmissionDate DATETIME2(0), @RespondedDate DATETIME2(0), @FinalReleasedOn DATETIME2(0),
    @SyncedOn DATETIME2(0)
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE dbo.DocumentRevision SET
        RevisionNumber = @RevisionNumber, DocNumber = @DocNumber, SheetNumber = @SheetNumber,
        FileName = COALESCE(@FileName, FileName), LifeCycleStatus = COALESCE(@LifeCycleStatus, LifeCycleStatus),
        DocStatus = COALESCE(@DocStatus, DocStatus),
        InternalApprovalStatusCode = COALESCE(@InternalApprovalStatusCode, InternalApprovalStatusCode),
        InternalApprovalStatusDescription = COALESCE(@InternalApprovalStatusDescription, InternalApprovalStatusDescription),
        CurrentStage = @CurrentStage,
        VendorSubmissionDate = COALESCE(@VendorSubmissionDate, VendorSubmissionDate),
        RespondedDate = COALESCE(@RespondedDate, RespondedDate),
        FinalReleasedOn = COALESCE(@FinalReleasedOn, FinalReleasedOn),
        LastSeenOn = @SyncedOn
    WHERE DocId = @DocId AND InternalRevisionNumber = @InternalRevisionNumber AND VersionNumber = @VersionNumber;

    IF @@ROWCOUNT = 0
        INSERT dbo.DocumentRevision (DocId, InternalRevisionNumber, VersionNumber, RevisionNumber, DocNumber, SheetNumber,
            FileName, LifeCycleStatus, DocStatus, InternalApprovalStatusCode, InternalApprovalStatusDescription, CurrentStage,
            VendorSubmissionDate, RespondedDate, FinalReleasedOn, FirstSeenOn, LastSeenOn)
        VALUES (@DocId, @InternalRevisionNumber, @VersionNumber, @RevisionNumber, @DocNumber, @SheetNumber,
            @FileName, @LifeCycleStatus, @DocStatus, @InternalApprovalStatusCode, @InternalApprovalStatusDescription, @CurrentStage,
            @VendorSubmissionDate, @RespondedDate, @FinalReleasedOn, @SyncedOn, @SyncedOn);
END
GO

/* ---------- Views ---------- */

/* Whole folder tree in display order (parents first, siblings by SortOrder). */
CREATE OR ALTER VIEW dbo.vw_SmartFolderTree
AS
WITH Tree AS
(
    SELECT f.FolderId, f.ParentFolderId, f.FolderName, f.Depth, f.FullPath, f.DocumentCount,
           CAST(RIGHT('00000' + CAST(f.SortOrder AS VARCHAR(5)), 5) AS VARCHAR(500)) AS OrderKey
      FROM dbo.SmartFolder f
     WHERE f.ParentFolderId IS NULL AND f.IsActive = 1
    UNION ALL
    SELECT c.FolderId, c.ParentFolderId, c.FolderName, c.Depth, c.FullPath, c.DocumentCount,
           CAST(t.OrderKey + '.' + RIGHT('00000' + CAST(c.SortOrder AS VARCHAR(5)), 5) AS VARCHAR(500))
      FROM dbo.SmartFolder c
      JOIN Tree t ON c.ParentFolderId = t.FolderId
     WHERE c.IsActive = 1
)
SELECT FolderId, ParentFolderId, FolderName, Depth, FullPath, DocumentCount, OrderKey,
       REPLICATE(N'    ', Depth) + FolderName AS IndentedName
  FROM Tree;
GO

/* Every document with the folder it belongs to, ready for reports / Excel. */
CREATE OR ALTER VIEW dbo.vw_FolderDocuments
AS
SELECT t.OrderKey AS FolderOrderKey, t.FullPath AS FolderPath, t.FolderName, fd.RowOrder,
       d.*
  FROM dbo.FolderDocument fd
  JOIN dbo.vw_SmartFolderTree t ON t.FolderId = fd.FolderId
  JOIN dbo.Document d           ON d.DocId    = fd.DocId;
GO

/* ---------- Drawing registers ---------- */

/* SUBMISSION REGISTER: every revision the vendor submitted, with the reply. */
CREATE OR ALTER VIEW dbo.vw_SubmissionRegister
AS
SELECT d.Discipline, d.Package, d.DocNumber, d.SheetNumber, r.RevisionNumber, d.DocDescription,
       d.DocumentType, d.PackageVendorName, d.VendorDocNumber,
       d.ScheduledSubmissionDate,
       r.VendorSubmissionDate,
       r.RespondedDate,
       CASE WHEN r.VendorSubmissionDate IS NOT NULL AND r.RespondedDate IS NOT NULL
            THEN DATEDIFF(DAY, r.VendorSubmissionDate, r.RespondedDate) END AS DaysToRespond,
       r.InternalApprovalStatusCode  AS ReplyCode,
       r.InternalApprovalStatusDescription AS ReplyDescription,
       r.CurrentStage,
       CASE WHEN r.RespondedDate IS NULL THEN N'Pending Reply' ELSE N'Replied' END AS SubmissionStatus,
       CASE WHEN d.ScheduledSubmissionDate IS NOT NULL AND r.VendorSubmissionDate > d.ScheduledSubmissionDate
            THEN DATEDIFF(DAY, d.ScheduledSubmissionDate, r.VendorSubmissionDate) ELSE 0 END AS DaysLate,
       r.FileName, r.LocalFilePath, r.DocId, r.InternalRevisionNumber, r.VersionNumber
  FROM dbo.DocumentRevision r
  JOIN dbo.Document d ON d.DocId = r.DocId;
GO

/* REVISION REGISTER: every revision of every drawing, newest first per drawing. */
CREATE OR ALTER VIEW dbo.vw_RevisionRegister
AS
SELECT d.Discipline, d.DocNumber, d.SheetNumber, d.DocDescription,
       r.RevisionNumber, r.InternalRevisionNumber, r.VersionNumber,
       CASE WHEN r.InternalRevisionNumber = d.InternalRevisionNumber AND r.VersionNumber = d.VersionNumber
            THEN N'Current' ELSE N'Superseded' END AS RevisionState,
       r.LifeCycleStatus, r.DocStatus, r.InternalApprovalStatusCode AS ApprovalCode,
       r.InternalApprovalStatusDescription AS ApprovalDescription,
       r.VendorSubmissionDate, r.RespondedDate, r.FinalReleasedOn,
       r.FirstSeenOn, r.LastSeenOn, r.FileName, r.LocalFilePath, r.DownloadedOn, r.DocId,
       d.InternalRevisionNumber AS CurrentInternalRevision
  FROM dbo.DocumentRevision r
  JOIN dbo.Document d ON d.DocId = r.DocId;
GO

/* PUBLISHED (RELEASED) DRAWING REGISTER: latest released revision of each drawing. */
CREATE OR ALTER VIEW dbo.vw_PublishedRegister
AS
SELECT d.Discipline, d.Package, d.DocNumber, d.SheetNumber, d.RevisionNumber, d.DocDescription,
       d.DocumentType, d.LifeCycleStatus AS IssuePurpose,
       d.InternalApprovalStatusCode AS ApprovalCode, d.InternalApprovalStatusDescription AS ApprovalDescription,
       COALESCE(d.FinalReleasedOn, d.LatestTransmittalReleasedOn, d.RespondedDate, d.LastEditedOn) AS PublishedOn,
       d.FinalReleasedOn, d.LatestTransmittalReleasedOn,
       d.FileName, r.LocalFilePath, d.DocId
  FROM dbo.Document d
  LEFT JOIN dbo.DocumentRevision r
         ON r.DocId = d.DocId AND r.InternalRevisionNumber = d.InternalRevisionNumber AND r.VersionNumber = d.VersionNumber
 WHERE d.DocStatus = N'Released'
    OR d.FinalReleasedOn IS NOT NULL
    OR d.LatestTransmittalReleasedOn IS NOT NULL;
GO

/* Example:
   SELECT IndentedName, DocumentCount FROM dbo.vw_SmartFolderTree ORDER BY OrderKey;
   SELECT FolderPath, DocNumber, SheetNumber, RevisionNumber, DocDescription, DocStatus
     FROM dbo.vw_FolderDocuments ORDER BY FolderOrderKey, DocNumber, SheetNumber, RevisionNumber;
*/
