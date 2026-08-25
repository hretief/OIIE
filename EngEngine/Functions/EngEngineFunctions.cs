using EngEngine.Application;
using EngEngine.Infrastructure.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EngEngine.Functions;

public sealed class EngEngineFunctions(
    EngPublicationService publisher,
    IEngEngineStateStore stateStore,
    IOptions<EngEngineOptions> options,
    ILogger<EngEngineFunctions> logger)
{
    private readonly EngEngineOptions _options = options.Value;

    /// <summary>
    /// Polls ENG for markers to publish. A timer rather than a long-running
    /// background service because the Consumption host recycles freely and would
    /// kill one.
    ///
    /// RunOnStartup is deliberately off: it would fire on every scale-out
    /// instance and every cold start, multiplying polls against ENG.
    ///
    /// The setting name is flat rather than EngEngine__PollSchedule. A %...%
    /// binding expression is resolved by WebJobs as a literal setting name,
    /// before the double underscore is folded into a configuration section -- so
    /// the sectioned form never resolves, and the function fails indexing and is
    /// silently disabled at startup rather than failing loudly at run time.
    /// </summary>
    [Function("EngEnginePoll")]
    public async Task EngEnginePoll(
        [TimerTrigger("%EngEnginePollSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var report = await publisher.DrainAsync(ct);

        if (report.Errors.Count > 0)
        {
            logger.LogError(
                "ENG poll completed with errors: {Errors}", string.Join("; ", report.Errors));
            return;
        }

        if (report.Idle) return;

        logger.LogInformation(
            "ENG poll published {Markers} marker(s) carrying {Segments} segment(s); " +
            "{Already} already published, {Skipped} skipped.",
            report.MarkersPublished, report.SegmentsPublished,
            report.AlreadyPublished, report.Skipped.Count);
    }

    /// <summary>Drains on demand, so a round trip can be tested without waiting for the timer.</summary>
    [Function("EngEngineDrain")]
    public async Task<IActionResult> EngEngineDrain(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/drain")] HttpRequest req,
        CancellationToken ct)
    {
        var report = await publisher.DrainAsync(ct);
        return new OkObjectResult(report);
    }

    /// <summary>
    /// Configuration and watermark, without exposing the ENG key.
    ///
    /// The published-marker set is reported as a count rather than in full: it
    /// grows without bound, and the question a status endpoint answers is "is the
    /// engine where I expect it to be", not "list everything it has ever sent".
    /// </summary>
    [Function("EngEngineStatus")]
    public async Task<IActionResult> EngEngineStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "engine/status")] HttpRequest req,
        CancellationToken ct)
    {
        var (state, _) = await stateStore.ReadAsync(ct);

        return new OkObjectResult(new
        {
            enabled = _options.Enabled,
            engBaseUrl = _options.EngBaseUrl,
            hasEngApiKey = !string.IsNullOrWhiteSpace(_options.EngApiKey),
            iModelId = _options.IModelId,

            // The parts, not the URI. The channel depends on which iTwin owns the
            // model, which only ENG can answer, and status is meant to be readable
            // when ENG is unreachable -- reporting the ingredients cannot fail.
            enterprise = _options.Enterprise,
            domain = _options.Domain,
            channelUriOverride = _options.ChannelUriOverride,

            topics = _options.Topics,
            pollOverlap = _options.PollOverlap,
            maxMarkersPerPoll = _options.MaxMarkersPerPoll,
            watermark = state.Watermark,
            publishedMarkers = state.PublishedVersions.Count
        });
    }
}
