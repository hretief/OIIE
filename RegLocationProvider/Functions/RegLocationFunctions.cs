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

    /// <summary>
    /// Day zero: empties the registry, keeping only the reference data it
    /// cannot operate without.
    ///
    /// Routed under "reglocation/" rather than "admin/" because the Functions
    /// host reserves the admin prefix for its own endpoints. A function
    /// declared there fails indexing and is silently disabled -- it does not
    /// appear in the started route list and calling it returns 404, which looks
    /// like a deployment problem rather than a naming one.
    /// </summary>
    [Function("ResetRegLocationData")]
    public async Task<HttpResponseData> ResetRegLocationData(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reglocation/reset")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            await store.ResetAsync(ct);
            logger.LogWarning(
                "REG-LOCATION data was reset; every object was dropped, then schema and bootstrap re-applied.");
            return await OkAsync(req, new { reset = true }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "REG-LOCATION reset failed.");
            return await ProblemAsync(req, HttpStatusCode.InternalServerError,
                $"Reset failed: {ex.Message}", ct);
        }
    }

    /// <summary>
    /// Lists scopes, or finds the one carrying a federation GUID.
    ///
    /// Single-valued by GUID, unlike the tag equivalent: a scope has no
    /// revisions, so one GUID names at most one scope. This is what makes
    /// SyncSites idempotent -- a redelivered site finds the scope the first
    /// delivery created instead of making a second one.
    /// </summary>
    [Function("GetScopes")]
    public async Task<HttpResponseData> GetScopes(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "scopes")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var rawGuid = query["guid"];

        if (!string.IsNullOrWhiteSpace(rawGuid))
        {
            if (!Guid.TryParse(rawGuid, out var guid))
            {
                return await ProblemAsync(req, HttpStatusCode.BadRequest,
                    $"guid must be a UUID, but was '{rawGuid}'.", ct);
            }

            var found = await store.FindScopeByGuidAsync(guid, ct);

            // A list either way, so a caller polling for a scope that does not
            // exist yet reads an empty array rather than handling a 404.
            return await OkAsync(req, found is null ? Array.Empty<RegScope>() : [found], ct);
        }

        return await OkAsync(req, await store.GetScopesAsync(ct), ct);
    }

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

    /// <summary>
    /// Points a scope at the business object whose context it represents.
    ///
    /// Its own route rather than a field on scope creation, because the object
    /// generally does not exist when the scope does. SyncSites creates the Scope
    /// first so the Serial has somewhere to live, then comes back here to link
    /// the two once the Serial has an id.
    /// </summary>
    [Function("SetScopeContext")]
    public async Task<HttpResponseData> SetScopeContext(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "scopes/{scopeId:int}/context")] HttpRequestData req,
        int scopeId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<SetScopeContextRequest>(req, ct);

            if (body is null)
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request body is required.", ct);

            var updated = await store.SetScopeContextAsync(scopeId, body, ct);

            return updated is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No scope '{scopeId}'.", ct)
                : await OkAsync(req, updated, ct);
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

    /// <summary>
    /// Deletes a scope and everything inside it.
    ///
    /// A separate route from <see cref="DeleteScope"/> rather than a query flag,
    /// so that destroying a site's contents cannot happen by accidentally
    /// omitting a parameter. The plain delete stays the safe default and still
    /// refuses while the scope holds anything.
    /// </summary>
    [Function("DeleteScopeCascade")]
    public async Task<HttpResponseData> DeleteScopeCascade(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "scopes/{scopeId:int}/cascade")] HttpRequestData req,
        int scopeId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var result = await store.DeleteScopeCascadeAsync(scopeId, ct);

            return result is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No scope '{scopeId}'.", ct)
                : await OkAsync(req, result, ct);
        }, ct);

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

    [Function("GetClass")]
    public async Task<HttpResponseData> GetClass(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "classes/{classId:int}")] HttpRequestData req,
        int classId,
        CancellationToken ct)
    {
        var found = await store.FindClassAsync(classId, ct);

        return found is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No class '{classId}'.", ct)
            : await OkAsync(req, found, ct);
    }

    /// <summary>
    /// Adds a class to the registry's vocabulary.
    ///
    /// The class id is in the body rather than minted by the registry, because a
    /// class id has to match the one the participant's own system uses; an id
    /// this registry chose would name a class nothing else recognises. That is
    /// also why this is a POST to the collection with an id inside rather than a
    /// PUT to classes/{id}: creating a class is not idempotent, and a repeat is
    /// reported as a conflict rather than quietly overwriting a vocabulary entry
    /// other rows are already classified against.
    /// </summary>
    [Function("CreateClass")]
    public async Task<HttpResponseData> CreateClass(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "classes")] HttpRequestData req,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<CreateClassRequest>(req, ct);

            if (body is null)
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request body is required.", ct);

            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A class requires both a code and a name.", ct);

            var created = await store.CreateClassAsync(body, ct);
            return await WriteAsync(req, HttpStatusCode.Created, created, ct);
        }, ct);

    /// <summary>
    /// Edits a class's code, name, description or parent.
    ///
    /// Group and namespace are not editable: moving a class between them is not
    /// a correction to that class, it is a different class.
    /// </summary>
    [Function("UpdateClass")]
    public async Task<HttpResponseData> UpdateClass(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "classes/{classId:int}")] HttpRequestData req,
        int classId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<UpdateClassRequest>(req, ct);

            if (body is null)
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request body is required.", ct);

            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A class requires both a code and a name.", ct);

            var updated = await store.UpdateClassAsync(classId, body, ct);

            return updated is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No class '{classId}'.", ct)
                : await OkAsync(req, updated, ct);
        }, ct);

    [Function("GetUnits")]
    public async Task<HttpResponseData> GetUnits(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "units")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetUnitsAsync(ct), ct);

    // ---- Items ------------------------------------------------------------

    /// <summary>
    /// Lists items, or finds the one carrying a federation GUID.
    ///
    /// The GUID lookup is what lets SyncSites reuse a site type: the second
    /// Highway project finds the item the first registered instead of creating
    /// a second 'Highway' nothing can tell apart from it.
    /// </summary>
    [Function("GetItems")]
    public async Task<HttpResponseData> GetItems(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "items")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var rawGuid = query["guid"];

        if (!string.IsNullOrWhiteSpace(rawGuid))
        {
            if (!Guid.TryParse(rawGuid, out var guid))
            {
                return await ProblemAsync(req, HttpStatusCode.BadRequest,
                    $"guid must be a UUID, but was '{rawGuid}'.", ct);
            }

            var found = await store.FindItemByGuidAsync(guid, ct);
            return await OkAsync(req, found is null ? Array.Empty<RegItem>() : [found], ct);
        }

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

    /// <summary>
    /// Updates an item's descriptive columns.
    ///
    /// Only code, description and item_type move. Namespace, unit and scope are
    /// what the item IS rather than what it is called, and other rows already
    /// depend on them.
    /// </summary>
    [Function("UpdateItem")]
    public async Task<HttpResponseData> UpdateItem(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "items/{itemId:int}")] HttpRequestData req,
        int itemId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<CreateItemRequest>(req, ct);

            if (body is null)
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request body is required.", ct);

            var updated = await store.UpdateItemAsync(itemId, body, ct);

            return updated is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No item '{itemId}'.", ct)
                : await OkAsync(req, updated, ct);
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

    // ---- Serials ----------------------------------------------------------

    /// <summary>
    /// Lists serials, optionally narrowed by item, or finds the one carrying a
    /// federation GUID.
    ///
    /// Single-valued by GUID: a serial is one instance and does not carry the
    /// revisions that make the tag lookup list-valued.
    /// </summary>
    [Function("GetSerials")]
    public async Task<HttpResponseData> GetSerials(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "serials")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var rawGuid = query["guid"];

        if (!string.IsNullOrWhiteSpace(rawGuid))
        {
            if (!Guid.TryParse(rawGuid, out var guid))
            {
                return await ProblemAsync(req, HttpStatusCode.BadRequest,
                    $"guid must be a UUID, but was '{rawGuid}'.", ct);
            }

            var found = await store.FindSerialByGuidAsync(guid, ct);
            return await OkAsync(req, found is null ? Array.Empty<RegSerialDetail>() : [found], ct);
        }

        if (!TryParseOptionalInt(query["itemId"], "itemId", out var itemId, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetSerialsAsync(itemId, ct), ct);
    }

    [Function("GetSerial")]
    public async Task<HttpResponseData> GetSerial(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "serials/{serialId:int}")] HttpRequestData req,
        int serialId,
        CancellationToken ct)
    {
        var serial = await store.FindSerialAsync(serialId, ct);

        return serial is null
            ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No serial '{serialId}'.", ct)
            : await OkAsync(req, serial, ct);
    }

    [Function("CreateSerial")]
    public async Task<HttpResponseData> CreateSerial(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "serials")] HttpRequestData req,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<CreateSerialRequest>(req, ct);

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A serial requires a name.", ct);

            var created = await store.CreateSerialAsync(body, ct);
            return await WriteAsync(req, HttpStatusCode.Created, created, ct);
        }, ct);

    [Function("UpdateSerial")]
    public async Task<HttpResponseData> UpdateSerial(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "serials/{serialId:int}")] HttpRequestData req,
        int serialId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<UpdateSerialRequest>(req, ct);

            if (body is null || string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A serial requires a name.", ct);

            var updated = await store.UpdateSerialAsync(serialId, body, ct);

            return updated is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No serial '{serialId}'.", ct)
                : await OkAsync(req, updated, ct);
        }, ct);

    [Function("DeleteSerial")]
    public async Task<HttpResponseData> DeleteSerial(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "serials/{serialId:int}")] HttpRequestData req,
        int serialId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
            await store.DeleteSerialAsync(serialId, ct)
                ? req.CreateResponse(HttpStatusCode.NoContent)
                : await ProblemAsync(req, HttpStatusCode.NotFound, $"No serial '{serialId}'.", ct), ct);

    // ---- Tags -------------------------------------------------------------

    /// <summary>
    /// Lists tags, optionally narrowed by item or scope.
    ///
    /// Also serves lookup by code and by federation GUID, through the tagCode and
    /// guid query parameters. Both are list-valued: a code is unique only per
    /// item and revision, and a GUID deliberately survives revision, so neither
    /// identifies exactly one row.
    ///
    /// The code filter is named tagCode rather than code because the Functions
    /// host reserves 'code' for the API key. A caller authenticating that way was
    /// silently read as asking for tags whose code is the key, and got an empty
    /// list back instead of the registry.
    /// </summary>
    [Function("GetTags")]
    public async Task<HttpResponseData> GetTags(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tags")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var code = query["tagCode"];
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
