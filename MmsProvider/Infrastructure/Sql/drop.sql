/* ============================================================================
   MMS -- drop every table in dbo.

   The companion to schema.sql, and deliberately a separate file. schema.sql
   creates and does not migrate; adding DROPs to it would make every cold start
   a potential data loss. This runs only for an explicit reset.

   Everything here is enumerated rather than named, for two reasons learned
   from the ENG teardown that this mirrors.

   Every foreign key is dropped before any table is, so table order cannot
   matter. A hand-written child-to-parent list is correct only for the schema
   it was written against; the moment a database carries a constraint from an
   older shape, the list fails on a reference it has never heard of.

   The tables are enumerated for the same reason. A demo database accumulates
   tables from earlier versions of the schema, and those are precisely the
   clutter a reset exists to remove -- naming only the current tables would
   leave the stale ones behind, still holding rows. MMS owns this database
   outright, so there is nothing here that is not MMS's to drop.
   ============================================================================ */

/* Foreign keys first, so table order cannot matter. */
DECLARE @sql NVARCHAR(MAX) = N'';

SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id))
            + N'.' + QUOTENAME(t.name)
            + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';' + CHAR(10)
FROM sys.foreign_keys AS fk
JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
WHERE SCHEMA_NAME(t.schema_id) = N'dbo';

IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO

/* Then the views, which schema.sql recreates. */
DECLARE @sql NVARCHAR(MAX) = N'';

SELECT @sql = @sql + N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(v.schema_id))
            + N'.' + QUOTENAME(v.name) + N';' + CHAR(10)
FROM sys.views AS v
WHERE SCHEMA_NAME(v.schema_id) = N'dbo';

IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO

/* Then every table. */
DECLARE @sql NVARCHAR(MAX) = N'';

SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id))
            + N'.' + QUOTENAME(t.name) + N';' + CHAR(10)
FROM sys.tables AS t
WHERE SCHEMA_NAME(t.schema_id) = N'dbo';

IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO
