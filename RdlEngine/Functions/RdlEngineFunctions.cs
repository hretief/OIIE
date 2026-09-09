using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RdlEngine.Application;

namespace RdlEngine.Functions;

/// <summary>
/// The RDL engine's triggers.
///
/// There is no HTTP route returning a ShowTaxonomySet directly. The BOD is a
/// channel contract, and an HTTP shortcut would let the payload be exercised
/// while leaving the correlation and transport — the parts most likely to be
/// wrong — untested. The drain route below runs the real ISBM path on demand.
/// </summary>
public sealed class RdlEngineFunctions(
    TaxonomySetRequestListener listener,
    IOptions<RdlIsbmOptions> options,
    ILogger<RdlEngineFunctions> logger)
{
    private readonly RdlIsbmOptions _options = options.Value;

    /// <summary>
    /// Polls the request channel.
    ///
    /// A flat setting name for the schedule: the WebJobs %binding% syntax
    /// resolves against configuration keys, and a nested key would need a
    /// colon that the binding expression does not accept.
    /// </summary>
    [Function("RdlEngineDrain")]
    public async Task RdlEngineDrain(
        [TimerTrigger("%RdlEngineDrainSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var report = await listener.DrainAsync(ct);

        if (report.Errors.Count > 0)
        {
            logger.LogError(
                "RDL drain finished with {Count} error(s) after handling {Handled} request(s).",
                report.Errors.Count, report.RequestsHandled);
        }
    }

    /// <summary>
    /// Drains on demand, so a round trip can be tested without waiting for the
    /// timer. Returns the report so the caller can see what happened rather
    /// than having to read the logs.
    /// </summary>
    [Function("RdlEngineDrainNow")]
    public async Task<IActionResult> RdlEngineDrainNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/drain")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await listener.DrainAsync(ct));
}
