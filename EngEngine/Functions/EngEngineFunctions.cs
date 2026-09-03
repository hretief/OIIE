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

    /// <summary>
    /// Receives an iModels.namedVersionCreated.v1 event.
    ///
    /// This is the low-latency path: ENG posts here the moment a marker commits,
    /// rather than the engine waiting up to a poll interval to notice. It does
    /// not replace <see cref="EngEnginePoll"/>, which stays as the backstop --
    /// a notification that is lost in flight is lost forever, whereas a poller
    /// that is behind catches up on its next pass. Both converge because the
    /// published-marker set is keyed by VersionGuid, so whichever arrives second
    /// finds the work already done.
    ///
    /// The event is treated as a nudge, not as data. It names a marker, and the
    /// engine then drains normally: trusting the payload's own copy of the
    /// marker would let a stale redelivery contradict ENG, and draining only the
    /// named marker would strand any earlier one whose notification was lost.
    /// </summary>
    [Function("EngEngineNamedVersionCreated")]
    public async Task<IActionResult> EngEngineNamedVersionCreated(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "events/named-version-created")] HttpRequest req,
        CancellationToken ct)
    {
        NamedVersionCreatedEvent? received;

        try
        {
            received = await JsonSerializer.DeserializeAsync<NamedVersionCreatedEvent>(
                req.Body, EventJson, ct);
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { detail = $"Malformed event: {ex.Message}" });
        }

        if (received is null || received.IModelId == Guid.Empty)
        {
            return new BadRequestObjectResult(new { detail = "The event carried no iModelId." });
        }

        // This engine serves one iModel by design -- draining another project's
        // model would publish its design onto this channel. An event for a model
        // this engine does not serve is acknowledged and ignored: it is a
        // correctly delivered notification that simply is not ours, and
        // answering with a failure would make the sender retry forever.
        if (received.IModelId != _options.IModelId)
        {
            logger.LogInformation(
                "Ignoring {EventType} for iModel {IModelId:D}; this engine serves {Served:D}.",
                received.EventType ?? "iModels.namedVersionCreated.v1",
                received.IModelId, _options.IModelId);

            return new OkObjectResult(new { accepted = false, reason = "Different iModel." });
        }

        logger.LogInformation(
            "Received {EventType} for named version {Version} in iModel {IModelId:D}.",
            received.EventType ?? "iModels.namedVersionCreated.v1",
            received.NamedVersionId, received.IModelId);

        var report = await publisher.DrainAsync(ct);

        // 200 even when the drain reported errors. The event was received and
        // understood; a transient ENG or ISBM failure is not a malformed
        // notification, and the poll will retry regardless.
        return new OkObjectResult(report);
    }

    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// ENG's namedVersionCreated payload, reduced to what the engine acts on.
    ///
    /// Only the identity is read. The event also carries the marker's name and
    /// changeset position, but the engine deliberately re-reads those from ENG:
    /// the payload is a nudge, and the record is the truth.
    /// </summary>
    private sealed record NamedVersionCreatedEvent(
        [property: JsonPropertyName("namedVersionId")] long NamedVersionId,
        [property: JsonPropertyName("iModelId")] Guid IModelId,
        [property: JsonPropertyName("eventType")] string? EventType);

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
    /// Receives an iTwins.iTwinDeleted.v1 event.
    ///
    /// The mirror of <see cref="EngEngineITwinCreated"/>, and shaped to the same
    /// payload so a live webhook would bind here unchanged. In the sandbox it is
    /// posted by the admin endpoint after ENG has removed the twin.
    ///
    /// Unlike the created handler this cannot read the twin back for its detail,
    /// because there is deliberately no twin left to read. That is why the delete
    /// BOD carries the UUID alone: it is the only thing still true.
    /// </summary>
    [Function("EngEngineITwinDeleted")]
    public async Task<IActionResult> EngEngineITwinDeleted(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "bootstrap/itwin-deleted")] HttpRequest req,
        CancellationToken ct)
    {
        ITwinDeletedEvent? received;

        try
        {
            received = await JsonSerializer.DeserializeAsync<ITwinDeletedEvent>(
                req.Body, EventJson, ct);
        }
        catch (JsonException ex)
        {
            return new BadRequestObjectResult(new { detail = $"Malformed event: {ex.Message}" });
        }

        if (received is null || received.ITwinId == Guid.Empty)
        {
            return new BadRequestObjectResult(new { detail = "The event carried no iTwinId." });
        }

        logger.LogInformation(
            "Received {EventType} for iTwin {ITwinId:D}.",
            received.EventType ?? "iTwins.iTwinDeleted.v1", received.ITwinId);

        var report = await sitePublisher.PublishDeleteAsync(received.ITwinId, ct);

        // 200 even when publication failed, matching the created handler: the
        // event was received and understood, and answering with a failure would
        // make the platform redeliver an event that fails the same way.
        return new OkObjectResult(report);
    }

    /// <summary>The platform's iTwinDeleted payload, reduced to what the engine acts on.</summary>
    private sealed record ITwinDeletedEvent(
        [property: JsonPropertyName("iTwinId")] Guid ITwinId,
        [property: JsonPropertyName("eventType")] string? EventType);

    /// <summary>
    /// Forgets the watermark and every published marker.
    ///
    /// Called by the Sandbox's day zero, after ENG's own tables are dropped. The
    /// two have to happen together: the engine's published-marker set is keyed by
    /// VersionGuid precisely so it survives ENG being rebuilt, so an ENG reset on
    /// its own would leave the engine declining to republish work it believes it
    /// has already sent.
    ///
    /// Not routed under admin/: the Functions host reserves that prefix, and a
    /// function that claims it fails indexing and is silently disabled rather
    /// than rejected at build time.
    /// </summary>
    [Function("EngEngineReset")]
    public async Task<IActionResult> EngEngineReset(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/reset")] HttpRequest req,
        CancellationToken ct)
    {
        try
        {
            await stateStore.ClearAsync(ct);
            logger.LogWarning("ENG engine state cleared: watermark and published markers forgotten.");

            return new OkObjectResult(new { reset = true });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ENG engine state reset failed.");
            return new ObjectResult(new { reset = false, detail = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
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
