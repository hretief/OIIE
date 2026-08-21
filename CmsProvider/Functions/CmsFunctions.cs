using System.Net;
using System.Text.Json;
using CmsProvider.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace CmsProvider.Functions;

/// <summary>
/// The CMS HTTP surface.
///
/// Routes are named in CMS's own terms — sites and assets — because that is what
/// this system holds. There is no /segments route and no BOD anywhere in this
/// project: a condition monitoring system has never heard of either.
/// </summary>
public sealed class CmsFunctions(ICmsAssetStore store, ILogger<CmsFunctions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("GetSites")]
    public async Task<HttpResponseData> GetSites(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sites")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetSitesAsync(ct), ct);

    /// <summary>
    /// Creates or updates sites.
    ///
    /// The caller supplies SiteID, so nothing is allocated and nothing surprising
    /// comes back. A site still has to be sent before any asset that references
    /// it, because Asset.SiteID is NOT NULL behind a foreign key.
    ///
    /// Status semantics match UpsertAssets deliberately: a caller should not have
    /// to learn two different conventions for the same customer system.
    /// </summary>
    [Function("UpsertSites")]
    public async Task<HttpResponseData> UpsertSites(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "sites")] HttpRequestData req,
        CancellationToken ct)
    {
        SiteUpsert[]? requested;

        try
        {
            requested = await JsonSerializer.DeserializeAsync<SiteUpsert[]>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (requested is null or { Length: 0 })
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "The request carried no sites.", ct);
        }

        var result = await store.UpsertSitesAsync(requested, ct);

        logger.LogInformation(
            "CMS created {Created}, updated {Updated}, rejected {Rejected} of {Total} site(s).",
            result.Created, result.Updated, result.Rejections.Count, requested.Length);

        var allTransient = result.Sites.Count == 0
            && result.Rejections.Count > 0
            && result.Rejections.All(r => r.Transient);

        return allTransient
            ? await WriteAsync(req, HttpStatusCode.ServiceUnavailable, result, ct)
            : await OkAsync(req, result, ct);
    }

    [Function("GetAssets")]
    public async Task<HttpResponseData> GetAssets(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "assets")] HttpRequestData req,
        CancellationToken ct)
    {
        var siteIdRaw = System.Web.HttpUtility
            .ParseQueryString(req.Url.Query)["siteId"];

        // An unparseable siteId is refused rather than ignored. Silently returning
        // every asset in the plant to a caller who asked for one site's worth is
        // worse than an error.
        if (siteIdRaw is { Length: > 0 } && !Guid.TryParse(siteIdRaw, out _))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Query parameter 'siteId' must be a UUID, but was '{siteIdRaw}'.", ct);
        }

        Guid? siteId = Guid.TryParse(siteIdRaw, out var parsed) ? parsed : null;

        return await OkAsync(req, await store.GetAssetsAsync(siteId, ct), ct);
    }

    [Function("GetAssetById")]
    public async Task<HttpResponseData> GetAssetById(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "assets/{assetId}")] HttpRequestData req,
        string assetId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(assetId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Asset identifier must be a UUID, but was '{assetId}'.", ct);
        }

        var asset = await store.FindAssetAsync(id, ct);

        return asset is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No asset with identifier '{id}'.", ct)
            : await OkAsync(req, asset, ct);
    }

    /// <summary>
    /// Creates or updates assets and returns the key CMS assigned to each.
    ///
    /// Returns 200 for a partial result rather than 207 or 400: the assets that
    /// landed are genuinely in the database, and the rejections are named in the
    /// body. A caller that treats any rejection as total failure would redeliver
    /// work that has already been done.
    ///
    /// 503 when every rejection is transient, so a caller can distinguish "CMS is
    /// briefly unavailable, try again" from "CMS understood you and said no".
    /// </summary>
    [Function("UpsertAssets")]
    public async Task<HttpResponseData> UpsertAssets(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "assets")] HttpRequestData req,
        CancellationToken ct)
    {
        AssetUpsert[]? requested;

        try
        {
            requested = await JsonSerializer.DeserializeAsync<AssetUpsert[]>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (requested is null or { Length: 0 })
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "The request carried no assets.", ct);
        }

        var result = await store.UpsertAssetsAsync(requested, ct);

        logger.LogInformation(
            "CMS created {Created}, updated {Updated}, rejected {Rejected} of {Total} asset(s).",
            result.Created, result.Updated, result.Rejections.Count, requested.Length);

        var allTransient = result.Assets.Count == 0
            && result.Rejections.Count > 0
            && result.Rejections.All(r => r.Transient);

        return allTransient
            ? await WriteAsync(req, HttpStatusCode.ServiceUnavailable, result, ct)
            : await OkAsync(req, result, ct);
    }

    [Function("GetAssetTypes")]
    public async Task<HttpResponseData> GetAssetTypes(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "assettypes")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetAssetTypesAsync(ct), ct);

    /// <summary>
    /// Resolves a site by the code a person would use, for a caller that holds a
    /// code but not a key.
    /// </summary>
    [Function("GetSiteByCode")]
    public async Task<HttpResponseData> GetSiteByCode(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sites/by-code/{siteCode}")] HttpRequestData req,
        string siteCode,
        CancellationToken ct)
    {
        var site = await store.FindSiteByCodeAsync(siteCode, ct);

        return site is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No site with code '{siteCode}'.", ct)
            : await OkAsync(req, site, ct);
    }

    /// <summary>
    /// Resolves an asset by its number within a site.
    ///
    /// The site is part of the route rather than optional because AssetNumber is
    /// only unique within one. A global lookup would have to pick a winner, and
    /// there is no correct way to choose.
    /// </summary>
    [Function("GetAssetByNumber")]
    public async Task<HttpResponseData> GetAssetByNumber(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "sites/{siteId}/assets/{assetNumber}")] HttpRequestData req,
        string siteId,
        string assetNumber,
        CancellationToken ct)
    {
        if (!Guid.TryParse(siteId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Site identifier must be a UUID, but was '{siteId}'.", ct);
        }

        var asset = await store.FindAssetByNumberAsync(id, assetNumber, ct);

        return asset is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No asset numbered '{assetNumber}' at site '{id}'.", ct)
            : await OkAsync(req, asset, ct);
    }

    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var sites = await store.GetSitesAsync(ct);
            return await OkAsync(req, new { status = "healthy", sites = sites.Count }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check failed.");
            return await WriteAsync(req, HttpStatusCode.ServiceUnavailable,
                new { status = "unhealthy", detail = ex.Message }, ct);
        }
    }

    private static Task<HttpResponseData> OkAsync<T>(
        HttpRequestData req, T body, CancellationToken ct)
        => WriteAsync(req, HttpStatusCode.OK, body, ct);

    private static Task<HttpResponseData> ProblemAsync(
        HttpRequestData req, HttpStatusCode status, string detail, CancellationToken ct)
        => WriteAsync(req, status, new { detail }, ct);

    private static async Task<HttpResponseData> WriteAsync<T>(
        HttpRequestData req, HttpStatusCode status, T body, CancellationToken ct)
    {
        // Serialized explicitly rather than via WriteAsJsonAsync: that helper's
        // options overload takes an ObjectSerializer, not JsonSerializerOptions,
        // and its default would ignore the camelCase policy configured here.
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");

        await JsonSerializer.SerializeAsync(response.Body, body, Json, ct);
        return response;
    }
}
