using EngEngine.Application;
using EngEngine.Infrastructure.State;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EngEngine.Functions;

public sealed class EngEngineFunctions(
    EngPublicationService publisher,
    EngSitePublicationService sitePublisher,
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
    /// Announces iTwins onto the enterprise sites channel.
    ///
    /// On demand rather than on a timer, because publishing a site is an act
    /// someone performs and not a condition to be detected. A twin enters the
    /// sandbox when an operator adds it; the same operator is the one who decides
    /// it is ready to be announced.
    ///
    /// Without an iTwinId this publishes every twin ENG holds, which is what
    /// bootstraps a fresh registry: SyncSegments has nowhere to land until the
    /// sites it references exist.
    /// </summary>
    [Function("EngEnginePublishSites")]
    public async Task<IActionResult> EngEnginePublishSites(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/publish-sites")] HttpRequest req,
        CancellationToken ct)
    {
        Guid? iTwinId = null;

        if (req.Query["iTwinId"].FirstOrDefault() is { Length: > 0 } raw)
        {
            if (!Guid.TryParse(raw, out var parsed))
            {
                return new BadRequestObjectResult(new
                {
                    detail = $"iTwinId must be a UUID, but was '{raw}'."
                });
            }

            iTwinId = parsed;
        }

        var report = await sitePublisher.PublishAsync(iTwinId, ct);
        return new OkObjectResult(report);
    }

    /// <summary>
    /// Receives an iTwins.iTwinCreated.v1 event.
    ///
    /// Shaped to the platform's payload so that when the live webhook is wired
    /// up it binds to this same handler unchanged. In the sandbox the event is
    /// posted by hand, or by the UI after an operator adds an existing twin.
    ///
    /// The spec's Step 2 has ENG call back to the platform for the twin's name
    /// and type, which the event does not carry. Here the callback is a read
    /// from the ENG provider instead: the provider is the sandbox's stand-in for
    /// the platform, and whoever registered the twin supplied those details when
    /// they did. So the shape of the flow is preserved -- the event names a twin,
    /// something else supplies its detail -- while the emulated system stays the
    /// only source of truth.
    /// </summary>
    [Function("EngEngineITwinCreated")]
    public async Task<IActionResult> EngEngineITwinCreated(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "bootstrap/itwin-created")] HttpRequest req,
        CancellationToken ct)
    {
        ITwinCreatedEvent? received;

        try
        {
            received = await JsonSerializer.DeserializeAsync<ITwinCreatedEvent>(
                req.Body, EventJson, ct);
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { detail = $"Malformed event: {ex.Message}" });
        }

        if (received is null || received.ITwinId == Guid.Empty)
        {
            return new BadRequestObjectResult(new
            {
                detail = "The event carried no iTwinId."
            });
        }

        logger.LogInformation(
            "Received {EventType} for iTwin {ITwinId:D}.",
            received.EventType ?? "iTwins.iTwinCreated.v1", received.ITwinId);

        var report = await sitePublisher.PublishAsync(received.ITwinId, ct);

        // 200 even when the twin could not be published. The event was received
        // and understood; a twin ENG does not yet hold is not a malformed
        // notification, and answering with a failure status would make the
        // platform redeliver an event that will fail the same way next time.
        return new OkObjectResult(report);
    }

    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The platform's iTwinCreated payload, reduced to what the engine acts on.
    ///
    /// The event's own content block carries class and subClass, but they are
    /// deliberately not read here: the engine publishes what ENG holds, and
    /// trusting an event's copy of a twin's classification would let a stale
    /// redelivery contradict the record.
    /// </summary>
    private sealed record ITwinCreatedEvent(
        [property: JsonPropertyName("iTwinId")] Guid ITwinId,
        [property: JsonPropertyName("eventType")] string? EventType);

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
