using EngEngine.Infrastructure.Eng;
using EngEngine.Infrastructure.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;

namespace EngEngine.Application;

public sealed record EngDrainReport
{
    public int MarkersSeen { get; init; }
    public int MarkersPublished { get; init; }
    public int SegmentsPublished { get; init; }
    public int AlreadyPublished { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Markers that were passed over rather than published, with the reason.
    /// Surfaced in the report rather than only in logs: an empty marker is a
    /// question for whoever cut it, and the person running the drain is the one
    /// who can ask.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public bool Idle => MarkersPublished == 0 && Errors.Count == 0;
}

/// <summary>
/// The engine proper: carries ENG's markers onto the ISBM channel.
///
/// This exists because neither end will do it. ENG emulates a design tool and has
/// never heard of a BOD; REG-LOCATION emulates a customer registry and does not
/// know it is being integrated with. The carrier has to live outside both, and
/// this is it.
///
/// Detection is by polling rather than by notification. A real iModels service
/// raises an event, and this will eventually listen for one, but the poll is what
/// makes the engine correct rather than merely prompt: an event that is missed is
/// missed forever, whereas a watermark that is behind simply catches up.
/// </summary>
public sealed class EngPublicationService(
    IEngClient eng,
    IIsbmClient isbm,
    IEngEngineStateStore stateStore,
    EngSegmentsBuilder builder,
    IOptions<EngEngineOptions> options,
    ILogger<EngPublicationService> logger)
{
    private readonly EngEngineOptions _options = options.Value;

    public async Task<EngDrainReport> DrainAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("EngEngine is disabled.");
            return new EngDrainReport();
        }

        if (string.IsNullOrWhiteSpace(_options.EngBaseUrl))
            return Failed("EngEngine__EngBaseUrl is not configured.");

        if (_options.IModelId == Guid.Empty)
            return Failed("EngEngine__IModelId is not configured.");

        if (string.IsNullOrWhiteSpace(_options.Enterprise))
            return Failed("EngEngine__Enterprise is not configured.");

        // Which iTwin owns the model decides which channel this publishes onto,
        // so it is resolved from ENG rather than configured. Configuring both
        // would create a pair that can disagree, and the failure would be a
        // handover delivered to the wrong twin's subscribers -- which looks like
        // a successful publication from here.
        var model = await eng.GetIModelAsync(_options.IModelId, ct);

        if (model is null)
            return Failed($"ENG does not have iModel '{_options.IModelId:D}'.");

        var channelUri = _options.ChannelUriFor(model.ITwinId);

        var (state, etag) = await stateStore.ReadAsync(ct);

        // Rewound by the overlap window, because ENG's modifiedSince is inclusive
        // and it warns readers to overlap rather than resume exactly: a marker
        // committed during the previous query but stamped just before it would
        // otherwise never be seen. The published set absorbs the re-reads.
        var since = state.Watermark?.Subtract(_options.PollOverlap);

        var markers = await eng.GetNamedVersionsAsync(_options.IModelId, since, ct);

        if (markers.Count == 0)
        {
            return new EngDrainReport();
        }

        var errors = new List<string>();
        var skipped = new List<string>();
        var published = new HashSet<Guid>(state.PublishedVersions);
        var watermark = state.Watermark;
        int publishedCount = 0, segmentCount = 0, alreadyPublished = 0;

        // Oldest first, so a drain that stops part-way leaves a watermark that is
        // behind rather than ahead. Behind costs a re-read; ahead loses a handover.
        var ordered = markers
            .OrderBy(m => m.ModifiedUtc)
            .ThenBy(m => m.ChangesetIndex)
            .Take(_options.MaxMarkersPerPoll)
            .ToList();

        string? sessionId = null;

        try
        {
            foreach (var marker in ordered)
            {
                if (published.Contains(marker.VersionGuid))
                {
                    alreadyPublished++;
                    watermark = Later(watermark, marker.ModifiedUtc);
                    continue;
                }

                try
                {
                    var contents = await eng.GetNamedVersionElementsAsync(
                        marker.NamedVersionId, ct);

                    // Only the federated ones. An element without a FederationGuid
                    // has no identity anyone outside ENG has agreed to, so there is
                    // nothing to say about it that a receiver could act on. The rest
                    // of the marker still goes: holding back a whole handover because
                    // one element was never federated would make a routine omission
                    // look like an outage.
                    var elements = contents
                        .Where(EngSegmentsBuilder.IsPublishable)
                        .ToList();

                    var unfederated = contents.Count - elements.Count;

                    if (unfederated > 0)
                    {
                        logger.LogWarning(
                            "Marker '{Marker}' has {Count} element(s) without a " +
                            "FederationGuid; they were not published.",
                            marker.Name, unfederated);
                    }

                    if (elements.Count == 0)
                    {
                        // Recorded as published rather than retried forever. An empty
                        // marker is a real thing an engineer can cut, and there is
                        // nothing to send; leaving it unrecorded would make every
                        // future drain re-examine it for as long as it exists.
                        skipped.Add($"{marker.Name}: contains no publishable elements.");
                        published.Add(marker.VersionGuid);
                        watermark = Later(watermark, marker.ModifiedUtc);
                        continue;
                    }

                    // Opened lazily: a drain that finds nothing to publish should not
                    // establish a session against the broker to prove it.
                    sessionId ??= await isbm.OpenPublicationSessionAsync(channelUri, ct);

                    var correlationId = Guid.NewGuid().ToString();

                    // The same twin the channel is rooted in, so what the message
                    // says about itself and where it was delivered cannot disagree.
                    var content = builder.Build(
                        marker, elements, model.ITwinId, correlationId);

                    var messageId = await isbm.PostPublicationAsync(
                        sessionId,
                        content,
                        _options.Topics,
                        ct: ct);

                    published.Add(marker.VersionGuid);
                    watermark = Later(watermark, marker.ModifiedUtc);
                    publishedCount++;
                    segmentCount += elements.Count;

                    logger.LogInformation(
                        "Published marker '{Marker}' with {Count} segment(s) as {MessageId} " +
                        "[{CorrelationId}].",
                        marker.Name, elements.Count, messageId, correlationId);
                }
                catch (Exception ex)
                {
                    // The marker is left unrecorded, so the next drain retries it.
                    // Publishing later is recoverable; recording a marker that never
                    // reached the channel is not.
                    logger.LogError(ex, "Publishing marker '{Marker}' failed.", marker.Name);
                    errors.Add($"{marker.Name}: {ex.Message}");
                    break;
                }
            }
        }
        finally
        {
            // Written even on failure, so the markers that did land are not sent
            // again. The watermark only ever advanced over markers that were dealt
            // with, because the loop breaks on the first failure.
            if (publishedCount > 0 || alreadyPublished > 0 || skipped.Count > 0)
            {
                var next = new EngEngineState
                {
                    Watermark = watermark,
                    PublishedVersions = published
                };

                if (!await stateStore.TryWriteAsync(next, etag, CancellationToken.None))
                {
                    logger.LogWarning(
                        "Engine state was modified concurrently; this drain's watermark " +
                        "was discarded. Published markers will be recognised on the next pass.");
                }
            }
        }

        return new EngDrainReport
        {
            MarkersSeen = markers.Count,
            MarkersPublished = publishedCount,
            SegmentsPublished = segmentCount,
            AlreadyPublished = alreadyPublished,
            Errors = errors,
            Skipped = skipped
        };
    }

    private static DateTime? Later(DateTime? current, DateTime candidate) =>
        current is null || candidate > current ? candidate : current;

    private static EngDrainReport Failed(string error) => new() { Errors = [error] };
}
