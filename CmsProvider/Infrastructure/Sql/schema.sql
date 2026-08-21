/* ============================================================
   CONDITION MONITORING SYSTEM (CMS)
   Customer database schema.

   Derived from docs/DDL/CMS.SQL with two deliberate departures,
   both recorded here so the difference is visible rather than
   discovered later:

   1. dbo instead of a named schema. This database emulates a
      customer system that owns its whole database, so there is
      nothing to namespace against.

   2. Idempotent. The source DDL is a one-shot create; this runs
      on every cold start and on scale-out, so every object is
      guarded. Column types, constraint names and nullability are
      otherwise unchanged from the source.

   The keys are UNIQUEIDENTIFIER and are supplied by the caller,
   not allocated here. They carry the originating system's
   FederationId, which is what makes a CMS row and the object it
   represents elsewhere provably the same thing without a lookup.

   That the key happens to equal the FederationId does not remove
   the need to register the mapping in CIR. A consumer holding a
   CMS key must be able to resolve it without knowing that this
   particular customer system chose to store identity in its
   primary key - most do not. The registration is what keeps the
   pattern working for a schema with no UUID column at all.
   ============================================================ */

/* ============================================================
   SITE
   ============================================================ */

IF OBJECT_ID('dbo.Site', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Site (
        SiteID              UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,

        SiteCode            VARCHAR(50) NOT NULL,
        SiteName            VARCHAR(200) NOT NULL,
        Description         VARCHAR(1000) NULL,
        SiteType            VARCHAR(50) NULL,

        ParentSiteID        UNIQUEIDENTIFIER NULL,

        Country             VARCHAR(100) NULL,
        Region              VARCHAR(100) NULL,
        Status              VARCHAR(20) NULL,

        CreatedDate         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedDate         DATETIME2 NULL,

        CONSTRAINT UQ_Site_Code UNIQUE (SiteCode),

        CONSTRAINT FK_Site_Parent
            FOREIGN KEY (ParentSiteID)
            REFERENCES dbo.Site(SiteID)
    );
END
GO

/* ============================================================
   ASSET TYPE
   ============================================================ */

IF OBJECT_ID('dbo.AssetType', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AssetType (
        AssetTypeID         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,

        AssetTypeCode       VARCHAR(50) NOT NULL,
        AssetTypeName       VARCHAR(200) NOT NULL,
        Description         VARCHAR(1000) NULL,

        ParentAssetTypeID   UNIQUEIDENTIFIER NULL,

        CriticalityClass    VARCHAR(50) NULL,
        ExpectedLifeYears   INT NULL,

        CreatedDate         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedDate         DATETIME2 NULL,

        CONSTRAINT UQ_AssetType_Code UNIQUE (AssetTypeCode),

        CONSTRAINT FK_AssetType_Parent
            FOREIGN KEY (ParentAssetTypeID)
            REFERENCES dbo.AssetType(AssetTypeID)
    );
END
GO

/* ============================================================
   ASSET

   AssetTypeID is NOT NULL behind a foreign key, so every asset
   must be classified. A sender describing a segment rarely knows
   the customer's own type taxonomy, so the UNCLASSIFIED row
   seeded below is what unclassified traffic lands on. That is
   preferable to rejecting the asset: the customer would rather
   hold an asset awaiting classification than not hold it.

   AssetNumber is unique within a site rather than globally.
   Asset numbering restarts per site in most plants, and a global
   constraint would reject the second site's first asset.
   ============================================================ */

IF OBJECT_ID('dbo.Asset', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Asset (
        AssetID                 UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,

        SiteID                  UNIQUEIDENTIFIER NOT NULL,
        AssetTypeID             UNIQUEIDENTIFIER NOT NULL,

        AssetNumber             VARCHAR(100) NOT NULL,

        AssetName               VARCHAR(250) NOT NULL,
        Description             VARCHAR(1000) NULL,

        Manufacturer            VARCHAR(200) NULL,
        ModelNumber             VARCHAR(100) NULL,
        SerialNumber            VARCHAR(100) NULL,

        CommissionDate          DATE NULL,
        RetirementDate          DATE NULL,

        CriticalityScore        DECIMAL(10,2) NULL,
        RiskRanking             VARCHAR(50) NULL,

        Status                  VARCHAR(50) NULL,

        ParentAssetID           UNIQUEIDENTIFIER NULL,

        CreatedDate             DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedDate             DATETIME2 NULL,

        CONSTRAINT UQ_Asset_Number_Site UNIQUE (SiteID, AssetNumber),

        CONSTRAINT FK_Asset_Site
            FOREIGN KEY (SiteID)
            REFERENCES dbo.Site(SiteID),

        CONSTRAINT FK_Asset_Type
            FOREIGN KEY (AssetTypeID)
            REFERENCES dbo.AssetType(AssetTypeID),

        CONSTRAINT FK_Asset_Parent
            FOREIGN KEY (ParentAssetID)
            REFERENCES dbo.Asset(AssetID)
    );
END
GO

/* Lookup index for the site-scoped asset reads. Not in the source DDL:
   the source declares no non-unique indexes at all. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Asset_Site')
BEGIN
    CREATE INDEX IX_Asset_Site ON dbo.Asset(SiteID);
END
GO

/* ============================================================
   SEED: UNCLASSIFIED ASSET TYPE

   A fixed GUID rather than NEWID(). The value must survive a
   day-zero reset, because assets already registered in CIR
   reference it and a regenerated key would orphan them.

   It is written as a literal rather than derived so that anyone
   reading a row carrying this key can find it here.
   ============================================================ */

IF NOT EXISTS (SELECT 1 FROM dbo.AssetType WHERE AssetTypeCode = 'UNCLASSIFIED')
BEGIN
    INSERT INTO dbo.AssetType (AssetTypeID, AssetTypeCode, AssetTypeName, Description)
    VALUES (
        '00000000-0000-0000-0000-0000000000FF',
        'UNCLASSIFIED',
        'Unclassified',
        'Assigned when a sender did not state an asset type. Awaiting classification by a planner.');
END
GO
