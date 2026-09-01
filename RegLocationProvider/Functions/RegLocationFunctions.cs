using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RegLocationProvider.Application;

namespace RegLocationProvider.Functions;

/// <summary>
/// The REG-LOCATION HTTP surface.
///
/// Routes are named in the registry's own terms -- scopes, namespaces, classes,
/// items, tags -- because that is what this system holds. There is no /segments
/// route and no BOD anywhere in this project: a functional location registry has
/// never heard of either.
///
/// Nor is there a /publish route. A tag created or retired stops at the
/// database; carrying that change onto a channel is the integrator's work.
/// </summary>
public sealed class RegLocationFunctions(
    IRegLocationStore store,
    IApprovalNotifier notifier,
    ILogger<RegLocationFunctions> logger)
{
    // Enums are written as names, not ordinals. A name survives a schema
    // reorder; an ordinal does not, and a caller reading it cannot notice.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ---- Scopes -----------------------------------------------------------

    [Function("GetScopes")]
    public async Task<HttpResponseData> GetScopes(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "scopes")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetScopesAsync(ct), ct);

    [Function("GetScope")]
    public async Task<HttpResponseData> GetScope(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "scopes/{scopeId:int}")] HttpRequestData req,
        int scopeId,
        CancellationToken ct)
    {
        var scope = await store.FindScopeAsync(scopeId, ct);

        return scope is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No scope '{scopeId}'.", ct)
            : await OkAsync(req, scope, ct);
    }

    [Function("CreateScope")]
    public async Task<HttpResponseData> CreateScope(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "scopes")] HttpRequestData req,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<CreateScopeRequest>(req, ct);

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A scope requires a name.", ct);

            var created = await store.CreateScopeAsync(body, ct);
            return await WriteAsync(req, HttpStatusCode.Created, created, ct);
        }, ct);

    [Function("DeleteScope")]
    public async Task<HttpResponseData> DeleteScope(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "scopes/{scopeId:int}")] HttpRequestData req,
        int scopeId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
            await store.DeleteScopeAsync(scopeId, ct)
                ? req.CreateResponse(HttpStatusCode.NoContent)
                : await ProblemAsync(req, HttpStatusCode.NotFound, $"No scope '{scopeId}'.", ct), ct);

    // ---- Catalogue --------------------------------------------------------

    /// <summary>
    /// The reference data a caller needs before it can post an item or a tag,
    /// since namespace, class and unit are all required and all must already
    /// exist.
    /// </summary>
    [Function("GetNamespaces")]
    public async Task<HttpResponseData> GetNamespaces(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "namespaces")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetNamespacesAsync(ct), ct);

    [Function("GetClasses")]
    public async Task<HttpResponseData> GetClasses(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "classes")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        if (!TryParseOptionalInt(query["namespaceId"], "namespaceId", out var ns, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetClassesAsync(ns, ct), ct);
    }

    [Function("GetUnits")]
    public async Task<HttpResponseData> GetUnits(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "units")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetUnitsAsync(ct), ct);

    // ---- Items ------------------------------------------------------------

    [Function("GetItems")]
    public async Task<HttpResponseData> GetItems(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "items")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        if (!TryParseOptionalInt(query["namespaceId"], "namespaceId", out var ns, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetItemsAsync(ns, ct), ct);
    }

    [Function("GetItem")]
    public async Task<HttpResponseData> GetItem(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "items/{itemId:int}")] HttpRequestData req,
        int itemId,
        CancellationToken ct)
    {
        var item = await store.FindItemAsync(itemId, ct);

        return item is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No item '{itemId}'.", ct)
            : await OkAsync(req, item, ct);
    }

    [Function("CreateItem")]
    public async Task<HttpResponseData> CreateItem(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "items")] HttpRequestData req,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<CreateItemRequest>(req, ct);

            if (body is null)
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request body is required.", ct);

            var created = await store.CreateItemAsync(body, ct);
            return await WriteAsync(req, HttpStatusCode.Created, created, ct);
        }, ct);

    [Function("DeleteItem")]
    public async Task<HttpResponseData> DeleteItem(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "items/{itemId:int}")] HttpRequestData req,
        int itemId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
            await store.DeleteItemAsync(itemId, ct)
                ? req.CreateResponse(HttpStatusCode.NoContent)
                : await ProblemAsync(req, HttpStatusCode.NotFound, $"No item '{itemId}'.", ct), ct);

    // ---- Tags -------------------------------------------------------------

    /// <summary>
    /// Lists tags, optionally narrowed by item or scope.
    ///
    /// Also serves lookup by code and by federation GUID, through the code and
    /// guid query parameters. Both are list-valued: a code is unique only per
    /// item and revision, and a GUID deliberately survives revision, so neither
    /// identifies exactly one row.
    /// </summary>
    [Function("GetTags")]
    public async Task<HttpResponseData> GetTags(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tags")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var code = query["code"];
        var rawGuid = query["guid"];

        if (!string.IsNullOrWhiteSpace(rawGuid))
        {
            if (!Guid.TryParse(rawGuid, out var guid))
            {
                return await ProblemAsync(req, HttpStatusCode.BadRequest,
                    $"guid must be a UUID, but was '{rawGuid}'.", ct);
            }

            return await OkAsync(req, await store.FindTagsByGuidAsync(guid, ct), ct);
        }

        if (!TryParseOptionalInt(query["revision"], "revision", out var revision, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        if (!string.IsNullOrWhiteSpace(code))
            return await OkAsync(req, await store.FindTagsByCodeAsync(code, revision, ct), ct);

        if (!TryParseOptionalInt(query["itemId"], "itemId", out var itemId, out error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        if (!TryParseOptionalInt(query["scopeId"], "scopeId", out var scopeId, out error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetTagsAsync(itemId, scopeId, ct), ct);
    }

    [Function("GetTag")]
    public async Task<HttpResponseData> GetTag(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tags/{tagId:int}")] HttpRequestData req,
        int tagId,
        CancellationToken ct)
    {
        var tag = await store.FindTagAsync(tagId, ct);

        return tag is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No tag '{tagId}'.", ct)
            : await OkAsync(req, tag, ct);
    }

    [Function("CreateTag")]
    public async Task<HttpResponseData> CreateTag(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tags")] HttpRequestData req,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<CreateTagRequest>(req, ct);

            if (body is null || string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A tag requires a code and a name.", ct);

            var created = await store.CreateTagAsync(body, ct);
            return await WriteAsync(req, HttpStatusCode.Created, created, ct);
        }, ct);

    [Function("UpdateTag")]
    public async Task<HttpResponseData> UpdateTag(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "tags/{tagId:int}")] HttpRequestData req,
        int tagId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<UpdateTagRequest>(req, ct);

            if (body is null || string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A tag requires a code and a name.", ct);

            var updated = await store.UpdateTagAsync(tagId, body, ct);

            return updated is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No tag '{tagId}'.", ct)
                : await OkAsync(req, updated, ct);
        }, ct);

    [Function("DeleteTag")]
    public async Task<HttpResponseData> DeleteTag(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "tags/{tagId:int}")] HttpRequestData req,
        int tagId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
            await store.DeleteTagAsync(tagId, ct)
                ? req.CreateResponse(HttpStatusCode.NoContent)
                : await ProblemAsync(req, HttpStatusCode.NotFound, $"No tag '{tagId}'.", ct), ct);

    // ---- Stewardship ------------------------------------------------------

    /// <summary>
    /// The queue a steward works: tags proposed by someone else and not yet
    /// admitted to the registry.
    /// </summary>
    [Function("GetProposedTags")]
    public async Task<HttpResponseData> GetProposedTags(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tags/proposed")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.FindTagsByStateAsync(RegTagState.Proposed, ct), ct);

    /// <summary>
    /// A steward admits a proposed tag to the registry.
    ///
    /// Its own route rather than a state field on PUT /tags/{id}: releasing a
    /// tag to operations and correcting its spelling are different acts with
    /// different authority behind them, and one route doing both would mean
    /// anything permitted to rename a tag were also permitted to release it.
    ///
    /// This is where SC01's gate actually sits. Nothing here publishes -- the
    /// approval is a fact about this registry, and carrying it onto a channel
    /// belongs to the engine.
    /// </summary>
    [Function("ApproveTag")]
    public async Task<HttpResponseData> ApproveTag(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tags/{tagId:int}/approve")] HttpRequestData req,
        int tagId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<ApproveTagRequest>(req, ct);

            // Who decided is required. An approval nobody is accountable for is
            // not a stewardship decision, and the question is always asked later.
            if (body is null || string.IsNullOrWhiteSpace(body.DecidedBy))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "An approval requires decidedBy.", ct);

            var approved = await store.ApproveTagAsync(tagId, body, ct);

            if (approved is null)
                return await ProblemAsync(req, HttpStatusCode.NotFound, $"No tag '{tagId}'.", ct);

            logger.LogInformation(
                "Tag {TagId} ({Code}) approved by {DecidedBy}.",
                tagId, approved.Tag.Code, body.DecidedBy);

            // Announced only after the store has committed, and never allowed to
            // fail the response: the decision is already recorded, and a steward
            // should not see an error because a listener was down.
            await notifier.NotifyTagApprovedAsync(
                new TagApprovedNotification(
                    approved.Tag.TagId,
                    approved.Tag.Code,
                    approved.Tag.Revision,
                    approved.Object.Guid,
                    approved.Object.ScopeId,
                    body.DecidedBy,
                    DateTimeOffset.UtcNow),
                ct);

            return await OkAsync(req, approved, ct);
        }, ct);

    // ---- Health -----------------------------------------------------------

    /// <summary>
    /// Reports connectivity AND the registry invariant.
    ///
    /// A degraded result is a 503 on purpose: orphaned rows or untrusted
    /// constraints mean the data can no longer be relied on, and a green light
    /// in that state is worse than no health check at all.
    /// </summary>
    [Function("Health")]
    public async Task<HttpResponseData> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var health = await store.GetHealthAsync(ct);

            if (!health.IsHealthy)
            {
                logger.LogError(
                    "Registry invariant violated: {Orphans} orphaned rows, {Untrusted} untrusted constraints.",
                    health.OrphanedRows, health.UntrustedConstraints);
            }

            return await WriteAsync(req,
                health.IsHealthy ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                new
                {
                    status = health.IsHealthy ? "healthy" : "degraded",
                    scopes = health.Scopes,
                    items = health.Items,
                    tags = health.Tags,
                    orphanedRows = health.OrphanedRows,
                    untrustedConstraints = health.UntrustedConstraints,
                }, ct);
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
    /// Runs a write and turns a registry rejection into a 409.
    ///
    /// Every write path shares this so a caller does not have to learn a
    /// different convention per resource. A constraint refusing a duplicate code
    /// or a still-referenced row is the registry working, not the server
    /// failing, and answering 500 would invite a retry that cannot ever succeed.
    /// </summary>
    private async Task<HttpResponseData> WriteGuardedAsync(
        HttpRequestData req, Func<Task<HttpResponseData>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (RegistryConflictException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.Conflict, ex.Message, ct);
        }
        catch (JsonException ex)
        {
            return await ProblemAsync(req, HttpStatusCode.BadRequest, $"Malformed JSON body: {ex.Message}", ct);
        }
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpRequestData req, CancellationToken ct)
        => await JsonSerializer.DeserializeAsync<T>(req.Body, Json, ct);

    private static bool TryParseOptionalInt(string? raw, string name, out int? value, out string? error)
    {
        value = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (!int.TryParse(raw, out var parsed))
        {
            error = $"{name} must be an integer, but was '{raw}'.";
            return false;
        }

        value = parsed;
        return true;
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
