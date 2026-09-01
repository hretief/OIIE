using CmsEngine.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CmsEngine.Functions;

/// <summary>
/// The CMS engine's HTTP and timer surface.
///
/// One leg only: CMS consumes sites and publishes nothing. There is no sweep
/// and no approval endpoint here because there is nothing for CMS to propose --
/// a condition monitoring system is told what exists.
/// </summary>
public sealed class CmsEngineFunctions(
    SiteIngestionService siteIngestion,
    IOptions<CmsEngineOptions> options,
    ILogger<CmsEngineFunctions> logger)
{
    private readonly CmsEngineOptions _options = options.Value;

    /// <summary>
    /// Reads sites published by ENG off the enterprise sites channel.
    ///
    /// Flat setting name for the schedule because the WebJobs binding resolves
    /// %...% against the flat configuration, not the bound options section.
    /// </summary>
    [Function("CmsEngineIngestSites")]
    public async Task CmsEngineIngestSites(
        [TimerTrigger("%CmsEngineSitesIngestSchedule%")] TimerInfo timer,
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
    [Function("CmsEngineIngestSitesNow")]
    public async Task<IActionResult> CmsEngineIngestSitesNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/ingest-sites")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await siteIngestion.DrainAsync(ct));

    /// <summary>
    /// Configuration and readiness, without exposing any key.
    /// </summary>
    [Function("CmsEngineStatus")]
    public IActionResult CmsEngineStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "engine/status")] HttpRequest req)
        => new OkObjectResult(new
        {
            sitesIngestEnabled = _options.SitesIngestEnabled,
            sitesChannelUri = _options.SitesChannelUri,
            sitesTopics = _options.SitesTopics,
            enterprise = _options.Enterprise,
            sourceId = _options.SourceId,
            cmsConfigured = !string.IsNullOrWhiteSpace(_options.CmsBaseUrl),
            registerInCir = _options.RegisterInCir,
            cirConfigured = !string.IsNullOrWhiteSpace(_options.CirBaseUrl),
            maxMessagesPerPoll = _options.MaxMessagesPerPoll
        });
}
