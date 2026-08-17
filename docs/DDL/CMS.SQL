/* ============================================================
   CONDITION MONITORING SYSTEM CMS - BASE SCHEMA
   Target: SQL Server
   ============================================================ */

CREATE SCHEMA cms;
GO

/* ============================================================
   LOCATION AND ASSET STRUCTURE
   ============================================================ */

CREATE TABLE cms.Location (
    LocationID           INT IDENTITY(1,1) PRIMARY KEY,
    ParentLocationID     INT NULL,
    LocationCode         NVARCHAR(100) NOT NULL,
    LocationName         NVARCHAR(255) NOT NULL,
    Description          NVARCHAR(1000) NULL,

    CONSTRAINT UQ_Location_Code UNIQUE (LocationCode),

    CONSTRAINT FK_Location_Parent
        FOREIGN KEY (ParentLocationID)
        REFERENCES cms.Location(LocationID)
);
GO


CREATE TABLE cms.AssetClass (
    AssetClassID         INT IDENTITY(1,1) PRIMARY KEY,
    ClassCode            NVARCHAR(100) NOT NULL,
    ClassName            NVARCHAR(255) NOT NULL,
    Description          NVARCHAR(1000) NULL,

    CONSTRAINT UQ_AssetClass_Code UNIQUE (ClassCode)
);
GO


CREATE TABLE cms.Asset (
    AssetID              INT IDENTITY(1,1) PRIMARY KEY,
    ParentAssetID        INT NULL,
    LocationID           INT NULL,
    AssetClassID         INT NULL,

    AssetTag             NVARCHAR(100) NOT NULL,
    AssetName            NVARCHAR(255) NOT NULL,
    Description          NVARCHAR(1000) NULL,

    SerialNumber         NVARCHAR(100) NULL,
    Manufacturer         NVARCHAR(255) NULL,
    Model                NVARCHAR(255) NULL,
    CommissionDate       DATE NULL,

    OperationalStatus    NVARCHAR(50) NULL,
    CriticalityLevel     NVARCHAR(50) NULL,

    CreatedAtUTC         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUTC         DATETIME2 NULL,

    CONSTRAINT UQ_Asset_Tag UNIQUE (AssetTag),

    CONSTRAINT FK_Asset_Parent
        FOREIGN KEY (ParentAssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_Asset_Location
        FOREIGN KEY (LocationID)
        REFERENCES cms.Location(LocationID),

    CONSTRAINT FK_Asset_AssetClass
        FOREIGN KEY (AssetClassID)
        REFERENCES cms.AssetClass(AssetClassID)
);
GO


/* ============================================================
   MONITORING INFRASTRUCTURE
   ============================================================ */

CREATE TABLE cms.Sensor (
    SensorID             INT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,

    SensorTag            NVARCHAR(100) NOT NULL,
    SensorType           NVARCHAR(100) NOT NULL,
    Manufacturer         NVARCHAR(255) NULL,
    Model                NVARCHAR(255) NULL,
    SerialNumber         NVARCHAR(100) NULL,

    InstallDate          DATE NULL,
    Status               NVARCHAR(50) NULL,

    CONSTRAINT UQ_Sensor_Tag UNIQUE (SensorTag),

    CONSTRAINT FK_Sensor_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID)
);
GO


CREATE TABLE cms.MeasurementPoint (
    MeasurementPointID   INT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,
    SensorID             INT NULL,

    PointTag             NVARCHAR(100) NOT NULL,
    PointName            NVARCHAR(255) NOT NULL,
    MeasurementType      NVARCHAR(100) NOT NULL,
    EngineeringUnit      NVARCHAR(50) NULL,

    Orientation          NVARCHAR(50) NULL,
    PositionDescription  NVARCHAR(255) NULL,

    IsActive             BIT NOT NULL DEFAULT 1,

    CONSTRAINT UQ_MeasurementPoint_Tag UNIQUE (PointTag),

    CONSTRAINT FK_MeasurementPoint_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_MeasurementPoint_Sensor
        FOREIGN KEY (SensorID)
        REFERENCES cms.Sensor(SensorID)
);
GO


/* ============================================================
   MONITORING DATA
   ============================================================ */

CREATE TABLE cms.Observation (
    ObservationID        BIGINT IDENTITY(1,1) PRIMARY KEY,
    MeasurementPointID   INT NOT NULL,

    ObservationTimeUTC   DATETIME2 NOT NULL,
    NumericValue         DECIMAL(18,6) NULL,
    TextValue            NVARCHAR(255) NULL,

    QualityCode          NVARCHAR(50) NULL,
    SourceSystem         NVARCHAR(100) NULL,

    CreatedAtUTC         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT FK_Observation_MeasurementPoint
        FOREIGN KEY (MeasurementPointID)
        REFERENCES cms.MeasurementPoint(MeasurementPointID)
);
GO


