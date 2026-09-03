/* ============================================================================
   ENG -- authoritative source, as a simplified relational iModel.

   Owned by EngProvider. This is the shape the app creates at startup, so it
   ships with the app rather than being linked out of docs/DDL. Those files are
   reference material describing ENG in the abstract; they are free to carry
   commentary or one-shot DDL that a running app must not depend on.

   Derived from docs/DDL/ENG.SQL. When the two need to agree, this copy is what
   actually runs -- change it here, and update the reference material separately
   if the model itself has moved.

   Purpose:
     - Acts as the authoritative source and a simplified relational iModel.
     - Uses BIGINT identity values for locally-created ECInstanceId values.
     - Represents Element-to-ECClass as a foreign-key relationship.
     - Preserves FederationGuid as the durable cross-system identifier.
     - Marks points in the iModel's history with named versions, whose contents
       are derived from changeset position rather than stored.

   Notes:
     - Source ECInstanceId values are local to this database.
     - A future Bentley iModel publisher should map source ECInstanceId values
       to target iModel ECInstanceId values.

   Every object is guarded so the script can be re-applied to an existing
   database without error. It creates; it does not migrate. Altering a table
   that already exists is deliberately out of scope -- a guard cannot tell an
   older shape from the current one, and silently leaving a stale table in
   place is safer than silently changing one holding data.

   There are no exceptions to that rule. Databases predating the current shape
   are dropped and recreated rather than migrated: they are disposable dev and
   demo databases, and a clean rebuild is both simpler and safer than a
   reshaping script that has to guess what an older table meant.

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
        /* The platform's own id, used unaltered as the federation identifier.
           Every dependent row -- iModel, and through it Element and
           NamedVersion -- hangs off this, so it is the one column that must
           never be reissued. */
        iTwinId         UNIQUEIDENTIFIER NOT NULL,

        /* The classification, named as the platform names it.

           Class and SubClass come from a closed vocabulary (Thing/Endeavor,
           and Asset/Portfolio/Project/Program/WorkPackage) and together say
           what kind of thing the twin is in lifecycle terms -- a physical
           asset, or the endeavour that delivers one.

           Type is a separate axis: it names the boundary of the digital twin
           -- what the twin is drawn around -- and is free text rather than a
           closed set. The boundary is whatever the owner says it is: a DOT
           scopes twins to a District because that is the area it manages,
           while an operator may scope one to a Plant or a Highway. So the
           values are not a fixed vocabulary and should not be validated
           against one.

           It does not refine Class/SubClass; a District can be either an
           Asset or the Project building it. ENG mints the site type UUID from
           Type alone, which is why two twins on opposite sides of the
           lifecycle can still classify as the same kind of site. */
        [Class]         NVARCHAR(50) NULL,
        SubClass        NVARCHAR(50) NULL,
        [Type]          NVARCHAR(100) NULL,

        /* displayName and number are the platform's engineering properties and
           are required to be unique per subClass. Held nullable here because a
           twin may be registered from a sparse payload, with the uniqueness
           enforced by filtered indexes below rather than by NOT NULL. */
        DisplayName     NVARCHAR(200) NULL,
        Number          NVARCHAR(100) NULL,

        /* active | inactive | trial. The platform's lifecycle flag. */
        [Status]        NVARCHAR(20) NULL,

        /* iTwins form parent-child hierarchies (Portfolio > Asset > Project,
           and so on). Self-referencing rather than a separate edge table
           because the platform models it as a single parent per twin.

           Deliberately not a FOREIGN KEY: the parent is frequently a twin the
           sandbox has never been told about, and a constraint would reject the
           child outright rather than record what the platform actually said. */
        ParentITwinId   UNIQUEIDENTIFIER NULL,

        /* Not in the published class/subClass documentation but present on the
           API resource, and the only free-text field the site description can
           be built from. */
        Description     NVARCHAR(500) NULL,

        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_iTwin_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc     DATETIME2(7) NOT NULL
            CONSTRAINT DF_iTwin_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_iTwin PRIMARY KEY (iTwinId)
    );
END
GO

