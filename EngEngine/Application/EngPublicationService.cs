using EngEngine.Infrastructure.Eng;
using EngEngine.Infrastructure.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;
using Oiie.Isbm.Client.Topology;

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

    /// <summary>
    /// What the RDL library had to say about this engine's outbound class map.
    ///
    /// Distinct from <see cref="Errors"/> and <see cref="Skipped"/> because it
    /// describes the configuration rather than the pass: nothing was skipped
    /// and nothing failed, but a segment type published this pass may name a
    /// class the reference library does not hold. Advisory, and never a reason
    /// to withhold a marker.
    /// </summary>
    public IReadOnlyList<string> RdlWarnings { get; init; } = [];

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
    TopologyClient topology,
    RdlTaxonomyValidator rdlValidator,
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

        if (string.IsNullOrWhiteSpace(_options.Enterprise))
            return Failed("EngEngine__Enterprise is not configured.");

        // Checked before anything is published, so the warning appears above
        // the segments it is about rather than after them. Advisory: the
        // result is reported and the drain proceeds either way, because a
        // marker ENG has already cut is not made wrong by a mapping typo.
        var rdlWarnings = await ValidateOutboundMapAsync(ct);

        // What to publish is discovered, not configured.
        //
        // An iModel id is data ENG generates rather than a deployment choice: a
        // provider reset mints new ones, so a setting naming one is stale from
        // that moment. Asking ENG makes the engine correct across a reset
        // without anyone editing app settings, which is the same reasoning that
        // already applies to the iTwin below -- and the same reasoning
        // REG-LOCATION follows when it discovers sites from the broker.
        IReadOnlyList<EngIModel> models;

        try
        {
            models = await eng.GetIModelsAsync(ct);
        }
        catch (Exception ex)
        {
            // Enumeration failing is not the same as there being nothing to
            // publish. Reported rather than returned as an idle pass, which
            // would look identical to a healthy engine with no new markers.
            logger.LogError(ex, "Could not enumerate iModels, so nothing was published this pass.");
            return Failed($"Could not enumerate iModels: {ex.Message}");
        }

        // The setting survives as an optional filter, for pinning a deployment
        // to one model deliberately -- a focused test, say. What is gone is the
        // requirement to set it, and its ability to go stale unnoticed.
        if (_options.IModelId != Guid.Empty)
        {
            models = [.. models.Where(m => m.IModelId == _options.IModelId)];

            if (models.Count == 0)
                return Failed($"ENG does not have iModel '{_options.IModelId:D}'.");
        }

        if (models.Count == 0)
        {
            return new EngDrainReport
            {
                Skipped = ["ENG holds no iModels, so there is nothing to publish."],
                RdlWarnings = rdlWarnings
            };
        }

        var (state, etag) = await stateStore.ReadAsync(ct);

        var run = new DrainRun
        {
            Published = new HashSet<Guid>(state.PublishedVersions),
            Watermark = state.Watermark
        };

        // Sequential rather than concurrent: the run accumulates shared state,
        // and several models under one twin publish onto the same channel, so
        // parallel drains would race on both.
        foreach (var model in models)
        {
            if (run.Errors.Count > 0)
            {
                // One model failing stops the pass. The watermark is shared, so
                // continuing past a failure would advance it over markers a
                // later model never got to publish.
                break;
            }

            try
            {
                await DrainModelAsync(model, run, ct);
            }
            catch (Exception ex)
            {
                // Caught here so the state written below still records whatever
                // earlier models managed to publish. Letting this escape would
                // lose their watermark and re-publish them on the next pass.
                logger.LogError(
                    ex, "Draining iModel {IModelId:D} failed.", model.IModelId);
                run.Errors.Add($"{model.IModelId:D}: {ex.Message}");
            }
        }

        // Written even on failure, so the markers that did land are not sent
        // again. The watermark only ever advanced over markers that were dealt
        // with, because the loop breaks on the first failure.
        if (run.PublishedCount > 0 || run.AlreadyPublished > 0 || run.Skipped.Count > 0)
        {
            var next = new EngEngineState
            {
                Watermark = run.Watermark,
                PublishedVersions = run.Published
            };

            if (!await stateStore.TryWriteAsync(next, etag, CancellationToken.None))
            {
                logger.LogWarning(
                    "Engine state was modified concurrently; this drain's watermark " +
                    "was discarded. Published markers will be recognised on the next pass.");
            }
        }

        return new EngDrainReport
        {
            MarkersSeen = run.MarkersSeen,
            MarkersPublished = run.PublishedCount,
            SegmentsPublished = run.SegmentCount,
            AlreadyPublished = run.AlreadyPublished,
            Errors = run.Errors,
            Skipped = run.Skipped,
            RdlWarnings = rdlWarnings
        };
    }

    /// <summary>
    /// Asks RDL whether the keys this engine publishes actually exist, and
    /// turns the answer into lines a human can act on.
    ///
    /// Never throws and never blocks the drain. A missing key is a real defect
    /// -- it puts an unresolvable segment type on the bus -- but it is one that
    /// only the person holding the settings file can fix, so the engine's job
    /// is to say so clearly and carry on.
    /// </summary>
    private async Task<IReadOnlyList<string>> ValidateOutboundMapAsync(CancellationToken ct)
    {
        RdlValidationResult result;

        try
        {
            result = await rdlValidator.ValidateAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The validator already swallows its own failures, so reaching
            // here means something unanticipated. Still not fatal: validation
            // is not why this engine exists.
            logger.LogWarning(ex, "Validating the outbound RDL map failed unexpectedly.");
            return [$"The outbound RDL map could not be validated: {ex.Message}"];
        }

        if (!result.Checked)
        {
            return result.Reason is { Length: > 0 } reason
                ? [$"The outbound RDL map is unverified: {reason}"]
                : [];
        }

        if (result.MissingKeys.Count == 0) return [];

        // A map where nothing at all resolves is almost never N independent
        // typos; it is the wrong library, the wrong namespace, or a code form
        // that changed. Said once, as one problem, because N warnings would
        // send someone looking for N fixes.
        if (result.MissingKeys.Count == _options.OutboundRdlClassMap.Count && result.LibrarySize > 0)
        {
            var line =
                $"None of the {result.MissingKeys.Count} configured RDL keys appear among the " +
                $"{result.LibrarySize} classes RDL holds. This looks like a mismatch in the key " +
                $"form rather than individual typos; expected keys like '{result.MissingKeys[0]}'.";

            logger.LogWarning("{Warning}", line);
            return [line];
        }

        var warnings = result.MissingKeys
            .Select(key =>
                $"RDL does not hold '{key}', which OutboundRdlClassMap publishes as a " +
                "SegmentType. Segments carrying it will name a class no subscriber can resolve.")
            .ToList();

        foreach (var warning in warnings)
        {
            logger.LogWarning("{Warning}", warning);
        }

        return warnings;
    }

    /// <summary>
    /// Everything one drain accumulates across the models it visits.
    ///
    /// Shared deliberately: the watermark and the published set describe the
    /// engine's progress as a whole, not one model's, so they cannot be reset
    /// per model without re-publishing every marker on every pass.
    /// </summary>
    private sealed class DrainRun
    {
        public required HashSet<Guid> Published { get; init; }
        public DateTime? Watermark { get; set; }
        public List<string> Errors { get; } = [];
        public List<string> Skipped { get; } = [];
        public int MarkersSeen { get; set; }
        public int PublishedCount { get; set; }
        public int SegmentCount { get; set; }
        public int AlreadyPublished { get; set; }
    }

    /// <summary>
    /// Publishes one iModel's outstanding markers onto its iTwin's channel.
    ///
    /// The channel is per-iTwin, not per-iModel: a twin may hold several models
    /// representing different disciplines and they all publish onto the one
    /// channel, with the discipline expressed as a topic. So two models under
    /// one twin will open sessions on the same channel in the same pass, which
    /// is correct and not a duplicate.
    /// </summary>
    private async Task DrainModelAsync(EngIModel model, DrainRun run, CancellationToken ct)
    {
        // The topology is asked first, and answers with both the channel and the
        // topics -- they belong together. A channel resolved centrally but
        // topics taken from local settings would let a subscriber be found on
        // the right channel filtering for a topic nobody posts under, which is
        // the same silent non-delivery by a narrower route.
        var declared = await topology.FindPublicationAsync(
            _options.ScenarioId, _options.ParticipantId, model.ITwinId, ct);

        var channelUri = declared?.Uri ?? _options.ChannelUriFor(model.ITwinId);

        var topics = declared is { Topics.Count: > 0 }
            ? [.. declared.Topics]
            : _options.Topics;

        if (declared is not null && !string.Equals(
                declared.Uri, _options.ChannelUriFor(model.ITwinId), StringComparison.Ordinal))
        {
            // Worth a line: the engine is publishing somewhere other than its
            // own settings say, which is intended but confusing to find later
            // when reading only this app's configuration.
            logger.LogInformation(
                "Publishing to {Channel} from topology, overriding the configured {Configured}.",
                declared.Uri, _options.ChannelUriFor(model.ITwinId));
        }

        // Rewound by the overlap window, because ENG's modifiedSince is inclusive
        // and it warns readers to overlap rather than resume exactly: a marker
        // committed during the previous query but stamped just before it would
        // otherwise never be seen. The published set absorbs the re-reads.
        var since = run.Watermark?.Subtract(_options.PollOverlap);

        var markers = await eng.GetNamedVersionsAsync(model.IModelId, since, ct);

        run.MarkersSeen += markers.Count;

        if (markers.Count == 0)
        {
            return;
        }

        // Oldest first, so a drain that stops part-way leaves a watermark that is
        // behind rather than ahead. Behind costs a re-read; ahead loses a handover.
        var ordered = markers
            .OrderBy(m => m.ModifiedUtc)
            .ThenBy(m => m.ChangesetIndex)
            .Take(_options.MaxMarkersPerPoll)
            .ToList();

        string? sessionId = null;

        foreach (var marker in ordered)
        {
            if (run.Published.Contains(marker.VersionGuid))
            {
                run.AlreadyPublished++;
                run.Watermark = Later(run.Watermark, marker.ModifiedUtc);
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
                    run.Skipped.Add($"{marker.Name}: contains no publishable elements.");
                    run.Published.Add(marker.VersionGuid);
                    run.Watermark = Later(run.Watermark, marker.ModifiedUtc);
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
                    topics,
                    ct: ct);

                run.Published.Add(marker.VersionGuid);
                run.Watermark = Later(run.Watermark, marker.ModifiedUtc);
                run.PublishedCount++;
                run.SegmentCount += elements.Count;

                logger.LogInformation(
                    "Published marker '{Marker}' from iModel {IModelId:D} with {Count} " +
                    "segment(s) as {MessageId} [{CorrelationId}].",
                    marker.Name, model.IModelId, elements.Count, messageId, correlationId);
            }
            catch (Exception ex)
            {
                // The marker is left unrecorded, so the next drain retries it.
                // Publishing later is recoverable; recording a marker that never
                // reached the channel is not.
                logger.LogError(ex, "Publishing marker '{Marker}' failed.", marker.Name);
                run.Errors.Add($"{marker.Name}: {ex.Message}");
                break;
            }
        }
    }

    private static DateTime? Later(DateTime? current, DateTime candidate) =>
        current is null || candidate > current ? candidate : current;

    private static EngDrainReport Failed(string error) => new() { Errors = [error] };
}
