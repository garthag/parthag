-- CONSTRUCT DPR: tables for Microsoft SQL Server (schema "cdpr").
-- You normally do NOT need to run this: the Windows app and the sync server create the tables on first start.
-- It is here for IT departments who want to review the database or create it by hand:
--   1. CREATE DATABASE ConstructDpr;   2. USE ConstructDpr;   3. run this file, then 02-views.sql.

IF SCHEMA_ID(N'cdpr') IS NULL EXEC(N'CREATE SCHEMA [cdpr];');
GO


CREATE TABLE [cdpr].[AspNetUsers] (
    [Id] nvarchar(450) NOT NULL,
    [DisplayName] nvarchar(max) NOT NULL,
    [UserName] nvarchar(256) NULL,
    [NormalizedUserName] nvarchar(256) NULL,
    [Email] nvarchar(256) NULL,
    [NormalizedEmail] nvarchar(256) NULL,
    [EmailConfirmed] bit NOT NULL,
    [PasswordHash] nvarchar(max) NULL,
    [SecurityStamp] nvarchar(max) NULL,
    [ConcurrencyStamp] nvarchar(max) NULL,
    [PhoneNumber] nvarchar(max) NULL,
    [PhoneNumberConfirmed] bit NOT NULL,
    [TwoFactorEnabled] bit NOT NULL,
    [LockoutEnd] datetimeoffset NULL,
    [LockoutEnabled] bit NOT NULL,
    [AccessFailedCount] int NOT NULL,
    CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[Audit] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NULL,
    [At] datetime2 NOT NULL,
    [User] nvarchar(max) NOT NULL,
    [Entity] nvarchar(450) NOT NULL,
    [EntityId] int NOT NULL,
    [Action] nvarchar(max) NOT NULL,
    [Details] nvarchar(max) NULL,
    CONSTRAINT [PK_Audit] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[Baselines] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [Revision] int NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Baselines] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[DataProtectionKeys] (
    [Id] int NOT NULL IDENTITY,
    [FriendlyName] nvarchar(max) NULL,
    [Xml] nvarchar(max) NULL,
    CONSTRAINT [PK_DataProtectionKeys] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[Dependencies] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [PredecessorId] int NOT NULL,
    [SuccessorId] int NOT NULL,
    [Type] int NOT NULL,
    [Lag] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Dependencies] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[Members] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [UserId] nvarchar(450) NOT NULL,
    [Email] nvarchar(max) NOT NULL,
    [DisplayName] nvarchar(max) NOT NULL,
    [Role] int NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Members] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[Projects] (
    [Id] int NOT NULL IDENTITY,
    [Code] nvarchar(450) NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [Client] nvarchar(max) NULL,
    [Contractor] nvarchar(max) NULL,
    [WorkOrderNo] nvarchar(max) NULL,
    [SiteAddress] nvarchar(max) NULL,
    [StartDate] date NOT NULL,
    [CompletionDate] date NULL,
    [Currency] nvarchar(max) NOT NULL,
    [WorkDays] nvarchar(max) NOT NULL,
    [AllowSelfApproval] bit NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Projects] PRIMARY KEY ([Id])
);
GO


