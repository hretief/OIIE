using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RegLocationProvider.Application;

namespace RegLocationProvider.Infrastructure.Sql;

/// <summary>
/// The registry store over SQL Server.
///
/// Two things shape almost every write below.
///
/// First, there are no IDENTITY columns. The schema is EIS-derived and ids are
/// allocated by the caller, so inserts take MAX(id)+1 under an UPDLOCK/HOLDLOCK
/// on the table. That serialises concurrent allocations; without the hint two
/// callers read the same MAX and one loses on the primary key.
///
/// Second, every registered row needs a matching dbo.objects row, and the
/// schema's foreign keys enforce it. So a create is always two inserts in one
/// transaction: objects first, then the domain row. Deletes are the reverse and
/// need only the domain row, because the AFTER DELETE triggers reap the
/// registry row themselves.
/// </summary>
public sealed class SqlRegLocationStore(IOptions<RegLocationOptions> options) : IRegLocationStore
{
    private readonly RegLocationOptions _options = options.Value;

    // Object type constants, from ebps_populate_base_types in EIS-POPULATE.SQL.
    // These are pinned by CHECK constraints in the schema, so they are facts
    // about the database rather than choices this class is free to make.
    private const int TypePhysicalItem = 1;
    private const int TypeTag = 212;
    private const int TypeScope = 227;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    // ---- Scopes -----------------------------------------------------------

    private const string ScopeColumns = """
        s.scope_id, s.name, s.namespace_id, s.object_id, s.object_type,
        s.parent_id, s.is_enabled, s.usage_count
        """;

    private static RegScope ReadScope(SqlDataReader r) => new(
        r.GetInt32(0),
        r.GetString(1),
        r.GetInt32(2),
        r.IsDBNull(3) ? null : r.GetInt32(3),
        r.IsDBNull(4) ? null : r.GetInt32(4),
        r.IsDBNull(5) ? null : r.GetInt32(5),
        r.GetString(6) == "Y",
        r.GetInt32(7));

    public async Task<IReadOnlyList<RegScope>> GetScopesAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT {ScopeColumns} FROM dbo.scopes AS s ORDER BY s.scope_id;", cn);

