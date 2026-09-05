/* ============================================================================
   ENG bootstrap data

   Registers the EC schemas and classes ENG can store an element against.

   This is ENG's own reference data. A participant provisions the vocabulary it
   knows, because a class list is customer data held in the customer's store --
   not something a shared host can hold on the participant's behalf. The
   Sandbox used to carry a parallel library for every participant; it had no
   writer once BOD handling moved into the engines, and has been removed.

   Deliberately limited to schema and class metadata. No iTwin, no iModel and
   no root element are seeded here: those are instances, they arrive through
   the API like anything else, and pre-creating them makes a greenfield look
   like work has already happened.

   Re-runnable. Every insert is guarded, so applying this twice leaves the same
   rows rather than duplicating them -- ECClass in particular is only unique on
   (ECSchemaId, ClassName), so an unguarded re-run would silently double every
   class rather than fail loudly.

   Run AFTER schema.sql.
   ============================================================================ */

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ============================================================================
   EC schemas
   ============================================================================ */
MERGE dbo.ECSchema AS target
USING
(
    VALUES
    (N'BisCore',    N'bis',  N'01.00.13', N'BIS Core',   N'Bentley BIS core schema'),
    (N'Functional', N'func', N'01.00.03', N'Functional', N'Bentley functional schema'),
    (N'ENG',        N'eng',  N'01.00.10', N'ENG',        N'ENG domain schema')
)
AS source (SchemaName, SchemaAlias, SchemaVersion, DisplayLabel, Description)
ON  target.SchemaName    = source.SchemaName
AND target.SchemaVersion = source.SchemaVersion
WHEN NOT MATCHED BY TARGET THEN
    INSERT (SchemaName, SchemaAlias, SchemaVersion, DisplayLabel, Description)
    VALUES (source.SchemaName, source.SchemaAlias, source.SchemaVersion,
            source.DisplayLabel, source.Description);
GO

/* ============================================================================
   EC classes

   The abstract BIS/Functional classes exist so the inheritance chain below has
   something to point at. They are marked Abstract, and schema.sql forbids
   creating an Element on an abstract class, so registering them here does not
   make them instantiable.
   ============================================================================ */
DECLARE @BisCoreSchemaId BIGINT =
(
    SELECT ECSchemaId FROM dbo.ECSchema
    WHERE SchemaName = N'BisCore' AND SchemaVersion = N'01.00.13'
);

DECLARE @FunctionalSchemaId BIGINT =
(
    SELECT ECSchemaId FROM dbo.ECSchema
    WHERE SchemaName = N'Functional' AND SchemaVersion = N'01.00.03'
);

DECLARE @EngSchemaId BIGINT =
(
    SELECT ECSchemaId FROM dbo.ECSchema
    WHERE SchemaName = N'ENG' AND SchemaVersion = N'01.00.10'
);

IF @BisCoreSchemaId IS NULL OR @FunctionalSchemaId IS NULL OR @EngSchemaId IS NULL
    THROW 50000, 'EC schema registration did not complete; cannot register classes.', 1;

MERGE dbo.ECClass AS target
USING
(
    VALUES
    -- Inheritance scaffolding.
    (@BisCoreSchemaId,    N'Element',                     N'Element',                      N'Base BIS Element class',       N'Abstract'),
    (@BisCoreSchemaId,    N'RoleElement',                 N'Role Element',                 N'BIS role element',             N'Abstract'),
    (@FunctionalSchemaId, N'FunctionalElement',           N'Functional Element',           N'Functional role element',      N'Abstract'),
    (@FunctionalSchemaId, N'FunctionalComponentElement',  N'Functional Component Element', N'Functional component element', N'None'),

    -- ENG concrete classes.
    (@EngSchemaId, N'CabinetFeederPillar', N'Cabinet | Feeder Pillar',         NULL, N'Sealed'),
    (@EngSchemaId, N'Cantilever',          N'Structures | Cantilever',         NULL, N'Sealed'),
    (@EngSchemaId, N'Controller',          N'Traffic Signal | Controller',     NULL, N'Sealed'),

    /* Deliberately 'None' rather than 'Sealed'. Equipment is a general
       classification rather than one specific product, so it is instantiable
       AND still available as a base for a more specific class later. Sealing
       it would force the next specialisation to be a sibling instead. */
    (@EngSchemaId, N'Equipment',           N'Equipment',                       N'General ENG equipment item', N'None'),

    (@EngSchemaId, N'Gantry',              N'Structures | Gantry',             NULL, N'Sealed'),
    (@EngSchemaId, N'LightingBracket',     N'Lighting | Bracket',              NULL, N'Sealed'),
    (@EngSchemaId, N'LightingColumn',      N'Lighting | Column',               NULL, N'Sealed'),
    (@EngSchemaId, N'LightingLantern',     N'Lighting | Lantern',              NULL, N'Sealed'),
    (@EngSchemaId, N'Loop',                N'Detector | Loop',                 NULL, N'Sealed'),
    (@EngSchemaId, N'MessageSign',         N'Signs | Message Sign',            NULL, N'Sealed'),
    (@EngSchemaId, N'MidHingeColumn',      N'CCTV | Mid-Hinge Column',         NULL, N'Sealed'),
    (@EngSchemaId, N'Pole',                N'Traffic Signal | Pole',           NULL, N'Sealed'),
    (@EngSchemaId, N'Portal',              N'Structures | Portal',             NULL, N'Sealed'),
    (@EngSchemaId, N'Post',                N'Signs | Post',                    NULL, N'Sealed'),
    (@EngSchemaId, N'PtzCamera',           N'CCTV | PTZ Camera',               NULL, N'Sealed'),
    (@EngSchemaId, N'Radar',               N'Detector | Radar',                NULL, N'Sealed'),
    (@EngSchemaId, N'SignalHead',          N'Traffic Signal | Signal Head',    NULL, N'Sealed'),
    (@EngSchemaId, N'SignFace',            N'Signs | Sign Face',               NULL, N'Sealed'),
    (@EngSchemaId, N'StaticCamera',        N'CCTV | Static Camera',            NULL, N'Sealed'),
    (@EngSchemaId, N'Streetlight',         N'Lighting | Streetlight',          NULL, N'Sealed'),
    (@EngSchemaId, N'Substation',          N'Power | Substation',              NULL, N'Sealed'),
    (@EngSchemaId, N'SupportPost',         N'Traffic Signal | Support Post',   NULL, N'Sealed'),
    (@EngSchemaId, N'Tower',               N'CCTV | Tower',                    NULL, N'Sealed'),
    (@EngSchemaId, N'TrafficLight',        N'Traffic Signal | Traffic Light',  NULL, N'Sealed'),
    (@EngSchemaId, N'TrafficSignal',       N'Traffic Signal | Traffic Signal', NULL, N'Sealed'),
    (@EngSchemaId, N'Vms',                 N'Signs | VMS',                     NULL, N'Sealed'),
    (@EngSchemaId, N'WeatherStation',      N'Detector | Weather Station',      NULL, N'Sealed')
)
AS source (ECSchemaId, ClassName, DisplayLabel, Description, ClassModifier)
ON  target.ECSchemaId = source.ECSchemaId
AND target.ClassName  = source.ClassName
WHEN NOT MATCHED BY TARGET THEN
    INSERT (ECSchemaId, ClassName, DisplayLabel, Description, ClassModifier)
    VALUES (source.ECSchemaId, source.ClassName, source.DisplayLabel,
            source.Description, source.ClassModifier);