/* ============================================================================
   iTwin migration to the platform contract

   An earlier shape modelled an iTwin as a Code plus a description, then bolted
   the platform's own fields on beside it under invented names (TwinClass,
   TwinType). Code was ENG's invention: the platform has no such field, so the
   provider was synthesising one from number, then displayName, then the id.
   That made the stored value depend on how sparse the payload happened to be.

   These steps bring an existing database to the shape declared above. They run
   in place and preserve iTwinId, because iModel references it and Element and
   NamedVersion reference iModel in turn -- recreating the table would discard
   all three.
   ============================================================================ */

/* Rename rather than add-and-copy, so existing values survive without a
   backfill and without a window where both spellings are live. */
IF COL_LENGTH('dbo.iTwin', 'TwinClass') IS NOT NULL
   AND COL_LENGTH('dbo.iTwin', 'Class') IS NULL
    EXEC sp_rename 'dbo.iTwin.TwinClass', 'Class', 'COLUMN';
GO

IF COL_LENGTH('dbo.iTwin', 'TwinType') IS NOT NULL
   AND COL_LENGTH('dbo.iTwin', 'Type') IS NULL
    EXEC sp_rename 'dbo.iTwin.TwinType', 'Type', 'COLUMN';
GO

IF COL_LENGTH('dbo.iTwin', 'Class') IS NULL
    ALTER TABLE dbo.iTwin ADD [Class] NVARCHAR(50) NULL;
GO

IF COL_LENGTH('dbo.iTwin', 'SubClass') IS NULL
    ALTER TABLE dbo.iTwin ADD SubClass NVARCHAR(50) NULL;
GO

IF COL_LENGTH('dbo.iTwin', 'Type') IS NULL
    ALTER TABLE dbo.iTwin ADD [Type] NVARCHAR(100) NULL;
GO

IF COL_LENGTH('dbo.iTwin', 'DisplayName') IS NULL
    ALTER TABLE dbo.iTwin ADD DisplayName NVARCHAR(200) NULL;
GO

IF COL_LENGTH('dbo.iTwin', 'Number') IS NULL
    ALTER TABLE dbo.iTwin ADD Number NVARCHAR(100) NULL;
GO

IF COL_LENGTH('dbo.iTwin', 'Status') IS NULL
    ALTER TABLE dbo.iTwin ADD [Status] NVARCHAR(20) NULL;
GO

IF COL_LENGTH('dbo.iTwin', 'ParentITwinId') IS NULL
    ALTER TABLE dbo.iTwin ADD ParentITwinId UNIQUEIDENTIFIER NULL;
GO

/* Description was NVARCHAR(200) when it only ever held seeded text. The
   platform's is longer; widening is safe, narrowing would not be. */
IF EXISTS (SELECT 1
           FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.iTwin')
             AND name = 'Description'
             AND max_length < 1000)
    ALTER TABLE dbo.iTwin ALTER COLUMN Description NVARCHAR(500) NULL;
GO

/* Recover what Code was standing in for before dropping it.

   Code was synthesised as number, else displayName, else the id as text. Only
   the first two are worth keeping: a Code equal to the id carries nothing the
   primary key does not already say. So Code seeds Number when Number is empty,
   and otherwise seeds DisplayName -- never both, or a twin would end up with
   its number repeated as its name.

   Deferred through EXEC because Code no longer exists in the declared schema,
   and a direct reference would fail to bind on a database that has already
   been migrated. */
IF COL_LENGTH('dbo.iTwin', 'Code') IS NOT NULL
BEGIN
    DECLARE @backfill NVARCHAR(MAX) = N'UPDATE dbo.iTwin SET Number = Code WHERE Number IS NULL AND Code IS NOT NULL AND TRY_CONVERT(UNIQUEIDENTIFIER, Code) IS NULL;'
        + N'UPDATE dbo.iTwin SET DisplayName = Code WHERE DisplayName IS NULL AND Code IS NOT NULL AND Code <> Number AND TRY_CONVERT(UNIQUEIDENTIFIER, Code) IS NULL;';
    EXEC sys.sp_executesql @backfill;
END
GO

/* The unique constraint has to go before the column it covers. */
IF EXISTS (SELECT 1 FROM sys.objects WHERE name = 'UQ_iTwin_Code' AND parent_object_id = OBJECT_ID('dbo.iTwin'))
    ALTER TABLE dbo.iTwin DROP CONSTRAINT UQ_iTwin_Code;
GO