        var result = new List<RegScope>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadScope(r));
        return result;
    }

    public async Task<RegScope?> FindScopeAsync(int scopeId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT {ScopeColumns} FROM dbo.scopes AS s WHERE s.scope_id = @id;", cn);
        cmd.Parameters.AddWithValue("@id", scopeId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadScope(r) : null;
    }

    public async Task<RegScope> CreateScopeAsync(CreateScopeRequest request, CancellationToken ct)
    {
        // Caught before the database so the caller gets a clear message rather
        // than a constraint name. The CHECK still stands behind this.
        if (request.ContextObjectId.HasValue != request.ContextObjectType.HasValue)
            throw new RegistryConflictException(
                "ContextObjectId and ContextObjectType must be supplied together or both omitted.");

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            var scopeId = await NextIdAsync(cn, tx, "dbo.scopes", "scope_id", ct);

            // A scope's registry row is scoped to itself. It is the boundary, so
            // there is no outer scope to point at; the Global scope in the
            // bootstrap does the same.
            await InsertObjectAsync(cn, tx, scopeId, TypeScope, scopeId, request.Guid, ct);

            await using (var cmd = new SqlCommand("""
                INSERT INTO dbo.scopes
                    (scope_id, name, namespace_id, object_id, object_type, parent_id)
                VALUES
                    (@id, @name, @ns, @ctxId, @ctxType, @parent);
                """, cn, tx))
            {
                cmd.Parameters.AddWithValue("@id", scopeId);
                cmd.Parameters.AddWithValue("@name", request.Name);
                cmd.Parameters.AddWithValue("@ns", request.NamespaceId);
                cmd.Parameters.AddWithValue("@ctxId", (object?)request.ContextObjectId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ctxType", (object?)request.ContextObjectType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@parent", (object?)request.ParentId ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return new RegScope(scopeId, request.Name, request.NamespaceId,
                request.ContextObjectId, request.ContextObjectType, request.ParentId, true, 0);
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            await tx.RollbackAsync(ct);
            throw Translate(ex);
        }
    }

    public async Task<bool> DeleteScopeAsync(int scopeId, CancellationToken ct)
    {
        // Only the domain row is deleted. The AFTER DELETE trigger reaps the
        // objects row, and refuses if anything still points at this scope.
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM dbo.scopes WHERE scope_id = @id;", cn);
        cmd.Parameters.AddWithValue("@id", scopeId);

        try
        {
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            throw Translate(ex);
        }
    }

    // ---- Catalogue --------------------------------------------------------

    public async Task<IReadOnlyList<RegNamespace>> GetNamespacesAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT namespace_id FROM dbo.namespaces ORDER BY namespace_id;", cn);

        var result = new List<RegNamespace>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new RegNamespace(r.GetInt32(0)));
        return result;
    }

    public async Task<IReadOnlyList<RegClass>> GetClassesAsync(int? namespaceId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT class_id, group_id, namespace_id
            FROM dbo.class_objects
            WHERE (@ns IS NULL OR namespace_id = @ns)
            ORDER BY class_id;
            """, cn);
        cmd.Parameters.AddWithValue("@ns", (object?)namespaceId ?? DBNull.Value);

        var result = new List<RegClass>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result.Add(new RegClass(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        return result;
    }

    public async Task<IReadOnlyList<RegUnit>> GetUnitsAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT unit_id FROM dbo.uom_units ORDER BY unit_id;", cn);

        var result = new List<RegUnit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new RegUnit(r.GetInt32(0)));
        return result;
    }

    // ---- Items ------------------------------------------------------------

    private static RegItem ReadItem(SqlDataReader r) =>
        new(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));

    public async Task<IReadOnlyList<RegItem>> GetItemsAsync(int? namespaceId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT item_id, namespace_id, unit_id, trn_id
            FROM dbo.items
            WHERE (@ns IS NULL OR namespace_id = @ns)
            ORDER BY item_id;
            """, cn);
        cmd.Parameters.AddWithValue("@ns", (object?)namespaceId ?? DBNull.Value);

        var result = new List<RegItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadItem(r));
        return result;
    }

    public async Task<RegItem?> FindItemAsync(int itemId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT item_id, namespace_id, unit_id, trn_id
            FROM dbo.items WHERE item_id = @id;
            """, cn);
        cmd.Parameters.AddWithValue("@id", itemId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadItem(r) : null;
    }

    public async Task<RegItem> CreateItemAsync(CreateItemRequest request, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            var itemId = await NextIdAsync(cn, tx, "dbo.items", "item_id", ct);

            await EnsureScopeExistsAsync(cn, tx, request.ScopeId, ct);
            await InsertObjectAsync(cn, tx, itemId, TypePhysicalItem, request.ScopeId, request.Guid, ct);

            await using (var cmd = new SqlCommand("""
                INSERT INTO dbo.items (item_id, namespace_id, unit_id, trn_id)
                VALUES (@id, @ns, @unit, @trn);
                """, cn, tx))
            {
                cmd.Parameters.AddWithValue("@id", itemId);
                cmd.Parameters.AddWithValue("@ns", request.NamespaceId);
                cmd.Parameters.AddWithValue("@unit", request.UnitId);
                cmd.Parameters.AddWithValue("@trn", request.TrnId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
            return new RegItem(itemId, request.NamespaceId, request.UnitId, request.TrnId);
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            await tx.RollbackAsync(ct);
            throw Translate(ex);
        }
    }

    public async Task<bool> DeleteItemAsync(int itemId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM dbo.items WHERE item_id = @id;", cn);
        cmd.Parameters.AddWithValue("@id", itemId);

        try
        {
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            // Tags reference items, so this is the common case: a caller trying
            // to delete an item that still has functional locations on it.
            throw Translate(ex);
        }
    }

    // ---- Tags -------------------------------------------------------------

    // The tag and its registry row are always read together: the GUID and scope
    // live in objects, and a tag without them is only half the answer.
    private const string TagSelect = """
        SELECT t.tag_id, t.item_id, t.class_id, t.code, t.revision, t.name, t.state,
               o.object_id, o.object_type, o.guid, o.scope_id,
               o.hide_flags, o.lock_flags,
               o.date_added, o.added_by, o.date_changed, o.changed_by
        FROM dbo.tags AS t
        INNER JOIN dbo.objects AS o
            ON o.object_id = t.tag_id AND o.object_type = t.object_type
        """;

    private static RegTagDetail ReadTagDetail(SqlDataReader r) => new(
        new RegTag(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6)),
        new RegObject(
            r.GetInt32(7),
            r.GetInt32(8),
            r.IsDBNull(9) ? null : r.GetGuid(9),
            r.GetInt32(10),
            r.GetByte(11),
            r.GetByte(12),
            r.IsDBNull(13) ? null : r.GetDateTime(13),
            r.IsDBNull(14) ? null : r.GetInt32(14),
            r.IsDBNull(15) ? null : r.GetDateTime(15),
            r.IsDBNull(16) ? null : r.GetInt32(16)));

    private static async Task<List<RegTagDetail>> ReadAllAsync(SqlCommand cmd, CancellationToken ct)
    {
        var result = new List<RegTagDetail>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadTagDetail(r));
        return result;
    }

    public async Task<IReadOnlyList<RegTagDetail>> GetTagsAsync(int? itemId, int? scopeId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            {TagSelect}
            WHERE (@item IS NULL OR t.item_id = @item)
              AND (@scope IS NULL OR o.scope_id = @scope)
            ORDER BY t.tag_id;
            """, cn);
        cmd.Parameters.AddWithValue("@item", (object?)itemId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scope", (object?)scopeId ?? DBNull.Value);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<RegTagDetail?> FindTagAsync(int tagId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{TagSelect} WHERE t.tag_id = @id;", cn);
        cmd.Parameters.AddWithValue("@id", tagId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadTagDetail(r) : null;
    }

    public async Task<IReadOnlyList<RegTagDetail>> FindTagsByCodeAsync(string code, int? revision, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            {TagSelect}
            WHERE t.code = @code
              AND (@rev IS NULL OR t.revision = @rev)
            ORDER BY t.item_id, t.revision;
            """, cn);
        cmd.Parameters.AddWithValue("@code", code);
        cmd.Parameters.AddWithValue("@rev", (object?)revision ?? DBNull.Value);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<RegTagDetail>> FindTagsByGuidAsync(Guid guid, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{TagSelect} WHERE o.guid = @guid ORDER BY t.revision;", cn);
        cmd.Parameters.AddWithValue("@guid", guid);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<RegTagDetail>> FindTagsByStateAsync(string state, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{TagSelect} WHERE t.state = @state ORDER BY t.tag_id;", cn);
        cmd.Parameters.AddWithValue("@state", state);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<RegTagDetail?> ApproveTagAsync(int tagId, ApproveTagRequest request, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);

        // The state test is in the UPDATE rather than in a preceding SELECT, so
        // two stewards approving at once cannot both pass a check and both write.
        // The loser updates nothing and is told the decision was already made.
        await using var cmd = new SqlCommand("""
            UPDATE dbo.tags
               SET state = @approved
             WHERE tag_id = @id
               AND state = @proposed;

            SELECT @@ROWCOUNT AS updated,
                   (SELECT COUNT(*) FROM dbo.tags WHERE tag_id = @id) AS present;
            """, cn);

        cmd.Parameters.AddWithValue("@id", tagId);
        cmd.Parameters.AddWithValue("@approved", RegTagState.Approved);
        cmd.Parameters.AddWithValue("@proposed", RegTagState.Proposed);

        int updated, present;

        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            updated = r.GetInt32(0);
            present = r.GetInt32(1);
        }

        if (present == 0) return null;

        if (updated == 0)
            throw new RegistryConflictException(
                $"Tag {tagId} is not awaiting approval; its decision has already been made.");

        // The registry records the outcome, not the deliberation. DecidedBy and
        // Note are accepted so callers need not vary by whether this registry
        // keeps an audit trail, and are dropped here because EIS has nowhere to
        // put them -- storing them would mean inventing a table this schema does
        // not have, which is a larger decision than this change.
        return await FindTagAsync(tagId, ct);
    }

    public async Task<RegTagDetail> CreateTagAsync(CreateTagRequest request, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            var tagId = await NextIdAsync(cn, tx, "dbo.tags", "tag_id", ct);

            // A tag always gets a GUID. It is the federation identity, and one
            // that exists only sometimes cannot be federated on.
            var guid = request.Guid ?? Guid.NewGuid();

            await EnsureScopeExistsAsync(cn, tx, request.ScopeId, ct);
            await InsertObjectAsync(cn, tx, tagId, TypeTag, request.ScopeId, guid, ct);

            await using (var cmd = new SqlCommand("""
                INSERT INTO dbo.tags (tag_id, item_id, class_id, code, revision, name, state)
                VALUES (@id, @item, @class, @code, @rev, @name, @state);
                """, cn, tx))
            {
                cmd.Parameters.AddWithValue("@id", tagId);
                cmd.Parameters.AddWithValue("@item", request.ItemId);
                cmd.Parameters.AddWithValue("@class", request.ClassId);
                cmd.Parameters.AddWithValue("@code", request.Code);
                cmd.Parameters.AddWithValue("@rev", request.Revision);
                cmd.Parameters.AddWithValue("@name", request.Name);

                // Defaulted here rather than left to the column default, because a
                // caller that says nothing means a tag authored directly in the
                // registry, which is established content and not a proposal.
                cmd.Parameters.AddWithValue("@state", request.State ?? RegTagState.Approved);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return (await FindTagAsync(tagId, ct))!;
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            await tx.RollbackAsync(ct);
            throw Translate(ex);
        }
    }

    public async Task<RegTagDetail?> UpdateTagAsync(int tagId, UpdateTagRequest request, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);

        int affected;
        await using (var cmd = new SqlCommand("""
            UPDATE dbo.tags
            SET class_id = @class, code = @code, revision = @rev, name = @name
            WHERE tag_id = @id;
            """, cn))
        {
            cmd.Parameters.AddWithValue("@id", tagId);
            cmd.Parameters.AddWithValue("@class", request.ClassId);
            cmd.Parameters.AddWithValue("@code", request.Code);
            cmd.Parameters.AddWithValue("@rev", request.Revision);
            cmd.Parameters.AddWithValue("@name", request.Name);

            try
            {
                affected = await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex) when (IsConstraintViolation(ex))
            {
                throw Translate(ex);
            }
        }

        // The registry row is untouched: an edit changes what the tag says, not
        // which thing it is, so the federation GUID must survive it.
        if (affected == 0) return null;

        await using var stamp = new SqlCommand("""
            UPDATE dbo.objects
            SET date_changed = SYSUTCDATETIME()
            WHERE object_id = @id AND object_type = @type;
            """, cn);
        stamp.Parameters.AddWithValue("@id", tagId);
        stamp.Parameters.AddWithValue("@type", TypeTag);
        await stamp.ExecuteNonQueryAsync(ct);

        return await FindTagAsync(tagId, ct);
    }

    public async Task<bool> DeleteTagAsync(int tagId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("DELETE FROM dbo.tags WHERE tag_id = @id;", cn);
        cmd.Parameters.AddWithValue("@id", tagId);

        try
        {
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            throw Translate(ex);
        }
    }

    // ---- Health -----------------------------------------------------------

    public async Task<RegistryHealth> GetHealthAsync(CancellationToken ct)
    {
        // The orphan check mirrors REG-LOCATION_VERIFY.SQL. Each registered
        // table is checked against dbo.objects on its own pinned type constant,
        // because an orphan in any one of them breaks the same invariant.
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM dbo.scopes),
                (SELECT COUNT(*) FROM dbo.tags),
                (SELECT COUNT(*) FROM dbo.items),
                (
                    SELECT
                        (SELECT COUNT(*) FROM dbo.namespaces d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.namespace_id AND o.object_type = 226))
                      + (SELECT COUNT(*) FROM dbo.class_groups d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.group_id AND o.object_type = 184))
                      + (SELECT COUNT(*) FROM dbo.uom_units d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.unit_id AND o.object_type = 285))
                      + (SELECT COUNT(*) FROM dbo.class_objects d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.class_id AND o.object_type = 185))
                      + (SELECT COUNT(*) FROM dbo.items d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.item_id AND o.object_type = 1))
                      + (SELECT COUNT(*) FROM dbo.tags d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.tag_id AND o.object_type = 212))
                      + (SELECT COUNT(*) FROM dbo.scopes d
                         WHERE NOT EXISTS (SELECT 1 FROM dbo.objects o
                                           WHERE o.object_id = d.scope_id AND o.object_type = 227))
                ),
                (SELECT COUNT(*) FROM sys.foreign_keys
                 WHERE is_disabled = 1 OR is_not_trusted = 1);
            """, cn) { CommandTimeout = 30 };

        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);

        return new RegistryHealth(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
    }

    // ---- Shared write helpers ---------------------------------------------

    /// <summary>
    /// Allocates the next id for a table that has no IDENTITY column.
    ///
    /// UPDLOCK/HOLDLOCK is what makes this correct under concurrency: it holds
    /// a range lock for the life of the transaction, so a second caller blocks
    /// here rather than reading the same MAX and losing on the primary key.
    /// </summary>
    private static async Task<int> NextIdAsync(
        SqlConnection cn, SqlTransaction tx, string table, string column, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            $"SELECT ISNULL(MAX({column}), 0) + 1 FROM {table} WITH (UPDLOCK, HOLDLOCK);", cn, tx);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Rejects a scope id that does not exist.
    ///
    /// objects.scope_id deliberately carries no foreign key -- the schema
    /// explains why: scopes and objects reference each other, and a key here
    /// would make the Global scope impossible to seed. That resolution leaves
    /// this one column unguarded, so a caller posting an unknown scope would
    /// otherwise write a row that only REG-LOCATION_VERIFY.SQL ever notices,
    /// long after the caller who caused it has gone.
    ///
    /// Checked inside the caller's transaction so the scope cannot be deleted
    /// between this read and the insert that depends on it.
    /// </summary>
    private static async Task EnsureScopeExistsAsync(
        SqlConnection cn, SqlTransaction tx, int scopeId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT 1 FROM dbo.scopes WITH (UPDLOCK, HOLDLOCK) WHERE scope_id = @scope;", cn, tx);
        cmd.Parameters.AddWithValue("@scope", scopeId);

        if (await cmd.ExecuteScalarAsync(ct) is null)
            throw new RegistryConflictException($"No scope '{scopeId}'.");
    }

    /// <summary>
    /// Writes the dbo.objects row that makes a domain row legal. Always called
    /// before the domain insert, because the foreign key points this way.
    /// </summary>
    private static async Task InsertObjectAsync(
        SqlConnection cn, SqlTransaction tx,
        int objectId, int objectType, int scopeId, Guid? guid, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO dbo.objects
                (object_id, object_type, guid, scope_id, hide_flags, lock_flags, date_added)
            VALUES
                (@id, @type, @guid, @scope, 0, 0, SYSUTCDATETIME());
            """, cn, tx);
        cmd.Parameters.AddWithValue("@id", objectId);
        cmd.Parameters.AddWithValue("@type", objectType);
        cmd.Parameters.AddWithValue("@guid", (object?)guid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scope", scopeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 547 constraint violation, 2601/2627 duplicate key. These mean the caller
    // asked for something the registry forbids, not that the server broke, so
    // they are translated rather than allowed to surface as a 500.
    private static bool IsConstraintViolation(SqlException ex) =>
        ex.Number is 547 or 2601 or 2627;

    private static RegistryConflictException Translate(SqlException ex) => new(
        ex.Number switch
        {
            2601 or 2627 => "That would duplicate an existing registry entry. " +
                            "A tag code is unique per item and revision.",
            _ => "The registry rejected this change because it would break a " +
                 "referential rule. A referenced row may not exist, or the row " +
                 "being deleted may still be in use.",
        });
}