CREATE TABLE [cdpr].[AspNetUserClaims] (
    [Id] int NOT NULL IDENTITY,
    [UserId] nvarchar(450) NOT NULL,
    [ClaimType] nvarchar(max) NULL,
    [ClaimValue] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [cdpr].[AspNetUsers] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[AspNetUserLogins] (
    [LoginProvider] nvarchar(450) NOT NULL,
    [ProviderKey] nvarchar(450) NOT NULL,
    [ProviderDisplayName] nvarchar(max) NULL,
    [UserId] nvarchar(450) NOT NULL,
    CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
    CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [cdpr].[AspNetUsers] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[AspNetUserTokens] (
    [UserId] nvarchar(450) NOT NULL,
    [LoginProvider] nvarchar(450) NOT NULL,
    [Name] nvarchar(450) NOT NULL,
    [Value] nvarchar(max) NULL,
    CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
    CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [cdpr].[AspNetUsers] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[BaselineItem] (
    [Id] int NOT NULL IDENTITY,
    [BaselineId] int NOT NULL,
    [ActivityId] int NOT NULL,
    [Start] date NULL,
    [Finish] date NULL,
    CONSTRAINT [PK_BaselineItem] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_BaselineItem_Baselines_BaselineId] FOREIGN KEY ([BaselineId]) REFERENCES [cdpr].[Baselines] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[Activities] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [Code] nvarchar(450) NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [Wbs] nvarchar(max) NULL,
    [Area] nvarchar(max) NULL,
    [Location] nvarchar(max) NULL,
    [Unit] nvarchar(max) NULL,
    [TotalQty] numeric(20,6) NOT NULL,
    [Rate] numeric(20,6) NOT NULL,
    [Weight] numeric(20,6) NOT NULL,
    [Duration] int NOT NULL,
    [IsMilestone] bit NOT NULL,
    [PlannedStart] date NULL,
    [PlannedFinish] date NULL,
    [BaselineStart] date NULL,
    [BaselineFinish] date NULL,
    [ActualStart] date NULL,
    [ActualFinish] date NULL,
    [Status] int NOT NULL,
    [Responsible] nvarchar(max) NULL,
    [SortOrder] int NOT NULL,
    [EarlyStart] date NULL,
    [EarlyFinish] date NULL,
    [LateStart] date NULL,
    [LateFinish] date NULL,
    [TotalFloat] int NULL,
    [FreeFloat] int NULL,
    [Critical] bit NOT NULL,
    [AcceptedQty] numeric(20,6) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Activities] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Activities_Projects_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [cdpr].[Projects] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[Dprs] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [Number] nvarchar(450) NOT NULL,
    [Revision] int NOT NULL,
    [ReportDate] date NOT NULL,
    [Shift] int NOT NULL,
    [Weather] nvarchar(max) NULL,
    [SiteConditions] nvarchar(max) NULL,
    [Status] int NOT NULL,
    [PreparedBy] nvarchar(max) NOT NULL,
    [ApprovedBy] nvarchar(max) NULL,
    [ApprovedAt] datetime2 NULL,
    [ReviewComment] nvarchar(max) NULL,
    [WorkCompleted] nvarchar(max) NULL,
    [DelayReasons] nvarchar(max) NULL,
    [TomorrowPlan] nvarchar(max) NULL,
    [SafetyNotes] nvarchar(max) NULL,
    [SnapshotHash] nvarchar(max) NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Dprs] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Dprs_Projects_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [cdpr].[Projects] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[Holidays] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [Date] date NOT NULL,
    [Name] nvarchar(max) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Holidays] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Holidays_Projects_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [cdpr].[Projects] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[DprActivityLines] (
    [Id] int NOT NULL IDENTITY,
    [DprId] int NOT NULL,
    [ActivityId] int NOT NULL,
    [QtyToday] numeric(20,6) NOT NULL,
    [QtyAccepted] numeric(20,6) NULL,
    [PrevCum] numeric(20,6) NOT NULL,
    [CumQty] numeric(20,6) NOT NULL,
    [Balance] numeric(20,6) NOT NULL,
    [ActualPct] numeric(20,6) NULL,
    [PlannedPct] numeric(20,6) NULL,
    [DelayReason] nvarchar(max) NULL,
    [Remarks] nvarchar(max) NULL,
    CONSTRAINT [PK_DprActivityLines] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_DprActivityLines_Activities_ActivityId] FOREIGN KEY ([ActivityId]) REFERENCES [cdpr].[Activities] ([Id]) ON DELETE NO ACTION,
    CONSTRAINT [FK_DprActivityLines_Dprs_DprId] FOREIGN KEY ([DprId]) REFERENCES [cdpr].[Dprs] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[EquipmentLines] (
    [Id] int NOT NULL IDENTITY,
    [DprId] int NOT NULL,
    [Code] nvarchar(max) NOT NULL,
    [Type] nvarchar(max) NOT NULL,
    [WorkingHours] numeric(20,6) NOT NULL,
    [IdleHours] numeric(20,6) NOT NULL,
    [BreakdownHours] numeric(20,6) NOT NULL,
    [BreakdownReason] nvarchar(max) NULL,
    CONSTRAINT [PK_EquipmentLines] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_EquipmentLines_Dprs_DprId] FOREIGN KEY ([DprId]) REFERENCES [cdpr].[Dprs] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[ManpowerLines] (
    [Id] int NOT NULL IDENTITY,
    [DprId] int NOT NULL,
    [Agency] nvarchar(max) NOT NULL,
    [Trade] nvarchar(max) NOT NULL,
    [Skilled] int NOT NULL,
    [SemiSkilled] int NOT NULL,
    [Unskilled] int NOT NULL,
    [Supervisors] int NOT NULL,
    [Hours] numeric(20,6) NOT NULL,
    CONSTRAINT [PK_ManpowerLines] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_ManpowerLines_Dprs_DprId] FOREIGN KEY ([DprId]) REFERENCES [cdpr].[Dprs] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[MaterialLines] (
    [Id] int NOT NULL IDENTITY,
    [DprId] int NOT NULL,
    [Code] nvarchar(max) NOT NULL,
    [Description] nvarchar(max) NOT NULL,
    [Unit] nvarchar(max) NOT NULL,
    [Opening] numeric(20,6) NOT NULL,
    [Received] numeric(20,6) NOT NULL,
    [Issued] numeric(20,6) NOT NULL,
    [ChallanNo] nvarchar(max) NULL,
    CONSTRAINT [PK_MaterialLines] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_MaterialLines_Dprs_DprId] FOREIGN KEY ([DprId]) REFERENCES [cdpr].[Dprs] ([Id]) ON DELETE CASCADE
);
GO


