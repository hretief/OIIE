using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EngProvider.Application;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace EngProvider.Functions;

/// <summary>
/// The ENG HTTP surface.
///
/// Routes are named in ENG's own terms — iTwins, iModels, elements, named
/// versions — because that is what this system holds. There is no /segments
/// route and no BOD anywhere in this project: an engineering design tool has
/// never heard of either.
///
/// There is no /tags route either. An element is what an engineer calls a tag,
/// so a second route for it would be a second name for one thing.
///
/// Nor is there a /publish route. Releasing a named version stops at the
/// database; carrying a release onto a channel is the integrator's work.
/// </summary>
public sealed class EngFunctions(IEngDesignStore store, ILogger<EngFunctions> logger)
{
    // Enums are written as names, not ordinals. 'Released' survives a schema
    // reorder; 1 does not, and a caller reading it has no way to notice.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ---- iTwins and iModels ----------------------------------------------

    [Function("GetITwins")]
    public async Task<HttpResponseData> GetITwins(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "itwins")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetITwinsAsync(ct), ct);

    [Function("GetIModels")]
    public async Task<HttpResponseData> GetIModels(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "imodels")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        if (!TryParseOptionalGuid(query["iTwinId"], out var iTwinId, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetIModelsAsync(iTwinId, ct), ct);
    }

    [Function("GetIModel")]
    public async Task<HttpResponseData> GetIModel(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "imodels/{iModelId}")] HttpRequestData req,
        string iModelId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(iModelId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"iModel identifier must be a UUID, but was '{iModelId}'.", ct);
        }

        var model = await store.FindIModelAsync(id, ct);

        return model is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No iModel '{id}'.", ct)
            : await OkAsync(req, model, ct);
    }

    // ---- Schema catalogue -------------------------------------------------

    /// <summary>
    /// The classes an element may be created on.
    ///
    /// A caller needs this before it can post an element, since ECClassId is
    /// required and abstract classes are refused.
    /// </summary>
    [Function("GetClasses")]
    public async Task<HttpResponseData> GetClasses(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "classes")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetConcreteClassesAsync(ct), ct);

    // ---- Elements ---------------------------------------------------------

    [Function("GetElements")]
    public async Task<HttpResponseData> GetElements(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "elements")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        if (!TryParseOptionalGuid(query["iModelId"], out var iModelId, out var guidError))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, guidError!, ct);

        long? namedVersionId = null;
        var versionRaw = query["namedVersionId"];

        if (!string.IsNullOrWhiteSpace(versionRaw))
        {
            if (!long.TryParse(versionRaw, out var parsed))
            {
                return await ProblemAsync(req, HttpStatusCode.BadRequest,
                    $"namedVersionId must be an integer, but was '{versionRaw}'.", ct);
            }

            namedVersionId = parsed;
        }

        bool? released = null;
        var releasedRaw = query["released"];

        if (!string.IsNullOrWhiteSpace(releasedRaw))
        {
            if (!bool.TryParse(releasedRaw, out var parsed))
            {
                return await ProblemAsync(req, HttpStatusCode.BadRequest,
                    $"released must be true or false, but was '{releasedRaw}'.", ct);
            }

            released = parsed;
        }

        return await OkAsync(req, await store.GetElementsAsync(iModelId, namedVersionId, released, ct), ct);
    }

    [Function("GetElement")]
    public async Task<HttpResponseData> GetElement(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "elements/{ecInstanceId}")] HttpRequestData req,
        string ecInstanceId,
        CancellationToken ct)
    {
        if (!long.TryParse(ecInstanceId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Element identifier must be an integer, but was '{ecInstanceId}'.", ct);
        }

        var element = await store.FindElementAsync(id, ct);

        return element is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No element '{id}'.", ct)
            : await OkAsync(req, element, ct);
    }

    /// <summary>
    /// Finds an element by the code a person would read off a drawing.
    ///
    /// The iModel is part of the route because a code is unique only within one:
    /// two projects may each hold a P-101 and mean different pumps.
    /// </summary>
    [Function("GetElementByCode")]
    public async Task<HttpResponseData> GetElementByCode(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "imodels/{iModelId}/elements/by-code/{codeValue}")] HttpRequestData req,
        string iModelId,
        string codeValue,
        CancellationToken ct)
    {
        if (!Guid.TryParse(iModelId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"iModel identifier must be a UUID, but was '{iModelId}'.", ct);
        }

        var element = await store.FindElementByCodeAsync(id, codeValue, ct);

        return element is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No element with code '{codeValue}' in iModel '{id}'.", ct)
            : await OkAsync(req, element, ct);
    }

    /// <summary>
    /// Creates or updates elements within one draft named version.
    ///
    /// The version is in the route rather than in each item, so a batch cannot
    /// span versions and half-succeed.
    /// </summary>
    [Function("UpsertElements")]
    public async Task<HttpResponseData> UpsertElements(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "named-versions/{namedVersionId}/elements")] HttpRequestData req,
        string namedVersionId,
        CancellationToken ct)
    {
        if (!long.TryParse(namedVersionId, out var versionId))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Named version identifier must be an integer, but was '{namedVersionId}'.", ct);
        }

        ElementUpsert[]? requested;

        try
        {
            requested = await JsonSerializer.DeserializeAsync<ElementUpsert[]>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (requested is null or { Length: 0 })
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "The request carried no elements.", ct);
        }

        var result = await store.UpsertElementsAsync(versionId, requested, ct);

        logger.LogInformation(
            "ENG created {Created}, updated {Updated}, rejected {Rejected} of {Total} element(s) in version {Version}.",
            result.Created, result.Updated, result.Rejections.Count, requested.Length, versionId);

        return await UpsertResponseAsync(
            req, result, result.Elements.Count, result.Rejections, ct);
    }

    // ---- Named versions ---------------------------------------------------

    [Function("GetNamedVersions")]
    public async Task<HttpResponseData> GetNamedVersions(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "named-versions")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        if (!TryParseOptionalGuid(query["iModelId"], out var iModelId, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetNamedVersionsAsync(iModelId, ct), ct);
    }

    [Function("GetNamedVersion")]
    public async Task<HttpResponseData> GetNamedVersion(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "named-versions/{namedVersionId}")] HttpRequestData req,
        string namedVersionId,
        CancellationToken ct)
    {
        if (!long.TryParse(namedVersionId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Named version identifier must be an integer, but was '{namedVersionId}'.", ct);
        }

        var version = await store.FindNamedVersionAsync(id, ct);

        return version is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No named version '{id}'.", ct)
            : await OkAsync(req, version, ct);
    }

    [Function("CreateNamedVersion")]
    public async Task<HttpResponseData> CreateNamedVersion(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "named-versions")] HttpRequestData req,
        CancellationToken ct)
    {
        NamedVersionDraft? draft;

        try
        {
            draft = await JsonSerializer.DeserializeAsync<NamedVersionDraft>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (draft is null || draft.IModelId == Guid.Empty || string.IsNullOrWhiteSpace(draft.Name))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "iModelId and name are required.", ct);
        }

        var created = await store.CreateNamedVersionAsync(draft, ct);

        logger.LogInformation(
            "ENG created named version {Version} '{Name}'.", created.NamedVersionId, created.Name);

        return await WriteAsync(req, HttpStatusCode.Created, created, ct);
    }

    /// <summary>
    /// Releases a named version, if the gate allows.
    ///
    /// A refusal is a 409, not a 400: the request is well formed and the caller
    /// may well be entitled to make it — the server's state is what says no, and
    /// the same request may succeed once the findings are resolved.
    /// </summary>
    [Function("ReleaseNamedVersion")]
    public async Task<HttpResponseData> ReleaseNamedVersion(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "named-versions/{namedVersionId}/release")] HttpRequestData req,
        string namedVersionId,
        CancellationToken ct)
    {
        if (!long.TryParse(namedVersionId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Named version identifier must be an integer, but was '{namedVersionId}'.", ct);
        }

        var result = await store.ReleaseNamedVersionAsync(id, ct);

        if (result.Released)
        {
            logger.LogInformation(
                "ENG released named version {Version} '{Name}' with {Count} element(s).",
                result.NamedVersionId, result.Name, result.ElementCount);

            return await OkAsync(req, result, ct);
        }

        logger.LogInformation(
            "ENG refused to release named version {Version}: {Reason}", id, result.Reason);

        // A version that does not exist is a 404; anything else is a state conflict.
        var status = result.Reason is not null && result.Reason.StartsWith("No named version")
            ? HttpStatusCode.NotFound
            : HttpStatusCode.Conflict;

        return await WriteAsync(req, status, result, ct);
    }

    // ---- Validation findings ----------------------------------------------

    [Function("GetFindings")]
    public async Task<HttpResponseData> GetFindings(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "named-versions/{namedVersionId}/findings")] HttpRequestData req,
        string namedVersionId,
        CancellationToken ct)
    {
        if (!long.TryParse(namedVersionId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Named version identifier must be an integer, but was '{namedVersionId}'.", ct);
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var openOnly = !string.Equals(query["all"], "true", StringComparison.OrdinalIgnoreCase);

        return await OkAsync(req, await store.GetFindingsAsync(id, openOnly, ct), ct);
    }

    [Function("RaiseFinding")]
    public async Task<HttpResponseData> RaiseFinding(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "named-versions/{namedVersionId}/findings")] HttpRequestData req,
        string namedVersionId,
        CancellationToken ct)
    {
        if (!long.TryParse(namedVersionId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Named version identifier must be an integer, but was '{namedVersionId}'.", ct);
        }

        FindingRequest? body;

        try
        {
            body = await JsonSerializer.DeserializeAsync<FindingRequest>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (body is null || string.IsNullOrWhiteSpace(body.Message))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "message is required.", ct);
        }

        var severity = string.IsNullOrWhiteSpace(body.Severity) ? "Error" : body.Severity;

        if (severity is not ("Error" or "Warning"))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"severity must be Error or Warning, but was '{severity}'.", ct);
        }

        var created = await store.RaiseFindingAsync(
            new FindingDraft(id, body.CodeValue, severity, body.Message), ct);

        return await WriteAsync(req, HttpStatusCode.Created, created, ct);
    }

    [Function("ResolveFinding")]
    public async Task<HttpResponseData> ResolveFinding(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "findings/{findingId}/resolve")] HttpRequestData req,
        string findingId,
        CancellationToken ct)
    {
        if (!long.TryParse(findingId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Finding identifier must be an integer, but was '{findingId}'.", ct);
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var resolved = await store.ResolveFindingAsync(id, query["resolvedBy"], ct);

        return resolved
            ? await OkAsync(req, new { findingId = id, state = "Resolved" }, ct)
            : await ProblemAsync(req, HttpStatusCode.NotFound,
                $"No open finding '{id}'.", ct);
    }

    // ---- Health ----------------------------------------------------------

    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var twins = await store.GetITwinsAsync(ct);
            return await OkAsync(req, new { status = "healthy", iTwins = twins.Count }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Health check failed.");
            return await WriteAsync(req, HttpStatusCode.ServiceUnavailable,
                new { status = "unhealthy", detail = ex.Message }, ct);
        }
    }

    // ---- Helpers ---------------------------------------------------------

    /// <summary>
    /// Body of a raise-finding request. The version comes from the route, so it
    /// is absent here: two sources for one value could disagree.
    /// </summary>
    private sealed record FindingRequest(string? CodeValue, string? Severity, string Message);

    private static bool TryParseOptionalGuid(string? raw, out Guid? value, out string? error)
    {
        value = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!Guid.TryParse(raw, out var parsed))
        {
            error = $"Identifier must be a UUID, but was '{raw}'.";
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Status semantics are shared across every write path, so a caller does not
    /// have to learn a different convention per resource: a batch in which nothing
    /// persisted and every rejection was transient is a 503 worth retrying,
    /// anything else is a 200 the caller must read.
    /// </summary>
    private static Task<HttpResponseData> UpsertResponseAsync<T>(
        HttpRequestData req, T result, int persistedCount,
        IReadOnlyList<UpsertRejection> rejections, CancellationToken ct)
    {
        var allTransient = persistedCount == 0
            && rejections.Count > 0
            && rejections.All(r => r.Transient);

        return allTransient
            ? WriteAsync(req, HttpStatusCode.ServiceUnavailable, result, ct)
            : WriteAsync(req, HttpStatusCode.OK, result, ct);
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
