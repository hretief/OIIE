using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MmsEngine.Application;

namespace MmsEngine.Functions;

/// <summary>
/// The MMS engine's HTTP and timer surface.
///
/// MMS consumes sites and approved segments. No approval endpoint is exposed:
/// REG-LOCATION owns that decision.
/// </summary>
public sealed class MmsEngineFunctions(
    SiteIngestionService siteIngestion,
    SegmentIngestionService segmentIngestion,
    IOptions<MmsEngineOptions> options,
    ILogger<MmsEngineFunctions> logger)
{
    private readonly MmsEngineOptions _options = options.Value;

    /// <summary>
    /// Reads sites published by ENG off the enterprise sites channel.
    ///
    /// Flat setting name for the schedule because the WebJobs binding resolves
    /// %...% against the flat configuration, not the bound options section.
    /// </summary>
    [Function("MmsEngineIngestSites")]
    public async Task MmsEngineIngestSites(
        [TimerTrigger("%MmsEngineSitesIngestSchedule%")] TimerInfo timer,
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
    [Function("MmsEngineIngestSitesNow")]
    public async Task<IActionResult> MmsEngineIngestSitesNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/ingest-sites")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await siteIngestion.DrainAsync(ct));

    /// <summary>Reads approved segments from per-site operations channels.</summary>
    [Function("MmsEngineIngestSegments")]
    public async Task MmsEngineIngestSegments(
        [TimerTrigger("%MmsEngineSegmentsIngestSchedule%")] TimerInfo timer, CancellationToken ct)
    {
        if (!_options.SegmentsIngestEnabled) return;
        var report = await segmentIngestion.DrainAsync(ct);
        if (report.Failed > 0 || report.Deferred > 0)
            logger.LogWarning("MMS segment drain retained publications: {Failed} failures, {Deferred} deferrals.",
                report.Failed, report.Deferred);
    }

    /// <summary>Polls approved segments now; the report distinguishes writes from acknowledgements.</summary>
    [Function("MmsEngineIngestSegmentsNow")]
    public async Task<IActionResult> MmsEngineIngestSegmentsNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/ingest-segments")] HttpRequest req,
        CancellationToken ct) =>
        new OkObjectResult(await segmentIngestion.DrainAsync(ct));

    /// <summary>Opens durable operations subscribers without consuming or replaying publications.</summary>
    [Function("MmsEngineBootstrap")]
    public async Task<IActionResult> MmsEngineBootstrap(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/bootstrap")] HttpRequest req,
        CancellationToken ct) =>
        new OkObjectResult(await segmentIngestion.BootstrapAsync(ct));

    /// <summary>Clears local session handles; does not remove durable backlog or reset MMS/CIR data.</summary>
    [Function("MmsEngineReset")]
    public async Task<IActionResult> MmsEngineReset(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/reset")] HttpRequest req,
        CancellationToken ct)
    {
        await segmentIngestion.ResetAsync(ct);
        await siteIngestion.ResetAsync(ct);
        return new OkObjectResult(new { reset = true });
    }

    /// <summary>
    /// Configuration and readiness, without exposing any key.
    /// </summary>
    [Function("MmsEngineStatus")]
    public IActionResult MmsEngineStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "engine/status")] HttpRequest req)
        => new OkObjectResult(new
        {
            sitesIngestEnabled = _options.SitesIngestEnabled,
            sitesChannelUri = _options.SitesChannelUri,
            sitesTopics = _options.SitesTopics,
            segmentsIngestEnabled = _options.SegmentsIngestEnabled,
            segmentsTopics = _options.SegmentsTopics,
            segmentsChannelTemplate = $"/{_options.Enterprise}/{{site}}/operations/publication",
            approvedLogicalId = _options.ApprovedLogicalId,
            approvedSourceId = _options.ApprovedSourceId,
            segmentSubscriptions = segmentIngestion.SubscriptionCount,
            iTwinFederationId = _options.ITwinFederationId,
            enterprise = _options.Enterprise,
            sourceId = _options.SourceId,
            mmsConfigured = !string.IsNullOrWhiteSpace(_options.MmsBaseUrl),
            registerInCir = _options.RegisterInCir,
            cirConfigured = !string.IsNullOrWhiteSpace(_options.CirBaseUrl),
            maxMessagesPerPoll = _options.MaxMessagesPerPoll
        });
}
