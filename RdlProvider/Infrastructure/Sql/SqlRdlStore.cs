using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using RdlProvider.Application;

namespace RdlProvider.Infrastructure.Sql;

/// <summary>
/// The class library over the shared EIS database.
///
/// This reads and writes dbo.class_objects -- the same table REG-LOCATION
/// uses. RDL and REG-LOCATION are two function apps over one persistence
/// layer, not two systems with two databases. RDL is the curation surface for
/// the vocabulary; REG-LOCATION consumes it to classify tags.
///
/// That sharing dictates the shape of everything below.
///
/// The DDL is owned by RegLocationProvider and is not duplicated here. This
/// app takes the schema as it finds it, which is why there is no
/// SchemaInitializer and no embedded .sql.
///
/// The schema is EIS-derived, so every registered row needs a paired
/// dbo.objects row and the foreign key enforces it. A class create is
/// therefore two inserts in one transaction: objects first, then
/// class_objects. This is not incidental -- writing class_objects alone fails
/// on FK_class_objects_objects.
/// </summary>
public sealed class SqlRdlStore(IOptions<RdlOptions> options) : IRdlStore
{
    private readonly RdlOptions _options = options.Value;

    // From ebps_populate_base_types in EIS-POPULATE.SQL, and pinned by
    // CK_class_objects_object_type in the schema. A fact about the database
    // rather than a choice this class is free to make.
    private const int TypeClass = 185;

    // The Global scope, seeded by REG-LOCATION's bootstrap. Vocabulary is
    // library-wide and belongs to no single site, so a class confined to one
    // scope could not classify anything outside it.
    private const int GlobalScopeId = 1;

    // The guid lives on dbo.objects, not class_objects, so every read of a
    // class joins the registry row that the foreign key already guarantees is
    // there. An INNER JOIN would be defensible for that reason, but LEFT is
    // used deliberately: if a class ever did lose its registry row the right
    // answer is a class with no identity, not a class that vanishes from the
    // library without explanation.
    private const string ClassSelect = """
        SELECT c.class_id, c.group_id, c.namespace_id, c.code, c.name, c.description, c.parent_class_id,
               o.guid
        FROM dbo.class_objects AS c
        LEFT JOIN dbo.objects AS o
               ON o.object_id = c.class_id AND o.object_type = 185
        """;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task<IReadOnlyList<RdlNamespace>> GetNamespacesAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT namespace_id FROM dbo.namespaces ORDER BY namespace_id;", cn);

