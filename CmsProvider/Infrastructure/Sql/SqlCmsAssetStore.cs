using CmsProvider.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CmsProvider.Infrastructure.Sql;

/// <summary>
/// ICmsAssetStore over Azure SQL.
///
/// Hand-written ADO.NET rather than EF: this project emulates a customer system,
/// and a customer system's data access is not the integrator's to model.
/// </summary>
public sealed class SqlCmsAssetStore(
    IOptions<CmsOptions> options) : ICmsAssetStore
{
    private readonly CmsOptions _options = options.Value;

    /// <summary>
    /// The seeded fallback type. Declared here as well as in schema.sql because
    /// the insert path needs it before any caller has had a chance to state one.
    /// </summary>
    private static readonly Guid UnclassifiedAssetType =
        new("00000000-0000-0000-0000-0000000000FF");

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task ResetAsync(CancellationToken ct)
    {
        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "CmsProvider.Infrastructure.Sql.drop.sql", ct);

        // Recreated immediately rather than left to the next cold start: a reset
        // that returned success against a database with no tables would leave
        // every later call failing on a missing object, which reads as a broken
        // system rather than an empty one.
        await SqlScriptRunner.ExecuteAsync(
            _options.SqlConnectionString, "CmsProvider.Infrastructure.Sql.schema.sql", ct);
    }

    public async Task<IReadOnlyList<CmsSite>> GetSitesAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{SiteSelect} ORDER BY SiteCode;", cn);

        var sites = new List<CmsSite>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) sites.Add(ReadSite(reader));
        return sites;
    }

    public async Task<CmsSite?> FindSiteByCodeAsync(string siteCode, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{SiteSelect} WHERE SiteCode = @code;", cn);
        cmd.Parameters.AddWithValue("@code", siteCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSite(reader) : null;
    }

    public async Task<CmsSite?> FindSiteAsync(Guid siteId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{SiteSelect} WHERE SiteID = @id;", cn);
        cmd.Parameters.AddWithValue("@id", siteId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSite(reader) : null;
    }

    public async Task<SiteUpsertResult> UpsertSitesAsync(
        IReadOnlyList<SiteUpsert> sites, CancellationToken ct)
    {
        var upserted = new List<UpsertedSite>();
        var rejections = new List<UpsertRejection>();

        if (sites.Count == 0) return new SiteUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            foreach (var site in sites)
            {
                if (site.SiteId == Guid.Empty)
                {
                    rejections.Add(new UpsertRejection(
                        site.SiteCode ?? string.Empty,
                        "SiteID is required: CMS does not mint site identifiers.",
                        false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(site.SiteCode))
                {
                    rejections.Add(new UpsertRejection(
                        site.SiteId.ToString(), "SiteCode is required.", false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(site.SiteName))
                {
                    rejections.Add(new UpsertRejection(
                        site.SiteCode, "SiteName is required.", false));
                    continue;
                }

                upserted.Add(await UpsertOneSiteAsync(cn, tx, site, ct));
            }

            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);

            return new SiteUpsertResult(
                [],
                [.. sites.Select(s => new UpsertRejection(
                    s.SiteCode ?? s.SiteId.ToString(), ex.Message, IsTransient(ex)))]);
        }

        return new SiteUpsertResult(upserted, rejections);
    }

    /// <summary>
    /// Matched on SiteID alone. The caller supplied the key, so there is no
    /// lookup to race against and no identifier to allocate.
    /// </summary>
    private static async Task<UpsertedSite> UpsertOneSiteAsync(
        SqlConnection cn, SqlTransaction tx, SiteUpsert site, CancellationToken ct)
    {
        await using var update = new SqlCommand(
            """
            UPDATE dbo.Site
            SET SiteCode     = @code,
                SiteName     = @name,
                Description  = COALESCE(@description, Description),
                SiteType     = COALESCE(@type, SiteType),
                ParentSiteID = COALESCE(@parent, ParentSiteID),
                Country      = COALESCE(@country, Country),
                Region       = COALESCE(@region, Region),
                Status       = COALESCE(@status, Status),
                UpdatedDate  = SYSUTCDATETIME()
            WHERE SiteID = @id;
            """, cn, tx);

        BindSite(update, site);

        if (await update.ExecuteNonQueryAsync(ct) > 0)
        {
            return new UpsertedSite(site.SiteId, site.SiteCode, Created: false);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.Site
                (SiteID, SiteCode, SiteName, Description, SiteType,
                 ParentSiteID, Country, Region, Status)
            VALUES
                (@id, @code, @name, @description, @type,
                 @parent, @country, @region, @status);
            """, cn, tx);

        BindSite(insert, site);
        await insert.ExecuteNonQueryAsync(ct);

        return new UpsertedSite(site.SiteId, site.SiteCode, Created: true);
    }

    private static void BindSite(SqlCommand cmd, SiteUpsert site)
    {
        cmd.Parameters.AddWithValue("@id", site.SiteId);
        cmd.Parameters.AddWithValue("@code", site.SiteCode);
        cmd.Parameters.AddWithValue("@name", site.SiteName);
        cmd.Parameters.AddWithValue("@description", (object?)site.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", (object?)site.SiteType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)site.ParentSiteId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@country", (object?)site.Country ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@region", (object?)site.Region ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (object?)site.Status ?? DBNull.Value);
    }

    public async Task<CmsAsset?> FindAssetAsync(Guid assetId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{AssetSelect} WHERE AssetID = @id;", cn);
        cmd.Parameters.AddWithValue("@id", assetId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAsset(reader) : null;
    }

    public async Task<CmsAsset?> FindAssetByNumberAsync(
        Guid siteId, string assetNumber, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"{AssetSelect} WHERE SiteID = @siteId AND AssetNumber = @number;", cn);
        cmd.Parameters.AddWithValue("@siteId", siteId);
        cmd.Parameters.AddWithValue("@number", assetNumber);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAsset(reader) : null;
    }

    public async Task<IReadOnlyList<CmsAssetType>> GetAssetTypesAsync(CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand($"{AssetTypeSelect} ORDER BY AssetTypeCode;", cn);

        var types = new List<CmsAssetType>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) types.Add(ReadAssetType(reader));
        return types;
    }

    public async Task<CmsAssetType?> FindAssetTypeByCodeAsync(
        string assetTypeCode, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            $"{AssetTypeSelect} WHERE AssetTypeCode = @code;", cn);
        cmd.Parameters.AddWithValue("@code", assetTypeCode);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAssetType(reader) : null;
    }

    public async Task<IReadOnlyList<CmsAsset>> GetAssetsAsync(Guid? siteId, CancellationToken ct)
    {
        await using var cn = await OpenAsync(ct);
        var sql = siteId is null
            ? $"{AssetSelect} ORDER BY AssetNumber;"
            : $"{AssetSelect} WHERE SiteID = @siteId ORDER BY AssetNumber;";

        await using var cmd = new SqlCommand(sql, cn);
        if (siteId is not null) cmd.Parameters.AddWithValue("@siteId", siteId.Value);

        var assets = new List<CmsAsset>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) assets.Add(ReadAsset(reader));
        return assets;
    }

    public async Task<AssetUpsertResult> UpsertAssetsAsync(
        IReadOnlyList<AssetUpsert> assets, CancellationToken ct)
    {
        var upserted = new List<UpsertedAsset>();
        var rejections = new List<UpsertRejection>();

        if (assets.Count == 0) return new AssetUpsertResult(upserted, rejections);

        await using var cn = await OpenAsync(ct);
        await using var tx = (SqlTransaction)await cn.BeginTransactionAsync(ct);

        try
        {
            // Read once. A caller sending fifty assets for one site should not pay
            // for fifty lookups, and neither set can change inside the transaction.
            var siteIds = await ExistingKeysAsync(cn, tx, "SELECT SiteID FROM dbo.Site;", ct);
            var typeIds = await ExistingKeysAsync(cn, tx, "SELECT AssetTypeID FROM dbo.AssetType;", ct);

            foreach (var asset in assets)
            {
                if (asset.AssetId == Guid.Empty)
                {
                    rejections.Add(new UpsertRejection(
                        asset.AssetNumber ?? string.Empty,
                        "AssetID is required: CMS does not mint asset identifiers.",
                        false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(asset.AssetNumber))
                {
                    rejections.Add(new UpsertRejection(
                        asset.AssetId.ToString(), "AssetNumber is required.", false));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(asset.AssetName))
                {
                    rejections.Add(new UpsertRejection(
                        asset.AssetNumber, "AssetName is required.", false));
                    continue;
                }

                // SiteID is NOT NULL with an FK, so an unknown site cannot be stored.
                // Rejecting here rather than letting the FK fire keeps the reason
                // specific and leaves the transaction usable for the rest of the batch.
                if (!siteIds.Contains(asset.SiteId))
                {
                    rejections.Add(new UpsertRejection(
                        asset.AssetNumber,
                        $"Site {asset.SiteId} is not provisioned in CMS.",
                        false));
                    continue;
                }

                // An unknown type is not a reason to refuse the asset, but silently
                // substituting for a type the caller explicitly named would hide a
                // real mismatch. Only an unstated type falls back.
                var assetTypeId = asset.AssetTypeId ?? UnclassifiedAssetType;

                if (!typeIds.Contains(assetTypeId))
                {
                    rejections.Add(new UpsertRejection(
                        asset.AssetNumber,
                        $"AssetType {assetTypeId} is not defined in CMS.",
                        false));
                    continue;
                }

                upserted.Add(await UpsertOneAssetAsync(cn, tx, asset, assetTypeId, ct));
            }

            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);

            // The whole batch failed, so nothing was stored. Report every asset as
            // rejected rather than returning a partial success that did not happen.
            return new AssetUpsertResult(
                [],
                [.. assets.Select(a => new UpsertRejection(
                    a.AssetNumber ?? a.AssetId.ToString(), ex.Message, IsTransient(ex)))]);
        }

        return new AssetUpsertResult(upserted, rejections);
    }

    /// <summary>
    /// Matched on AssetID. An asset renumbered at the plant updates the same row,
    /// where matching on AssetNumber would create a second one.
    /// </summary>
    private static async Task<UpsertedAsset> UpsertOneAssetAsync(
        SqlConnection cn, SqlTransaction tx, AssetUpsert asset, Guid assetTypeId, CancellationToken ct)
    {
        // The update names only the columns an integrator may legitimately assert.
        // Criticality, risk ranking, serial and manufacturer belong to whoever
        // surveyed the asset and are never overwritten from outside.
        await using var update = new SqlCommand(
            """
            UPDATE dbo.Asset
            SET SiteID        = @siteId,
                AssetTypeID   = @typeId,
                AssetNumber   = @number,
                AssetName     = @name,
                Description   = COALESCE(@description, Description),
                Status        = COALESCE(@status, Status),
                ParentAssetID = COALESCE(@parent, ParentAssetID),
                UpdatedDate   = SYSUTCDATETIME()
            WHERE AssetID = @id;
            """, cn, tx);

        BindAsset(update, asset, assetTypeId);

        if (await update.ExecuteNonQueryAsync(ct) > 0)
        {
            return new UpsertedAsset(asset.AssetId, assetTypeId, Created: false);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.Asset
                (AssetID, SiteID, AssetTypeID, AssetNumber, AssetName,
                 Description, Status, ParentAssetID)
            VALUES
                (@id, @siteId, @typeId, @number, @name,
                 @description, @status, @parent);
            """, cn, tx);

        BindAsset(insert, asset, assetTypeId);
        await insert.ExecuteNonQueryAsync(ct);

        return new UpsertedAsset(asset.AssetId, assetTypeId, Created: true);
    }

    private static void BindAsset(SqlCommand cmd, AssetUpsert asset, Guid assetTypeId)
    {
        cmd.Parameters.AddWithValue("@id", asset.AssetId);
        cmd.Parameters.AddWithValue("@siteId", asset.SiteId);
        cmd.Parameters.AddWithValue("@typeId", assetTypeId);
        cmd.Parameters.AddWithValue("@number", asset.AssetNumber);
        cmd.Parameters.AddWithValue("@name", asset.AssetName);
        cmd.Parameters.AddWithValue("@description", (object?)asset.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", (object?)asset.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)asset.ParentAssetId ?? DBNull.Value);
    }

    private static async Task<HashSet<Guid>> ExistingKeysAsync(
        SqlConnection cn, SqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, cn, tx);
        var ids = new HashSet<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// Classifies by SQL error number, never by message text. Message parsing is
    /// how the previous generation of this integration acquired its scar tissue:
    /// it is locale-dependent and silently wrong when a provider reworders a string.
    /// </summary>
    private static bool IsTransient(SqlException ex) => ex.Number switch
    {
        -2 => true,     // timeout
        49918 => true,  // cannot process request, not enough resources
        49919 => true,  // cannot process create or update request
        49920 => true,  // cannot process request, too many operations
        40501 => true,  // service busy
        40613 => true,  // database unavailable
        40197 => true,  // service error processing request
        40143 => true,  // could not process request
        4060 => true,   // cannot open database
        1205 => true,   // deadlock victim
        233 => true,    // no process on the other end of the pipe
        10053 => true,  // transport-level error
        10054 => true,  // existing connection forcibly closed
        10060 => true,  // network or instance-specific error
        64 => true,     // connection failed during login
        _ => false
    };

    private const string SiteSelect =
        """
        SELECT SiteID, SiteCode, SiteName, Description, SiteType,
               ParentSiteID, Country, Region, Status, CreatedDate, UpdatedDate
        FROM dbo.Site
        """;

    private const string AssetSelect =
        """
        SELECT AssetID, SiteID, AssetTypeID, AssetNumber, AssetName, Description,
               Manufacturer, ModelNumber, SerialNumber, CommissionDate,
               RetirementDate, CriticalityScore, RiskRanking, Status,
               ParentAssetID, CreatedDate, UpdatedDate
        FROM dbo.Asset
        """;

    private const string AssetTypeSelect =
        """
        SELECT AssetTypeID, AssetTypeCode, AssetTypeName, Description,
               ParentAssetTypeID, CriticalityClass, ExpectedLifeYears
        FROM dbo.AssetType
        """;

    private static CmsAssetType ReadAssetType(SqlDataReader r) => new(
        AssetTypeId: r.GetGuid(0),
        AssetTypeCode: r.GetString(1),
        AssetTypeName: r.GetString(2),
        Description: r.IsDBNull(3) ? null : r.GetString(3),
        ParentAssetTypeId: r.IsDBNull(4) ? null : r.GetGuid(4),
        CriticalityClass: r.IsDBNull(5) ? null : r.GetString(5),
        ExpectedLifeYears: r.IsDBNull(6) ? null : r.GetInt32(6));

    private static CmsSite ReadSite(SqlDataReader r) => new(
        SiteId: r.GetGuid(0),
        SiteCode: r.GetString(1),
        SiteName: r.GetString(2),
        Description: r.IsDBNull(3) ? null : r.GetString(3),
        SiteType: r.IsDBNull(4) ? null : r.GetString(4),
        ParentSiteId: r.IsDBNull(5) ? null : r.GetGuid(5),
        Country: r.IsDBNull(6) ? null : r.GetString(6),
        Region: r.IsDBNull(7) ? null : r.GetString(7),
        Status: r.IsDBNull(8) ? null : r.GetString(8),
        CreatedDate: r.GetDateTime(9),
        UpdatedDate: r.IsDBNull(10) ? null : r.GetDateTime(10));

    private static CmsAsset ReadAsset(SqlDataReader r) => new(
        AssetId: r.GetGuid(0),
        SiteId: r.GetGuid(1),
        AssetTypeId: r.GetGuid(2),
        AssetNumber: r.GetString(3),
        AssetName: r.GetString(4),
        Description: r.IsDBNull(5) ? null : r.GetString(5),
        Manufacturer: r.IsDBNull(6) ? null : r.GetString(6),
        ModelNumber: r.IsDBNull(7) ? null : r.GetString(7),
        SerialNumber: r.IsDBNull(8) ? null : r.GetString(8),
        CommissionDate: r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)),
        RetirementDate: r.IsDBNull(10) ? null : DateOnly.FromDateTime(r.GetDateTime(10)),
        CriticalityScore: r.IsDBNull(11) ? null : r.GetDecimal(11),
        RiskRanking: r.IsDBNull(12) ? null : r.GetString(12),
        Status: r.IsDBNull(13) ? null : r.GetString(13),
        ParentAssetId: r.IsDBNull(14) ? null : r.GetGuid(14),
        CreatedDate: r.GetDateTime(15),
        UpdatedDate: r.IsDBNull(16) ? null : r.GetDateTime(16));
}
