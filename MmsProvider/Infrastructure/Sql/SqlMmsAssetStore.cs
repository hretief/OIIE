using MmsProvider.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace MmsProvider.Infrastructure.Sql;

/// <summary>
/// IMmsAssetStore over Azure SQL, against the TAMS lighting-domain schema.
///
/// Hand-written ADO.NET rather than EF: this project emulates a customer system,
/// and a customer system's data access is not the integrator's to model.
/// </summary>
public sealed class SqlMmsAssetStore(
    IOptions<MmsOptions> options) : IMmsAssetStore
{
    private readonly MmsOptions _options = options.Value;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "MmsProvider.Infrastructure.Sql.drop.sql", ct);

        // Recreated immediately rather than left to the next cold start: a reset
        // that returned success against a database with no tables would leave
        // every later call failing on a missing object, which reads as a broken
        // system rather than an empty one.
        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "MmsProvider.Infrastructure.Sql.schema.sql", ct);

        // Reference data follows schema for the same reason SchemaInitializer
        // orders them this way: the asset tables' foreign keys point at these
        // lookup tables, and a day-zero reset that left them empty would leave
        // MMS unable to accept a single asset until an operator seeded them by
        // hand.
        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "MmsProvider.Infrastructure.Sql.refdata.sql", ct);
    }

    private const string LightSystemSelect = """
        SELECT LIGHT_SYSTEM_ID, LIGHT_SYSTEM_NAME, LIGHT_SYSTEM_CLASS_CODE_ID,
               LIGHT_SYSTEM_STATUS_ID, OWNER_ID, SGL_ELEC_JUR_OWNER_ID, COUNTY_ID,
               EXT_ASSET_ID, DATE_UPDATE
        FROM dbo.LIGHT_SYSTEM_INVENTORY
        """;

    private static MmsLightSystem ReadLightSystem(SqlDataReader r) => new(
        LightSystemId: r.GetInt64(0),
        LightSystemName: r.GetString(1),
        LightSystemClassCodeId: r.GetInt64(2),
        LightSystemStatusId: r.IsDBNull(3) ? null : r.GetInt64(3),
        OwnerId: r.IsDBNull(4) ? null : r.GetInt64(4),
        SglElecJurOwnerId: r.IsDBNull(5) ? null : r.GetInt64(5),
        CountyId: r.IsDBNull(6) ? null : r.GetInt64(6),
        ExtAssetId: r.IsDBNull(7) ? null : r.GetString(7),
        DateUpdate: r.IsDBNull(8) ? null : r.GetDateTime(8));

    public async Task<IReadOnlyList<MmsLightSystem>> GetLightSystemsAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{LightSystemSelect} ORDER BY LIGHT_SYSTEM_NAME;", cn);

        var systems = new List<MmsLightSystem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) systems.Add(ReadLightSystem(reader));
        return systems;
    }

    public async Task<MmsLightSystem?> FindLightSystemAsync(long lightSystemId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{LightSystemSelect} WHERE LIGHT_SYSTEM_ID = @id;", cn);
        cmd.Parameters.AddWithValue("@id", lightSystemId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLightSystem(reader) : null;
    }

    public async Task<MmsLightSystem?> FindLightSystemByExtAssetIdAsync(Guid extAssetId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{LightSystemSelect} WHERE EXT_ASSET_ID = @ext;", cn);
        cmd.Parameters.AddWithValue("@ext", extAssetId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLightSystem(reader) : null;
    }

    public async Task<LightSystemUpsertResult> UpsertLightSystemsAsync(
        IReadOnlyList<LightSystemUpsert> systems, CancellationToken ct)
    {
        var upserted = new List<UpsertedLightSystem>();
        var rejections = new List<UpsertRejection>();

        if (systems.Count == 0) return new LightSystemUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            foreach (var system in systems)
            {
                if (system.ExtAssetId == Guid.Empty)
                {
                    rejections.Add(new UpsertRejection(
                        system.LightSystemName ?? string.Empty,
                        "ExtAssetId is required: MMS matches light systems by federation id.",
                        false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(system.LightSystemName))
                {
                    rejections.Add(new UpsertRejection(
                        system.ExtAssetId.ToString(), "LightSystemName is required.", false));
                    continue;
                }

                upserted.Add(await UpsertOneLightSystemAsync(cn, tx, system, ct));
            }

            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);

            return new LightSystemUpsertResult(
                [],
                [.. systems.Select(s => new UpsertRejection(
                    s.ExtAssetId.ToString(), ex.Message, IsTransient(ex)))]);
        }

        return new LightSystemUpsertResult(upserted, rejections);
    }

    /// <summary>
    /// Matched on ExtAssetId: the caller does not know the TAMS-minted
    /// LightSystemId until this method returns one.
    /// </summary>
    private static async Task<UpsertedLightSystem> UpsertOneLightSystemAsync(
        SqlConnection cn, SqlTransaction tx, LightSystemUpsert system, CancellationToken ct)
    {
        var extAssetId = system.ExtAssetId.ToString();

        await using var update = new SqlCommand(
            """
            UPDATE dbo.LIGHT_SYSTEM_INVENTORY
            SET LIGHT_SYSTEM_NAME          = @name,
                LIGHT_SYSTEM_CLASS_CODE_ID = @classCode,
                LIGHT_SYSTEM_STATUS_ID     = COALESCE(@statusId, LIGHT_SYSTEM_STATUS_ID),
                OWNER_ID                   = COALESCE(@ownerId, OWNER_ID),
                SGL_ELEC_JUR_OWNER_ID      = COALESCE(@jurOwnerId, SGL_ELEC_JUR_OWNER_ID),
                COUNTY_ID                  = COALESCE(@countyId, COUNTY_ID),
                DATE_UPDATE                = SYSUTCDATETIME()
            OUTPUT INSERTED.LIGHT_SYSTEM_ID
            WHERE EXT_ASSET_ID = @ext;
            """, cn, tx);

        BindLightSystem(update, system, extAssetId);

        var updatedId = await update.ExecuteScalarAsync(ct);
        if (updatedId is long id)
        {
            return new UpsertedLightSystem(id, system.ExtAssetId, Created: false);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.LIGHT_SYSTEM_INVENTORY
                (LIGHT_SYSTEM_NAME, LIGHT_SYSTEM_CLASS_CODE_ID, LIGHT_SYSTEM_STATUS_ID,
                 OWNER_ID, SGL_ELEC_JUR_OWNER_ID, COUNTY_ID, EXT_ASSET_ID, DATE_UPDATE)
            OUTPUT INSERTED.LIGHT_SYSTEM_ID
            VALUES
                (@name, @classCode, @statusId, @ownerId, @jurOwnerId, @countyId, @ext, SYSUTCDATETIME());
            """, cn, tx);

        BindLightSystem(insert, system, extAssetId);

        var newId = (long)(await insert.ExecuteScalarAsync(ct))!;
        return new UpsertedLightSystem(newId, system.ExtAssetId, Created: true);
    }

    private static void BindLightSystem(SqlCommand cmd, LightSystemUpsert system, string extAssetId)
    {
        cmd.Parameters.AddWithValue("@ext", extAssetId);
        cmd.Parameters.AddWithValue("@name", system.LightSystemName);
        cmd.Parameters.AddWithValue("@classCode", system.LightSystemClassCodeId);
        cmd.Parameters.AddWithValue("@statusId", (object?)system.LightSystemStatusId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ownerId", (object?)system.OwnerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@jurOwnerId", (object?)system.SglElecJurOwnerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@countyId", (object?)system.CountyId ?? DBNull.Value);
    }

    private const string LightUnitSelect = """
        SELECT LIGHT_UNIT_ID, LIGHT_UNIT_NAME, LIGHT_SYSTEM_ID, LIGHT_UNIT_CLASS_CODE_ID,
               LIGHT_UNIT_STATUS_ID, OWNER_ID, EXT_ASSET_ID, MNDOT_ASSET_NUMBER, DATE_UPDATE
        FROM dbo.LIGHT_UNIT_INVENTORY
        """;

    private static MmsLightUnit ReadLightUnit(SqlDataReader r) => new(
        LightUnitId: r.GetInt64(0),
        LightUnitName: r.GetString(1),
        LightSystemId: r.IsDBNull(2) ? null : r.GetInt64(2),
        LightUnitClassCodeId: r.IsDBNull(3) ? null : r.GetInt64(3),
        LightUnitStatusId: r.IsDBNull(4) ? null : r.GetInt64(4),
        OwnerId: r.IsDBNull(5) ? null : r.GetInt64(5),
        ExtAssetId: r.IsDBNull(6) ? null : r.GetString(6),
        MndotAssetNumber: r.IsDBNull(7) ? null : r.GetString(7),
        DateUpdate: r.IsDBNull(8) ? null : r.GetDateTime(8));

    public async Task<IReadOnlyList<MmsLightUnit>> GetLightUnitsAsync(
        long? lightSystemId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        var sql = lightSystemId is null
            ? $"{LightUnitSelect} ORDER BY LIGHT_UNIT_NAME;"
            : $"{LightUnitSelect} WHERE LIGHT_SYSTEM_ID = @systemId ORDER BY LIGHT_UNIT_NAME;";

        await using var cmd = new SqlCommand(sql, cn);
        if (lightSystemId is not null) cmd.Parameters.AddWithValue("@systemId", lightSystemId.Value);

        var units = new List<MmsLightUnit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) units.Add(ReadLightUnit(reader));
        return units;
    }

    public async Task<MmsLightUnit?> FindLightUnitAsync(long lightUnitId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{LightUnitSelect} WHERE LIGHT_UNIT_ID = @id;", cn);
        cmd.Parameters.AddWithValue("@id", lightUnitId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLightUnit(reader) : null;
    }

    public async Task<MmsLightUnit?> FindLightUnitByExtAssetIdAsync(Guid extAssetId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{LightUnitSelect} WHERE EXT_ASSET_ID = @ext;", cn);
        cmd.Parameters.AddWithValue("@ext", extAssetId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadLightUnit(reader) : null;
    }

    public async Task<LightUnitUpsertResult> UpsertLightUnitsAsync(
        IReadOnlyList<LightUnitUpsert> units, CancellationToken ct)
    {
        var upserted = new List<UpsertedLightUnit>();
        var rejections = new List<UpsertRejection>();

        if (units.Count == 0) return new LightUnitUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            foreach (var unit in units)
            {
                if (unit.ExtAssetId == Guid.Empty)
                {
                    rejections.Add(new UpsertRejection(
                        unit.LightUnitName ?? string.Empty,
                        "ExtAssetId is required: MMS matches light units by federation id.",
                        false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(unit.LightUnitName))
                {
                    rejections.Add(new UpsertRejection(
                        unit.ExtAssetId.ToString(), "LightUnitName is required.", false));
                    continue;
                }

                if (unit.LightSystemId <= 0)
                {
                    rejections.Add(new UpsertRejection(
                        unit.ExtAssetId.ToString(),
                        "LightSystemId is required: a unit must belong to a system already in MMS.",
                        false));
                    continue;
                }

                upserted.Add(await UpsertOneLightUnitAsync(cn, tx, unit, ct));
            }

            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);

            return new LightUnitUpsertResult(
                [],
                [.. units.Select(u => new UpsertRejection(
                    u.ExtAssetId.ToString(), ex.Message, IsTransient(ex)))]);
        }

        return new LightUnitUpsertResult(upserted, rejections);
    }

    private static async Task<UpsertedLightUnit> UpsertOneLightUnitAsync(
        SqlConnection cn, SqlTransaction tx, LightUnitUpsert unit, CancellationToken ct)
    {
        var extAssetId = unit.ExtAssetId.ToString();

        await using var update = new SqlCommand(
            """
            UPDATE dbo.LIGHT_UNIT_INVENTORY
            SET LIGHT_UNIT_NAME          = @name,
                LIGHT_SYSTEM_ID          = @systemId,
                LIGHT_UNIT_CLASS_CODE_ID = COALESCE(@classCode, LIGHT_UNIT_CLASS_CODE_ID),
                LIGHT_UNIT_STATUS_ID     = COALESCE(@statusId, LIGHT_UNIT_STATUS_ID),
                OWNER_ID                 = COALESCE(@ownerId, OWNER_ID),
                MNDOT_ASSET_NUMBER       = COALESCE(@mndotNum, MNDOT_ASSET_NUMBER),
                DATE_UPDATE              = SYSUTCDATETIME()
            OUTPUT INSERTED.LIGHT_UNIT_ID
            WHERE EXT_ASSET_ID = @ext;
            """, cn, tx);

        BindLightUnit(update, unit, extAssetId);

        var updatedId = await update.ExecuteScalarAsync(ct);
        if (updatedId is long id)
        {
            return new UpsertedLightUnit(id, unit.ExtAssetId, Created: false);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.LIGHT_UNIT_INVENTORY
                (LIGHT_UNIT_NAME, LIGHT_SYSTEM_ID, LIGHT_UNIT_CLASS_CODE_ID,
                 LIGHT_UNIT_STATUS_ID, OWNER_ID, MNDOT_ASSET_NUMBER, EXT_ASSET_ID, DATE_UPDATE)
            OUTPUT INSERTED.LIGHT_UNIT_ID
            VALUES
                (@name, @systemId, @classCode, @statusId, @ownerId, @mndotNum, @ext, SYSUTCDATETIME());
            """, cn, tx);

        BindLightUnit(insert, unit, extAssetId);

        var newId = (long)(await insert.ExecuteScalarAsync(ct))!;
        return new UpsertedLightUnit(newId, unit.ExtAssetId, Created: true);
    }

    private static void BindLightUnit(SqlCommand cmd, LightUnitUpsert unit, string extAssetId)
    {
        cmd.Parameters.AddWithValue("@ext", extAssetId);
        cmd.Parameters.AddWithValue("@name", unit.LightUnitName);
        cmd.Parameters.AddWithValue("@systemId", unit.LightSystemId);
        cmd.Parameters.AddWithValue("@classCode", (object?)unit.LightUnitClassCodeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusId", (object?)unit.LightUnitStatusId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ownerId", (object?)unit.OwnerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@mndotNum", (object?)unit.MndotAssetNumber ?? DBNull.Value);
    }

    // ---- Lookups ------------------------------------------------------

    public Task<IReadOnlyList<MmsLookup>> GetLightSystemClassCodesAsync(CancellationToken ct) =>
        GetLookupAsync("LIGHT_SYSTEM_CLASS_CODE", "LIGHT_SYSTEM_CLASS_CODE_ID", "LIGHT_SYSTEM_CLASS_CODE_NAME", ct);

    public Task<IReadOnlyList<MmsLookup>> GetAssetStatusesAsync(CancellationToken ct) =>
        GetLookupAsync("SETUP_ASSET_STATUS", "ASSET_STATUS_ID", "ASSET_STATUS_NAME", ct);

    public Task<IReadOnlyList<MmsLookup>> GetOwnersAsync(CancellationToken ct) =>
        GetLookupAsync("SETUP_OWNER", "OWNER_ID", "OWNER_NAME", ct);

    public Task<IReadOnlyList<MmsLookup>> GetCountiesAsync(CancellationToken ct) =>
        GetLookupAsync("SETUP_COUNTY", "COUNTY_ID", "COUNTY_NAME", ct);

    public Task<IReadOnlyList<MmsLookup>> GetJurisdictionCodesAsync(CancellationToken ct) =>
        GetLookupAsync("SETUP_SGL_ELEC_JUR_CODE", "SGL_ELEC_JUR_CODE_ID", "SGL_ELEC_JUR_CODE_NAME", ct);

    // ---- Owners ---------------------------------------------------------

    public async Task<OwnerUpsertResult> UpsertOwnersAsync(
        IReadOnlyList<OwnerUpsert> owners, CancellationToken ct)
    {
        var upserted = new List<UpsertedOwner>();
        var rejections = new List<UpsertRejection>();

        if (owners.Count == 0) return new OwnerUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            foreach (var owner in owners)
            {
                if (string.IsNullOrWhiteSpace(owner.OwnerName))
                {
                    rejections.Add(new UpsertRejection(
                        owner.OwnerId?.ToString() ?? string.Empty,
                        "OwnerName is required: SETUP_OWNER.OWNER_NAME is NOT NULL.",
                        false));
                    continue;
                }

                var result = await UpsertOneOwnerAsync(cn, tx, owner, ct);

                if (result is null)
                {
                    // The caller named an OWNER_ID that is not there. Not
                    // transient: a row that was deleted will not return, and
                    // creating one under the caller's id is impossible against
                    // an IDENTITY column.
                    rejections.Add(new UpsertRejection(
                        owner.OwnerId!.Value.ToString(),
                        $"No SETUP_OWNER row with OWNER_ID {owner.OwnerId}.",
                        false));
                    continue;
                }

                upserted.Add(result);
            }

            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);

            return new OwnerUpsertResult(
                [],
                [.. owners.Select(o => new UpsertRejection(
                    o.OwnerName ?? string.Empty, ex.Message, IsTransient(ex)))]);
        }

        return new OwnerUpsertResult(upserted, rejections);
    }

    /// <summary>
    /// Resolves one owner to a row, creating it if neither the caller's
    /// OWNER_ID nor its name matches.
    ///
    /// Returns null only when the caller supplied an OWNER_ID that no longer
    /// exists, which the caller reports rather than silently creating a
    /// replacement under a different key.
    ///
    /// The name lookup takes UPDLOCK/HOLDLOCK because SETUP_OWNER carries no
    /// unique constraint on OWNER_NAME -- the real customer table does not have
    /// one, and adding it here would make this emulator diverge from what it
    /// emulates. Without the hint two concurrent drains of the same site both
    /// miss and both insert.
    /// </summary>
    private static async Task<UpsertedOwner?> UpsertOneOwnerAsync(
        SqlConnection cn, SqlTransaction tx, OwnerUpsert owner, CancellationToken ct)
    {
        var name = owner.OwnerName.Trim();

        // Known key: this is a rename, and the incoming name wins. The caller
        // resolved this owner through CIR, so the federation is asserting what
        // this owner is now called.
        if (owner.OwnerId is > 0)
        {
            await using var rename = new SqlCommand(
                """
                UPDATE dbo.SETUP_OWNER
                SET OWNER_NAME  = @name,
                    DATE_UPDATE = SYSUTCDATETIME()
                OUTPUT INSERTED.OWNER_ID
                WHERE OWNER_ID = @id;
                """, cn, tx);

            rename.Parameters.AddWithValue("@name", name);
            rename.Parameters.AddWithValue("@id", owner.OwnerId.Value);

            return await rename.ExecuteScalarAsync(ct) is long renamedId
                ? new UpsertedOwner(renamedId, name, Created: false)
                : null;
        }

        // Case-insensitive and trimmed: the column collation is already
        // case-insensitive, but LTRIM/RTRIM is stated so the match does not
        // depend on it.
        await using var find = new SqlCommand(
            """
            SELECT TOP (1) OWNER_ID
            FROM dbo.SETUP_OWNER WITH (UPDLOCK, HOLDLOCK)
            WHERE LTRIM(RTRIM(OWNER_NAME)) = @name
            ORDER BY OWNER_ID;
            """, cn, tx);

        find.Parameters.AddWithValue("@name", name);

        if (await find.ExecuteScalarAsync(ct) is long existingId)
        {
            return new UpsertedOwner(existingId, name, Created: false);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.SETUP_OWNER (OWNER_NAME, ACTIVE_FLAG, DATE_UPDATE)
            OUTPUT INSERTED.OWNER_ID
            VALUES (@name, 1, SYSUTCDATETIME());
            """, cn, tx);

        insert.Parameters.AddWithValue("@name", name);

        var newId = (long)(await insert.ExecuteScalarAsync(ct))!;
        return new UpsertedOwner(newId, name, Created: true);
    }

    /// <summary>
    /// Every TAMS lookup table shares the same shape: an identity id, a name,
    /// and ACTIVE_FLAG. Table and column names are baked in as constants by
    /// each public method above rather than accepted from a caller, so there
    /// is no user input anywhere near this string concatenation.
    /// </summary>
    private async Task<IReadOnlyList<MmsLookup>> GetLookupAsync(
        string table, string idColumn, string nameColumn, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"SELECT {idColumn}, {nameColumn}, ACTIVE_FLAG FROM dbo.{table} ORDER BY {nameColumn};", cn);

        var rows = new List<MmsLookup>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new MmsLookup(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
        }
        return rows;
    }

    /// <summary>
    /// Whether a later identical attempt could plausibly succeed. Deadlock
    /// (1205) and lock-request timeout (1222) are the two SQL Server errors
    /// this store can hit on a concurrent upsert without there being anything
    /// wrong with the request itself.
    /// </summary>
    private static bool IsTransient(SqlException ex) =>
        ex.Errors.Cast<SqlError>().Any(e => e.Number is 1205 or 1222);
}
