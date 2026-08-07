-- ws-CIR 1.0 relational schema.
-- Surrogate BIGINT keys carry the foreign keys; the natural composite keys from
-- the spec are enforced as unique indexes. Idempotent: safe to run on every start.

IF SCHEMA_ID(N'cir') IS NULL
    EXEC(N'CREATE SCHEMA cir');
GO

-- Registry -------------------------------------------------------------------
IF OBJECT_ID(N'cir.Registry', N'U') IS NULL
BEGIN
    CREATE TABLE cir.Registry
    (
        RegistryKey     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Registry PRIMARY KEY,
        RegistryId      NVARCHAR(200)  NOT NULL,
        Description     NVARCHAR(MAX)  NULL,          -- JSON array of { value, languageId }
        CreatedUtc      DATETIME2(3)   NOT NULL CONSTRAINT DF_Registry_Created DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(3)   NOT NULL CONSTRAINT DF_Registry_Modified DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_Registry_Natural UNIQUE (RegistryId)
    );
END
GO

-- Category -------------------------------------------------------------------
IF OBJECT_ID(N'cir.Category', N'U') IS NULL
BEGIN
    CREATE TABLE cir.Category
    (
        CategoryKey       BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
        RegistryKey       BIGINT         NOT NULL,
        CategoryId        NVARCHAR(200)  NOT NULL,
        CategorySourceId  NVARCHAR(200)  NOT NULL,
        Description       NVARCHAR(MAX)  NULL,
        CreatedUtc        DATETIME2(3)   NOT NULL CONSTRAINT DF_Category_Created DEFAULT SYSUTCDATETIME(),
        ModifiedUtc       DATETIME2(3)   NOT NULL CONSTRAINT DF_Category_Modified DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Category_Registry FOREIGN KEY (RegistryKey)
            REFERENCES cir.Registry (RegistryKey) ON DELETE CASCADE,
        CONSTRAINT UQ_Category_Natural UNIQUE (RegistryKey, CategoryId, CategorySourceId)
    );
END
GO

-- Entry ----------------------------------------------------------------------
IF OBJECT_ID(N'cir.Entry', N'U') IS NULL
BEGIN
    CREATE TABLE cir.Entry
    (
        EntryKey        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Entry PRIMARY KEY,
        CategoryKey     BIGINT           NOT NULL,
        IdInSource      NVARCHAR(400)    NOT NULL,
        SourceId        NVARCHAR(200)    NOT NULL,
        Cirid           UNIQUEIDENTIFIER NULL,
        SourceOwnerId   NVARCHAR(200)    NULL,
        Name            NVARCHAR(400)    NULL,
        Description     NVARCHAR(MAX)    NULL,        -- JSON { value, languageId }
        Inactive        BIT              NULL,
        CreatedUtc      DATETIME2(3)     NOT NULL CONSTRAINT DF_Entry_Created DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(3)     NOT NULL CONSTRAINT DF_Entry_Modified DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Entry_Category FOREIGN KEY (CategoryKey)
            REFERENCES cir.Category (CategoryKey) ON DELETE CASCADE,
        CONSTRAINT UQ_Entry_Natural UNIQUE (CategoryKey, IdInSource, SourceId)
    );
END
GO

-- The whole point of the registry: CIRID lookup across every registry/category.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Entry_Cirid' AND object_id = OBJECT_ID(N'cir.Entry'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Entry_Cirid
        ON cir.Entry (Cirid)
        INCLUDE (CategoryKey, IdInSource, SourceId, Name, SourceOwnerId)
        WHERE Cirid IS NOT NULL;
END
GO

-- Supports GetEquivalentEntries / TargetSourceID filtering.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Entry_SourceId' AND object_id = OBJECT_ID(N'cir.Entry'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_Entry_SourceId ON cir.Entry (SourceId) INCLUDE (Cirid);
END
GO

-- Property -------------------------------------------------------------------
IF OBJECT_ID(N'cir.Property', N'U') IS NULL
BEGIN
    CREATE TABLE cir.Property
    (
        PropertyKey     BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Property PRIMARY KEY,
        EntryKey        BIGINT         NOT NULL,
        PropertyId      NVARCHAR(200)  NOT NULL,
        DataType        NVARCHAR(100)  NULL,
        PropertyValue   NVARCHAR(MAX)  NULL,          -- JSON array of { key, value, unitOfMeasure }
        CreatedUtc      DATETIME2(3)   NOT NULL CONSTRAINT DF_Property_Created DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(3)   NOT NULL CONSTRAINT DF_Property_Modified DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_Property_Entry FOREIGN KEY (EntryKey)
            REFERENCES cir.Entry (EntryKey) ON DELETE CASCADE,
        CONSTRAINT UQ_Property_Natural UNIQUE (EntryKey, PropertyId)
    );
END
GO

-- ISBM session state --------------------------------------------------------
-- Session ids must survive host recycling: on Consumption the Function host is
-- replaced freely, and re-opening on every poll would leak sessions on the
-- broker that still accumulate messages.
IF OBJECT_ID(N'cir.IsbmSession', N'U') IS NULL
BEGIN
    CREATE TABLE cir.IsbmSession
    (
        SessionKind  NVARCHAR(40)   NOT NULL CONSTRAINT PK_IsbmSession PRIMARY KEY,
        SessionId    NVARCHAR(200)  NOT NULL,
        ChannelUri   NVARCHAR(1000) NOT NULL,
        OpenedUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_IsbmSession_Opened DEFAULT SYSUTCDATETIME()
    );
END
GO
