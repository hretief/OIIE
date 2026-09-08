using System.Net;
using System.Text.Json;
using MmsProvider.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace MmsProvider.Functions;

/// <summary>
/// The MMS HTTP surface, over the TAMS lighting domain.
///
/// Routes are named in TAMS's own terms -- light systems and light units --
/// because that is what this system holds. There is no /segments route and no
/// BOD anywhere in this project: a maintenance management system has never
/// heard of either.
/// </summary>
public sealed class MmsFunctions(IMmsAssetStore store, ILogger<MmsFunctions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Day zero: empties MMS entirely and reseeds its reference data.
    ///
    /// Routed under "mms/" rather than "admin/" because the Functions host
    /// reserves the admin prefix for its own endpoints. A function declared
    /// there fails indexing and is silently disabled -- it does not appear in
    /// the started route list and calling it returns 404, which looks like a
    /// deployment problem rather than a naming one.
    /// </summary>
    [Function("ResetMmsData")]
    public async Task<HttpResponseData> ResetMmsData(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "mms/reset")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            await store.ResetAsync(ct);
            logger.LogWarning("MMS data was reset; every table was dropped, recreated and reseeded.");
            return await OkAsync(req, new { reset = true }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MMS reset failed.");
            return await ProblemAsync(req, HttpStatusCode.InternalServerError,
                $"Reset failed: {ex.Message}", ct);
        }
    }

    [Function("GetLightSystems")]
    public async Task<HttpResponseData> GetLightSystems(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lightsystems")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetLightSystemsAsync(ct), ct);

    [Function("GetLightSystemById")]
    public async Task<HttpResponseData> GetLightSystemById(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lightsystems/{lightSystemId}")] HttpRequestData req,
        string lightSystemId,
        CancellationToken ct)
    {
        if (!long.TryParse(lightSystemId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Light system identifier must be an integer, but was '{lightSystemId}'.", ct);
        }

        var system = await store.FindLightSystemAsync(id, ct);

        return system is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No light system with identifier '{id}'.", ct)
            : await OkAsync(req, system, ct);
    }

    /// <summary>
    /// Resolves a light system by the federation identifier its originating
    /// system carries, for a caller that holds only that GUID.
    /// </summary>
    [Function("GetLightSystemByExtAssetId")]
    public async Task<HttpResponseData> GetLightSystemByExtAssetId(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lightsystems/by-ext/{extAssetId}")] HttpRequestData req,
        string extAssetId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(extAssetId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"External asset identifier must be a UUID, but was '{extAssetId}'.", ct);
        }

        var system = await store.FindLightSystemByExtAssetIdAsync(id, ct);

        return system is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No light system with external asset identifier '{id}'.", ct)
            : await OkAsync(req, system, ct);
    }

    /// <summary>
    /// Creates or updates light systems, matched on the caller's own federation
    /// identifier rather than TAMS's identity column, since the caller cannot
    /// know that column's value before the first upsert.
    /// </summary>
    [Function("UpsertLightSystems")]
    public async Task<HttpResponseData> UpsertLightSystems(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "lightsystems")] HttpRequestData req,
        CancellationToken ct)
    {
        LightSystemUpsert[]? requested;

        try
        {
            requested = await JsonSerializer.DeserializeAsync<LightSystemUpsert[]>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (requested is null or { Length: 0 })
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "The request carried no light systems.", ct);
        }

        var result = await store.UpsertLightSystemsAsync(requested, ct);

        logger.LogInformation(
            "MMS created {Created}, updated {Updated}, rejected {Rejected} of {Total} light system(s).",
            result.Created, result.Updated, result.Rejections.Count, requested.Length);

        var allTransient = result.Systems.Count == 0
            && result.Rejections.Count > 0
            && result.Rejections.All(r => r.Transient);

        return allTransient
            ? await WriteAsync(req, HttpStatusCode.ServiceUnavailable, result, ct)
            : await OkAsync(req, result, ct);
    }

    [Function("GetLightUnits")]
    public async Task<HttpResponseData> GetLightUnits(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lightunits")] HttpRequestData req,
        CancellationToken ct)
    {
        var lightSystemIdRaw = System.Web.HttpUtility
            .ParseQueryString(req.Url.Query)["lightSystemId"];

        // An unparseable lightSystemId is refused rather than ignored. Silently
        // returning every unit in TAMS to a caller who asked for one system's
        // worth is worse than an error.
        if (lightSystemIdRaw is { Length: > 0 } && !long.TryParse(lightSystemIdRaw, out _))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Query parameter 'lightSystemId' must be an integer, but was '{lightSystemIdRaw}'.", ct);
        }

        long? lightSystemId = long.TryParse(lightSystemIdRaw, out var parsed) ? parsed : null;

        return await OkAsync(req, await store.GetLightUnitsAsync(lightSystemId, ct), ct);
    }

    [Function("GetLightUnitById")]
    public async Task<HttpResponseData> GetLightUnitById(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lightunits/{lightUnitId}")] HttpRequestData req,
        string lightUnitId,
        CancellationToken ct)
    {
        if (!long.TryParse(lightUnitId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Light unit identifier must be an integer, but was '{lightUnitId}'.", ct);
        }

        var unit = await store.FindLightUnitAsync(id, ct);

        return unit is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No light unit with identifier '{id}'.", ct)
            : await OkAsync(req, unit, ct);
    }

    [Function("GetLightUnitByExtAssetId")]
    public async Task<HttpResponseData> GetLightUnitByExtAssetId(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lightunits/by-ext/{extAssetId}")] HttpRequestData req,
        string extAssetId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(extAssetId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"External asset identifier must be a UUID, but was '{extAssetId}'.", ct);
        }

        var unit = await store.FindLightUnitByExtAssetIdAsync(id, ct);

        return unit is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No light unit with external asset identifier '{id}'.", ct)
            : await OkAsync(req, unit, ct);
    }

    /// <summary>
    /// Creates or updates light units and returns the key TAMS assigned to each.
    ///
    /// Returns 200 for a partial result rather than 207 or 400: the units that
    /// landed are genuinely in the database, and the rejections are named in the
    /// body. A caller that treats any rejection as total failure would redeliver
    /// work that has already been done.
    ///
    /// 503 when every rejection is transient, so a caller can distinguish "MMS is
    /// briefly unavailable, try again" from "MMS understood you and said no".
    /// </summary>
    [Function("UpsertLightUnits")]
    public async Task<HttpResponseData> UpsertLightUnits(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "lightunits")] HttpRequestData req,
        CancellationToken ct)
    {
        LightUnitUpsert[]? requested;

        try
        {
            requested = await JsonSerializer.DeserializeAsync<LightUnitUpsert[]>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (requested is null or { Length: 0 })
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "The request carried no light units.", ct);
        }

        var result = await store.UpsertLightUnitsAsync(requested, ct);

        logger.LogInformation(
            "MMS created {Created}, updated {Updated}, rejected {Rejected} of {Total} light unit(s).",
            result.Created, result.Updated, result.Rejections.Count, requested.Length);

        var allTransient = result.Units.Count == 0
            && result.Rejections.Count > 0
            && result.Rejections.All(r => r.Transient);

        return allTransient
            ? await WriteAsync(req, HttpStatusCode.ServiceUnavailable, result, ct)
            : await OkAsync(req, result, ct);
    }

    // ---- Owners ---------------------------------------------------------

    /// <summary>
    /// Creates or renames owners and returns the OWNER_ID TAMS assigned to each.
    ///
    /// A write onto what is otherwise a lookup table, because SETUP_OWNER is
    /// where an incoming site lands: a site is the context a work order is
    /// raised within, and TAMS spells that as an owner. The caller cannot
    /// supply OWNER_ID on first contact -- it is IDENTITY-assigned -- which is
    /// the whole reason this returns it.
    ///
    /// Same partial-success and 503-on-all-transient convention as the light
    /// system and unit paths, so the ingest leg's retry behaviour does not have
    /// to special-case owners.
    /// </summary>
    [Function("UpsertOwners")]
    public async Task<HttpResponseData> UpsertOwners(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "owners")] HttpRequestData req,
        CancellationToken ct)
    {
        OwnerUpsert[]? requested;

        try
        {
            requested = await JsonSerializer.DeserializeAsync<OwnerUpsert[]>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (requested is null or { Length: 0 })
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "The request carried no owners.", ct);
        }

        var result = await store.UpsertOwnersAsync(requested, ct);

        logger.LogInformation(
            "MMS created {Created}, updated {Updated}, rejected {Rejected} of {Total} owner(s).",
            result.Created, result.Updated, result.Rejections.Count, requested.Length);

        var allTransient = result.Owners.Count == 0
            && result.Rejections.Count > 0
            && result.Rejections.All(r => r.Transient);

        return allTransient
            ? await WriteAsync(req, HttpStatusCode.ServiceUnavailable, result, ct)
            : await OkAsync(req, result, ct);
    }

    // ---- Lookups --------------------------------------------------------

    [Function("GetLightSystemClassCodes")]
    public async Task<HttpResponseData> GetLightSystemClassCodes(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lookups/lightsystemclasscodes")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetLightSystemClassCodesAsync(ct), ct);

    [Function("GetAssetStatuses")]
    public async Task<HttpResponseData> GetAssetStatuses(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lookups/assetstatuses")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetAssetStatusesAsync(ct), ct);

    [Function("GetOwners")]
    public async Task<HttpResponseData> GetOwners(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lookups/owners")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetOwnersAsync(ct), ct);

    [Function("GetCounties")]
    public async Task<HttpResponseData> GetCounties(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lookups/counties")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetCountiesAsync(ct), ct);

    [Function("GetJurisdictionCodes")]
    public async Task<HttpResponseData> GetJurisdictionCodes(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "lookups/jurisdictioncodes")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetJurisdictionCodesAsync(ct), ct);

    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var systems = await store.GetLightSystemsAsync(ct);
            return await OkAsync(req, new { status = "healthy", lightSystems = systems.Count }, ct);
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