IF COL_LENGTH('dbo.iTwin', 'Code') IS NOT NULL
BEGIN
    DECLARE @dropCode NVARCHAR(MAX) = N'ALTER TABLE dbo.iTwin DROP COLUMN Code;';
    EXEC sys.sp_executesql @dropCode;
END
GO

/* ----------------------------------------------------------------------------
   Uniqueness

   The platform requires displayName and number to be unique within a subClass,
   so the same constraint is enforced here rather than trusting every write path
   to have come from the platform.

   Filtered, because the columns are nullable and a plain unique index would
   treat two unknown numbers as a collision -- which would block registering a
   second sparsely-described twin.

   Scoped to SubClass and not globally: an Asset and the Project that delivers
   it legitimately share a number, and a global constraint would reject the
   Portfolio > Asset > Project hierarchies the contract recommends.
   ---------------------------------------------------------------------------- */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_iTwin_SubClass_Number' AND object_id = OBJECT_ID('dbo.iTwin'))
    CREATE UNIQUE INDEX UX_iTwin_SubClass_Number
        ON dbo.iTwin (SubClass, Number)
        WHERE SubClass IS NOT NULL AND Number IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_iTwin_SubClass_DisplayName' AND object_id = OBJECT_ID('dbo.iTwin'))
    CREATE UNIQUE INDEX UX_iTwin_SubClass_DisplayName
        ON dbo.iTwin (SubClass, DisplayName)
        WHERE SubClass IS NOT NULL AND DisplayName IS NOT NULL;
GO

/* Children are looked up by parent when walking a hierarchy. Not unique. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_iTwin_ParentITwinId' AND object_id = OBJECT_ID('dbo.iTwin'))
    CREATE INDEX IX_iTwin_ParentITwinId
        ON dbo.iTwin (ParentITwinId)
        WHERE ParentITwinId IS NOT NULL;
GO

/* ============================================================================
   iTwinType

   The boundary a twin is drawn around, held as a row so it has an identity of
   its own. Many iTwins reference one type: the FK lives on dbo.iTwin below.

   Site.Type.UUID is this table's iTwinTypeId, carried through unchanged. It is
   stored rather than derived from the name so that the identity survives a
   rename -- correcting a spelling leaves the UUID alone, and nothing already
   published to REG-LOCATION reclassifies.

   Number is the name that goes on the wire as Site.Type.ShortName. It is
   NVARCHAR rather than CHAR because CHAR blank-pads to its full width, and the
   padding would travel with the value.
   ============================================================================ */