CREATE INDEX IX_Observation_Point_Time
ON cms.Observation (MeasurementPointID, ObservationTimeUTC);
GO


CREATE TABLE cms.Indicator (
    IndicatorID          BIGINT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,
    MeasurementPointID   INT NULL,

    IndicatorType        NVARCHAR(100) NOT NULL,
    IndicatorValue       DECIMAL(18,6) NULL,
    EngineeringUnit      NVARCHAR(50) NULL,

    CalculationTimeUTC   DATETIME2 NOT NULL,
    Confidence           DECIMAL(5,2) NULL,
    SourceSystem         NVARCHAR(100) NULL,

    CONSTRAINT FK_Indicator_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_Indicator_MeasurementPoint
        FOREIGN KEY (MeasurementPointID)
        REFERENCES cms.MeasurementPoint(MeasurementPointID)
);
GO


/* ============================================================
   CONDITION MANAGEMENT
   ============================================================ */

CREATE TABLE cms.Condition (
    ConditionID          BIGINT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,

    ConditionCode        NVARCHAR(100) NULL,
    ConditionState       NVARCHAR(50) NOT NULL,
    AssessmentTimeUTC    DATETIME2 NOT NULL,

    Confidence           DECIMAL(5,2) NULL,
    Comments             NVARCHAR(2000) NULL,
    SourceSystem         NVARCHAR(100) NULL,

    CONSTRAINT FK_Condition_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID)
);
GO


CREATE INDEX IX_Condition_Asset_Time
ON cms.Condition (AssetID, AssessmentTimeUTC);
GO


CREATE TABLE cms.ConditionHistory (
    ConditionHistoryID   BIGINT IDENTITY(1,1) PRIMARY KEY,
    ConditionID          BIGINT NOT NULL,

    PreviousState        NVARCHAR(50) NULL,
    NewState             NVARCHAR(50) NOT NULL,
    ChangeTimeUTC        DATETIME2 NOT NULL,

    ChangedBy            NVARCHAR(255) NULL,
    Reason               NVARCHAR(1000) NULL,

    CONSTRAINT FK_ConditionHistory_Condition
        FOREIGN KEY (ConditionID)
        REFERENCES cms.Condition(ConditionID)
);
GO


/* ============================================================
   ALARM MANAGEMENT
   ============================================================ */

CREATE TABLE cms.AlarmDefinition (
    AlarmDefinitionID    INT IDENTITY(1,1) PRIMARY KEY,
    MeasurementPointID   INT NOT NULL,

    AlarmCode            NVARCHAR(100) NOT NULL,
    AlarmName            NVARCHAR(255) NOT NULL,
    Severity             NVARCHAR(50) NOT NULL,

    HighHighLimit        DECIMAL(18,6) NULL,
    HighLimit            DECIMAL(18,6) NULL,
    LowLimit             DECIMAL(18,6) NULL,
    LowLowLimit          DECIMAL(18,6) NULL,

    Enabled              BIT NOT NULL DEFAULT 1,

    CONSTRAINT UQ_AlarmDefinition_Code UNIQUE (AlarmCode),

    CONSTRAINT FK_AlarmDefinition_MeasurementPoint
        FOREIGN KEY (MeasurementPointID)
        REFERENCES cms.MeasurementPoint(MeasurementPointID)
);
GO


CREATE TABLE cms.Alarm (
    AlarmID              BIGINT IDENTITY(1,1) PRIMARY KEY,
    AlarmDefinitionID    INT NOT NULL,
    AssetID              INT NOT NULL,
    MeasurementPointID   INT NULL,

    RaisedTimeUTC        DATETIME2 NOT NULL,
    AcknowledgedTimeUTC  DATETIME2 NULL,
    ClearedTimeUTC       DATETIME2 NULL,

    AlarmState           NVARCHAR(50) NOT NULL,
    Severity             NVARCHAR(50) NOT NULL,

    TriggerValue         DECIMAL(18,6) NULL,
    MaxObservedValue     DECIMAL(18,6) NULL,

    AcknowledgedBy       NVARCHAR(255) NULL,
    Comments             NVARCHAR(2000) NULL,

    CONSTRAINT FK_Alarm_AlarmDefinition
        FOREIGN KEY (AlarmDefinitionID)
        REFERENCES cms.AlarmDefinition(AlarmDefinitionID),

    CONSTRAINT FK_Alarm_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_Alarm_MeasurementPoint
        FOREIGN KEY (MeasurementPointID)
        REFERENCES cms.MeasurementPoint(MeasurementPointID)
);
GO


CREATE INDEX IX_Alarm_Asset_RaisedTime
ON cms.Alarm (AssetID, RaisedTimeUTC);
GO


/* ============================================================
   EVENT MANAGEMENT
   ============================================================ */

