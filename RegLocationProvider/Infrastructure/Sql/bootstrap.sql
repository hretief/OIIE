/* ============================================================================
   REG-LOCATION bootstrap data

   Owned by RegLocationProvider, for the same reason as schema.sql: this is
   the seed the app actually applies, so it ships with the app rather than
   being linked out of docs/DDL. Derived from
   docs/DDL/REG-LOCATION_BOOTSTRAP.SQL.

   Seeds the minimal reference data the functional location registry needs:
   the Global namespace, the two class groups a location registry actually
   uses, the mandatory transaction row, a couple of units, a small set of
   functional location tags to hang a demo from, and the matching rows in the
   objects master registry.

   Re-runnable. Every insert is guarded on the primary key -- or on the
   natural key where one exists -- so applying this twice leaves the same rows
   rather than duplicating them.

   Run AFTER schema.sql.

   ----------------------------------------------------------------------------
   THE OBJECTS INVARIANT -- read this before adding rows to any table

   Every registered row in this schema must have a matching row in
   dbo.objects, keyed (object_id, object_type). This is how EIS works: each
   populate routine that creates a class group, class or namespace inserts the
   registry row in the same breath.

   Nothing enforces it. There is no foreign key into dbo.objects, no trigger
   and no check constraint -- the foreign keys all point the other way
   (class_objects -> class_groups, tags -> items). An INSERT into tags that
   forgets its registry row therefore SUCCEEDS, silently, and the orphan is
   only visible to a query that goes looking for it.

   So this is a writer's obligation, not something the database will remind
   anyone about. Whatever inserts a tag, item, class, class group, namespace
   or unit -- this script, the provider, a migration -- must insert the
   objects row too, ideally in the same transaction.

   The object_type for each table is listed in the objects section below.

   One deliberate exception: transactions. EIS defines no base type for it
   (there is no ebps_populate_base_types entry for trn_id), because it is
   bookkeeping rather than a registered object. It is the only table here with
   no registry row, and that is intentional rather than an omission.

   The verification query at the foot of this script reports orphans in every
   registered table. It is worth running after any bulk load.

   ----------------------------------------------------------------------------
   On the relationship to EIS-POPULATE.SQL

   Values here are harvested from EIS-POPULATE.SQL so the identifiers match a
   real EIS instance, but that script cannot be run against this schema and
   was not translated wholesale. schema.sql is deliberately a minimal
   TAGS domain model, whereas EIS-POPULATE targets the full product schema.
   It inserts columns that do not exist here (namespaces.name,
   class_groups.object_type/name/status,
   class_objects.code/name/sys_type/parent_class_id,
   transactions.date_added/person_id), depends on tables this model omits
   (settings, setting_defs, uom_dimensions, uom_unit_systems), and drives
   everything through EIS stored procedures (ebps_drop, ebp_new_id,
   ebp_add_unit).

   So most of the names below survive only as comments. That is a real
   limitation rather than a stylistic choice: this schema cannot store them,
   and inventing columns to hold them would stop it being the minimal model
   it is documented to be. The ids are the part that has to agree with EIS,
   and those are carried across exactly.

   The objects registry IS present here, so the object_type codes it records
   are carried as data rather than as comments -- see the objects section.

   EIS-POPULATE also contains no items or tags seed data at all -- in EIS
   those are customer data, not populate content -- so the tags below are
   sandbox fixtures chosen to match the reg-location personality pack rather
   than harvested values.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ============================================================================
   objects -- SEEDED FIRST, and it must stay that way

   The master registry. schema.sql enforces the registry invariant with
   a composite foreign key from every registered table into dbo.objects, so a
   domain row cannot be inserted until its registry row exists. This section
   therefore runs BEFORE the domain tables. Moving it back to the foot of the
   script will fail with Msg 547 on the very first insert.

   The ids below are repeated from the domain sections rather than selected
   out of the domain tables, which is deliberate. An earlier version did
   INSERT ... SELECT FROM dbo.items, which meant re-running the bootstrap
   silently adopted any stray unregistered row instead of leaving it visible
   to REG-LOCATION_VERIFY.SQL. Registering only this script's own seed data
   keeps the bootstrap and the integrity check independent of each other.
   Adding a row to a domain section below means adding its id here too.

   object_type codes are from ebps_populate_base_types in EIS-POPULATE.SQL and
   are authoritative -- unlike the names elsewhere in this script, they have a
   column here and so are carried as data:

       184  ClassGroup    -> class_groups.group_id
       185  Class         -> class_objects.class_id
       212  Tag           -> tags.tag_id
       226  Namespace     -> namespaces.namespace_id
       227  Scope         -> scopes.scope_id
       285  Unit          -> uom_units.unit_id
         1  PhysicalItem  -> items.item_id

   transactions has no entry: EIS defines no base type for it, because it is
   bookkeeping rather than a registered object.

   The Scope row comes first, because every objects row here carries
   scope_id = 1 and the Global scope is what that refers to. EIS hits the same
   knot -- its comments call it "a chicken-egg issue" -- and resolves it by
   inserting scopes first and back-filling objects. This script does the
   reverse, so registry-first stays the single ordering rule everywhere.

   The primary key is (object_id, object_type), so ids only need to be unique
   within a type -- group_id 5 and class_id 5 would be distinct rows. Every
   guard below therefore matches on both columns; matching on object_id alone
   would wrongly skip a row of a different type that happens to share an id.

   scope_id is 1 throughout, as EIS-POPULATE does for global reference data.

   hide_flags and lock_flags follow the source: reference data created by the
   populate scripts is locked against user modification (lock_flags 18 for
   classes, 0 for class groups), whereas the tags and items below are ordinary
   registry content and are left unlocked. TINYINT here, so the values must
   stay within 0-255.

   date_added and date_changed are nullable and deliberately left NULL rather
   than set to GETDATE(). This script is re-runnable and its output is
   compared between runs; stamping a wall-clock time would make two runs
   differ for no meaningful reason.

   guid is the FederationGuid and is populated only for tags -- see the tag
   objects note further down for why, and for the unresolved question about
   who authors those values.
   ============================================================================ */