        var result = new List<RdlNamespace>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new RdlNamespace(r.GetInt32(0)));
        return result;
    }

    public async Task<IReadOnlyList<RdlClassGroup>> GetClassGroupsAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT group_id FROM dbo.class_groups ORDER BY group_id;", cn);

        var result = new List<RdlClassGroup>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new RdlClassGroup(r.GetInt32(0)));
        return result;
    }

    public async Task<IReadOnlyList<RdlClass>> GetClassesAsync(int? namespaceId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            {ClassSelect}
            WHERE (@ns IS NULL OR c.namespace_id = @ns)
            ORDER BY c.class_id;
            """, cn);
        cmd.Parameters.AddWithValue("@ns", (object?)namespaceId ?? DBNull.Value);

        var result = new List<RdlClass>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadClass(r));
        return result;
    }

    public async Task<RdlClass?> FindClassAsync(int classId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            {ClassSelect}
            WHERE c.class_id = @id;
            """, cn);
        cmd.Parameters.AddWithValue("@id", classId);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadClass(r) : null;
    }

    /// <summary>
    /// Resolves a class by its governed code.
    ///
    /// Ordered rather than assuming a single row: class_objects has no unique
    /// constraint on code in the EIS schema, so a duplicate is possible in
    /// principle. Ordering by class_id makes the answer deterministic rather
    /// than dependent on the plan the server happens to choose.
    /// </summary>
    public async Task<RdlClass?> FindClassByCodeAsync(string code, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"""
            {ClassSelect}
            WHERE c.code = @code
            ORDER BY c.class_id;
            """, cn);
        cmd.Parameters.AddWithValue("@code", code);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadClass(r) : null;
    }

    public async Task<RdlClass> CreateClassAsync(CreateClassRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            throw new RdlConflictException("A class requires both a code and a name.");

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            // The id is the caller's, not minted here, so a collision is a real
            // possibility rather than a theoretical one. Checked explicitly
            // because the primary key would report it as a duplicate-key error
            // naming an index, which is not what the caller did wrong.
            //
            // The code is checked too. There is no unique constraint on it in
            // the EIS schema, so nothing else would catch a duplicate -- and a
            // duplicated governed code is exactly the failure that makes every
            // consumer's lookup ambiguous.
            await using (var exists = new SqlCommand("""
                SELECT TOP 1 CASE WHEN class_id = @id THEN 'id' ELSE 'code' END
                FROM dbo.class_objects WITH (UPDLOCK, HOLDLOCK)
                WHERE class_id = @id OR code = @code;
                """, cn, tx))
            {
                exists.Parameters.AddWithValue("@id", request.ClassId);
                exists.Parameters.AddWithValue("@code", request.Code);

                if (await exists.ExecuteScalarAsync(ct) is string clash)
                    throw new RdlConflictException(clash == "id"
                        ? $"Class '{request.ClassId}' already exists."
                        : $"Class code '{request.Code}' is already in use.");
            }

            // Every class leaves this method with an identity. The caller's is
            // honoured when given, so a class already governed elsewhere keeps
            // the UUID its consumers know it by; otherwise this library is the
            // origin of the class and mints one. What is not an option is
            // storing nothing -- a class with a NULL guid cannot be quoted in
            // ShowTaxonomySet, and the responder would be left inventing a
            // value that no two systems would agree on.
            var uuid = request.Uuid ?? Guid.NewGuid();

            await InsertObjectAsync(cn, tx, request.ClassId, uuid, GlobalScopeId, ct);

            // Classes are locked (lock_flags 18) to match how REG-LOCATION's
            // bootstrap and EIS's own ebps_pop_announce_class_objs record them:
            // vocabulary is not user-editable through the generic object routes.
            await using (var cmd = new SqlCommand("""
                UPDATE dbo.objects
                   SET lock_flags = 18
                 WHERE object_id = @id AND object_type = @type;
                """, cn, tx))
            {
                cmd.Parameters.AddWithValue("@id", request.ClassId);
                cmd.Parameters.AddWithValue("@type", TypeClass);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using (var cmd = new SqlCommand("""
                INSERT INTO dbo.class_objects
                    (class_id, group_id, namespace_id, code, name, description, parent_class_id)
                VALUES
                    (@id, @group, @ns, @code, @name, @desc, @parent);
                """, cn, tx))
            {
                cmd.Parameters.AddWithValue("@id", request.ClassId);
                cmd.Parameters.AddWithValue("@group", request.GroupId);
                cmd.Parameters.AddWithValue("@ns", request.NamespaceId);
                cmd.Parameters.AddWithValue("@code", request.Code);
                cmd.Parameters.AddWithValue("@name", request.Name);
                cmd.Parameters.AddWithValue("@desc", (object?)request.Description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@parent", (object?)request.ParentClassId ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);

            return new RdlClass(
                request.ClassId, request.GroupId, request.NamespaceId,
                request.Code, request.Name, request.Description, request.ParentClassId, uuid);
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            await tx.RollbackAsync(ct);
            throw Translate(ex);
        }
    }

    public async Task<RdlClass?> UpdateClassAsync(
        int classId, UpdateClassRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RdlConflictException("A class requires a name.");

        // A class that is its own parent is a cycle the foreign key cannot see,
        // since the row it points at does exist. Refused here because the
        // ancestor walk that resolves a degraded classification would not
        // terminate.
        if (request.ParentClassId == classId)
            throw new RdlConflictException("A class cannot be its own parent.");

        await using var cn = await OpenAsync(ct);

        try
        {
            await using var cmd = new SqlCommand("""
                UPDATE dbo.class_objects
                   SET group_id = @group,
                       name = @name,
                       description = @desc,
                       parent_class_id = @parent
                 WHERE class_id = @id;
                """, cn);
            cmd.Parameters.AddWithValue("@id", classId);
            cmd.Parameters.AddWithValue("@group", request.GroupId);
            cmd.Parameters.AddWithValue("@name", request.Name);
            cmd.Parameters.AddWithValue("@desc", (object?)request.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@parent", (object?)request.ParentClassId ?? DBNull.Value);

            var affected = await cmd.ExecuteNonQueryAsync(ct);
            return affected == 0 ? null : await FindClassAsync(classId, ct);
        }
        catch (SqlException ex) when (IsConstraintViolation(ex))
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Creates the dbo.objects row that class_objects' foreign key requires.
    ///
    /// A per-project copy of REG-LOCATION's equivalent rather than a shared
    /// helper: these apps deliberately take no ProjectReference on each other,
    /// and the duplication is the cheaper price for that isolation.
    ///
    /// The guid is written here because this is where the object begins to
    /// exist. Assigning it later would leave a window in which the class is
    /// readable but unquotable, and a back-fill after the fact cannot tell the
    /// difference between an object that never had an identity and one whose
    /// identity was deliberately withheld.
    /// </summary>
    private static async Task InsertObjectAsync(
        SqlConnection cn, SqlTransaction tx, int objectId, Guid uuid, int scopeId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO dbo.objects
                (object_id, object_type, guid, scope_id, hide_flags, lock_flags, date_added)
            VALUES
                (@id, @type, @guid, @scope, 0, 0, SYSUTCDATETIME());
            """, cn, tx);
        cmd.Parameters.AddWithValue("@id", objectId);
        cmd.Parameters.AddWithValue("@type", TypeClass);
        cmd.Parameters.AddWithValue("@guid", uuid);
        cmd.Parameters.AddWithValue("@scope", scopeId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // 547 constraint violation, 2601/2627 duplicate key, and 50000 for the
    // RAISERROR the delete triggers use. These mean the caller asked for
    // something the database forbids, not that the server broke.
    private static bool IsConstraintViolation(SqlException ex) =>
        ex.Number is 547 or 2601 or 2627 or 50000;

    private static RdlConflictException Translate(SqlException ex) => new(
        ex.Number switch
        {
            2601 or 2627 => "That would duplicate an existing library entry.",
            50000 => ex.Message,
            _ => "The library rejected this change because it would break a " +
                 "referential rule. A referenced group, namespace or parent " +
                 "class may not exist.",
        });

    private static RdlClass ReadClass(SqlDataReader r) => new(
        r.GetInt32(0),
        r.GetInt32(1),
        r.GetInt32(2),
        r.GetString(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetGuid(7));
}
