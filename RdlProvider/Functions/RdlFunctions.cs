using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using RdlProvider.Application;

namespace RdlProvider.Functions;

/// <summary>
/// The RDL HTTP surface.
///
/// Routes are named in the library's own terms -- namespaces, groups, classes
/// -- because that is what this system holds. There is no /segments route and
/// no BOD anywhere in this project: a reference data library has never heard of
/// either.
///
/// Nor is there a route for answering a channel request. A participant asking
/// for the library over ISBM reaches RdlEngine, which reads through this API.
/// The library itself does not know it is being integrated with.
/// </summary>
public sealed class RdlFunctions(
    IRdlStore store,
    ILogger<RdlFunctions> logger)
{
    // Enums are written as names, not ordinals. A name survives a schema
    // reorder; an ordinal does not, and a caller reading it cannot notice.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // ---- Catalogue --------------------------------------------------------

    [Function("GetRdlNamespaces")]
    public async Task<HttpResponseData> GetNamespaces(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "namespaces")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetNamespacesAsync(ct), ct);

    [Function("GetRdlClassGroups")]
    public async Task<HttpResponseData> GetClassGroups(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "classgroups")] HttpRequestData req,
        CancellationToken ct)
        => await OkAsync(req, await store.GetClassGroupsAsync(ct), ct);

    /// <summary>
    /// The library, optionally narrowed by namespace or resolved by code.
    ///
    /// The code filter is what RdlEngine uses to answer a single-class request,
    /// and what a curator uses to check whether a code is taken. It is spelled
    /// classCode rather than code because the Functions host reserves code as a
    /// query parameter for its own function key -- a collision that costs an
    /// afternoon to diagnose, since the host strips it before the function
    /// ever sees it.
    /// </summary>
    [Function("GetRdlClasses")]
    public async Task<HttpResponseData> GetClasses(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "classes")] HttpRequestData req,
        CancellationToken ct)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);

        var classCode = query["classCode"];
        if (!string.IsNullOrWhiteSpace(classCode))
        {
            var match = await store.FindClassByCodeAsync(classCode, ct);

            // A code filter that matches nothing returns an empty list rather
            // than 404: the caller asked the collection a question, and "no
            // classes carry that code" is a valid answer to it.
            return await OkAsync(
                req, match is null ? Array.Empty<RdlClass>() : [match], ct);
        }

        if (!TryParseOptionalInt(query["namespaceId"], "namespaceId", out var ns, out var error))
            return await ProblemAsync(req, HttpStatusCode.BadRequest, error!, ct);

        return await OkAsync(req, await store.GetClassesAsync(ns, ct), ct);
    }

    [Function("GetRdlClass")]
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
    /// Adds a class to the library.
    ///
    /// A POST to the collection with the id inside, rather than a PUT to
    /// classes/{id}: creating a class is not idempotent, and a repeat is
    /// reported as a conflict rather than quietly overwriting an entry other
    /// participants are already classified against.
    /// </summary>
    [Function("CreateRdlClass")]
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
            logger.LogInformation("RDL class '{Code}' ({ClassId}) added to the library.", created.Code, created.ClassId);
            return await WriteAsync(req, HttpStatusCode.Created, created, ct);
        }, ct);

    /// <summary>
    /// Edits a class's group, name, description or parent.
    ///
    /// The code is not editable, and namespace is not either. Both are part of
    /// what identifies the class to every participant that has resolved
    /// against it; changing one in place would rewrite the meaning of data
    /// already published rather than correct it.
    /// </summary>
    [Function("UpdateRdlClass")]
    public async Task<HttpResponseData> UpdateClass(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "classes/{classId:int}")] HttpRequestData req,
        int classId,
        CancellationToken ct)
        => await WriteGuardedAsync(req, async () =>
        {
            var body = await ReadBodyAsync<UpdateClassRequest>(req, ct);

            if (body is null)
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A request body is required.", ct);

            if (string.IsNullOrWhiteSpace(body.Name))
                return await ProblemAsync(req, HttpStatusCode.BadRequest, "A class requires a name.", ct);

            var updated = await store.UpdateClassAsync(classId, body, ct);

            return updated is null
                ? await ProblemAsync(req, HttpStatusCode.NotFound, $"No class '{classId}'.", ct)
                : await OkAsync(req, updated, ct);
        }, ct);

    // ---- Health -----------------------------------------------------------

    /// <summary>
    /// Reports whether the library is readable and how much of it there is.
    ///
    /// The class count is the useful signal: an empty library is readable and
    /// therefore "up", but it is also the state in which every consumer will
    /// fail closed, so it must not be reported as healthy.
    ///
    /// An empty library here usually means RegLocationProvider has not yet
    /// applied schema.sql and bootstrap.sql to the shared EIS database. This
    /// app does not create or seed those tables, so it cannot fix that itself
    /// and reports the condition rather than papering over it.
    /// </summary>
    [Function("GetRdlHealth")]
    public async Task<HttpResponseData> GetHealth(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "health")] HttpRequestData req,
        CancellationToken ct)
    {
        try
        {
            var classes = await store.GetClassesAsync(null, ct);

            return await WriteAsync(req,
                classes.Count > 0 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable,
                new
                {
                    status = classes.Count > 0 ? "healthy" : "degraded",
                    classes = classes.Count,
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
    /// Runs a write and turns a library rejection into a 409.
    ///
    /// A constraint refusing a duplicate code is the library working, not the
    /// server failing, and answering 500 would invite a retry that cannot ever
    /// succeed.
    /// </summary>
    private async Task<HttpResponseData> WriteGuardedAsync(
        HttpRequestData req, Func<Task<HttpResponseData>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (RdlConflictException ex)
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
