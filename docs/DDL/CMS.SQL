/* ============================================================
   CONDITION MONITORING SYSTEM (CMS)
   BASE REFERENCE SCHEMA
   SQL Server DDL
   ============================================================ */

CREATE SCHEMA cms;
GO

/* ============================================================
   SITE
   ============================================================ */

CREATE TABLE cms.Site (
    SiteID              INT IDENTITY(1,1) PRIMARY KEY,

    SiteUUID            UNIQUEIDENTIFIER NOT NULL
        DEFAULT NEWID(),

    SiteCode            NVARCHAR(100) NOT NULL,
    SiteName            NVARCHAR(255) NOT NULL,
    Description         NVARCHAR(1000) NULL,

    CreatedAtUTC        DATETIME2 NOT NULL
        DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_Site_UUID UNIQUE (SiteUUID),
    CONSTRAINT UQ_Site_Code UNIQUE (SiteCode)
);
GO

/* ============================================================
   LOCATION
   ============================================================ */

CREATE TABLE cms.Location (
    LocationID          INT IDENTITY(1,1) PRIMARY KEY,
    ParentLocationID    INT NULL,

    LocationCode        NVARCHAR(100) NOT NULL,
    LocationName        NVARCHAR(255) NOT NULL,
    Description         NVARCHAR(1000) NULL,

    CONSTRAINT UQ_Location_Code UNIQUE (LocationCode),

    CONSTRAINT FK_Location_Parent
        FOREIGN KEY (ParentLocationID)
        REFERENCES cms.Location(LocationID)
);
GO

/* ============================================================
   ASSET CLASS
   ============================================================ */

CREATE TABLE cms.AssetClass (
    AssetClassID        INT IDENTITY(1,1) PRIMARY KEY,

    ClassCode           NVARCHAR(100) NOT NULL,
    ClassName           NVARCHAR(255) NOT NULL,
    Description         NVARCHAR(1000) NULL,

    CONSTRAINT UQ_AssetClass_Code UNIQUE (ClassCode)
);
GO

/* ============================================================
   ASSET
   ============================================================ */

CREATE TABLE cms.Asset (
    AssetID             INT IDENTITY(1,1) PRIMARY KEY,

    SiteID              INT NOT NULL,
    ParentAssetID       INT NULL,
    LocationID          INT NULL,
    AssetClassID        INT NULL,

    AssetTag            NVARCHAR(100) NOT NULL,
    AssetName           NVARCHAR(255) NOT NULL,
    Description         NVARCHAR(1000) NULL,

    SerialNumber        NVARCHAR(100) NULL,
    Manufacturer        NVARCHAR(255) NULL,
    Model               NVARCHAR(255) NULL,
    CommissionDate      DATE NULL,

    OperationalStatus   NVARCHAR(50) NULL,
    CriticalityLevel    NVARCHAR(50) NULL,

    CreatedAtUTC        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUTC        DATETIME2 NULL,

    CONSTRAINT UQ_Asset_Tag UNIQUE (AssetTag),

    CONSTRAINT FK_Asset_Site
        FOREIGN KEY (SiteID)
        REFERENCES cms.Site(SiteID),

    CONSTRAINT FK_Asset_Parent
        FOREIGN KEY (ParentAssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_Asset_Location
        FOREIGN KEY (LocationID)
        REFERENCES cms.Location(LocationID),

    