CREATE TABLE cms.Event (
    EventID              BIGINT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NULL,
    MeasurementPointID   INT NULL,
    AlarmID              BIGINT NULL,
    ConditionID          BIGINT NULL,

    EventType            NVARCHAR(100) NOT NULL,
    EventTimeUTC         DATETIME2 NOT NULL,
    Severity             NVARCHAR(50) NULL,

    Description          NVARCHAR(2000) NULL,
    SourceSystem         NVARCHAR(100) NULL,

    CONSTRAINT FK_Event_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_Event_MeasurementPoint
        FOREIGN KEY (MeasurementPointID)
        REFERENCES cms.MeasurementPoint(MeasurementPointID),

    CONSTRAINT FK_Event_Alarm
        FOREIGN KEY (AlarmID)
        REFERENCES cms.Alarm(AlarmID),

    CONSTRAINT FK_Event_Condition
        FOREIGN KEY (ConditionID)
        REFERENCES cms.Condition(ConditionID)
);
GO


CREATE INDEX IX_Event_Asset_Time
ON cms.Event (AssetID, EventTimeUTC);
GO


/* ============================================================
   RELIABILITY OBJECTS
   ============================================================ */

CREATE TABLE cms.FailureMechanism (
    FailureMechanismID   INT IDENTITY(1,1) PRIMARY KEY,
    MechanismCode        NVARCHAR(100) NOT NULL,
    MechanismName        NVARCHAR(255) NOT NULL,
    Description          NVARCHAR(1000) NULL,

    CONSTRAINT UQ_FailureMechanism_Code UNIQUE (MechanismCode)
);
GO


CREATE TABLE cms.FailureMode (
    FailureModeID        INT IDENTITY(1,1) PRIMARY KEY,
    FailureMechanismID   INT NULL,

    FailureModeCode      NVARCHAR(100) NOT NULL,
    FailureModeName      NVARCHAR(255) NOT NULL,
    Description          NVARCHAR(1000) NULL,

    CONSTRAINT UQ_FailureMode_Code UNIQUE (FailureModeCode),

    CONSTRAINT FK_FailureMode_FailureMechanism
        FOREIGN KEY (FailureMechanismID)
        REFERENCES cms.FailureMechanism(FailureMechanismID)
);
GO


CREATE TABLE cms.AssetRisk (
    AssetRiskID          BIGINT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,
    FailureModeID        INT NOT NULL,

    ProbabilityScore     DECIMAL(5,2) NULL,
    ConsequenceScore     DECIMAL(5,2) NULL,
    RiskScore            DECIMAL(8,2) NULL,

    AssessmentTimeUTC    DATETIME2 NOT NULL,
    AssessedBy           NVARCHAR(255) NULL,
    Comments             NVARCHAR(2000) NULL,

    CONSTRAINT FK_AssetRisk_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_AssetRisk_FailureMode
        FOREIGN KEY (FailureModeID)
        REFERENCES cms.FailureMode(FailureModeID)
);
GO


CREATE TABLE cms.FunctionalFailure (
    FunctionalFailureID  BIGINT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,
    FailureModeID        INT NULL,

    FailureTimeUTC       DATETIME2 NOT NULL,
    RestoredTimeUTC      DATETIME2 NULL,
    DowntimeHours        DECIMAL(10,2) NULL,

    Description          NVARCHAR(2000) NULL,
    SourceSystem         NVARCHAR(100) NULL,

    CONSTRAINT FK_FunctionalFailure_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_FunctionalFailure_FailureMode
        FOREIGN KEY (FailureModeID)
        REFERENCES cms.FailureMode(FailureModeID)
);
GO


/* ============================================================
   DIAGNOSTICS
   ============================================================ */

CREATE TABLE cms.Diagnosis (
    DiagnosisID          BIGINT IDENTITY(1,1) PRIMARY KEY,
    AssetID              INT NOT NULL,
    ConditionID          BIGINT NULL,
    FailureModeID        INT NULL,

    DiagnosisCode        NVARCHAR(100) NULL,
    DiagnosisText        NVARCHAR(2000) NOT NULL,
    Confidence           DECIMAL(5,2) NULL,

    CreatedTimeUTC       DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    SourceSystem         NVARCHAR(100) NULL,

    CONSTRAINT FK_Diagnosis_Asset
        FOREIGN KEY (AssetID)
        REFERENCES cms.Asset(AssetID),

    CONSTRAINT FK_Diagnosis_Condition
        FOREIGN KEY (ConditionID)
        REFERENCES cms.Condition(ConditionID),

    CONSTRAINT FK_Diagnosis_FailureMode
        FOREIGN KEY (FailureModeID)
        REFERENCES cms.FailureMode(FailureModeID)
);
GO


CREATE INDEX IX_Diagnosis_Asset_Time
ON cms.Diagnosis (AssetID, CreatedTimeUTC);
GO