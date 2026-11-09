CREATE SCHEMA [sync];
GO
CREATE TABLE [sync].[Cases]
(
    [Scope] nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [SourceCaseId] bigint NOT NULL,
    [LeadHash] binary(32) NULL,
    [Status] varchar(32) NOT NULL,
    [Payload] varbinary(max) NOT NULL,
    [UpdatedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_Cases_Updated] DEFAULT SYSUTCDATETIME(),
    CONSTRAINT [PK_Cases] PRIMARY KEY ([Scope], [SourceCaseId]),
    CONSTRAINT [CK_Cases_PositiveId] CHECK ([SourceCaseId] > 0)
);
GO
CREATE UNIQUE INDEX [UX_Cases_Lead] ON [sync].[Cases]([Scope], [LeadHash]) WHERE [LeadHash] IS NOT NULL;
GO
CREATE VIEW [sync].[Health]
AS SELECT [Scope], [Status], COUNT_BIG(*) AS [CaseCount], MIN([UpdatedAtUtc]) AS [OldestStateAtUtc], MAX([UpdatedAtUtc]) AS [LatestStateAtUtc]
FROM [sync].[Cases] GROUP BY [Scope], [Status];
GO
CREATE TABLE [sync].[Configuration]
(
    [Scope] nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT [PK_Configuration] PRIMARY KEY,
    [Version] int NOT NULL,
    [Payload] nvarchar(max) NOT NULL,
    [SavedAtUtc] datetime2(7) NOT NULL,
    CONSTRAINT [CK_Configuration_Json] CHECK (ISJSON([Payload]) = 1)
);
GO
CREATE TABLE [sync].[Runs]
(
    [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_Runs] PRIMARY KEY,
    [Scope] nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [StartedAtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_Runs_Start] DEFAULT SYSUTCDATETIME(),
    [FinishedAtUtc] datetime2(7) NULL,
    [Outcome] varchar(24) NOT NULL,
    [DryRun] bit NOT NULL,
    [ConfigurationVersion] int NOT NULL,
    [Discovered] int NOT NULL CONSTRAINT [DF_Runs_Discovered] DEFAULT 0,
    [Processed] int NOT NULL CONSTRAINT [DF_Runs_Processed] DEFAULT 0,
    [Failed] int NOT NULL CONSTRAINT [DF_Runs_Failed] DEFAULT 0,
    [Attention] int NOT NULL CONSTRAINT [DF_Runs_Attention] DEFAULT 0
);
GO
CREATE INDEX [IX_Runs_Scope_Start] ON [sync].[Runs]([Scope], [StartedAtUtc] DESC);
GO
CREATE TABLE [sync].[AccessAudit]
(
    [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_AccessAudit] PRIMARY KEY,
    [Scope] nvarchar(100) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [Actor] nvarchar(150) NOT NULL,
    [Action] varchar(40) NOT NULL,
    [SourceCaseId] bigint NULL,
    [ConfigurationVersion] int NULL,
    [AtUtc] datetime2(7) NOT NULL CONSTRAINT [DF_AccessAudit_Time] DEFAULT SYSUTCDATETIME()
);
GO
