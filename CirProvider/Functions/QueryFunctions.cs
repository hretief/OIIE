using CirProvider.Application;
using CirProvider.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace CirProvider.Functions;

public sealed class QueryFunctions(ICirStore store)
{
    /// <summary>
    /// ws-CIR §3.2.3 GetEntriesByCIRID. The one query that fits cleanly in a GET:
    /// a UUID plus repeatable scalar filters.
    /// </summary>
    [Function("GetEntriesByCirid")]
    public async Task<IActionResult> GetEntriesByCirid(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "entries")] HttpRequest req,
        CancellationToken ct)
    {
        if (!Guid.TryParse(req.Query["cirid"], out var cirid))
            return new BadRequestObjectResult(new { detail = "Query parameter 'cirid' must be a UUID." });

        var targets = req.Query["targetSourceId"].Where(v => v is not null).Select(v => v!).ToList();

        var result = await store.GetEntriesByCiridAsync(cirid, targets, ct);
        return new OkObjectResult(new { registry = result });
    }

    /// <summary>
    /// ws-CIR §3.2.2 GetEquivalentEntries. POST rather than GET: the input is
    /// 1..* five-part composite keys, which will not survive a query string.
    /// </summary>
    [Function("GetEquivalentEntries")]
    public async Task<IActionResult> GetEquivalentEntries(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "queries/equivalent-entries")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<GetEquivalentEntriesRequest>(ct);
        var result = await store.GetEquivalentEntriesAsync(body.EntryIdentifier, body.TargetSourceId, ct);
        return new OkObjectResult(new { registry = result });
    }

    /// <summary>
    /// ws-CIR §3.2.1 GetRegistry. POST because the filter set is a nested AND/OR
    /// structure carrying regex values — '+' in a query string decodes to a space.
    /// </summary>
    [Function("GetRegistry")]
    public async Task<IActionResult> GetRegistry(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "queries/registry")] HttpRequest req,
        CancellationToken ct)
    {
        var body = await req.ReadJsonAsync<GetRegistryRequest>(ct);
        var result = await store.GetRegistryAsync(body.Filter, ct);
        return new OkObjectResult(new { registry = result });
    }
}