GO

/* ============================================================================
   Class inheritance

   Element <- RoleElement <- FunctionalElement <- FunctionalComponentElement,
   and every ENG concrete class derives from FunctionalComponentElement.
   ============================================================================ */
DECLARE @ElementClassId BIGINT =
(
    SELECT c.ECClassId
    FROM dbo.ECClass AS c
    INNER JOIN dbo.ECSchema AS s ON s.ECSchemaId = c.ECSchemaId
    WHERE s.SchemaName = N'BisCore' AND c.ClassName = N'Element'
);

DECLARE @RoleElementClassId BIGINT =
(
    SELECT c.ECClassId
    FROM dbo.ECClass AS c
    INNER JOIN dbo.ECSchema AS s ON s.ECSchemaId = c.ECSchemaId
    WHERE s.SchemaName = N'BisCore' AND c.ClassName = N'RoleElement'
);

DECLARE @FunctionalElementClassId BIGINT =
(
    SELECT c.ECClassId
    FROM dbo.ECClass AS c
    INNER JOIN dbo.ECSchema AS s ON s.ECSchemaId = c.ECSchemaId
    WHERE s.SchemaName = N'Functional' AND c.ClassName = N'FunctionalElement'
);

DECLARE @FunctionalComponentElementClassId BIGINT =
(
    SELECT c.ECClassId
    FROM dbo.ECClass AS c
    INNER JOIN dbo.ECSchema AS s ON s.ECSchemaId = c.ECSchemaId
    WHERE s.SchemaName = N'Functional' AND c.ClassName = N'FunctionalComponentElement'
);

IF @ElementClassId IS NULL
   OR @RoleElementClassId IS NULL
   OR @FunctionalElementClassId IS NULL
   OR @FunctionalComponentElementClassId IS NULL
    THROW 50000, 'EC class registration did not complete; cannot register inheritance.', 1;

INSERT INTO dbo.ECClassBase (ECClassId, BaseECClassId, BaseClassOrder)
SELECT v.ECClassId, v.BaseECClassId, v.BaseClassOrder
FROM
(
    VALUES
    (@RoleElementClassId,                @ElementClassId,           CAST(1 AS SMALLINT)),
    (@FunctionalElementClassId,          @RoleElementClassId,       CAST(1 AS SMALLINT)),
    (@FunctionalComponentElementClassId, @FunctionalElementClassId, CAST(1 AS SMALLINT))
) AS v (ECClassId, BaseECClassId, BaseClassOrder)
WHERE NOT EXISTS
(
    SELECT 1 FROM dbo.ECClassBase AS b
    WHERE b.ECClassId = v.ECClassId AND b.BaseECClassId = v.BaseECClassId
);

/* Every ENG concrete class derives from FunctionalComponentElement in this
   simplified bootstrap metadata. */
INSERT INTO dbo.ECClassBase (ECClassId, BaseECClassId, BaseClassOrder)
SELECT
    c.ECClassId,
    @FunctionalComponentElementClassId,
    CAST(1 AS SMALLINT)
FROM dbo.ECClass AS c
INNER JOIN dbo.ECSchema AS s ON s.ECSchemaId = c.ECSchemaId
WHERE s.SchemaName = N'ENG'
  AND NOT EXISTS
  (
      SELECT 1 FROM dbo.ECClassBase AS b
      WHERE b.ECClassId = c.ECClassId
        AND b.BaseECClassId = @FunctionalComponentElementClassId
  );
GO