INSERT INTO dbo.objects (object_id, object_type, guid, scope_id, hide_flags, lock_flags)
SELECT v.object_id, v.object_type, v.guid, 1, 0, v.lock_flags
FROM (VALUES
    -- Scopes (227). Global. lock_flags 18 (2|16) as EIS sets it: cannot be
    -- modified or deleted.
    (1,    227, 18, CONVERT(UNIQUEIDENTIFIER, NULL)),   -- Global

    -- Namespaces (226)
    (1,    226, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- Global

    -- Class groups (184)
    (5,    184, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- Locations
    (17,   184, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- Tags

    -- Units (285)
    (1,    285, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- ONE, dimensionless
    (2,    285, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- CELSIUS (degC)

    -- Classes (185). Locked against user modification, as
    -- ebps_pop_announce_class_objs does for populate-created classes.
    (1001, 185, 18, CONVERT(UNIQUEIDENTIFIER, NULL)),   -- rdl:FunctionalLocation
    (1002, 185, 18, CONVERT(UNIQUEIDENTIFIER, NULL)),   -- rdl:Site
    (1701, 185, 18, CONVERT(UNIQUEIDENTIFIER, NULL)),   -- rdl:Equipment
    (1702, 185, 18, CONVERT(UNIQUEIDENTIFIER, NULL)),   -- rdl:Instrument

    -- Physical items (1)
    (1,      1, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- site
    (2,      1, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- process area
    (3,      1, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- separator
    (4,      1, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- temperature instrument
    (5,      1, 0,  CONVERT(UNIQUEIDENTIFIER, NULL)),   -- temperature instrument

    -- Tags (212), with federation identity
    (1,    212, 0,  CONVERT(UNIQUEIDENTIFIER, '550e8400-e29b-41d4-a716-446655440001')),  -- ACME-NORTH
    (2,    212, 0,  CONVERT(UNIQUEIDENTIFIER, '550e8400-e29b-41d4-a716-446655440002')),  -- ACME-NORTH-100
    (3,    212, 0,  CONVERT(UNIQUEIDENTIFIER, '550e8400-e29b-41d4-a716-446655440003')),  -- V-101
    (4,    212, 0,  CONVERT(UNIQUEIDENTIFIER, '550e8400-e29b-41d4-a716-446655440004')),  -- TIC-101 rev 1
    (5,    212, 0,  CONVERT(UNIQUEIDENTIFIER, '550e8400-e29b-41d4-a716-446655440004')),  -- TIC-101 rev 2 -- same thing, same identity
    (6,    212, 0,  CONVERT(UNIQUEIDENTIFIER, '550e8400-e29b-41d4-a716-446655440006'))   -- TI-102
) AS v(object_id, object_type, lock_flags, guid)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.objects AS o
    WHERE o.object_id = v.object_id AND o.object_type = v.object_type
);
GO

/* ============================================================================
   Tag federation identity -- note on the GUIDs seeded above

   objects.guid is the FederationGuid described in
   docs/FederationId/federation-guid-guideline.md: the identity of the thing
   itself, which becomes the CIRID in the Common Interoperability Registry.
   This is the one place in this schema where a federated identifier can be
   stored, which is what makes the tag rows different from the reference data.

   Fixed literals rather than NEWID(): a re-run must reproduce the same
   identity, and NEWID() would mint a fresh CIRID for the same tag on every
   run, which is precisely the failure the guideline exists to prevent.

   Revisions 1 and 2 of TIC-101 deliberately share one GUID. They are two
   revisions of the same functional location, not two things, and the
   guideline states the GUID survives revision -- a new one is minted only
   when the physical asset is replaced.

   NOTE -- unresolved, needs a decision before this is used beyond the sandbox.

   Per the guideline the FederationGuid originates in the iModel and travels
   with the published data; REG-LOCATION is a Subscriber on the Eng channel,
   so in a real exchange it would RECEIVE these GUIDs from ENG rather than
   author them. The values above are therefore placeholders that let the
   registry stand up standalone. They do not currently correspond to any
   element in ENG_BOOTSTRAP.SQL, so a demo spanning both participants will not
   federate on identity until they are reconciled -- either by seeding the
   matching FederationGuids here, or by leaving these NULL and letting the
   inbound flow populate them.
   ============================================================================ */

/* ============================================================================
   namespaces

   EIS-POPULATE.SQL adds exactly one namespace, id 1, named 'Global', and
   notes that the namespace scope is added later by scope.sql. Everything
   below hangs off it.
   ============================================================================ */
INSERT INTO dbo.namespaces (namespace_id)
SELECT 1
WHERE NOT EXISTS (SELECT 1 FROM dbo.namespaces WHERE namespace_id = 1);
GO

/* ============================================================================
   scopes

   The site boundary this dataset applies to -- the equivalent of an iTwin.
   Seeded after namespaces because of FK_scopes_namespace, and after the
   objects block above because of FK_scopes_objects.

   EIS creates exactly one scope in the populate path: scope_id 1, 'Global'.
   Its note is worth repeating, because it explains the three NULLs below:

       "The 'Global' Scope is the only scope to have no parent and never has
        a concrete underwriting Object."

   So parent_id is NULL, and object_id/object_type -- the OPTIONAL business
   object whose context the scope represents, such as an Organization (type 5)
   or a Project (9) -- are both NULL. Global is a plain boundary, not the
   context of any particular business object.

   Those two columns are NOT this scope's registry entry; that is the (1, 227)
   row inserted in the objects block above. The similarity of the column names
   is a genuine trap: setting them to (1, 227) here would claim the Global
   scope represents the Scope object itself, which is meaningless, and EIS is
   explicit that Global never has a concrete object behind it.

   usage_count 1 matches EIS, which seeds it as already in use rather than 0.
   ============================================================================ */
INSERT INTO dbo.scopes (scope_id, name, namespace_id, object_id, object_type, parent_id, is_enabled, usage_count)
SELECT 1, N'Global', 1, NULL, NULL, NULL, 'Y', 1
WHERE NOT EXISTS (SELECT 1 FROM dbo.scopes WHERE scope_id = 1);
GO

/* ============================================================================
   class_groups

   EIS defines ~36 groups. A functional location registry uses two of them,
   so only those two are seeded -- carrying the rest would be reference data
   this participant never reads.

   Ids and names are from ebps_populate_class_groups in EIS-POPULATE.SQL:

       group_id 5   object_type 12    Locations
       group_id 17  object_type 212   Tags

   The ids are what matter and are preserved. object_type, name and status
   have no column in this model.
   ============================================================================ */
INSERT INTO dbo.class_groups (group_id)
SELECT v.group_id
FROM (VALUES
    (5),    -- Locations
    (17)    -- Tags
) AS v(group_id)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.class_groups AS g WHERE g.group_id = v.group_id
);
GO

/* ============================================================================
   transactions

   EIS-POPULATE.SQL: "There must alway be one transaction record to start
   with", inserting trn_id 0. items.trn_id is NOT NULL with an FK here, so
   without this row nothing can be inserted into items at all.
   ============================================================================ */
INSERT INTO dbo.transactions (trn_id)
SELECT 0
WHERE NOT EXISTS (SELECT 1 FROM dbo.transactions WHERE trn_id = 0);
GO

/* ============================================================================
   uom_units

   items.unit_id is NOT NULL, so at least one unit must exist before any item
   can be created.

   EIS assigns unit ids at run time through ebp_add_unit rather than fixing
   them in the populate script, so unlike the class group ids above there are
   no authoritative values to carry across. These are sandbox-local ids; the
   codes they stand for are recorded in comments because this model has no
   column for them.

   Chosen to match the reg-location personality pack, whose property
   definitions use degC for the instrument range properties.
   ============================================================================ */
INSERT INTO dbo.uom_units (unit_id)
SELECT v.unit_id
FROM (VALUES
    (1),    -- ONE, dimensionless -- the default for a location, which is counted rather than measured
    (2)     -- CELSIUS (degC) -- carried by rdl:RangeMinimum and rdl:RangeMaximum
) AS v(unit_id)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.uom_units AS u WHERE u.unit_id = v.unit_id
);
GO

/* ============================================================================
   class_objects

   The classes a location registry classifies against. Keys mirror the
   reg-location personality pack (Oiie.Sandbox.Core/PersonalityPacks/
   reg-location/Fixtures/classes.yaml) so the emulation and the sandbox agree
   on what this participant knows.

   Note what is deliberately absent: rdl:TemperatureIndicatingController.
   REG-LOCATION holds the parent rdl:Instrument but not that leaf, which is
   what makes graceful degradation testable -- a tag classified against the
   leaf binds at rdl:Instrument and is recorded degraded rather than
   rejected. Adding it here would quietly destroy that scenario, so it is
   omitted on purpose rather than by oversight.

   class_id values are sandbox-local for the same reason as the unit ids:
   EIS mints them through ebp_new_id at run time.
   ============================================================================ */
INSERT INTO dbo.class_objects (class_id, group_id, namespace_id)
SELECT v.class_id, v.group_id, v.namespace_id
FROM (VALUES
    (1001, 5,  1),   -- rdl:FunctionalLocation -- Locations group
    (1002, 5,  1),   -- rdl:Site               -- Locations group
    (1701, 17, 1),   -- rdl:Equipment          -- Tags group
    (1702, 17, 1)    -- rdl:Instrument         -- Tags group, child of rdl:Equipment in the pack
) AS v(class_id, group_id, namespace_id)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.class_objects AS c WHERE c.class_id = v.class_id
);
GO

/* ============================================================================
   items

   tags.item_id is NOT NULL, and the unique constraint is on
   (item_id, code, revision) rather than on code alone -- so an item is the
   thing a tag is a revision *of*, and two tags may share a code only if they
   sit under different items.

   One item per functional location below.
   ============================================================================ */
INSERT INTO dbo.items (item_id, namespace_id, unit_id, trn_id)
SELECT v.item_id, v.namespace_id, v.unit_id, v.trn_id
FROM (VALUES
    (1, 1, 1, 0),   -- site
    (2, 1, 1, 0),   -- process area
    (3, 1, 1, 0),   -- separator
    (4, 1, 2, 0),   -- temperature instrument -- degC
    (5, 1, 2, 0)    -- temperature instrument -- degC
) AS v(item_id, namespace_id, unit_id, trn_id)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.items AS i WHERE i.item_id = v.item_id
);
GO

/* ============================================================================
   tags

   The functional locations themselves. Codes follow ISA-style plant
   numbering so the demo reads as a real register.

   Guarded on (item_id, code, revision) rather than tag_id, matching
   UQ_tags_item_code_revision -- guarding on the surrogate alone would let a
   re-run with a different tag_id violate the natural key instead of being
   skipped.
   ============================================================================ */
INSERT INTO dbo.tags (tag_id, item_id, class_id, code, revision, name)
SELECT v.tag_id, v.item_id, v.class_id, v.code, v.revision, v.name
FROM (VALUES
    (1, 1, 1002, N'ACME-NORTH',      1, N'Acme North Site'),
    (2, 2, 1001, N'ACME-NORTH-100',  1, N'Separation Area 100'),
    (3, 3, 1701, N'V-101',           1, N'Inlet Separator V-101'),
    (4, 4, 1702, N'TIC-101',         1, N'Inlet Separator Temperature Control'),
    -- Second revision of the same code under the same item: the revision
    -- column exists precisely so a register can hold supersession, and a
    -- fixture that never exercised it would leave that untested.
    (5, 4, 1702, N'TIC-101',         2, N'Inlet Separator Temperature Control'),
    (6, 5, 1702, N'TI-102',          1, N'Overhead Line Temperature Indicator')
) AS v(tag_id, item_id, class_id, code, revision, name)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.tags AS t
    WHERE t.item_id = v.item_id AND t.code = v.code AND t.revision = v.revision
);
GO

/* ============================================================================
   Verification -- the objects invariant

   Reports any registered row missing its dbo.objects entry.

   Since schema.sql now enforces the invariant with composite foreign
   keys, this should be structurally impossible: an unregistered insert fails
   with Msg 547 rather than succeeding quietly. The check is kept as a
   backstop, because the constraints can be bypassed -- a bulk insert run with
   IGNORE_CONSTRAINTS, or a constraint left NOCHECK after a restore, would
   both let orphans in without any error.

   Expect every count to be zero. A non-zero row means the constraints were
   circumvented, so check the trust state of the foreign keys as well as
   adding the missing objects row.

   This check also lives on its own in REG-LOCATION_VERIFY.SQL, which is the
   copy to run after any bulk load or restore.

   transactions is absent by design -- see the header.
   ============================================================================ */
SELECT 'namespaces' AS table_name, 226 AS object_type, COUNT(*) AS orphaned
FROM dbo.namespaces AS n
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = n.namespace_id AND o.object_type = 226)
UNION ALL
SELECT 'class_groups', 184, COUNT(*)
FROM dbo.class_groups AS g
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = g.group_id AND o.object_type = 184)
UNION ALL
SELECT 'uom_units', 285, COUNT(*)
FROM dbo.uom_units AS u
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = u.unit_id AND o.object_type = 285)
UNION ALL
SELECT 'class_objects', 185, COUNT(*)
FROM dbo.class_objects AS c
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = c.class_id AND o.object_type = 185)
UNION ALL
SELECT 'items', 1, COUNT(*)
FROM dbo.items AS i
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = i.item_id AND o.object_type = 1)
UNION ALL
SELECT 'tags', 212, COUNT(*)
FROM dbo.tags AS t
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = t.tag_id AND o.object_type = 212);
GO
