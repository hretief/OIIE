/********************************************************************
 TAGS DOMAIN MINIMAL SCHEMA
 Includes OBJECTS master registry

 RE-RUNNABLE. Every object below is guarded on its own existence, so
 this script can be applied repeatedly against the same database
 without error. That matters because RegLocationProvider applies it
 at startup, which happens on every cold start and on scale-out.

 The guards check for the specific object being created rather than
 wrapping the file in one big "if the first table is missing" test:
 a half-applied schema from an interrupted run must be completed by
 the next run, not skipped because one table already exists.
********************************************************************/

/* Set explicitly so the AFTER DELETE triggers at the foot of this
   script are created with the option settings SQL Server requires,
   rather than inheriting whatever the client happens to default to.
   The enforcement columns deliberately avoid PERSISTED computed
   columns, so no session-level option is imposed on callers -- see
   the enforcement notes below. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

------------------------------------------------------------
-- namespaces
------------------------------------------------------------

IF OBJECT_ID('dbo.namespaces','U') IS NULL
CREATE TABLE dbo.namespaces
(
    namespace_id INT NOT NULL,

    CONSTRAINT PK_namespaces
        PRIMARY KEY (namespace_id)
);
GO

------------------------------------------------------------
-- class_groups
------------------------------------------------------------

IF OBJECT_ID('dbo.class_groups','U') IS NULL
CREATE TABLE dbo.class_groups
(
    group_id INT NOT NULL,

    CONSTRAINT PK_class_groups
        PRIMARY KEY (group_id)
);
GO

------------------------------------------------------------
-- transactions
------------------------------------------------------------

IF OBJECT_ID('dbo.transactions','U') IS NULL
CREATE TABLE dbo.transactions
(
    trn_id INT NOT NULL,

    CONSTRAINT PK_transactions
        PRIMARY KEY (trn_id)
);
GO

------------------------------------------------------------
-- uom_units
------------------------------------------------------------

IF OBJECT_ID('dbo.uom_units','U') IS NULL
CREATE TABLE dbo.uom_units
(
    unit_id INT NOT NULL,

    CONSTRAINT PK_uom_units
        PRIMARY KEY (unit_id)
);
GO

------------------------------------------------------------
-- objects
------------------------------------------------------------

IF OBJECT_ID('dbo.objects','U') IS NULL
CREATE TABLE dbo.objects
(
    object_id      INT NOT NULL,
    object_type    INT NOT NULL,

    guid           UNIQUEIDENTIFIER NULL,

    scope_id       INT NOT NULL,

    hide_flags     TINYINT NOT NULL,
    lock_flags     TINYINT NOT NULL,

    date_added     DATETIME NULL,
    added_by       INT NULL,

    date_changed   DATETIME NULL,
    changed_by     INT NULL,

    CONSTRAINT PK_objects
        PRIMARY KEY
        (
            object_id,
            object_type
        )
);
GO

------------------------------------------------------------
-- scopes
------------------------------------------------------------
--
-- A scope is the boundary of the site this dataset applies to --
-- the equivalent of an iTwin. objects.scope_id has always pointed
-- here; until now there was no table for it to point at.
--
-- object_id/object_type are OPTIONAL and represent the business
-- object whose context this scope stands for -- an Organization
-- (type 5), a Project (9), a Site (255). A scope may instead be a
-- plain boundary with no such backing, in which case both are NULL.
--
-- They are NOT the scope's own registry entry. That is a separate
-- row in objects keyed (scope_id, 227), maintained through the
-- registry_object_type column added at the foot of this script.
-- The column names collide with the registry pair by coincidence,
-- not by meaning: object_id here identifies the Project or
-- Organization the scope represents, not the scope itself.
--
-- EIS: "The 'Global' Scope is the only scope to have no parent and
-- never has a concrete underwriting Object."
------------------------------------------------------------

IF OBJECT_ID('dbo.scopes','U') IS NULL
CREATE TABLE dbo.scopes
(
    scope_id        INT NOT NULL,

    name            NVARCHAR(255) NOT NULL,

    namespace_id    INT NOT NULL,

    -- Optional business-object context (Organization, Project, ...).
    -- NOT the scope's registry row.
    object_id       INT NULL,
    object_type     INT NULL,

    parent_id       INT NULL,

    is_enabled      CHAR(1) NOT NULL
        CONSTRAINT DF_scopes_is_enabled
        DEFAULT ('Y'),

    usage_count     INT NOT NULL
        CONSTRAINT DF_scopes_usage_count
        DEFAULT (0),

    CONSTRAINT PK_scopes
        PRIMARY KEY (scope_id),

    CONSTRAINT FK_scopes_namespace
        FOREIGN KEY (namespace_id)
        REFERENCES dbo.namespaces(namespace_id),

    CONSTRAINT FK_scopes_parent
        FOREIGN KEY (parent_id)
        REFERENCES dbo.scopes(scope_id)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_scopes_namespace_id'
                 AND object_id = OBJECT_ID('dbo.scopes'))
CREATE INDEX IX_scopes_namespace_id ON dbo.scopes(namespace_id);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_scopes_parent_id'
                 AND object_id = OBJECT_ID('dbo.scopes'))
CREATE INDEX IX_scopes_parent_id ON dbo.scopes(parent_id);
GO

------------------------------------------------------------
-- class_objects
------------------------------------------------------------

IF OBJECT_ID('dbo.class_objects','U') IS NULL
CREATE TABLE dbo.class_objects
(
    class_id      INT NOT NULL,
    group_id      INT NOT NULL,
    namespace_id  INT NOT NULL,

    CONSTRAINT PK_class_objects
        PRIMARY KEY (class_id),

    CONSTRAINT FK_class_objects_group
        FOREIGN KEY (group_id)
        REFERENCES dbo.class_groups(group_id),

    CONSTRAINT FK_class_objects_namespace
        FOREIGN KEY (namespace_id)
        REFERENCES dbo.namespaces(namespace_id)
);
GO

------------------------------------------------------------
-- items
------------------------------------------------------------

IF OBJECT_ID('dbo.items','U') IS NULL
CREATE TABLE dbo.items
(
    item_id         INT NOT NULL,

    namespace_id    INT NOT NULL,
    unit_id         INT NOT NULL,
    trn_id          INT NOT NULL,

    CONSTRAINT PK_items
        PRIMARY KEY (item_id),

    CONSTRAINT FK_items_namespace
        FOREIGN KEY (namespace_id)
        REFERENCES dbo.namespaces(namespace_id),

    CONSTRAINT FK_items_units
        FOREIGN KEY (unit_id)
        REFERENCES dbo.uom_units(unit_id),

    CONSTRAINT FK_items_transaction
        FOREIGN KEY (trn_id)
        REFERENCES dbo.transactions(trn_id)
);
GO

------------------------------------------------------------
-- tags
------------------------------------------------------------

IF OBJECT_ID('dbo.tags','U') IS NULL
CREATE TABLE dbo.tags
(
    tag_id      INT NOT NULL,

    item_id     INT NOT NULL,
    class_id    INT NOT NULL,

    code        NVARCHAR(100) NOT NULL,
    revision    INT NOT NULL,
    name        NVARCHAR(255) NOT NULL,

    CONSTRAINT PK_tags
        PRIMARY KEY (tag_id),

    CONSTRAINT UQ_tags_item_code_revision
        UNIQUE
        (
            item_id,
            code,
            revision
        ),

    CONSTRAINT FK_tags_item
        FOREIGN KEY (item_id)
        REFERENCES dbo.items(item_id),

    CONSTRAINT FK_tags_class
        FOREIGN KEY (class_id)
        REFERENCES dbo.class_objects(class_id)
);
GO

/********************************************************************
 OBJECTS REGISTRY ENFORCEMENT

 Every registered row must have a matching row in dbo.objects, keyed
 (object_id, object_type). Previously this was convention only: an
 INSERT that forgot its registry row succeeded silently.

 It is now enforced declaratively. Each registered table carries an
 object_type column pinned to its constant by a DEFAULT plus a CHECK,
 which gives a real composite foreign key into dbo.objects. The
 constant comes from ebps_populate_base_types in EIS-POPULATE.SQL:

     namespaces     226   Namespace
     class_groups   184   ClassGroup
     uom_units      285   Unit
     class_objects  185   Class
     items            1   PhysicalItem
     tags           212   Tag
     scopes         227   Scope

 scopes is the awkward one, for two reasons.

 First, it already HAS object_id/object_type columns, and they mean
 something else entirely: the business object whose context the scope
 represents -- an Organization, a Project, a Site. Optional, so both
 are NULL for a scope that is a plain boundary, and per EIS always
 NULL for 'Global'. Reusing that pair for the registry link would
 conflate two unrelated ideas: it would make a Project-backed scope
 claim its registry entry was the Project row. So scopes gets a
 separately named registry_object_type column instead.

 Second, there is a genuine circularity: objects.scope_id points at
 scopes, and every scope needs a registry row in objects. EIS calls
 this out in comments -- "Add the Scopes's Scope here - a chicken-egg
 issue" -- and resolves it by inserting the scopes row first, then
 back-filling the objects row.

 This schema resolves it the other way round: insert the objects row
 (1, 227, scope_id 1) first, then the scopes row. That keeps the same
 registry-first rule as every other table, so there is one ordering
 to remember rather than one rule plus an exception.

 That resolution depends on objects.scope_id NOT having a foreign key
 into scopes. Adding one would close the loop and make the first row
 of either table impossible to insert without disabling constraints.
 It is therefore left unenforced deliberately, not by oversight, and
 REG-LOCATION_VERIFY.SQL checks scope_id integrity by query instead.

 The column defaults, so existing INSERT statements that do not
 mention object_type keep working unchanged, and the CHECK stops
 anyone pointing a row at the wrong registry type.

 A PERSISTED computed column was tried first and rejected. It works,
 but it makes every one of these tables unwritable from any session
 with QUOTED_IDENTIFIER OFF, failing with Msg 1934 rather than
 anything that hints at the real cause. sqlcmd defaults that option
 OFF, so a plain command-line INSERT would break. A defaulted column
 carries no such session requirement.

 Consequences, both intended:

   - INSERT into a registered table FAILS unless the objects row
     already exists. Insert the registry row FIRST.
   - DELETE of an objects row FAILS while a domain row still
     references it. Delete the domain row FIRST.

 transactions is deliberately excluded: EIS defines no base type for
 it, because it is bookkeeping rather than a registered object.

 Deletion of the registry row is handled by the AFTER DELETE triggers
 below, so callers delete the domain row only and the objects row is
 reaped automatically. The triggers are set-based and guard against
 removing a registry row that another table still uses.

 Each addition below is guarded independently -- the column on
 sys.columns, the constraint on its own name. Guarding the column and
 its constraints as one unit would be wrong: an interrupted run can
 leave the column present but a constraint missing, and only a
 per-object check will repair that on the next run.
********************************************************************/

