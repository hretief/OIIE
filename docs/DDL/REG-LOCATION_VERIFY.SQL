/* ============================================================================
   REG-LOCATION -- objects invariant check

   Every registered row in this schema must have a matching row in dbo.objects,
   keyed (object_id, object_type). REG-LOCATION.SQL enforces this with a
   composite foreign key from each registered table into dbo.objects, plus
   AFTER DELETE triggers that reap the registry row when a domain row goes.

   So orphans should be structurally impossible, and this script is a backstop
   rather than the primary defence. It still earns its place: the constraints
   can be bypassed by a bulk insert run with IGNORE_CONSTRAINTS, or left
   untrusted by a restore or an ALTER ... NOCHECK, and in either case orphans
   would appear with no error raised.

   It is read-only by design -- do not use the bootstrap for this purpose,
   since it inserts registry rows and would mask what it is meant to reveal.

   Expect every count to be zero. A non-zero row means the constraints were
   circumvented; check the untrusted-constraint report below as well as adding
   the missing objects row.

   transactions is deliberately absent: EIS defines no base type for it
   (no ebps_populate_base_types entry), because it is bookkeeping rather than
   a registered object.

   Usage:
     sqlcmd -S "(localdb)\MSSQLLocalDB" -d <database> -i REG-LOCATION_VERIFY.SQL
   ============================================================================ */

SET NOCOUNT ON;

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
                  WHERE o.object_id = t.tag_id AND o.object_type = 212)
UNION ALL
SELECT 'scopes', 227, COUNT(*)
FROM dbo.scopes AS s
WHERE NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = s.scope_id AND o.object_type = 227);
GO

/* ============================================================================
   objects.scope_id integrity -- checked here because it CANNOT be a FK

   objects.scope_id points at scopes, and every scope needs a row in objects.
   Enforcing both directions with foreign keys would close a cycle and make the
   first row of either table impossible to insert. REG-LOCATION.SQL therefore
   leaves objects.scope_id without a foreign key, deliberately, and this query
   is what covers the gap.

   TR_scopes_delete_object blocks deleting a scope that objects still point at,
   so the usual route to a dangling scope_id is closed. What remains uncovered
   is a direct INSERT or UPDATE of objects.scope_id naming a scope that does
   not exist -- nothing prevents that, and only this check will reveal it.

   Expect zero.
   ============================================================================ */
SELECT COUNT(*) AS objects_with_dangling_scope_id
FROM dbo.objects AS o
WHERE NOT EXISTS (SELECT 1 FROM dbo.scopes AS s
                  WHERE s.scope_id = o.scope_id);
GO

/* ============================================================================
   scopes business-object context

   scopes.object_id/object_type are the OPTIONAL business object whose context
   the scope represents -- an Organization, a Project, a Site -- and are
   distinct from the scope's own registry row. FK_scopes_context_object covers
   them when both are present, and CK_scopes_context_object_pair stops one
   being set without the other, so this is a backstop only.

   Expect zero. A non-zero count means a scope claims a business object that
   is not in the registry.
   ============================================================================ */
SELECT COUNT(*) AS scopes_with_missing_context_object
FROM dbo.scopes AS s
WHERE s.object_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.objects AS o
                  WHERE o.object_id = s.object_id
                    AND o.object_type = s.object_type);
GO

/* ============================================================================
   Enforcement health

   The orphan counts above are only meaningful if the constraints that prevent
   orphans are present, enabled and trusted. A restore or a bulk load can
   leave a foreign key as is_not_trusted = 1, which means SQL Server stopped
   guaranteeing the data behind it -- rows inserted while it was off are never
   re-validated.

   Expect 8 foreign keys and 7 triggers, all enabled and trusted, and an empty
   problem list. The 8 foreign keys are one per registered table (7) plus
   FK_scopes_context_object. Re-trust a constraint with:

       ALTER TABLE dbo.<table> WITH CHECK CHECK CONSTRAINT <constraint>;
   ============================================================================ */
SELECT
    'foreign_keys' AS component,
    COUNT(*)                                              AS present,
    SUM(CASE WHEN is_disabled = 1 THEN 1 ELSE 0 END)      AS disabled,
    SUM(CASE WHEN is_not_trusted = 1 THEN 1 ELSE 0 END)   AS untrusted
FROM sys.foreign_keys
WHERE referenced_object_id = OBJECT_ID('dbo.objects')
UNION ALL
SELECT
    'delete_triggers',
    COUNT(*),
    SUM(CASE WHEN is_disabled = 1 THEN 1 ELSE 0 END),
    0
FROM sys.triggers
WHERE is_ms_shipped = 0
  AND name LIKE 'TR_%_delete_object';
GO

SELECT name AS problem_constraint,
       OBJECT_NAME(parent_object_id) AS on_table,
       is_disabled,
       is_not_trusted
FROM sys.foreign_keys
WHERE referenced_object_id = OBJECT_ID('dbo.objects')
  AND (is_disabled = 1 OR is_not_trusted = 1);
GO
