/* ============================================================================
   REG-LOCATION -- drop every object in dbo.

   The companion to schema.sql, and deliberately a separate file. schema.sql
   creates and does not migrate; adding DROPs to it would make every cold start
   a potential data loss. This runs only for an explicit reset.

   Everything here is enumerated rather than named, for two reasons learned
   from the ENG teardown that this mirrors.

   Every foreign key is dropped before any table is, so table order cannot
   matter. This schema is EIS-derived and densely self-referential -- objects,
   class_objects, class_groups, items, tags -- so a hand-written child-to-parent
   list would be both long and fragile, and would fail on the first constraint
   carried over from an older shape.

   The objects themselves are enumerated for the same reason: a demo database
   accumulates tables from earlier versions of the schema, and those are
   precisely the clutter a reset exists to remove. REG-LOCATION owns this
   database outright, so there is nothing here that is not its own to drop.

   Wider than the ENG and MMS equivalents because this schema carries more than
   tables: the registry maintains dbo.objects through AFTER DELETE triggers, and
   the EIS lineage brings procedures and functions with it. Triggers go with
   their tables, but procedures and functions are schema-scoped and would
   otherwise survive as objects bound to tables that no longer exist.
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

/* Then views, procedures and functions, all of which schema.sql recreates. */
DECLARE @sql NVARCHAR(MAX) = N'';

SELECT @sql = @sql + N'DROP VIEW ' + QUOTENAME(SCHEMA_NAME(v.schema_id))
            + N'.' + QUOTENAME(v.name) + N';' + CHAR(10)
FROM sys.views AS v
WHERE SCHEMA_NAME(v.schema_id) = N'dbo';

SELECT @sql = @sql + N'DROP PROCEDURE ' + QUOTENAME(SCHEMA_NAME(p.schema_id))
            + N'.' + QUOTENAME(p.name) + N';' + CHAR(10)
FROM sys.procedures AS p
WHERE SCHEMA_NAME(p.schema_id) = N'dbo';

SELECT @sql = @sql + N'DROP FUNCTION ' + QUOTENAME(SCHEMA_NAME(o.schema_id))
            + N'.' + QUOTENAME(o.name) + N';' + CHAR(10)
FROM sys.objects AS o
WHERE SCHEMA_NAME(o.schema_id) = N'dbo'
  AND o.type IN (N'FN', N'IF', N'TF');

IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO

/* Then every table. Triggers go with the table they are defined on. */
DECLARE @sql NVARCHAR(MAX) = N'';

SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id))
            + N'.' + QUOTENAME(t.name) + N';' + CHAR(10)
FROM sys.tables AS t
WHERE SCHEMA_NAME(t.schema_id) = N'dbo';

IF LEN(@sql) > 0 EXEC sp_executesql @sql;
GO