IF COL_LENGTH('dbo.namespaces','object_type') IS NULL
    ALTER TABLE dbo.namespaces
        ADD object_type INT NOT NULL
            CONSTRAINT DF_namespaces_object_type DEFAULT (226),
            CONSTRAINT CK_namespaces_object_type CHECK (object_type = 226);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_namespaces_objects')
    ALTER TABLE dbo.namespaces
        ADD CONSTRAINT FK_namespaces_objects
            FOREIGN KEY (namespace_id, object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

IF COL_LENGTH('dbo.class_groups','object_type') IS NULL
    ALTER TABLE dbo.class_groups
        ADD object_type INT NOT NULL
            CONSTRAINT DF_class_groups_object_type DEFAULT (184),
            CONSTRAINT CK_class_groups_object_type CHECK (object_type = 184);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_class_groups_objects')
    ALTER TABLE dbo.class_groups
        ADD CONSTRAINT FK_class_groups_objects
            FOREIGN KEY (group_id, object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

IF COL_LENGTH('dbo.uom_units','object_type') IS NULL
    ALTER TABLE dbo.uom_units
        ADD object_type INT NOT NULL
            CONSTRAINT DF_uom_units_object_type DEFAULT (285),
            CONSTRAINT CK_uom_units_object_type CHECK (object_type = 285);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_uom_units_objects')
    ALTER TABLE dbo.uom_units
        ADD CONSTRAINT FK_uom_units_objects
            FOREIGN KEY (unit_id, object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

IF COL_LENGTH('dbo.class_objects','object_type') IS NULL
    ALTER TABLE dbo.class_objects
        ADD object_type INT NOT NULL
            CONSTRAINT DF_class_objects_object_type DEFAULT (185),
            CONSTRAINT CK_class_objects_object_type CHECK (object_type = 185);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_class_objects_objects')
    ALTER TABLE dbo.class_objects
        ADD CONSTRAINT FK_class_objects_objects
            FOREIGN KEY (class_id, object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

IF COL_LENGTH('dbo.items','object_type') IS NULL
    ALTER TABLE dbo.items
        ADD object_type INT NOT NULL
            CONSTRAINT DF_items_object_type DEFAULT (1),
            CONSTRAINT CK_items_object_type CHECK (object_type = 1);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_items_objects')
    ALTER TABLE dbo.items
        ADD CONSTRAINT FK_items_objects
            FOREIGN KEY (item_id, object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

IF COL_LENGTH('dbo.tags','object_type') IS NULL
    ALTER TABLE dbo.tags
        ADD object_type INT NOT NULL
            CONSTRAINT DF_tags_object_type DEFAULT (212),
            CONSTRAINT CK_tags_object_type CHECK (object_type = 212);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_tags_objects')
    ALTER TABLE dbo.tags
        ADD CONSTRAINT FK_tags_objects
            FOREIGN KEY (tag_id, object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

-- scopes: registry_object_type rather than object_type, because the
-- existing object_type column holds the type of the business object
-- the scope represents, which is a different thing and is nullable.
IF COL_LENGTH('dbo.scopes','registry_object_type') IS NULL
    ALTER TABLE dbo.scopes
        ADD registry_object_type INT NOT NULL
            CONSTRAINT DF_scopes_registry_object_type DEFAULT (227),
            CONSTRAINT CK_scopes_registry_object_type CHECK (registry_object_type = 227);
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_scopes_objects')
    ALTER TABLE dbo.scopes
        ADD CONSTRAINT FK_scopes_objects
            FOREIGN KEY (scope_id, registry_object_type)
            REFERENCES dbo.objects(object_id, object_type);
GO

-- The business object a scope represents, when present, must itself be
-- a real registry entry -- a scope cannot stand for an Organization or
-- Project that the registry has never heard of.
--
-- Both columns are nullable and MUST be supplied together. A composite
-- FK is not checked at all when either column is NULL, so without this
-- CHECK a row naming an object_id with no object_type would bypass the
-- foreign key entirely rather than being rejected.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_scopes_context_object_pair')
    ALTER TABLE dbo.scopes
        ADD CONSTRAINT CK_scopes_context_object_pair
            CHECK
            (
                (object_id IS NULL AND object_type IS NULL)
                OR
                (object_id IS NOT NULL AND object_type IS NOT NULL)
            );
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_scopes_context_object')
    ALTER TABLE dbo.scopes
        ADD CONSTRAINT FK_scopes_context_object
        FOREIGN KEY (object_id, object_type)
        REFERENCES dbo.objects(object_id, object_type);
GO

/********************************************************************
 Registry reaping on delete

 The foreign keys above block deleting an objects row while its domain
 row lives, which is what makes the invariant hold. These triggers
 supply the other half: deleting the domain row reaps the registry row
 in the same transaction, so no orphan is left behind in objects.

 Each trigger is set-based and re-checks the domain table, so a delete
 that is rolled back or partially applied cannot strand a registry row
 that is still in use.
********************************************************************/

CREATE OR ALTER TRIGGER dbo.TR_namespaces_delete_object
ON dbo.namespaces
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.namespace_id AND o.object_type = 226
    WHERE NOT EXISTS (SELECT 1 FROM dbo.namespaces AS n
                      WHERE n.namespace_id = d.namespace_id);
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_class_groups_delete_object
ON dbo.class_groups
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.group_id AND o.object_type = 184
    WHERE NOT EXISTS (SELECT 1 FROM dbo.class_groups AS g
                      WHERE g.group_id = d.group_id);
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_uom_units_delete_object
ON dbo.uom_units
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.unit_id AND o.object_type = 285
    WHERE NOT EXISTS (SELECT 1 FROM dbo.uom_units AS u
                      WHERE u.unit_id = d.unit_id);
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_class_objects_delete_object
ON dbo.class_objects
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.class_id AND o.object_type = 185
    WHERE NOT EXISTS (SELECT 1 FROM dbo.class_objects AS c
                      WHERE c.class_id = d.class_id);
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_items_delete_object
ON dbo.items
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.item_id AND o.object_type = 1
    WHERE NOT EXISTS (SELECT 1 FROM dbo.items AS i
                      WHERE i.item_id = d.item_id);
END;
GO

CREATE OR ALTER TRIGGER dbo.TR_tags_delete_object
ON dbo.tags
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;
    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.tag_id AND o.object_type = 212
    WHERE NOT EXISTS (SELECT 1 FROM dbo.tags AS t
                      WHERE t.tag_id = d.tag_id);
END;
GO

-- scopes: the extra guard here is objects.scope_id. Because that
-- column has no foreign key (see the circularity note above), nothing
-- would otherwise stop this trigger reaping a registry row while other
-- objects still sit inside that scope, leaving them pointing at a
-- scope that no longer exists.
CREATE OR ALTER TRIGGER dbo.TR_scopes_delete_object
ON dbo.scopes
AFTER DELETE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS
    (
        SELECT 1
        FROM deleted AS d
        WHERE EXISTS (SELECT 1 FROM dbo.objects AS o
                      WHERE o.scope_id = d.scope_id)
          AND NOT EXISTS (SELECT 1 FROM dbo.scopes AS s
                          WHERE s.scope_id = d.scope_id)
    )
    BEGIN
        RAISERROR
        (
            'Cannot delete scope: objects still reference it via objects.scope_id. Reassign or remove them first.',
            16, 1
        );
        ROLLBACK TRANSACTION;
        RETURN;
    END;

    DELETE o
    FROM dbo.objects AS o
    INNER JOIN deleted AS d
        ON o.object_id = d.scope_id AND o.object_type = 227
    WHERE NOT EXISTS (SELECT 1 FROM dbo.scopes AS s
                      WHERE s.scope_id = d.scope_id);
END;
GO
