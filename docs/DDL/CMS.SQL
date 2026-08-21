/* ============================================================
   CONDITION MONITORING SYSTEM (CMS)
   BASE REFERENCE SCHEMA
   SQL Server DDL
   ============================================================ */

/* ============================================================
   SITE
   ============================================================ */

CREATE TABLE Site (
    SiteID              UNIQUEIDENTIFIER PRIMARY KEY,
    SiteCode            VARCHAR(50) NOT NULL UNIQUE,
    SiteName            VARCHAR(200) NOT NULL,
    Description         VARCHAR(1000),
    SiteType            VARCHAR(50),
    ParentSiteID        UNIQUEIDENTIFIER NULL,
    Country             VARCHAR(100),
    Region              VARCHAR(100),
    Status              VARCHAR(20),
    CreatedDate         DATETIME2,
    UpdatedDate         DATETIME2,

    CONSTRAINT FK_Site_Parent
        FOREIGN KEY (ParentSiteID)
        REFERENCES Site(SiteID)
);
GO

/* ============================================================
   ASSET CLASS
   ============================================================ */

CREATE TABLE AssetType (
    AssetTypeID         UNIQUEIDENTIFIER PRIMARY KEY,
    AssetTypeCode       VARCHAR(50) NOT NULL UNIQUE,
    AssetTypeName       VARCHAR(200) NOT NULL,
    Description         VARCHAR(1000),

    ParentAssetTypeID   UNIQUEIDENTIFIER NULL,

    CriticalityClass    VARCHAR(50),
    ExpectedLifeYears   INT,

    CreatedDate         DATETIME2,
    UpdatedDate         DATETIME2,

    CONSTRAINT FK_AssetType_Parent
        FOREIGN KEY (ParentAssetTypeID)
        REFERENCES AssetType(AssetTypeID)
);
GO

/* ============================================================
   ASSET
   ============================================================ */

CREATE TABLE Asset (
    AssetID                 UNIQUEIDENTIFIER PRIMARY KEY,

    SiteID                  UNIQUEIDENTIFIER NOT NULL,
    AssetTypeID             UNIQUEIDENTIFIER NOT NULL,

    AssetNumber             VARCHAR(100) NOT NULL,
    AssetTag                VARCHAR(100),

    AssetName               VARCHAR(250) NOT NULL,
    Description             VARCHAR(1000),

    Manufacturer            VARCHAR(200),
    ModelNumber             VARCHAR(100),
    SerialNumber            VARCHAR(100),

    CommissionDate          DATE,
    RetirementDate          DATE,

    CriticalityScore        DECIMAL(10,2),
    RiskRanking             VARCHAR(50),

    Status                  VARCHAR(50),

    ParentAssetID           UNIQUEIDENTIFIER NULL,

    CreatedDate             DATETIME2,
    UpdatedDate             DATETIME2,

    CONSTRAINT FK_Asset_Site
        FOREIGN KEY (SiteID)
        REFERENCES Site(SiteID),

    CONSTRAINT FK_Asset_Type
        FOREIGN KEY (AssetTypeID)
        REFERENCES AssetType(AssetTypeID),

    CONSTRAINT FK_Asset_Parent
        FOREIGN KEY (ParentAssetID)
        REFERENCES Asset(AssetID)
);