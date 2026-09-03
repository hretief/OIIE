using System.Globalization;
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
public sealed class EngFunctions(
    IEngDesignStore store,
    INamedVersionNotifier notifier,
    ILogger<EngFunctions> logger)
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

    /// <summary>
    /// Registers an iTwin the sandbox has been told about, or refreshes one it
    /// already holds.
    ///
    /// This is how a twin that exists on the platform enters the sandbox. It
    /// stands in for the iTwinCreated webhook during development, and it is what
    /// the UI's "add an existing iTwin" action calls.
    ///
    /// Deliberately not a publish. Recording the twin and announcing it are
    /// separate acts, and the engine owns the second one.
    /// </summary>
    [Function("UpsertITwin")]
    public async Task<HttpResponseData> UpsertITwin(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "itwins")] HttpRequestData req,
        CancellationToken ct)
    {
        UpsertITwinRequest? body;

        try
        {
            body = await JsonSerializer.DeserializeAsync<UpsertITwinRequest>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (body is null || body.ITwinId == Guid.Empty)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "An iTwin requires an iTwinId.", ct);
        }

        var existing = await store.FindITwinAsync(body.ITwinId, ct);
        var saved = await store.UpsertITwinAsync(body, ct);

        return await WriteAsync(req,
            existing is null ? HttpStatusCode.Created : HttpStatusCode.OK, saved, ct);
    }

    /// <summary>
    /// Removes an iTwin the sandbox holds.
    ///
    /// Stands in for the iTwinDeleted webhook the same way <see cref="UpsertITwin"/>
    /// stands in for iTwinCreated, and is the system of record for the deletion:
    /// the sandbox admin endpoint calls this, then asks the engine to announce it.
    ///
    /// Deliberately not a publish, for the same reason the upsert is not. Removing
    /// the twin and telling the ecosystem are separate acts, and the engine owns
    /// the second one.
    /// </summary>
    [Function("DeleteITwin")]
    public async Task<HttpResponseData> DeleteITwin(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "itwins/{iTwinId:guid}")] HttpRequestData req,
        Guid iTwinId,
        CancellationToken ct)
    {
        try
        {
            // 404 rather than 204 when there was nothing to delete. The caller is
            // the sandbox, which needs to distinguish 'removed it' from 'never had
            // it' -- publishing a delete for a twin ENG never held would announce
            // the removal of something no subscriber ever heard about.
            return await store.DeleteITwinAsync(iTwinId, ct)
                ? req.CreateResponse(HttpStatusCode.NoContent)
                : await ProblemAsync(req, HttpStatusCode.NotFound,
                    $"No iTwin '{iTwinId:D}'.", ct);
        }
        catch (InvalidOperationException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.Conflict, ex.Message, ct);
        }
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

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

    /// <summary>
    /// Records an iModel the caller has read from the platform.
    ///
    /// ENG does not create iModels -- the platform does -- so this endpoint only
    /// mirrors one into the sandbox so that elements can be scoped to it. It is
    /// idempotent for the same reason UpsertITwin is: the UI re-lists a twin's
    /// iModels on every selection, and doing so must not accumulate rows.
    /// </summary>
    [Function("UpsertIModel")]
    public async Task<HttpResponseData> UpsertIModel(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "imodels")] HttpRequestData req,
        CancellationToken ct)
    {
        UpsertIModelRequest? body;

        try
        {
            body = await JsonSerializer.DeserializeAsync<UpsertIModelRequest>(req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Malformed request body: {ex.Message}", ct);
        }

        if (body is null || body.IModelId == Guid.Empty)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                "An iModel requires an iModelId.", ct);
        }

        // The foreign key would refuse this anyway, but saying so here names the
        // missing twin rather than surfacing a constraint violation.
        if (body.ITwinId == Guid.Empty || await store.FindITwinAsync(body.ITwinId, ct) is null)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"An iModel must belong to a known iTwin, but '{body.ITwinId}' is not registered.", ct);
        }

        // Code is NOT NULL; the platform calls the field displayName, so fall back
        // through it and finally to the id rather than rejecting a real payload.
        var code = FirstNonBlank(body.Code) ?? body.IModelId.ToString();

        var existing = await store.FindIModelAsync(body.IModelId, ct);
        var saved = await store.UpsertIModelAsync(body with { Code = code }, ct);

        return await WriteAsync(req,
            existing is null ? HttpStatusCode.Created : HttpStatusCode.OK, saved, ct);
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

        if (!TryParseOptionalUtc(query["modifiedSince"], out var modifiedSince, out var sinceError))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, sinceError!, ct);

        return await OkAsync(req,
            await store.GetElementsAsync(iModelId, namedVersionId, modifiedSince, ct), ct);
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
    /// Creates or updates elements in an iModel.
    ///
    /// The iModel is in the route, not a named version: elements are authored
    /// into the model itself, and a marker is cut afterwards over whatever is
    /// then present. One batch is one changeset, so it lands at one position.
    /// </summary>
    [Function("UpsertElements")]
    public async Task<HttpResponseData> UpsertElements(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "imodels/{iModelId}/elements")] HttpRequestData req,
        string iModelId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(iModelId, out var modelId))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"iModel identifier must be a UUID, but was '{iModelId}'.", ct);
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

        var result = await store.UpsertElementsAsync(modelId, requested, ct);

        logger.LogInformation(
            "ENG created {Created}, updated {Updated}, rejected {Rejected} of {Total} element(s) in iModel {IModel}.",
            result.Created, result.Updated, result.Rejections.Count, requested.Length, modelId);

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

        if (!TryParseOptionalUtc(query["modifiedSince"], out var modifiedSince, out var sinceError))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, sinceError!, ct);

        return await OkAsync(req, await store.GetNamedVersionsAsync(iModelId, modifiedSince, ct), ct);
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

        // Announced only after the store has committed, and never allowed to
        // fail the response: the marker is already recorded, and an author
        // should not see an error because a listener was down. A missed
        // notification is recovered by EngEngine's poll, which remains the
        // backstop -- this only shortens the wait.
        await notifier.NotifyNamedVersionCreatedAsync(
            new NamedVersionCreatedNotification(
                created.NamedVersionId,
                created.VersionGuid,
                created.IModelId,
                created.Name,
                created.ChangesetIndex,
                created.CreatedUtc),
            ct);

        return await WriteAsync(req, HttpStatusCode.Created, created, ct);
    }

    /// <summary>
    /// The elements a marker contains.
    ///
    /// Derived on read from changeset position rather than stored, so the answer
    /// is the same however long after the fact it is asked for. This is what a
    /// consumer fetches after being told a named version exists.
    /// </summary>
    [Function("GetNamedVersionElements")]
    public async Task<HttpResponseData> GetNamedVersionElements(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "named-versions/{namedVersionId}/elements")] HttpRequestData req,
        string namedVersionId,
        CancellationToken ct)
    {
        if (!long.TryParse(namedVersionId, out var id))
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest,
                $"Named version identifier must be an integer, but was '{namedVersionId}'.", ct);
        }

        if (await store.FindNamedVersionAsync(id, ct) is null)
            return await ProblemAsync(req, HttpStatusCode.NotFound, $"No named version '{id}'.", ct);

        return await OkAsync(req, await store.GetNamedVersionElementsAsync(id, ct), ct);
    }
    // ---- Lifecycle -------------------------------------------------------

    /// <summary>
    /// Drops ENG's tables and recreates them empty.
    ///
    /// Called by the Sandbox's day zero, which needs ENG cleared along with
    /// everything else: ENG is the authoritative source, so twins left behind
    /// here reappear in the UI after a reset that claimed to remove them.
    ///
    /// Destructive and unguarded by anything but the function key. That is the
    /// same protection every other route here carries, and this host is a
    /// sandbox emulation of a customer system rather than one holding real
    /// engineering data.
    ///
    /// Not routed under admin/: the Functions host reserves that prefix for its
    /// own endpoints, and a function that claims it fails indexing and is
    /// silently disabled rather than rejected at build time.
    /// </summary>
    [Function("ResetEngData")]
    public async Task<HttpResponseData> ResetEngData(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "eng/reset")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            await store.ResetAsync(ct);
            logger.LogWarning("ENG data reset: all tables dropped and recreated.");

            return await OkAsync(req, new { reset = true }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ENG data reset failed.");
            return await ProblemAsync(req, HttpStatusCode.InternalServerError,
                $"Reset failed: {ex.Message}", ct);
        }
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
    /// Parses an optional instant supplied as a query parameter.
    ///
    /// Round-trip format is required rather than accepted-if-parseable. A caller
    /// polling for changes is comparing against timestamps this service stores in
    /// UTC, and a value carrying no offset would be read in the server's local
    /// zone -- silently shifting the window by hours and skipping whatever fell
    /// inside the shift. Demanding an explicit offset makes that impossible to
    /// express by accident.
    /// </summary>
    private static bool TryParseOptionalUtc(string? raw, out DateTime? value, out string? error)
    {
        value = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            error = $"modifiedSince must be an ISO 8601 instant such as "
                  + $"'2026-08-24T19:30:00Z', but was '{raw}'.";
            return false;
        }

        value = parsed.UtcDateTime;
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
