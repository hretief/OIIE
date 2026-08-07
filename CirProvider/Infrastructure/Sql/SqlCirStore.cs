using System.Data;
using System.Text.Json;
using CirProvider.Application;
using CirProvider.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CirProvider.Infrastructure.Sql;

public sealed class SqlCirStore(IOptions<CirOptions> options, ILogger<SqlCirStore> logger) : ICirStore
{
    private readonly CirOptions _options = options.Value;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT 1", cn);
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    // =======================================================================
    // Command services
    // =======================================================================

    public async Task CreateRegistryAsync(CreateRegistryRequest request, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            foreach (var registry in request.Registry)
            {
                var registryKey = await ResolveRegistryKeyAsync(cn, tx, registry.Id, ct);

                if (registryKey is null)
                {
                    if (!_options.AllowNewRegistries)
                        throw new CirFaultException(CirFaultCode.CreateRegistryFault,
                            $"Server is not configured to allow new registries ('{registry.Id}').");

                    registryKey = await InsertRegistryAsync(cn, tx, registry, ct);
                }

                foreach (var category in registry.Categories)
                {
                    var categoryKey = await ResolveCategoryKeyAsync(
                        cn, tx, registryKey.Value, category.Id, category.SourceId, ct);

                    if (categoryKey is null)
                    {
                        if (!_options.AllowNewCategories)
                            throw new CirFaultException(CirFaultCode.CreateCategoryFault,
                                $"Server is not configured to allow new categories ('{category.Id}'/'{category.SourceId}').");

                        categoryKey = await InsertCategoryAsync(cn, tx, registryKey.Value, category, ct);
                    }

                    foreach (var entry in category.Entries)
                    {
                        var cirid = entry.Cirid ?? (request.CreateCirid ? Guid.NewGuid() : null);
                        var entryKey = await InsertEntryAsync(cn, tx, categoryKey.Value, entry, cirid, ct);

                        foreach (var property in entry.Properties)
                            await InsertPropertyAsync(cn, tx, entryKey, property, ct);
                    }
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task DeleteRegistryAsync(string registryId, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "DELETE FROM cir.Registry WHERE RegistryId = @rid", cn);
        cmd.Parameters.AddWithValue("@rid", registryId);

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected == 0)
            throw new CirFaultException(CirFaultCode.RegistryNotFoundFault,
                $"Registry '{registryId}' was not found.");
    }

    public async Task CreateEquivalentEntriesAsync(IReadOnlyList<EquivalentEntryRequest> requests, CancellationToken ct = default)
    {
        if (requests is null || requests.Count == 0) return;

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            foreach (var request in requests)
            {
                var registryKey = await ResolveRegistryKeyAsync(cn, tx, request.RegistryId, ct)
                    ?? throw new CirFaultException(CirFaultCode.RegistryNotFoundFault,
                        $"Registry '{request.RegistryId}' was not found.");

                var categoryKey = await ResolveCategoryKeyAsync(
                        cn, tx, registryKey, request.CategoryId, request.CategorySourceId, ct)
                    ?? throw new CirFaultException(CirFaultCode.CategoryNotFoundFault,
                        $"Category '{request.CategoryId}'/'{request.CategorySourceId}' was not found in registry '{request.RegistryId}'.");

                var existing = await ResolveEntryAsync(
                        cn, tx, categoryKey, request.ExistingIdInSource, request.ExistingSourceId, ct)
                    ?? throw new CirFaultException(CirFaultCode.EntryNotFoundFault,
                        $"Entry '{request.ExistingIdInSource}'/'{request.ExistingSourceId}' was not found in category '{request.CategoryId}'.");

                var cirid = await MergeCiridAsync(cn, tx, existing, request.Entry.Cirid, ct);

                var newEntryKey = await InsertEntryAsync(cn, tx, categoryKey, request.Entry, cirid, ct);

                foreach (var property in request.Entry.Properties)
                    await InsertPropertyAsync(cn, tx, newEntryKey, property, ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// The §3.1.2 CIRID merge. Three cases, and the middle one is the surprising
    /// one — it writes to a record the caller did not ask to modify:
    ///
    ///   existing has a CIRID          -> the new Entry adopts it, and any CIRID
    ///                                    supplied on the new Entry is ignored
    ///   only the new Entry has one    -> it propagates backward to the existing Entry
    ///   neither has one               -> the server mints one and assigns it to both
    /// </summary>
    private async Task<Guid> MergeCiridAsync(
        SqlConnection cn, SqlTransaction tx, ResolvedEntry existing, Guid? supplied, CancellationToken ct)
    {
        if (existing.Cirid is not null)
        {
            if (supplied is not null && supplied.Value != existing.Cirid.Value)
            {
                logger.LogInformation(
                    "Discarding supplied CIRID {Supplied}; the existing entry's {Existing} takes precedence (§3.1.2).",
                    supplied, existing.Cirid);
            }
            return existing.Cirid.Value;
        }

        var cirid = supplied ?? Guid.NewGuid();
        await SetEntryCiridAsync(cn, tx, existing.EntryKey, cirid, ct);
        return cirid;
    }

    private sealed record ResolvedEntry(long EntryKey, Guid? Cirid);

    private static async Task<ResolvedEntry?> ResolveEntryAsync(
        SqlConnection cn, SqlTransaction tx, long categoryKey, string idInSource, string sourceId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT EntryKey, Cirid FROM cir.Entry WHERE CategoryKey = @ck AND IdInSource = @id AND SourceId = @sid",
            cn, tx);
        cmd.Parameters.AddWithValue("@ck", categoryKey);
        cmd.Parameters.AddWithValue("@id", idInSource);
        cmd.Parameters.AddWithValue("@sid", sourceId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new ResolvedEntry(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1));
    }

    private static async Task SetEntryCiridAsync(
        SqlConnection cn, SqlTransaction tx, long entryKey, Guid cirid, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "UPDATE cir.Entry SET Cirid = @cirid, ModifiedUtc = SYSUTCDATETIME() WHERE EntryKey = @ek",
            cn, tx);
        cmd.Parameters.AddWithValue("@cirid", cirid);
        cmd.Parameters.AddWithValue("@ek", entryKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// ws-CIR §3.1.3 UpdateRegistry. Snapshot replace: every non-primary-key
    /// attribute of each supplied object is set from the supplied data, so an
    /// omitted attribute is cleared rather than preserved.
    ///
    /// Two interpretations, both recorded in the conformance statement:
    ///   - Children that are not supplied are left alone. The specification has
    ///     a separate Delete family, so treating an omitted Category or Entry as
    ///     a removal would make partial updates impossible.
    ///   - CIRID is preserved when omitted rather than cleared. §3.1.4 exists as
    ///     a dedicated operation for it, and §3.1.2 treats it as server-managed
    ///     correlation state rather than caller-owned data.
    /// </summary>
    public async Task UpdateRegistryAsync(IReadOnlyList<Registry> registries, CancellationToken ct = default)
    {
        if (registries is null || registries.Count == 0) return;

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            foreach (var registry in registries)
            {
                var registryKey = await ResolveRegistryKeyAsync(cn, tx, registry.Id, ct)
                    ?? throw new CirFaultException(CirFaultCode.RegistryNotFoundFault,
                        $"Registry '{registry.Id}' was not found.");

                await ExecuteAsync(cn, tx, ct,
                    "UPDATE cir.Registry SET Description = @desc, ModifiedUtc = SYSUTCDATETIME() WHERE RegistryKey = @rk",
                    ("@desc", Serialize(registry.Description)), ("@rk", registryKey));

                foreach (var category in registry.Categories)
                {
                    var categoryKey = await ResolveCategoryKeyAsync(
                            cn, tx, registryKey, category.Id, category.SourceId, ct)
                        ?? throw new CirFaultException(CirFaultCode.CategoryNotFoundFault,
                            $"Category '{category.Id}'/'{category.SourceId}' was not found in registry '{registry.Id}'.");

                    await ExecuteAsync(cn, tx, ct,
                        "UPDATE cir.Category SET Description = @desc, ModifiedUtc = SYSUTCDATETIME() WHERE CategoryKey = @ck",
                        ("@desc", Serialize(category.Description)), ("@ck", categoryKey));

                    foreach (var entry in category.Entries)
                    {
                        var resolved = await ResolveEntryAsync(cn, tx, categoryKey, entry.IdInSource, entry.SourceId, ct)
                            ?? throw new CirFaultException(CirFaultCode.EntryNotFoundFault,
                                $"Entry '{entry.IdInSource}'/'{entry.SourceId}' was not found in category '{category.Id}'.");

                        await using (var entryCmd = new SqlCommand("""
                            UPDATE cir.Entry
                               SET SourceOwnerId = @owner,
                                   Name          = @name,
                                   Description   = @desc,
                                   Inactive      = @inactive,
                                   Cirid         = COALESCE(@cirid, Cirid),
                                   ModifiedUtc   = SYSUTCDATETIME()
                             WHERE EntryKey = @ek
                            """, cn, tx))
                        {
                            entryCmd.Parameters.AddWithValue("@owner", (object?)entry.SourceOwnerId ?? DBNull.Value);
                            entryCmd.Parameters.AddWithValue("@name", (object?)entry.Name ?? DBNull.Value);
                            entryCmd.Parameters.AddWithValue("@desc", Serialize(entry.Description));
                            entryCmd.Parameters.Add("@inactive", SqlDbType.Bit).Value =
                                (object?)entry.Inactive ?? DBNull.Value;
                            entryCmd.Parameters.Add("@cirid", SqlDbType.UniqueIdentifier).Value =
                                (object?)entry.Cirid ?? DBNull.Value;
                            entryCmd.Parameters.AddWithValue("@ek", resolved.EntryKey);

                            await entryCmd.ExecuteNonQueryAsync(ct);
                        }

                        foreach (var property in entry.Properties)
                        {
                            var propertyKey = await ResolvePropertyKeyAsync(cn, tx, resolved.EntryKey, property.Id, ct)
                                ?? throw new CirFaultException(CirFaultCode.PropertyNotFoundFault,
                                    $"Property '{property.Id}' was not found on entry '{entry.IdInSource}'/'{entry.SourceId}'.");

                            await ExecuteAsync(cn, tx, ct, """
                                UPDATE cir.Property
                                   SET DataType = @dt, PropertyValue = @pv, ModifiedUtc = SYSUTCDATETIME()
                                 WHERE PropertyKey = @pk
                                """,
                                ("@dt", (object?)property.DataType ?? DBNull.Value),
                                ("@pv", Serialize(property.PropertyValue)),
                                ("@pk", propertyKey));
                        }
                    }
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// ws-CIR §3.1.4 UpdateEntryCIRID. Collapses several CIRIDs onto one. The
    /// specification defines no faults for this operation, so an OldCIRID that
    /// matches nothing is a no-op rather than an error.
    ///
    /// One UPDATE inside one transaction. This is the operation that argued
    /// hardest against Table Storage: the same fan-out crosses arbitrarily many
    /// partitions there and cannot be made atomic.
    /// </summary>
    public async Task UpdateEntryCiridAsync(UpdateEntryCiridRequest request, CancellationToken ct = default)
    {
        if (request?.OldCirid is null || request.OldCirid.Count == 0) return;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            UPDATE cir.Entry
               SET Cirid = @new, ModifiedUtc = SYSUTCDATETIME()
             WHERE Cirid IN (SELECT CAST(value AS UNIQUEIDENTIFIER) FROM OPENJSON(@old))
            """, cn);
        cmd.Parameters.AddWithValue("@new", request.NewCirid);
        cmd.Parameters.AddWithValue("@old", JsonSerializer.Serialize(request.OldCirid));

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("UpdateEntryCIRID re-pointed {Count} entries to {NewCirid}.", affected, request.NewCirid);
    }

    /// <summary>ws-CIR §3.1.6 DeleteCategory. Entries and Properties go with it.</summary>
    public async Task DeleteCategoryAsync(CategoryIdentifier id, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            var registryKey = await ResolveRegistryKeyAsync(cn, tx, id.RegistryId, ct)
                ?? throw new CirFaultException(CirFaultCode.RegistryNotFoundFault,
                    $"Registry '{id.RegistryId}' was not found.");

            var categoryKey = await ResolveCategoryKeyAsync(cn, tx, registryKey, id.CategoryId, id.CategorySourceId, ct)
                ?? throw new CirFaultException(CirFaultCode.CategoryNotFoundFault,
                    $"Category '{id.CategoryId}'/'{id.CategorySourceId}' was not found in registry '{id.RegistryId}'.");

            // ON DELETE CASCADE carries Entries and Properties.
            await ExecuteAsync(cn, tx, ct,
                "DELETE FROM cir.Category WHERE CategoryKey = @ck", ("@ck", categoryKey));

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>ws-CIR §3.1.7 DeleteEntries. Properties go with each Entry.</summary>
    public async Task DeleteEntriesAsync(IReadOnlyList<EntryIdentifier> ids, CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0) return;

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            foreach (var id in ids)
            {
                var (_, _, entry) = await WalkToEntryAsync(cn, tx, id, ct);
                await ExecuteAsync(cn, tx, ct,
                    "DELETE FROM cir.Entry WHERE EntryKey = @ek", ("@ek", entry.EntryKey));
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>ws-CIR §3.1.8 DeleteProperties.</summary>
    public async Task DeletePropertiesAsync(IReadOnlyList<PropertyIdentifier> ids, CancellationToken ct = default)
    {
        if (ids is null || ids.Count == 0) return;

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            foreach (var id in ids)
            {
                var (_, _, entry) = await WalkToEntryAsync(cn, tx, id, ct);

                var propertyKey = await ResolvePropertyKeyAsync(cn, tx, entry.EntryKey, id.PropertyId, ct)
                    ?? throw new CirFaultException(CirFaultCode.PropertyNotFoundFault,
                        $"Property '{id.PropertyId}' was not found on entry '{id.EntryIdInSource}'/'{id.EntrySourceId}'.");

                await ExecuteAsync(cn, tx, ct,
                    "DELETE FROM cir.Property WHERE PropertyKey = @pk", ("@pk", propertyKey));
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>
    /// Resolves Registry -> Category -> Entry, faulting at the first level that
    /// is missing so the caller learns which part of the identifier was wrong.
    /// </summary>
    private static async Task<(long RegistryKey, long CategoryKey, ResolvedEntry Entry)> WalkToEntryAsync(
        SqlConnection cn, SqlTransaction tx, EntryIdentifier id, CancellationToken ct)
    {
        var registryKey = await ResolveRegistryKeyAsync(cn, tx, id.RegistryId, ct)
            ?? throw new CirFaultException(CirFaultCode.RegistryNotFoundFault,
                $"Registry '{id.RegistryId}' was not found.");

        var categoryKey = await ResolveCategoryKeyAsync(cn, tx, registryKey, id.CategoryId, id.CategorySourceId, ct)
            ?? throw new CirFaultException(CirFaultCode.CategoryNotFoundFault,
                $"Category '{id.CategoryId}'/'{id.CategorySourceId}' was not found in registry '{id.RegistryId}'.");

        var entry = await ResolveEntryAsync(cn, tx, categoryKey, id.EntryIdInSource, id.EntrySourceId, ct)
            ?? throw new CirFaultException(CirFaultCode.EntryNotFoundFault,
                $"Entry '{id.EntryIdInSource}'/'{id.EntrySourceId}' was not found in category '{id.CategoryId}'.");

        return (registryKey, categoryKey, entry);
    }

    private static async Task<long?> ResolvePropertyKeyAsync(
        SqlConnection cn, SqlTransaction tx, long entryKey, string propertyId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT PropertyKey FROM cir.Property WHERE EntryKey = @ek AND PropertyId = @pid", cn, tx);
        cmd.Parameters.AddWithValue("@ek", entryKey);
        cmd.Parameters.AddWithValue("@pid", propertyId);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    private static async Task ExecuteAsync(
        SqlConnection cn, SqlTransaction tx, CancellationToken ct, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new SqlCommand(sql, cn, tx);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // =======================================================================
    // Query services
    // =======================================================================

    public async Task<IReadOnlyList<Registry>> GetEntriesByCiridAsync(
        Guid cirid, IReadOnlyList<string> targetSourceIds, CancellationToken ct = default)
    {
        const string sql = """
            SELECT r.RegistryId, r.Description AS RegistryDescription,
                   c.CategoryId, c.CategorySourceId, c.Description AS CategoryDescription,
                   e.EntryKey, e.IdInSource, e.SourceId, e.Cirid, e.SourceOwnerId,
                   e.Name, e.Description AS EntryDescription, e.Inactive
            FROM cir.Entry e
            JOIN cir.Category c ON c.CategoryKey = e.CategoryKey
            JOIN cir.Registry r ON r.RegistryKey = c.RegistryKey
            WHERE e.Cirid = @cirid
              AND (@noTarget = 1 OR EXISTS (
                    SELECT 1 FROM OPENJSON(@targets) t
                    WHERE REGEXP_LIKE(e.SourceId, '^(' + CAST(t.value AS NVARCHAR(400)) + ')$')))
            ORDER BY r.RegistryId, c.CategoryId, c.CategorySourceId, e.IdInSource, e.SourceId
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@cirid", cirid);
        cmd.Parameters.AddWithValue("@noTarget", targetSourceIds.Count == 0 ? 1 : 0);
        cmd.Parameters.AddWithValue("@targets", JsonSerializer.Serialize(targetSourceIds));

        return await ReadRegistryGraphAsync(cn, cmd, ct);
    }

    public async Task<IReadOnlyList<Registry>> GetEquivalentEntriesAsync(
        IReadOnlyList<EntryIdentifier> identifiers,
        IReadOnlyList<string> targetSourceIds,
        CancellationToken ct = default)
    {
        // §3.2.2: the specified Entry is returned alongside its equivalents so the
        // client can correlate, even when it has no CIRID of its own.
        var results = new List<Registry>();

        foreach (var id in identifiers)
        {
            var self = await GetSingleEntryAsync(id, ct);
            if (self.Count > 0) results.AddRange(self);

            var cirid = ExtractCirid(self);
            if (cirid is not null)
                results.AddRange(await GetEntriesByCiridAsync(cirid.Value, targetSourceIds, ct));
        }

        return Merge(results);
    }

    public async Task<IReadOnlyList<Registry>> GetRegistryAsync(IReadOnlyList<CirFilter> filters, CancellationToken ct = default)
    {
        var translator = new CirFilterTranslator();
        var where = translator.BuildWhere(filters ?? []);

        // §3.2.1 returns Entries plus their associated Registry, Category and
        // Properties, so Entry is the grain and the graph is rebuilt around it.
        var sql = $"""
            SELECT r.RegistryId, r.Description AS RegistryDescription,
                   c.CategoryId, c.CategorySourceId, c.Description AS CategoryDescription,
                   e.EntryKey, e.IdInSource, e.SourceId, e.Cirid, e.SourceOwnerId,
                   e.Name, e.Description AS EntryDescription, e.Inactive
            FROM cir.Entry e
            JOIN cir.Category c ON c.CategoryKey = e.CategoryKey
            JOIN cir.Registry r ON r.RegistryKey = c.RegistryKey
            WHERE {where}
            ORDER BY r.RegistryId, c.CategoryId, c.CategorySourceId, e.IdInSource, e.SourceId
            """;

        logger.LogDebug("GetRegistry predicate: {Where}", where);

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        foreach (var p in translator.Parameters) cmd.Parameters.Add(p);

        return await ReadRegistryGraphAsync(cn, cmd, ct);
    }

    // =======================================================================
    // Helpers
    // =======================================================================

    private async Task<IReadOnlyList<Registry>> GetSingleEntryAsync(EntryIdentifier id, CancellationToken ct)
    {
        const string sql = """
            SELECT r.RegistryId, r.Description AS RegistryDescription,
                   c.CategoryId, c.CategorySourceId, c.Description AS CategoryDescription,
                   e.EntryKey, e.IdInSource, e.SourceId, e.Cirid, e.SourceOwnerId,
                   e.Name, e.Description AS EntryDescription, e.Inactive
            FROM cir.Entry e
            JOIN cir.Category c ON c.CategoryKey = e.CategoryKey
            JOIN cir.Registry r ON r.RegistryKey = c.RegistryKey
            WHERE r.RegistryId = @rid AND c.CategoryId = @cid AND c.CategorySourceId = @csid
              AND e.IdInSource = @eid AND e.SourceId = @esid
            """;

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@rid", id.RegistryId);
        cmd.Parameters.AddWithValue("@cid", id.CategoryId);
        cmd.Parameters.AddWithValue("@csid", id.CategorySourceId);
        cmd.Parameters.AddWithValue("@eid", id.EntryIdInSource);
        cmd.Parameters.AddWithValue("@esid", id.EntrySourceId);

        return await ReadRegistryGraphAsync(cn, cmd, ct);
    }

    private static Guid? ExtractCirid(IReadOnlyList<Registry> graph) =>
        graph.SelectMany(r => r.Categories)
             .SelectMany(c => c.Entries)
             .Select(e => e.Cirid)
             .FirstOrDefault(g => g is not null);

    /// <summary>Reshapes the flat join result back into the nested Registry graph the spec returns.</summary>
    private async Task<IReadOnlyList<Registry>> ReadRegistryGraphAsync(SqlConnection cn, SqlCommand cmd, CancellationToken ct)
    {
        var rows = new List<Row>();

        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new Row(
                    EntryKey: reader.GetInt64(reader.GetOrdinal("EntryKey")),
                    RegistryId: reader.GetString(reader.GetOrdinal("RegistryId")),
                    RegistryDescription: GetNullableString(reader, "RegistryDescription"),
                    CategoryId: reader.GetString(reader.GetOrdinal("CategoryId")),
                    CategorySourceId: reader.GetString(reader.GetOrdinal("CategorySourceId")),
                    CategoryDescription: GetNullableString(reader, "CategoryDescription"),
                    Entry: new Entry
                    {
                        IdInSource = reader.GetString(reader.GetOrdinal("IdInSource")),
                        SourceId = reader.GetString(reader.GetOrdinal("SourceId")),
                        Cirid = GetNullable<Guid>(reader, "Cirid"),
                        SourceOwnerId = GetNullableString(reader, "SourceOwnerId"),
                        Name = GetNullableString(reader, "Name"),
                        Description = Deserialize<LocalizedText>(GetNullableString(reader, "EntryDescription")),
                        Inactive = GetNullable<bool>(reader, "Inactive")
                    }));
            }
        }

        if (rows.Count == 0) return [];

        var properties = await LoadPropertiesAsync(cn, rows.Select(r => r.EntryKey).ToList(), ct);

        return rows
            .GroupBy(r => (r.RegistryId, r.RegistryDescription))
            .Select(rg => new Registry
            {
                Id = rg.Key.RegistryId,
                Description = DeserializeList<LocalizedText>(rg.Key.RegistryDescription),
                Categories = rg
                    .GroupBy(r => (r.CategoryId, r.CategorySourceId, r.CategoryDescription))
                    .Select(cg => new Category
                    {
                        Id = cg.Key.CategoryId,
                        SourceId = cg.Key.CategorySourceId,
                        Description = DeserializeList<LocalizedText>(cg.Key.CategoryDescription),
                        Entries = cg
                            .Select(r => properties.TryGetValue(r.EntryKey, out var props)
                                ? r.Entry with { Properties = props }
                                : r.Entry)
                            .ToList()
                    })
                    .ToList()
            })
            .ToList();
    }

    private readonly record struct Row(
        long EntryKey,
        string RegistryId,
        string? RegistryDescription,
        string CategoryId,
        string CategorySourceId,
        string? CategoryDescription,
        Entry Entry);

    private async Task<Dictionary<long, List<Property>>> LoadPropertiesAsync(
        SqlConnection cn, IReadOnlyList<long> entryKeys, CancellationToken ct)
    {
        var result = new Dictionary<long, List<Property>>();
        if (entryKeys.Count == 0) return result;

        await using var cmd = new SqlCommand("""
            SELECT p.EntryKey, p.PropertyId, p.DataType, p.PropertyValue
            FROM cir.Property p
            JOIN OPENJSON(@keys) k ON p.EntryKey = CAST(k.value AS BIGINT)
            """, cn);
        cmd.Parameters.AddWithValue("@keys", JsonSerializer.Serialize(entryKeys.Distinct()));

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetInt64(0);
            if (!result.TryGetValue(key, out var list))
                result[key] = list = [];

            list.Add(new Property
            {
                Id = reader.GetString(1),
                DataType = reader.IsDBNull(2) ? null : reader.GetString(2),
                PropertyValue = DeserializeList<PropertyValue>(reader.IsDBNull(3) ? null : reader.GetString(3))
            });
        }

        return result;
    }

    private static async Task<long?> ResolveRegistryKeyAsync(SqlConnection cn, SqlTransaction tx, string registryId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("SELECT RegistryKey FROM cir.Registry WHERE RegistryId = @rid", cn, tx);
        cmd.Parameters.AddWithValue("@rid", registryId);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    private static async Task<long?> ResolveCategoryKeyAsync(
        SqlConnection cn, SqlTransaction tx, long registryKey, string categoryId, string sourceId, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(
            "SELECT CategoryKey FROM cir.Category WHERE RegistryKey = @rk AND CategoryId = @cid AND CategorySourceId = @csid",
            cn, tx);
        cmd.Parameters.AddWithValue("@rk", registryKey);
        cmd.Parameters.AddWithValue("@cid", categoryId);
        cmd.Parameters.AddWithValue("@csid", sourceId);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    private static async Task<long> InsertRegistryAsync(SqlConnection cn, SqlTransaction tx, Registry registry, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO cir.Registry (RegistryId, Description)
            OUTPUT INSERTED.RegistryKey
            VALUES (@rid, @desc)
            """, cn, tx);
        cmd.Parameters.AddWithValue("@rid", registry.Id);
        cmd.Parameters.AddWithValue("@desc", Serialize(registry.Description));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> InsertCategoryAsync(
        SqlConnection cn, SqlTransaction tx, long registryKey, Category category, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO cir.Category (RegistryKey, CategoryId, CategorySourceId, Description)
            OUTPUT INSERTED.CategoryKey
            VALUES (@rk, @cid, @csid, @desc)
            """, cn, tx);
        cmd.Parameters.AddWithValue("@rk", registryKey);
        cmd.Parameters.AddWithValue("@cid", category.Id);
        cmd.Parameters.AddWithValue("@csid", category.SourceId);
        cmd.Parameters.AddWithValue("@desc", Serialize(category.Description));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<long> InsertEntryAsync(
        SqlConnection cn, SqlTransaction tx, long categoryKey, Entry entry, Guid? cirid, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO cir.Entry (CategoryKey, IdInSource, SourceId, Cirid, SourceOwnerId, Name, Description, Inactive)
            OUTPUT INSERTED.EntryKey
            VALUES (@ck, @idsrc, @sid, @cirid, @owner, @name, @desc, @inactive)
            """, cn, tx);
        cmd.Parameters.AddWithValue("@ck", categoryKey);
        cmd.Parameters.AddWithValue("@idsrc", entry.IdInSource);
        cmd.Parameters.AddWithValue("@sid", entry.SourceId);
        cmd.Parameters.AddWithValue("@cirid", (object?)cirid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@owner", (object?)entry.SourceOwnerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", (object?)entry.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@desc", Serialize(entry.Description));
        cmd.Parameters.AddWithValue("@inactive", (object?)entry.Inactive ?? DBNull.Value);

        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new CirFaultException(CirFaultCode.DuplicateEntryFault,
                $"Entry '{entry.IdInSource}'/'{entry.SourceId}' already exists in this category.");
        }
    }

    private static async Task InsertPropertyAsync(
        SqlConnection cn, SqlTransaction tx, long entryKey, Property property, CancellationToken ct)
    {
        await using var cmd = new SqlCommand("""
            INSERT INTO cir.Property (EntryKey, PropertyId, DataType, PropertyValue)
            VALUES (@ek, @pid, @dt, @pv)
            """, cn, tx);
        cmd.Parameters.AddWithValue("@ek", entryKey);
        cmd.Parameters.AddWithValue("@pid", property.Id);
        cmd.Parameters.AddWithValue("@dt", (object?)property.DataType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pv", Serialize(property.PropertyValue));

        try
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new CirFaultException(CirFaultCode.DuplicatePropertyFault,
                $"Property '{property.Id}' already exists on this entry.");
        }
    }


    /// <summary>De-duplicates entries that appear in more than one partial result.</summary>
    private static IReadOnlyList<Registry> Merge(IEnumerable<Registry> parts) =>
        parts.SelectMany(r => r.Categories.Select(c => (r, c)))
             .SelectMany(x => x.c.Entries.Select(e => (x.r, x.c, e)))
             .GroupBy(x => (x.r.Id, x.c.Id, x.c.SourceId, x.e.IdInSource, x.e.SourceId))
             .Select(g => g.First())
             .GroupBy(x => x.r.Id)
             .Select(rg => new Registry
             {
                 Id = rg.Key,
                 Description = rg.First().r.Description,
                 Categories = rg
                     .GroupBy(x => (x.c.Id, x.c.SourceId))
                     .Select(cg => new Category
                     {
                         Id = cg.Key.Id,
                         SourceId = cg.Key.SourceId,
                         Description = cg.First().c.Description,
                         Entries = cg.Select(x => x.e).ToList()
                     })
                     .ToList()
             })
             .ToList();

    // --- Serialization helpers ---------------------------------------------

    private static object Serialize<T>(T? value) =>
        value is null ? DBNull.Value : JsonSerializer.Serialize(value, Json);

    private static object Serialize<T>(IReadOnlyList<T> value) =>
        value.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(value, Json);

    private static T? Deserialize<T>(string? json) where T : class =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<T>(json, Json);

    private static IReadOnlyList<T> DeserializeList<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];

    private static string? GetNullableString(SqlDataReader r, string column)
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static T? GetNullable<T>(SqlDataReader r, string column) where T : struct
    {
        var i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetFieldValue<T>(i);
    }
}
