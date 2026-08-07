/*
    Sandbox schema and grant model.

    Run once per environment against the target database. Idempotent: safe to re-run.

    Users are CONTAINED database users, created by provision.ps1 before this script
    runs. Server-level logins would be shared by every database on the server, so
    the dev, CI and demo databases would collide on sb_eng and friends — and the
    collision would surface as an authentication failure days later, not at
    provisioning time.

    Placeholders are substituted by provision.ps1:
        {{DATABASE}}   target database name

    The grants are the point of this script. Schema separation alone is a naming
    convention; separate logins with schema-scoped permissions make a cross-schema
    shortcut fail at development time rather than silently succeed and hollow out
    the demonstration.
*/

SET NOCOUNT ON;
GO

-------------------------------------------------------------------------------
-- Schemas
-------------------------------------------------------------------------------

DECLARE @schemas TABLE (name SYSNAME);
INSERT INTO @schemas (name) VALUES
    ('eng'), ('construct'), ('reg_location'), ('reg_asset'),
    ('reg_product'), ('reg_material'), ('mms'), ('rdl'),
    ('sandbox'), ('tower');

DECLARE @schema SYSNAME, @sql NVARCHAR(MAX);
DECLARE schema_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @schemas;

OPEN schema_cursor;
FETCH NEXT FROM schema_cursor INTO @schema;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schema)
    BEGIN
        SET @sql = N'CREATE SCHEMA ' + QUOTENAME(@schema) + N';';
        EXEC sp_executesql @sql;
        PRINT 'Created schema ' + @schema;
    END
    FETCH NEXT FROM schema_cursor INTO @schema;
END
CLOSE schema_cursor;
DEALLOCATE schema_cursor;
GO

-------------------------------------------------------------------------------
-- Participant users, each confined to one schema
-------------------------------------------------------------------------------

DECLARE @participants TABLE (schema_name SYSNAME, user_name SYSNAME);
INSERT INTO @participants (schema_name, user_name) VALUES
    ('eng',          'sb_eng'),
    ('construct',    'sb_construct'),
    ('reg_location', 'sb_reg_location'),
    ('reg_asset',    'sb_reg_asset'),
    ('reg_product',  'sb_reg_product'),
    ('reg_material', 'sb_reg_material'),
    ('mms',          'sb_mms'),
    ('rdl',          'sb_rdl');

DECLARE @schemaName SYSNAME, @userName SYSNAME, @stmt NVARCHAR(MAX);
DECLARE participant_cursor CURSOR LOCAL FAST_FORWARD
    FOR SELECT schema_name, user_name FROM @participants;

OPEN participant_cursor;
FETCH NEXT FROM participant_cursor INTO @schemaName, @userName;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @userName)
    BEGIN
        RAISERROR('Contained user %s is missing. Run provision.ps1, which creates it with a generated password.', 16, 1, @userName);
        RETURN;
    END

    SET @stmt = N'ALTER USER ' + QUOTENAME(@userName)
              + N' WITH DEFAULT_SCHEMA = ' + QUOTENAME(@schemaName) + N';';
    EXEC sp_executesql @stmt;

    -- Own schema: full DML, plus ALTER so objects can be created inside it.
    SET @stmt = N'GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE, ALTER, REFERENCES ON SCHEMA::'
              + QUOTENAME(@schemaName) + N' TO ' + QUOTENAME(@userName) + N';';
    EXEC sp_executesql @stmt;

    -- CREATE TABLE is a database-level permission and cannot be granted ON SCHEMA.
    -- Isolation still holds: creating a table also requires ALTER on the target
    -- schema, which each participant has only for its own.
    SET @stmt = N'GRANT CREATE TABLE TO ' + QUOTENAME(@userName) + N';';
    EXEC sp_executesql @stmt;

    -- Everything else: explicitly denied. DENY outranks any future GRANT, so a
    -- later convenience grant cannot silently reopen the boundary.
    DECLARE @other SYSNAME;
    DECLARE other_cursor CURSOR LOCAL FAST_FORWARD
        FOR SELECT schema_name FROM @participants WHERE schema_name <> @schemaName
            UNION SELECT 'tower';

    OPEN other_cursor;
    FETCH NEXT FROM other_cursor INTO @other;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @stmt = N'DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::'
                  + QUOTENAME(@other) + N' TO ' + QUOTENAME(@userName) + N';';
        EXEC sp_executesql @stmt;
        FETCH NEXT FROM other_cursor INTO @other;
    END
    CLOSE other_cursor;
    DEALLOCATE other_cursor;

    -- Scenario runs and assertions are written by every participant.
    SET @stmt = N'GRANT SELECT, INSERT, UPDATE ON SCHEMA::sandbox TO ' + QUOTENAME(@userName) + N';';
    EXEC sp_executesql @stmt;

    PRINT 'Confined ' + @userName + ' to schema ' + @schemaName;

    FETCH NEXT FROM participant_cursor INTO @schemaName, @userName;
END
CLOSE participant_cursor;
DEALLOCATE participant_cursor;
GO

-------------------------------------------------------------------------------
-- Orchestrator: scenario engine, reset, seeding
-------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'sb_orchestrator')
    RAISERROR('Contained user sb_orchestrator is missing. Run provision.ps1 first.', 16, 1);
GO

ALTER USER [sb_orchestrator] WITH DEFAULT_SCHEMA = [sandbox];
GO

-- Reset truncates every participant schema, so this principal is deliberately broad.
ALTER ROLE db_owner ADD MEMBER [sb_orchestrator];
GO

-------------------------------------------------------------------------------
-- Control tower: the single sanctioned cross-schema reader
-------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'sb_tower')
    RAISERROR('Contained user sb_tower is missing. Run provision.ps1 first.', 16, 1);
GO

ALTER USER [sb_tower] WITH DEFAULT_SCHEMA = [tower];
GO

GRANT SELECT, ALTER ON SCHEMA::tower TO [sb_tower];
GO

-- CREATE VIEW, like CREATE TABLE, is database-level rather than schema-scoped.
GRANT CREATE VIEW TO [sb_tower];
GO

-- Read-only across participants. Never granted INSERT/UPDATE/DELETE: the tower
-- observes the ecosystem, it does not participate in it.
DECLARE @towerSchema SYSNAME, @towerStmt NVARCHAR(MAX);
DECLARE tower_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT name FROM sys.schemas
    WHERE name IN ('eng','construct','reg_location','reg_asset',
                   'reg_product','reg_material','mms','rdl','sandbox');

OPEN tower_cursor;
FETCH NEXT FROM tower_cursor INTO @towerSchema;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @towerStmt = N'GRANT SELECT ON SCHEMA::' + QUOTENAME(@towerSchema) + N' TO [sb_tower];';
    EXEC sp_executesql @towerStmt;
    SET @towerStmt = N'DENY INSERT, UPDATE, DELETE ON SCHEMA::' + QUOTENAME(@towerSchema) + N' TO [sb_tower];';
    EXEC sp_executesql @towerStmt;
    FETCH NEXT FROM tower_cursor INTO @towerSchema;
END
CLOSE tower_cursor;
DEALLOCATE tower_cursor;
GO

PRINT 'Sandbox schema and grant model applied.';
GO
