using CirProvider.Application;
using CirProvider.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CirProvider.Functions;

public sealed class CommandFunctions(ICirStore store, ILogger<CommandFunctions> logger)
{
    [Function("Health")]
    public async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req,
        CancellationToken ct)
    {
        bool sql;
        string? error = null;
        try
        {
            sql = await store.PingAsync(ct);
        }
        catch (Exception ex)
        {
            sql = false;
            error = ex.Message;
            logger.LogError(ex, "Health check failed to reach SQL.");
        }

        return new ObjectResult(new
        {
            status = sql ? "healthy" : "degraded",
            spec = "ws-CIR 1.0",
            binding = "REST",
            sql,
            error,
            utc = DateTimeOffset.UtcNow
        })
        { StatusCode = sql ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable };
    }

    /// <summary>ws-CIR §3.1.1 CreateRegistry.</summary>
    [Function("CreateRegistry")]
    public async Task<IActionResult> CreateRegistry(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "registries")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<CreateRegistryRequest>(ct);
        await store.CreateRegistryAsync(body, ct);
        return new StatusCodeResult(StatusCodes.Status201Created);
    }

    /// <summary>ws-CIR §3.1.5 DeleteRegistry. Cascades to Categories, Entries and Properties.</summary>
    [Function("DeleteRegistry")]
    public async Task<IActionResult> DeleteRegistry(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "registries/{registryId}")] HttpRequest req,
        string registryId,
        CancellationToken ct)
    {
        await store.DeleteRegistryAsync(registryId, ct);
        return new NoContentResult();
    }

    /// <summary>ws-CIR §3.1.2 CreateEquivalentEntries.</summary>
    [Function("CreateEquivalentEntries")]
    public async Task<IActionResult> CreateEquivalentEntries(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "equivalent-entries")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<List<EquivalentEntryRequest>>(ct);
        await store.CreateEquivalentEntriesAsync(body, ct);
        return new StatusCodeResult(StatusCodes.Status201Created);
    }

    /// <summary>
    /// ws-CIR §3.1.3 UpdateRegistry. PUT, not PATCH: §3.1.3 replaces every
    /// non-primary-key attribute from the supplied data, and Annex A states the
    /// Change verb SHOULD use a snapshot approach.
    /// </summary>
    [Function("UpdateRegistry")]
    public async Task<IActionResult> UpdateRegistry(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "registries")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<UpdateRegistryRequest>(ct);
        await store.UpdateRegistryAsync(body.Registry, ct);
        return new NoContentResult();
    }

    /// <summary>ws-CIR §3.1.6 DeleteCategory. Cascades to Entries and Properties.</summary>
    [Function("DeleteCategory")]
    public async Task<IActionResult> DeleteCategory(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "categories")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<CategoryIdentifier>(ct);
        await store.DeleteCategoryAsync(body, ct);
        return new NoContentResult();
    }

    /// <summary>
    /// ws-CIR §3.1.7 DeleteEntries. POST rather than DELETE: the input is
    /// 1..* five-part composite keys, which will not survive a path or query string.
    /// </summary>
    [Function("DeleteEntries")]
    public async Task<IActionResult> DeleteEntries(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "entries/batch-delete")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<List<EntryIdentifier>>(ct);
        await store.DeleteEntriesAsync(body, ct);
        return new NoContentResult();
    }

    /// <summary>ws-CIR §3.1.8 DeleteProperties.</summary>
    [Function("DeleteProperties")]
    public async Task<IActionResult> DeleteProperties(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "properties/batch-delete")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<List<PropertyIdentifier>>(ct);
        await store.DeletePropertiesAsync(body, ct);
        return new NoContentResult();
    }

    /// <summary>ws-CIR §3.1.4 UpdateEntryCIRID.</summary>
    [Function("UpdateEntryCirid")]
    public async Task<IActionResult> UpdateEntryCirid(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "cirids/replace")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<UpdateEntryCiridRequest>(ct);
        await store.UpdateEntryCiridAsync(body, ct);
        return new NoContentResult();
    }
}
