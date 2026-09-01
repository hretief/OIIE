/* ============================================================================
   ENG -- drop every table in dbo.

   The companion to schema.sql, and deliberately a separate file. schema.sql
   creates and does not migrate; it has no opinion about removal, and adding
   DROPs to it would make every cold start a potential data loss.

   This runs only for an explicit reset.

   Two things are dynamic rather than a fixed list, both learned the hard way.

   First, every foreign key is dropped before any table is. A fixed drop order
   works only while the schema is the one this file was written against; the
   moment a database carries a constraint from an older shape, a
   child-to-parent list fails on a reference it has never heard of.

   Second, the tables themselves are enumerated rather than named. A demo
   database accumulates tables from earlier versions of the schema, and those
   are precisely the clutter a reset exists to remove -- naming only the
   current tables would leave the stale ones behind, still holding data and
   still referencing each other. ENG owns this database outright, so there is
   nothing here that is not ENG's to drop.

   Views are dropped too: schema.sql recreates them with CREATE OR ALTER, so
   leaving a view bound to a table that no longer exists serves no purpose.
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

/* Then the views. */
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
