/* ============================================================================
   ENG -- authoritative source, as a simplified relational iModel.

   Purpose:
     - Acts as the authoritative source and a simplified relational iModel.
     - Uses BIGINT identity values for locally-created ECInstanceId values.
     - Represents Element-to-ECClass as a foreign-key relationship.
     - Preserves FederationGuid as the durable cross-system identifier.
     - Releases work through named versions, which is what ENG hands over.

   Notes:
     - Source ECInstanceId values are local to this database.
     - A future Bentley iModel publisher should map source ECInstanceId values
       to target iModel ECInstanceId values.

   Every object is guarded so the script can be re-applied to an existing
   database without error. It creates; it does not migrate. Altering a table
   that already exists is deliberately out of scope -- a guard cannot tell an
   older shape from the current one, and silently leaving a stale table in
   place is safer than silently changing one holding data.

   Simplifications worth knowing about, all deliberate:

     - ECProperty is not modelled. Elements carry identity and classification
       only, so two elements of different classes differ by class and by
       nothing else. Attributes would need a property or aspect table.

     - There is no element-to-element relationship other than parent. BIS has
       ElementRefersToElements; containment is all that is modelled here.

     - Code uniqueness is scoped to the iModel. BIS scopes a code by
       CodeSpec and CodeScope; this collapses that to the containing iModel,
       which is the only scope this database has.
   ============================================================================ */

/* Set as their own batch so they persist for the session that follows.
   Not optional: this script creates filtered indexes, and SQL Server refuses
   those under QUOTED_IDENTIFIER OFF with error 1934. SqlClient connects with
   both ON, but sqlcmd connects with QUOTED_IDENTIFIER OFF -- so a script that
   relies on the client default succeeds from the application and fails from
   the command line. These settings are also captured with the view and the
   triggers when they are created, so fixing them here fixes their stored
   setting too. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ============================================================================
   iTwin

   A Bentley project. Contains iModels; it is not itself a model, and it is
   not the "twin" of a plant -- that concept does not appear in this schema.
   ============================================================================ */
IF OBJECT_ID('dbo.iTwin', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.iTwin
    (
        iTwinId         UNIQUEIDENTIFIER NOT NULL,
        Code            NVARCHAR(100) NOT NULL,
        Description     NVARCHAR(200) NULL,
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_iTwin_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(7) NOT NULL
            CONSTRAINT DF_iTwin_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_iTwin PRIMARY KEY (iTwinId),
        CONSTRAINT UQ_iTwin_Code UNIQUE (Code)
    );
END
GO

/* ============================================================================
   iModel
   ============================================================================ */
IF OBJECT_ID('dbo.iModel', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.iModel
    (
        iModelId        UNIQUEIDENTIFIER NOT NULL,
        iTwinId         UNIQUEIDENTIFIER NOT NULL,
        Code            NVARCHAR(100) NOT NULL,
        Description     NVARCHAR(200) NULL,
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_iModel_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(7) NOT NULL
            CONSTRAINT DF_iModel_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_iModel PRIMARY KEY (iModelId),
        CONSTRAINT FK_iModel_iTwin
            FOREIGN KEY (iTwinId) REFERENCES dbo.iTwin (iTwinId),
        CONSTRAINT UQ_iModel_iTwin_Code UNIQUE (iTwinId, Code)
    );
END
GO

/* ============================================================================
   ECSchema metadata
   ============================================================================ */
IF OBJECT_ID('dbo.ECSchema', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ECSchema
    (
        ECSchemaId      BIGINT IDENTITY(1,1) NOT NULL,
        SchemaName      NVARCHAR(128) NOT NULL,
        SchemaAlias     NVARCHAR(64) NULL,
        SchemaVersion   NVARCHAR(20) NOT NULL,
        DisplayLabel    NVARCHAR(255) NULL,
        Description     NVARCHAR(1000) NULL,
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_ECSchema_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(7) NOT NULL
            CONSTRAINT DF_ECSchema_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_ECSchema PRIMARY KEY (ECSchemaId),
        CONSTRAINT UQ_ECSchema_NameVersion
            UNIQUE (SchemaName, SchemaVersion)
    );
END
GO

/* ============================================================================
   ECClass metadata
   ============================================================================ */
IF OBJECT_ID('dbo.ECClass', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ECClass
    (
        ECClassId       BIGINT IDENTITY(1,1) NOT NULL,
        ECSchemaId      BIGINT NOT NULL,
        ClassName       NVARCHAR(128) NOT NULL,
        DisplayLabel    NVARCHAR(255) NULL,
        Description     NVARCHAR(1000) NULL,
        ClassModifier   NVARCHAR(20) NOT NULL
            CONSTRAINT DF_ECClass_ClassModifier DEFAULT N'None',
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_ECClass_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(7) NOT NULL
            CONSTRAINT DF_ECClass_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_ECClass PRIMARY KEY (ECClassId),
        CONSTRAINT FK_ECClass_ECSchema
            FOREIGN KEY (ECSchemaId) REFERENCES dbo.ECSchema (ECSchemaId),
        CONSTRAINT UQ_ECClass_SchemaClass
            UNIQUE (ECSchemaId, ClassName),
        CONSTRAINT CK_ECClass_ClassModifier
            CHECK (ClassModifier IN (N'None', N'Abstract', N'Sealed')),

        /* Redundant on its own -- ECClassId is already the key. It exists so
           Element can carry a composite foreign key to (ECClassId,
           ClassModifier) and then CHECK the modifier, which is the only way
           to stop an Element being created on an abstract class: a CHECK
           constraint cannot read another table. */
        CONSTRAINT UQ_ECClass_Id_Modifier
            UNIQUE (ECClassId, ClassModifier)
    );
END
GO

/* ============================================================================
   EC class inheritance

   A class can have multiple base classes in EC metadata. BaseClassOrder retains
   the order in which BaseClass entries occur in the source schema.
   ============================================================================ */
IF OBJECT_ID('dbo.ECClassBase', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ECClassBase
    (
        ECClassId       BIGINT NOT NULL,
        BaseECClassId   BIGINT NOT NULL,
        BaseClassOrder  SMALLINT NOT NULL,

        CONSTRAINT PK_ECClassBase
            PRIMARY KEY (ECClassId, BaseECClassId),
        CONSTRAINT UQ_ECClassBase_Order
            UNIQUE (ECClassId, BaseClassOrder),
        CONSTRAINT FK_ECClassBase_Class
            FOREIGN KEY (ECClassId) REFERENCES dbo.ECClass (ECClassId),
        CONSTRAINT FK_ECClassBase_BaseClass
            FOREIGN KEY (BaseECClassId) REFERENCES dbo.ECClass (ECClassId),

        /* Blocks a class being its own base. It does NOT block a longer
           cycle (A -> B -> A); no declarative constraint can. If the
           bootstrap is ever generated rather than hand-written, the
           generator has to check that itself. */
        CONSTRAINT CK_ECClassBase_NotSelf
            CHECK (ECClassId <> BaseECClassId),
        CONSTRAINT CK_ECClassBase_Order
            CHECK (BaseClassOrder > 0)
    );
END
GO

/* ============================================================================
   Named version

   The release container, and what ENG actually hands over. A Bentley Named
   Version marks a point in an iModel's timeline; this models the same idea
   without a changeset history to anchor to, so a version instead accumulates
   the elements released under it.

   State is checked rather than free text. A release is a small closed
   lifecycle, and an unconstrained column would let a bad write produce a
   state the promotion gate cannot reason about.

   Declared before Element because Element points at it.
   ============================================================================ */
IF OBJECT_ID('dbo.NamedVersion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NamedVersion
    (
        NamedVersionId  BIGINT IDENTITY(1,1) NOT NULL,
        iModelId        UNIQUEIDENTIFIER NOT NULL,

        Name            NVARCHAR(200) NOT NULL,
        Description     NVARCHAR(1000) NULL,
        State           NVARCHAR(20) NOT NULL
            CONSTRAINT DF_NamedVersion_State DEFAULT N'Draft',

        CreatedBy       NVARCHAR(100) NOT NULL
            CONSTRAINT DF_NamedVersion_CreatedBy DEFAULT N'system',
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_NamedVersion_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ReleasedUtc     DATETIME2(7) NULL,
        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_NamedVersion PRIMARY KEY (NamedVersionId),
        CONSTRAINT FK_NamedVersion_iModel
            FOREIGN KEY (iModelId) REFERENCES dbo.iModel (iModelId),

        /* A version name is only meaningful within its iModel. */
        CONSTRAINT UQ_NamedVersion_iModel_Name
            UNIQUE (iModelId, Name),

        /* Redundant as a key, present so Element can carry a composite FK
           and be prevented from publishing into another iModel's version. */
        CONSTRAINT UQ_NamedVersion_Id_iModel
            UNIQUE (NamedVersionId, iModelId),

        CONSTRAINT CK_NamedVersion_State
            CHECK (State IN (N'Draft', N'Released')),

        /* The timestamp and the state cannot disagree. */
        CONSTRAINT CK_NamedVersion_ReleasedUtc
            CHECK (
                (State = N'Released' AND ReleasedUtc IS NOT NULL)
                OR (State = N'Draft' AND ReleasedUtc IS NULL)
            )
    );
END
GO

/* ============================================================================
   Element

   ECInstanceId is generated by this source database. ECClassId is a required
   relationship to ECClass. ParentECInstanceId models the BIS Element hierarchy.

   An element is what an engineer would call a tag: the two are the same thing
   here, which is why there is no separate tag table.
   ============================================================================ */
IF OBJECT_ID('dbo.Element', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Element
    (
        ECInstanceId        BIGINT IDENTITY(1,1) NOT NULL,
        ECClassId           BIGINT NOT NULL,

        /* Nullable, and NOT defaulted. A federation identifier is assigned by
           whoever federates, and an element that has never been published
           does not have one yet. Minting it on insert would have every local
           row claim a cross-system identity before anything had agreed to it,
           and NEWSEQUENTIALID is guessable, which is a poor property for a
           value handed to other systems. */
        FederationGuid      UNIQUEIDENTIFIER NULL,

        /* Carried so the class modifier can be constrained below. It
           duplicates ECClass.ClassModifier by design; the composite foreign
           key keeps the two in step. */
        ECClassModifier     NVARCHAR(20) NOT NULL,

        CodeValue           NVARCHAR(255) NULL,
        UserLabel           NVARCHAR(255) NULL,
        DisplayName         NVARCHAR(255) NULL,
        ParentECInstanceId  BIGINT NULL,
        iModelId            UNIQUEIDENTIFIER NOT NULL,

        /* Mandatory. An element is created directly into an open Draft
           version, so it belongs to one from birth and is never version-less.

           Maturity is therefore NOT a property of the element: it is read from
           the state of the version the element sits in. Draft means work in
           progress, Released means handed over. There is deliberately no
           status column on the element, because it could contradict this. */
        NamedVersionId      BIGINT NOT NULL,

        CreatedUtc          DATETIME2(7) NOT NULL
            CONSTRAINT DF_Element_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc         DATETIME2(7) NOT NULL
            CONSTRAINT DF_Element_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion          ROWVERSION NOT NULL,

        CONSTRAINT PK_Element PRIMARY KEY (ECInstanceId),
        CONSTRAINT FK_Element_iModel
            FOREIGN KEY (iModelId) REFERENCES dbo.iModel (iModelId),

        /* Composite rather than a plain FK to ECClassId, so the modifier
           travels with the class and the CHECK below can see it. */
        CONSTRAINT FK_Element_ECClass
            FOREIGN KEY (ECClassId, ECClassModifier)
            REFERENCES dbo.ECClass (ECClassId, ClassModifier),

        /* An abstract class exists to be derived from, not instantiated.
           Without this, an Element could be created as BisCore.Element. */
        CONSTRAINT CK_Element_NotAbstract
            CHECK (ECClassModifier <> N'Abstract'),

        /* Needed so the parent and publication foreign keys below can be
           composite and thereby confined to one iModel. */
        CONSTRAINT UQ_Element_Id_iModel
            UNIQUE (ECInstanceId, iModelId),

        /* Composite, so a child cannot parent to an element in a different
           iModel. NULL ParentECInstanceId leaves it unenforced, which is the
           wanted behaviour for a root element. */
        CONSTRAINT FK_Element_Parent
            FOREIGN KEY (ParentECInstanceId, iModelId)
            REFERENCES dbo.Element (ECInstanceId, iModelId),

        /* Likewise: an element cannot belong to a version owned by some other
           iModel. */
        CONSTRAINT FK_Element_NamedVersion
            FOREIGN KEY (NamedVersionId, iModelId)
            REFERENCES dbo.NamedVersion (NamedVersionId, iModelId),

        /* Blocks the one-step cycle only. A longer parent loop is not
           reachable declaratively. */
        CONSTRAINT CK_Element_NotSelfParent
            CHECK (ParentECInstanceId IS NULL OR ParentECInstanceId <> ECInstanceId)
    );
END
GO

/* ============================================================================
   Validation findings

   Why a promotion was refused. A finding is open until somebody resolves it,
   and an open finding blocks release of its version -- enforced by trigger
   below, not merely by application convention.

   Findings are therefore NOT cleared and rewritten on each attempt: doing so
   would silently reopen the gate, since the record of what was wrong is the
   very thing holding it shut. Resolution is explicit.

   The element is recorded by CodeValue rather than by ECInstanceId: a finding
   may be about a code that does not resolve to an element at all, which is
   itself one of the things the gate objects to.
   ============================================================================ */
IF OBJECT_ID('dbo.ValidationFinding', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ValidationFinding
    (
        FindingId       BIGINT IDENTITY(1,1) NOT NULL,
        NamedVersionId  BIGINT NOT NULL,

        CodeValue       NVARCHAR(255) NULL,
        Severity        NVARCHAR(20) NOT NULL
            CONSTRAINT DF_ValidationFinding_Severity DEFAULT N'Error',
        Message         NVARCHAR(1000) NOT NULL,

        /* Open until explicitly resolved. This is what the release gate
           reads. */
        State           NVARCHAR(20) NOT NULL
            CONSTRAINT DF_ValidationFinding_State DEFAULT N'Open',

        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_ValidationFinding_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ResolvedUtc     DATETIME2(7) NULL,
        ResolvedBy      NVARCHAR(100) NULL,

        CONSTRAINT PK_ValidationFinding PRIMARY KEY (FindingId),
        CONSTRAINT FK_ValidationFinding_NamedVersion
            FOREIGN KEY (NamedVersionId)
            REFERENCES dbo.NamedVersion (NamedVersionId),
        CONSTRAINT CK_ValidationFinding_Severity
            CHECK (Severity IN (N'Error', N'Warning')),
        CONSTRAINT CK_ValidationFinding_State
            CHECK (State IN (N'Open', N'Resolved')),

        /* The timestamp and the state cannot disagree. */
        CONSTRAINT CK_ValidationFinding_ResolvedUtc
            CHECK (
                (State = N'Resolved' AND ResolvedUtc IS NOT NULL)
                OR (State = N'Open' AND ResolvedUtc IS NULL)
            )
    );
END
GO

/* ============================================================================
   Publication mapping

   Optional mapping between source database Elements and identifiers allocated
   by a target Bentley iModel.
   ============================================================================ */
IF OBJECT_ID('dbo.ElementPublication', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ElementPublication
    (
        SourceECInstanceId  BIGINT NOT NULL,
        TargetiModelId      UNIQUEIDENTIFIER NOT NULL,
        TargetECInstanceId  BIGINT NOT NULL,
        PublishedUtc        DATETIME2(7) NOT NULL
            CONSTRAINT DF_ElementPublication_PublishedUtc DEFAULT SYSUTCDATETIME(),
        LastPublishedUtc    DATETIME2(7) NOT NULL
            CONSTRAINT DF_ElementPublication_LastPublishedUtc DEFAULT SYSUTCDATETIME(),
        SourceRowVersion    BINARY(8) NULL,

        CONSTRAINT PK_ElementPublication
            PRIMARY KEY (SourceECInstanceId, TargetiModelId),
        CONSTRAINT FK_ElementPublication_Element
            FOREIGN KEY (SourceECInstanceId)
            REFERENCES dbo.Element (ECInstanceId),
        CONSTRAINT UQ_ElementPublication_Target
            UNIQUE (TargetiModelId, TargetECInstanceId)
    );
END
GO

/* ============================================================================
   Indexes
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Element_FederationGuid' AND object_id = OBJECT_ID('dbo.Element'))
    /* Filtered, because FederationGuid is now nullable: many elements may be
       awaiting one, but no two may claim the same. */
    CREATE UNIQUE INDEX UX_Element_FederationGuid
        ON dbo.Element (FederationGuid)
        WHERE FederationGuid IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_iModel_iTwinId' AND object_id = OBJECT_ID('dbo.iModel'))
    CREATE INDEX IX_iModel_iTwinId
        ON dbo.iModel (iTwinId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ECClass_ECSchemaId' AND object_id = OBJECT_ID('dbo.ECClass'))
    CREATE INDEX IX_ECClass_ECSchemaId
        ON dbo.ECClass (ECSchemaId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ECClassBase_BaseECClassId' AND object_id = OBJECT_ID('dbo.ECClassBase'))
    CREATE INDEX IX_ECClassBase_BaseECClassId
        ON dbo.ECClassBase (BaseECClassId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Element_ECClassId' AND object_id = OBJECT_ID('dbo.Element'))
    CREATE INDEX IX_Element_ECClassId
        ON dbo.Element (ECClassId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Element_iModelId' AND object_id = OBJECT_ID('dbo.Element'))
    CREATE INDEX IX_Element_iModelId
        ON dbo.Element (iModelId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Element_ParentECInstanceId' AND object_id = OBJECT_ID('dbo.Element'))
    CREATE INDEX IX_Element_ParentECInstanceId
        ON dbo.Element (ParentECInstanceId)
        WHERE ParentECInstanceId IS NOT NULL;
GO

/* UNIQUE, not merely indexed. This database calls itself the authoritative
   source, and CodeValue is the identifier any downstream system correlates
   on; two elements in one iModel answering to the same code would make it
   authoritative in name only. Scoped to the iModel because that is the only
   code scope this schema models. Filtered because an element may legitimately
   have no code yet, and NULLs are not duplicates of each other. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Element_iModel_CodeValue' AND object_id = OBJECT_ID('dbo.Element'))
    CREATE UNIQUE INDEX UX_Element_iModel_CodeValue
        ON dbo.Element (iModelId, CodeValue)
        WHERE CodeValue IS NOT NULL;
GO

/* The gate's hot path: every element in a given version. Not filtered, since
   NamedVersionId is now mandatory and there is no null case to exclude. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Element_NamedVersionId' AND object_id = OBJECT_ID('dbo.Element'))
    CREATE INDEX IX_Element_NamedVersionId
        ON dbo.Element (NamedVersionId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_NamedVersion_iModelId' AND object_id = OBJECT_ID('dbo.NamedVersion'))
    CREATE INDEX IX_NamedVersion_iModelId
        ON dbo.NamedVersion (iModelId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ValidationFinding_NamedVersionId' AND object_id = OBJECT_ID('dbo.ValidationFinding'))
    CREATE INDEX IX_ValidationFinding_NamedVersionId
        ON dbo.ValidationFinding (NamedVersionId);
GO

/* The release gate's lookup: open findings for a version. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_ValidationFinding_Open' AND object_id = OBJECT_ID('dbo.ValidationFinding'))
    CREATE INDEX IX_ValidationFinding_Open
        ON dbo.ValidationFinding (NamedVersionId)
        WHERE State = N'Open';
GO

/* ============================================================================
   Views
   ============================================================================ */
CREATE OR ALTER VIEW dbo.vElement
AS
SELECT
    e.ECInstanceId,
    e.ECClassId,
    s.SchemaName,
    s.SchemaAlias,
    s.SchemaVersion,
    c.ClassName AS ECClassName,
    CONCAT(s.SchemaName, N'.', c.ClassName) AS FullyQualifiedECClassName,
    c.DisplayLabel AS ECClassDisplayLabel,
    c.ClassModifier,
    e.FederationGuid,
    e.CodeValue,
    e.UserLabel,
    e.DisplayName,
    e.ParentECInstanceId,
    e.iModelId,
    e.NamedVersionId,
    nv.Name AS NamedVersionName,
    nv.State AS NamedVersionState,
    nv.ReleasedUtc,
    /* Maturity is read from the version, never stored on the element. */
    CAST(CASE WHEN nv.State = N'Released' THEN 1 ELSE 0 END AS BIT)
        AS IsReleased,
    e.CreatedUtc,
    e.ModifiedUtc,
    e.RowVersion
FROM dbo.Element AS e
INNER JOIN dbo.ECClass AS c
    ON c.ECClassId = e.ECClassId
INNER JOIN dbo.ECSchema AS s
    ON s.ECSchemaId = c.ECSchemaId
/* INNER, not LEFT: every element has a version. */
INNER JOIN dbo.NamedVersion AS nv
    ON nv.NamedVersionId = e.NamedVersionId;
GO

/* ============================================================================
   Audit triggers

   CREATE OR ALTER rather than guarded CREATE: unlike a table, a trigger body
   holds no data, so replacing it is always safe and keeps the definition in
   step with this file.

   Consequence worth knowing before writing data access against these tables:
   a table with an enabled trigger cannot be the target of a bare OUTPUT clause
   (error 334). Use OUTPUT ... INTO a table variable, or SCOPE_IDENTITY(). Do
   not use @@IDENTITY -- it would return whatever the trigger last inserted.

   TRIGGER_NESTLEVEL() > 1 stops the trigger re-firing on its own UPDATE.
   It also skips the stamp when an update arrives from inside another
   trigger; there are none today, which is the only reason that is acceptable.
   ============================================================================ */
CREATE OR ALTER TRIGGER dbo.TR_iTwin_ModifiedUtc
ON dbo.iTwin
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE t
       SET ModifiedUtc = SYSUTCDATETIME()
    FROM dbo.iTwin AS t
    INNER JOIN inserted AS i ON i.iTwinId = t.iTwinId;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_iModel_ModifiedUtc
ON dbo.iModel
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE m
       SET ModifiedUtc = SYSUTCDATETIME()
    FROM dbo.iModel AS m
    INNER JOIN inserted AS i ON i.iModelId = m.iModelId;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_ECSchema_ModifiedUtc
ON dbo.ECSchema
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE s
       SET ModifiedUtc = SYSUTCDATETIME()
    FROM dbo.ECSchema AS s
    INNER JOIN inserted AS i ON i.ECSchemaId = s.ECSchemaId;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_ECClass_ModifiedUtc
ON dbo.ECClass
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE c
       SET ModifiedUtc = SYSUTCDATETIME()
    FROM dbo.ECClass AS c
    INNER JOIN inserted AS i ON i.ECClassId = c.ECClassId;
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_Element_ModifiedUtc
ON dbo.Element
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE e
       SET ModifiedUtc = SYSUTCDATETIME()
    FROM dbo.Element AS e
    INNER JOIN inserted AS i ON i.ECInstanceId = e.ECInstanceId;
END;
GO

/* ============================================================================
   Release gate

   Enforced here rather than only in application code. A release is the point
   at which this database's contents become someone else's input, so the rule
   that guards it should hold regardless of which client is connected -- an
   ad-hoc UPDATE from a management tool is exactly the case that would
   otherwise slip through.

   Set-based, not row-by-row: a single statement may release several versions.
   ============================================================================ */
CREATE OR ALTER TRIGGER dbo.TR_NamedVersion_ReleaseGate
ON dbo.NamedVersion
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    /* Only interested in transitions into Released. */
    IF NOT EXISTS
    (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.NamedVersionId = i.NamedVersionId
        WHERE i.State = N'Released' AND d.State <> N'Released'
    )
        RETURN;

    /* Any open finding blocks the release, whatever its severity. */
    IF EXISTS
    (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.NamedVersionId = i.NamedVersionId
        INNER JOIN dbo.ValidationFinding AS f
            ON f.NamedVersionId = i.NamedVersionId
        WHERE i.State = N'Released'
          AND d.State <> N'Released'
          AND f.State = N'Open'
    )
    BEGIN
        THROW 50001, 'Cannot release a named version while validation findings remain open.', 1;
    END

    /* A version with nothing in it is not a release. */
    IF EXISTS
    (
        SELECT 1
        FROM inserted AS i
        INNER JOIN deleted AS d ON d.NamedVersionId = i.NamedVersionId
        WHERE i.State = N'Released'
          AND d.State <> N'Released'
          AND NOT EXISTS
          (
              SELECT 1 FROM dbo.Element AS e
              WHERE e.NamedVersionId = i.NamedVersionId
          )
    )
    BEGIN
        THROW 50002, 'Cannot release a named version that contains no elements.', 1;
    END
END;
GO

/* ============================================================================
   Released-version immutability

   A released version is a handover record. If elements could still be added to
   it, or moved out of it, then what was handed over would not be what the
   version now says was handed over. New work belongs in a new Draft version.
   ============================================================================ */
CREATE OR ALTER TRIGGER dbo.TR_Element_ReleasedImmutable
ON dbo.Element
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    /* Blocks writing INTO a released version, and equally moving an element
       OUT of one, since deleted rows are checked too. */
    IF EXISTS
    (
        SELECT 1
        FROM inserted AS i
        INNER JOIN dbo.NamedVersion AS nv ON nv.NamedVersionId = i.NamedVersionId
        WHERE nv.State = N'Released'
    )
    OR EXISTS
    (
        SELECT 1
        FROM deleted AS d
        INNER JOIN dbo.NamedVersion AS nv ON nv.NamedVersionId = d.NamedVersionId
        WHERE nv.State = N'Released'
    )
    BEGIN
        THROW 50003, 'Cannot add, modify or move elements of a released named version.', 1;
    END
END;
GO

