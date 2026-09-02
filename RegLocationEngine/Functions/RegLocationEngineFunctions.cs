using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RegLocationEngine.Application;
using RegLocationEngine.Infrastructure.RegLocation;
using RegLocationEngine.Infrastructure.State;

namespace RegLocationEngine.Functions;

public sealed class RegLocationEngineFunctions(
    RegLocationApprovalService approvals,
    SegmentIngestionService ingestion,
    SiteIngestionService siteIngestion,
    IRegLocationEngineStateStore stateStore,
    IOptions<RegLocationEngineOptions> options,
    ILogger<RegLocationEngineFunctions> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly RegLocationEngineOptions _options = options.Value;

    /// <summary>
    /// Receives an approval from REG-LOCATION.
    ///
    /// Not gated by Enabled. The provider does not retry, so a notification
    /// refused here is a release that silently never happens; refusing it would
    /// convert a configuration mistake into lost work rather than into an error
    /// somebody can see. The service still checks its own configuration, so a
    /// misconfigured engine answers with the reason rather than pretending.
    /// </summary>
    [Function("RegLocationApproved")]
    public async Task<IActionResult> RegLocationApproved(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/approved")] HttpRequest req,
        CancellationToken ct)
    {
        TagApprovedNotification? notification;

        try
        {
            notification = await JsonSerializer.DeserializeAsync<TagApprovedNotification>(
                req.Body, Json, ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Approval notification could not be parsed.");
            return new BadRequestObjectResult(new { error = "Malformed approval notification." });
        }

        if (notification is null || notification.TagId <= 0)
            return new BadRequestObjectResult(new { error = "An approval notification requires a tagId." });

        var report = await approvals.HandleApprovalAsync(notification, ct);

        if (report.Errors.Count > 0)
        {
            logger.LogError(
                "Approval of tag {TagId} completed with errors: {Errors}",
                notification.TagId, string.Join("; ", report.Errors));

            // 500 rather than 200-with-errors, so a listener that does retry can
            // tell this apart from a successful no-op. The sweep is the real
            // safety net either way.
            return new ObjectResult(report) { StatusCode = StatusCodes.Status500InternalServerError };
        }

        return new OkObjectResult(report);
    }

    /// <summary>
    /// Reconciles the registry against what has been published.
    ///
    /// A timer rather than a long-running background service because the
    /// Consumption host recycles freely and would kill one.
    ///
    /// The setting name is flat rather than RegLocationEngine__SweepSchedule. A
    /// %...% binding expression is resolved by WebJobs as a literal setting name,
    /// before the double underscore is folded into a configuration section -- so
    /// the sectioned form never resolves, and the function fails indexing and is
    /// silently disabled at startup rather than failing loudly at run time.
    /// </summary>
    [Function("RegLocationEngineSweep")]
    public async Task RegLocationEngineSweep(
        [TimerTrigger("%RegLocationEngineSweepSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.Enabled) return;

        var report = await approvals.SweepAsync(ct);

        if (report.Errors.Count > 0)
        {
            logger.LogError(
                "REG-LOCATION sweep completed with errors: {Errors}", string.Join("; ", report.Errors));
            return;
        }

        if (report.Idle) return;

        logger.LogInformation(
            "REG-LOCATION sweep published {Published} tag(s), registered {Registered} entry(ies); " +
            "{Already} already published, {Skipped} skipped.",
            report.TagsPublished, report.EntriesRegistered,
            report.AlreadyPublished, report.Skipped.Count);
    }

    /// <summary>Sweeps on demand, so a round trip can be tested without waiting for the timer.</summary>
    [Function("RegLocationEngineSweepNow")]
    public async Task<IActionResult> RegLocationEngineSweepNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/sweep")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await approvals.SweepAsync(ct));

    /// <summary>
    /// Reads proposals published by ENG off the inbound channel.
    ///
    /// Gated by its own IngestEnabled rather than by Enabled, because the two
    /// legs fail independently: receiving proposals is useful long before there
    /// is a CIR to register approvals in, and a broker problem on one channel
    /// should not silence the other.
    ///
    /// Flat setting name for the schedule, for the same WebJobs binding reason as
    /// the sweep above.
    /// </summary>
    [Function("RegLocationEngineIngest")]
    public async Task RegLocationEngineIngest(
        [TimerTrigger("%RegLocationEngineIngestSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.IngestEnabled) return;

        var report = await ingestion.DrainAsync(ct);

        if (report.Failed > 0)
        {
            logger.LogError(
                "Inbound drain left {Failed} message(s) on the channel after {Read} read.",
                report.Failed, report.MessagesRead);
        }
    }

    /// <summary>Drains the inbound channel on demand, without waiting for the timer.</summary>
    [Function("RegLocationEngineIngestNow")]
    public async Task<IActionResult> RegLocationEngineIngestNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/ingest")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await ingestion.DrainAsync(ct));

    /// <summary>
    /// Reads sites published by ENG off the enterprise sites channel.
    ///
    /// Shares a schedule with the segment ingest rather than having its own, but
    /// deliberately runs first within the tick: a segment can only land in a
    /// scope that a site created, so draining the sites channel first lets a
    /// site and the segments that depend on it be handled in the same pass
    /// instead of the segments having to wait for the next one.
    /// </summary>
    [Function("RegLocationEngineIngestSites")]
    public async Task RegLocationEngineIngestSites(
        [TimerTrigger("%RegLocationEngineSitesIngestSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.SitesIngestEnabled) return;

        var report = await siteIngestion.DrainAsync(ct);

        if (report.Failed > 0)
        {
            logger.LogError(
                "Site drain left {Failed} message(s) on the channel after {Read} read.",
                report.Failed, report.MessagesRead);
        }
    }

    /// <summary>Drains the sites channel on demand, without waiting for the timer.</summary>
    [Function("RegLocationEngineIngestSitesNow")]
    public async Task<IActionResult> RegLocationEngineIngestSitesNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/ingest-sites")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await siteIngestion.DrainAsync(ct));

    /// <summary>
    /// Configuration and progress, without exposing any key.
    ///
    /// The published set is reported as a count rather than in full: it grows
    /// without bound, and the question a status endpoint answers is "is the
    /// engine where I expect it to be", not "list everything it has ever sent".
    /// </summary>
    [Function("RegLocationEngineStatus")]
    public async Task<IActionResult> RegLocationEngineStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "engine/status")] HttpRequest req,
        CancellationToken ct)
    {
        var (state, _) = await stateStore.ReadAsync(ct);

        return new OkObjectResult(new
        {
            enabled = _options.Enabled,
            regLocationBaseUrl = _options.RegLocationBaseUrl,
            hasRegLocationApiKey = !string.IsNullOrWhiteSpace(_options.RegLocationApiKey),
            scopeId = _options.ScopeId,

            cirBaseUrl = _options.CirBaseUrl,
            hasCirApiKey = !string.IsNullOrWhiteSpace(_options.CirApiKey),
            registerInCir = _options.RegisterInCir,

            // The parts as well as the URI, so status stays readable and
            // diagnosable when the derived value is not what was expected.
            enterprise = _options.Enterprise,
            domain = _options.Domain,
            channelUriOverride = _options.ChannelUriOverride,
            channelUri = _options.ChannelUriFor(_options.ITwinFederationId),

            topics = _options.Topics,
            iTwinFederationId = _options.ITwinFederationId,
            maxTagsPerSweep = _options.MaxTagsPerSweep,
            publishedTags = state.PublishedTags.Count,

            // The inbound leg reported beside the outbound one, and the channel
            // spelled out: the commonest misconfiguration is an engine
            // subscribed to its own publication channel, which produces silence
            // rather than an error and is invisible unless the two URIs can be
            // read side by side.
            ingestEnabled = _options.IngestEnabled,
            inboundDomain = _options.InboundDomain,
            inboundChannelUri = _options.InboundChannelUriFor(_options.ITwinFederationId),
            inboundTopics = _options.InboundTopics,
            inboundItemId = _options.InboundItemId,
            inboundScopeId = _options.InboundScopeId,
            inboundRevision = _options.InboundRevision,
            inboundClassMap = _options.InboundClassMap,
            inboundFallbackClassId = _options.InboundFallbackClassId,
            maxMessagesPerPoll = _options.MaxMessagesPerPoll
        });
    }
}