CREATE TABLE [cdpr].[Photos] (
    [Id] int NOT NULL IDENTITY,
    [ProjectId] int NOT NULL,
    [DprId] int NOT NULL,
    [FileName] nvarchar(max) NOT NULL,
    [ContentType] nvarchar(max) NOT NULL,
    [Caption] nvarchar(max) NULL,
    [TakenBy] nvarchar(max) NOT NULL,
    [Size] int NOT NULL,
    [Sha256] nvarchar(max) NOT NULL,
    [Content] varbinary(max) NOT NULL,
    [CreatedAt] datetime2 NOT NULL,
    [UpdatedAt] datetime2 NOT NULL,
    [Version] int NOT NULL,
    CONSTRAINT [PK_Photos] PRIMARY KEY ([Id]),
    CONSTRAINT [FK_Photos_Dprs_DprId] FOREIGN KEY ([DprId]) REFERENCES [cdpr].[Dprs] ([Id]) ON DELETE CASCADE
);
GO


CREATE UNIQUE INDEX [IX_Activities_ProjectId_Code] ON [cdpr].[Activities] ([ProjectId], [Code]);
GO


CREATE INDEX [IX_AspNetUserClaims_UserId] ON [cdpr].[AspNetUserClaims] ([UserId]);
GO


CREATE INDEX [IX_AspNetUserLogins_UserId] ON [cdpr].[AspNetUserLogins] ([UserId]);
GO


CREATE INDEX [EmailIndex] ON [cdpr].[AspNetUsers] ([NormalizedEmail]);
GO


CREATE UNIQUE INDEX [UserNameIndex] ON [cdpr].[AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL;
GO


CREATE INDEX [IX_Audit_Entity_EntityId] ON [cdpr].[Audit] ([Entity], [EntityId]);
GO


CREATE INDEX [IX_Audit_ProjectId_Id] ON [cdpr].[Audit] ([ProjectId], [Id]);
GO


CREATE INDEX [IX_BaselineItem_BaselineId] ON [cdpr].[BaselineItem] ([BaselineId]);
GO


CREATE UNIQUE INDEX [IX_Dependencies_PredecessorId_SuccessorId] ON [cdpr].[Dependencies] ([PredecessorId], [SuccessorId]);
GO


CREATE INDEX [IX_DprActivityLines_ActivityId] ON [cdpr].[DprActivityLines] ([ActivityId]);
GO


CREATE INDEX [IX_DprActivityLines_DprId] ON [cdpr].[DprActivityLines] ([DprId]);
GO


CREATE UNIQUE INDEX [IX_Dprs_ProjectId_Number_Revision] ON [cdpr].[Dprs] ([ProjectId], [Number], [Revision]);
GO


CREATE INDEX [IX_EquipmentLines_DprId] ON [cdpr].[EquipmentLines] ([DprId]);
GO


CREATE INDEX [IX_Holidays_ProjectId] ON [cdpr].[Holidays] ([ProjectId]);
GO


CREATE INDEX [IX_ManpowerLines_DprId] ON [cdpr].[ManpowerLines] ([DprId]);
GO


CREATE INDEX [IX_MaterialLines_DprId] ON [cdpr].[MaterialLines] ([DprId]);
GO


CREATE UNIQUE INDEX [IX_Members_ProjectId_UserId] ON [cdpr].[Members] ([ProjectId], [UserId]);
GO


CREATE INDEX [IX_Photos_DprId] ON [cdpr].[Photos] ([DprId]);
GO


CREATE INDEX [IX_Photos_ProjectId_DprId] ON [cdpr].[Photos] ([ProjectId], [DprId]);
GO


CREATE UNIQUE INDEX [IX_Projects_Code] ON [cdpr].[Projects] ([Code]);
GO