IF OBJECT_ID('dbo.iTwinType', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.iTwinType
    (
        iTwinTypeId     UNIQUEIDENTIFIER NOT NULL,
        Number          NVARCHAR(100) NOT NULL,
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_iTwinType_CreatedUtc DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_iTwinType PRIMARY KEY (iTwinTypeId),

        -- Two rows with the same Number would be two identities for one
        -- boundary, which is the split this table exists to prevent.
        CONSTRAINT UQ_iTwinType_Number UNIQUE (Number)
    );
END
GO

/* Migration from the earlier shape, which keyed on the name and called the
   identity TypeUuid. Renamed in place rather than recreated so the District
   identity already published to REG-LOCATION is preserved. */
IF COL_LENGTH('dbo.iTwinType', 'TypeUuid') IS NOT NULL
BEGIN
    IF OBJECT_ID('PK_iTwinType', 'PK') IS NOT NULL
        ALTER TABLE dbo.iTwinType DROP CONSTRAINT PK_iTwinType;

    IF OBJECT_ID('UQ_iTwinType_TypeUuid', 'UQ') IS NOT NULL
        ALTER TABLE dbo.iTwinType DROP CONSTRAINT UQ_iTwinType_TypeUuid;

    EXEC sp_rename 'dbo.iTwinType.TypeUuid', 'iTwinTypeId', 'COLUMN';
    EXEC sp_rename 'dbo.iTwinType.TypeName', 'Number', 'COLUMN';

    ALTER TABLE dbo.iTwinType
        ADD CONSTRAINT PK_iTwinType PRIMARY KEY (iTwinTypeId);

    ALTER TABLE dbo.iTwinType
        ADD CONSTRAINT UQ_iTwinType_Number UNIQUE (Number);
END
GO

/* 'District' is bootstrapped.

   The iTwin Platform has no UI for Type, so the value cannot be entered at
   source and is seeded here instead. The UUID is fixed rather than generated
   so a day-zero reset reproduces it: REG-LOCATION's copy may outlive an ENG
   rebuild, and a fresh UUID would leave it holding two Districts.

   This sits outside the CREATE TABLE guard on purpose -- the table is only
   created when absent, but the seed must reapply on every run, including
   against a database that has the table but lost the row. */
IF NOT EXISTS (SELECT 1 FROM dbo.iTwinType
               WHERE iTwinTypeId = '3f2b8c14-6d5e-4a97-9c31-7b0d2e5a4f18')
   AND NOT EXISTS (SELECT 1 FROM dbo.iTwinType WHERE Number = N'District')
BEGIN
    INSERT INTO dbo.iTwinType (iTwinTypeId, Number)
    VALUES ('3f2b8c14-6d5e-4a97-9c31-7b0d2e5a4f18', N'District');
END
GO

/* The reference from iTwin to its boundary.

   Nullable: a twin may be registered before it has a boundary, and
   EngSitesBuilder.IsPublishable skips exactly those rather than publishing a
   site with an empty classification. NOT NULL would force every twin to carry
   one and make an unclassified twin publishable. */
IF COL_LENGTH('dbo.iTwin', 'iTwinTypeId') IS NULL
    ALTER TABLE dbo.iTwin ADD iTwinTypeId UNIQUEIDENTIFIER NULL;
GO

IF OBJECT_ID('FK_iTwin_iTwinType', 'F') IS NULL
    ALTER TABLE dbo.iTwin
        ADD CONSTRAINT FK_iTwin_iTwinType
            FOREIGN KEY (iTwinTypeId) REFERENCES dbo.iTwinType(iTwinTypeId);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_iTwin_iTwinTypeId')
    CREATE INDEX IX_iTwin_iTwinTypeId ON dbo.iTwin (iTwinTypeId)
        WHERE iTwinTypeId IS NOT NULL;
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

   A marker, not a container. A Bentley Named Version pins a point in an
   iModel's timeline; this models the same idea, so a version does not hold
   elements -- it records the changeset position elements are measured against.

   Membership is therefore derived, never stored: a marker's contents are the
   elements lying between the previous marker's position and this one's. That
   is what makes a baseline reconstructable after the fact. If membership were
   a column on Element, a marker nobody remembered to create up front could
   never be recovered, and the first baseline of a database could not exist at
   all.

   There is no lifecycle here. Creating the marker is the publication: there is
   nothing to declare first and release later, and consequently nothing to
   validate against. ENG publishes what the design says; whether that content
   is acceptable is REG-LOCATION's judgement, reported back out of band and
   remedied by cutting a new marker.

   Declared before Element only for readability; nothing points at it now.
   ============================================================================ */
IF OBJECT_ID('dbo.NamedVersion', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.NamedVersion
    (
        NamedVersionId  BIGINT IDENTITY(1,1) NOT NULL,
        iModelId        UNIQUEIDENTIFIER NOT NULL,

        /* ---- Bentley-shaped identifiers ------------------------------------
           Real iModels identifies a named version by GUID and pins it to a
           changeset. This emulation allocates a BIGINT, which cannot appear in
           an iModels event payload.

           These columns exist so the published event carries the identifiers
           the real system would emit. They are emulation scaffolding standing
           in for values Bentley mints, not integration state: when a real
           iModels replaces this database, they are supplied by the source and
           nothing downstream changes.

           VersionGuid is defaulted rather than assigned by the application so a
           row inserted by any path still has one. A GUID that could be absent
           would eventually be absent, and the event could not be built. */
        VersionGuid     UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT DF_NamedVersion_VersionGuid DEFAULT NEWID(),

        /* Where this marker sits in the iModel's history. NOT NULL: a marker
           that is not pinned to a position cannot have its contents derived,
           and an unpinned marker is not a baseline at all.

           This is the column membership is computed from -- see the
           NamedVersionElement view.

           Both are ENG-internal. They describe a position in this model's own
           history and carry no meaning for any other participant, so they are
           for correlation within ENG and the notification envelope only and
           must never be keyed, stored or reconciled against downstream. */
        ChangesetId     VARCHAR(64) NOT NULL,
        ChangesetIndex  INT NOT NULL,

        Name            NVARCHAR(200) NOT NULL,
        Description     NVARCHAR(1000) NULL,

        CreatedBy       NVARCHAR(100) NOT NULL
            CONSTRAINT DF_NamedVersion_CreatedBy DEFAULT N'system',
        CreatedUtc      DATETIME2(7) NOT NULL
            CONSTRAINT DF_NamedVersion_CreatedUtc DEFAULT SYSUTCDATETIME(),

        /* What an incremental reader polls on. Every other table here carries
           one; NamedVersion did not, because nothing had needed to ask when a
           version last changed. Maintained by trigger below, since a column the
           application had to remember to set would drift on the one write path
           that forgot. */
        ModifiedUtc     DATETIME2(7) NOT NULL
            CONSTRAINT DF_NamedVersion_ModifiedUtc DEFAULT SYSUTCDATETIME(),

        RowVersion      ROWVERSION NOT NULL,

        CONSTRAINT PK_NamedVersion PRIMARY KEY (NamedVersionId),
        CONSTRAINT FK_NamedVersion_iModel
            FOREIGN KEY (iModelId) REFERENCES dbo.iModel (iModelId),

        /* A version name is only meaningful within its iModel. */
        CONSTRAINT UQ_NamedVersion_iModel_Name
            UNIQUE (iModelId, Name),

                /* The identifier that leaves this system must be unique across it,
                   not merely within an iModel: a consumer holds the GUID alone. */
                CONSTRAINT UQ_NamedVersion_VersionGuid
                    UNIQUE (VersionGuid)
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

        /* Where this element sits in the iModel's history, and the only thing
           that ties it to a baseline.

           An element deliberately does NOT point at a named version. Elements
           accumulate as work is done, with no knowledge of which baselines will
           later be drawn around them; a marker cut afterwards derives its
           contents from this column. Storing a version reference instead would
           force the baseline to be chosen before the work, and would make the
           first baseline of a database -- and any baseline nobody thought to
           declare in advance -- impossible to reconstruct.

           There is likewise no status column. An element is not draft or
           released; it is simply at a position, and whether that position falls
           inside a published baseline is a question about markers, not about
           the element. */
        ChangesetIndex      INT NOT NULL,

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

        /* Blocks the one-step cycle only. A longer parent loop is not
           reachable declaratively. */
        CONSTRAINT CK_Element_NotSelfParent
            CHECK (ParentECInstanceId IS NULL OR ParentECInstanceId <> ECInstanceId),

        /* A position in history is an ordinal; zero or negative would place an
           element before the beginning of the iModel. */
        CONSTRAINT CK_Element_ChangesetIndex
            CHECK (ChangesetIndex > 0)
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

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_NamedVersion_iModelId' AND object_id = OBJECT_ID('dbo.NamedVersion'))
    CREATE INDEX IX_NamedVersion_iModelId
        ON dbo.NamedVersion (iModelId);
GO

/* A changeset index orders markers within one iModel, so two markers claiming
   the same position would make the ordering membership is derived from
   ambiguous. Unfiltered: ChangesetIndex is NOT NULL, since a marker is always
   pinned. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_NamedVersion_iModel_ChangesetIndex' AND object_id = OBJECT_ID('dbo.NamedVersion'))
    CREATE UNIQUE INDEX UX_NamedVersion_iModel_ChangesetIndex
        ON dbo.NamedVersion (iModelId, ChangesetIndex);
GO

/* Membership is a range scan over this ordering, so it is the access path the
   derived-contents view depends on. */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Element_iModel_ChangesetIndex' AND object_id = OBJECT_ID('dbo.Element'))
    CREATE INDEX IX_Element_iModel_ChangesetIndex
        ON dbo.Element (iModelId, ChangesetIndex);
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
    e.ChangesetIndex,
    e.CreatedUtc,
    e.ModifiedUtc,
    e.RowVersion
FROM dbo.Element AS e
INNER JOIN dbo.ECClass AS c
    ON c.ECClassId = e.ECClassId
INNER JOIN dbo.ECSchema AS s
    ON s.ECSchemaId = c.ECSchemaId;
GO

/* ============================================================================
   Derived baseline membership

   What a marker contains, computed rather than stored. A marker's elements are
   those lying above the previous marker in the same iModel and at or below the
   marker itself.

   The previous marker is found by position, not by identity, so the answer does
   not depend on markers having been created in any particular order or on any
   bookkeeping done at the time. That is what makes a baseline recoverable long
   after the fact -- and what makes the first marker in a database work at all:
   with no predecessor the lower bound is 0, so it captures everything from
   day 0.

   Expressed as a view so the derivation has exactly one definition. A query
   that recomputed these bounds by hand would eventually disagree with this one,
   and a baseline that two callers describe differently is not a baseline.
   ============================================================================ */
CREATE OR ALTER VIEW dbo.vNamedVersionElement
AS
SELECT
    nv.NamedVersionId,
    nv.VersionGuid,
    nv.iModelId,
    nv.Name AS NamedVersionName,
    nv.ChangesetId,
    nv.ChangesetIndex AS NamedVersionChangesetIndex,
    e.ECInstanceId,
    e.FederationGuid,
    e.CodeValue,
    e.UserLabel,
    e.DisplayName,
    e.ParentECInstanceId,
    e.ECClassId,
    e.ChangesetIndex AS ElementChangesetIndex,
    e.CreatedUtc,
    e.ModifiedUtc
FROM dbo.NamedVersion AS nv
CROSS APPLY
(
    /* 0 when this is the first marker in the iModel, which makes the range
       below open at the bottom and the marker a day-0 baseline. */
    SELECT PreviousChangesetIndex =
        ISNULL
        (
            (
                SELECT MAX(prev.ChangesetIndex)
                FROM dbo.NamedVersion AS prev
                WHERE prev.iModelId = nv.iModelId
                  AND prev.ChangesetIndex < nv.ChangesetIndex
            ),
            0
        )
) AS bounds
INNER JOIN dbo.Element AS e
    ON e.iModelId = nv.iModelId
   AND e.ChangesetIndex > bounds.PreviousChangesetIndex
   AND e.ChangesetIndex <= nv.ChangesetIndex;
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

CREATE OR ALTER TRIGGER dbo.TR_NamedVersion_ModifiedUtc
ON dbo.NamedVersion
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL() > 1 RETURN;

    UPDATE nv
       SET ModifiedUtc = SYSUTCDATETIME()
    FROM dbo.NamedVersion AS nv
    INNER JOIN inserted AS i ON i.NamedVersionId = nv.NamedVersionId;
END;
GO

/* ============================================================================
   Baseline immutability

   A marker is a statement about what the design said at a position, and that
   statement has already been published. If an element at or below the highest
   marker could still be altered, the contents derived for that marker would
   change after the fact: a consumer holding the baseline would disagree with
   this database about what it was sent, and the detector's content hash would
   no longer match what it published.

   So history is closed and the present is open. Elements above the highest
   marker are ordinary work in progress and freely editable; elements at or
   below it are frozen. A database with no markers yet freezes nothing.

   Remediation is deliberately forward-only: a defect reported by REG-LOCATION
   is fixed by authoring new work and cutting a new marker, never by editing
   what an earlier marker already described.

   Enforced here rather than only in application code, so the rule holds
   regardless of which client is connected -- an ad-hoc UPDATE from a
   management tool is exactly the case that would otherwise slip through.
   Set-based, since one statement may touch many elements.
   ============================================================================ */
CREATE OR ALTER TRIGGER dbo.TR_Element_BaselineImmutable
ON dbo.Element
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    /* The highest marker per iModel is the frozen watermark. Computed per
       iModel because markers are per iModel: work in one must not be frozen
       by a baseline cut in another. */
    IF EXISTS
    (
        SELECT 1
        FROM
        (
            SELECT iModelId, ChangesetIndex FROM inserted
            UNION ALL
            SELECT iModelId, ChangesetIndex FROM deleted
        ) AS touched
        INNER JOIN
        (
            SELECT iModelId, MAX(ChangesetIndex) AS FrozenThrough
            FROM dbo.NamedVersion
            GROUP BY iModelId
        ) AS marker
            ON marker.iModelId = touched.iModelId
        WHERE touched.ChangesetIndex <= marker.FrozenThrough
    )
    BEGIN
        THROW 50003, 'Cannot modify elements at or below the most recent named version. Author new work and create a new named version instead.', 1;
    END
END;
GO

