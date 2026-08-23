/*=========================================================
  LOCATIONS
=========================================================*/

CREATE TABLE dbo.Location
(
    SiteID              VARCHAR(20)     NOT NULL,
    LocationCode        VARCHAR(50)     NOT NULL,

    ParentLocationCode  VARCHAR(50)     NULL,

    Description         VARCHAR(255)    NULL,
    TypeCode            VARCHAR(20)     NULL,
    StatusCode          VARCHAR(20)     NULL,

    OrgID               VARCHAR(20)     NULL,

    ClassStructureID    BIGINT          NULL,
    FailureCode         VARCHAR(30)     NULL,

    ChangedDate         DATETIME2       NULL,

    CONSTRAINT PK_Location
        PRIMARY KEY (SiteID, LocationCode)
);
GO


/*=========================================================
  LOCATION HIERARCHY
=========================================================*/

ALTER TABLE dbo.Location
ADD CONSTRAINT FK_Location_Parent
FOREIGN KEY
(
    SiteID,
    ParentLocationCode
)
REFERENCES dbo.Location
(
    SiteID,
    LocationCode
);
GO


/*=========================================================
  ASSET
=========================================================*/

CREATE TABLE dbo.Asset
(
    SiteID              VARCHAR(20)     NOT NULL,
    AssetNum            VARCHAR(50)     NOT NULL,

    ParentAssetNum      VARCHAR(50)     NULL,

    LocationCode        VARCHAR(50)     NULL,

    Description         VARCHAR(255)    NULL,
    StatusCode          VARCHAR(20)     NULL,

    AssetTypeCode       VARCHAR(20)     NULL,

    SerialNum           VARCHAR(100)    NULL,
    Manufacturer        VARCHAR(100)    NULL,
    ModelNum            VARCHAR(100)    NULL,
    Vendor              VARCHAR(100)    NULL,

    InstallDate         DATETIME2       NULL,

    ClassStructureID    BIGINT          NULL,
    FailureCode         VARCHAR(30)     NULL,

    ChangedDate         DATETIME2       NULL,

    CONSTRAINT PK_Asset
        PRIMARY KEY (SiteID, AssetNum)
);
GO


/*=========================================================
  ASSET HIERARCHY
=========================================================*/

ALTER TABLE dbo.Asset
ADD CONSTRAINT FK_Asset_Parent
FOREIGN KEY
(
    SiteID,
    ParentAssetNum
)
REFERENCES dbo.Asset
(
    SiteID,
    AssetNum
);
GO


/*=========================================================
  ASSET -> LOCATION
=========================================================*/

ALTER TABLE dbo.Asset
ADD CONSTRAINT FK_Asset_Location
FOREIGN KEY
(
    SiteID,
    LocationCode
)
REFERENCES dbo.Location
(
    SiteID,
    LocationCode
);
GO


/*=========================================================
  INDEXES
=========================================================*/

CREATE INDEX IX_Location_Parent
    ON dbo.Location
    (
        SiteID,
        ParentLocationCode
    );
GO

CREATE INDEX IX_Asset_Parent
    ON dbo.Asset
    (
        SiteID,
        ParentAssetNum
    );
GO

CREATE INDEX IX_Asset_Location
    ON dbo.Asset
    (
        SiteID,
        LocationCode
    );
GO

CREATE INDEX IX_Asset_Status
    ON dbo.Asset
    (
        StatusCode
    );
GO

CREATE INDEX IX_Location_Status
    ON dbo.Location
    (
        StatusCode
    );
GO