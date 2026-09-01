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
/// One leg only: MMS consumes sites and publishes nothing about them. There is
/// no sweep and no approval endpoint here because there is nothing for MMS to
/// propose -- a maintenance system is told what plants exist.
/// </summary>
public sealed class MmsEngineFunctions(
    SiteIngestionService siteIngestion,
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
            enterprise = _options.Enterprise,
            sourceId = _options.SourceId,
            mmsConfigured = !string.IsNullOrWhiteSpace(_options.MmsBaseUrl),
            registerInCir = _options.RegisterInCir,
            cirConfigured = !string.IsNullOrWhiteSpace(_options.CirBaseUrl),
            maxMessagesPerPoll = _options.MaxMessagesPerPoll
        });
}
