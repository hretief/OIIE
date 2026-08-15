-- Carries the originator's iTwin context through the REG-LOCATION gate.
--
-- ENG publishes each segment with a RegistrationSite naming the twin it belongs
-- to. Before these columns existed the registry dropped that context on arrival,
-- so by the time an approved location reached MMS there was nothing left to
-- resolve an OWNER_ID against and the row landed unowned, invisible to every
-- twin-scoped view.
--
-- Applied separately rather than through the usual schema initialiser: that path
-- only creates tables it cannot find, so it has no effect on a StewardshipItem or
-- Location table that already exists. Dropping and recreating the schema would
-- work, but takes the other participants' data with it.
--
-- Idempotent, so it is safe to re-run against a database that already has them.

IF COL_LENGTH('reg_location.StewardshipItem', 'ContextSourceId') IS NULL
    ALTER TABLE reg_location.StewardshipItem ADD ContextSourceId nvarchar(64) NULL;

IF COL_LENGTH('reg_location.StewardshipItem', 'ContextIdInSource') IS NULL
    ALTER TABLE reg_location.StewardshipItem ADD ContextIdInSource nvarchar(200) NULL;

IF COL_LENGTH('reg_location.StewardshipItem', 'ContextName') IS NULL
    ALTER TABLE reg_location.StewardshipItem ADD ContextName nvarchar(400) NULL;

IF COL_LENGTH('reg_location.Location', 'ContextSourceId') IS NULL
    ALTER TABLE reg_location.Location ADD ContextSourceId nvarchar(64) NULL;

IF COL_LENGTH('reg_location.Location', 'ContextIdInSource') IS NULL
    ALTER TABLE reg_location.Location ADD ContextIdInSource nvarchar(200) NULL;

IF COL_LENGTH('reg_location.Location', 'ContextName') IS NULL
    ALTER TABLE reg_location.Location ADD ContextName nvarchar(400) NULL;
