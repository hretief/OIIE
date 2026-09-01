using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;
using RegLocationEngine.Infrastructure.Cir;
using RegLocationEngine.Infrastructure.RegLocation;
using RegLocationEngine.Infrastructure.State;

namespace RegLocationEngine.Application;

public sealed record ApprovalReport
{
    public int TagsSeen { get; init; }
    public int TagsPublished { get; init; }
    public int AlreadyPublished { get; init; }
    public int EntriesRegistered { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    /// Tags passed over rather than published, with the reason. Surfaced in the
    /// report rather than only in logs: a tag without a federation GUID is a
    /// question for whoever created it, and the person running this is the one
    /// who can ask.
    /// </summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];

    public bool Idle => TagsPublished == 0 && Errors.Count == 0;
}

/// <summary>
/// The engine proper: carries approved REG-LOCATION tags onto the ISBM channel
/// and registers their identity in CIR.
///
/// This exists because neither end will do it. REG-LOCATION emulates a customer
/// registry and does not know it is being integrated with; CIR is a peer that
/// answers questions rather than one that goes looking. The carrier has to live
/// outside both, and this is it.
///
/// The stewardship gate is upstream of everything here. By the time this class
/// runs, a steward has already decided, and the decision is recorded in the
/// registry whether or not this engine ever hears about it. That ordering is the
/// point: an engine that held the gate itself would mean approvals only counted
/// while the integration was running.
///
/// Two ways in, and they are not redundant. The notification makes the engine
/// prompt; the sweep makes it correct. The provider deliberately does not fail an
/// approval when this engine is unreachable, so a notification can simply be
/// lost -- and a lost event is lost forever, whereas a sweep that is behind
/// merely catches up.
/// </summary>
public sealed class RegLocationApprovalService(
    IRegLocationClient registry,
    IIsbmClient isbm,
    ICirClient cir,
    IRegLocationEngineStateStore stateStore,
    RegLocationSegmentsBuilder builder,
    IOptions<RegLocationEngineOptions> options,
    ILogger<RegLocationApprovalService> logger)
{
    private readonly RegLocationEngineOptions _options = options.Value;

    /// <summary>
    /// Handles one approval notification.
    ///
    /// The tag is read back rather than taken from the payload. The notification
    /// is a claim about a past moment; the registry is the only thing that knows
    /// the present one, and a tag whose approval was reversed between send and
    /// receipt must not still reach the channel.
    /// </summary>
    public async Task<ApprovalReport> HandleApprovalAsync(
        TagApprovedNotification notification, CancellationToken ct = default)
    {
        if (Misconfigured(out var reason))
            return Failed(reason);

        var detail = await registry.GetTagAsync(notification.TagId, ct);

        if (detail is null)
            return Failed($"REG-LOCATION does not have tag '{notification.TagId}'.");

        return await PublishAsync([detail], notification.DecidedBy, ct);
    }

    /// <summary>
    /// Reconciles the whole registry against what has been published.
    ///
    /// Gated by Enabled, unlike the notification path: a sweep against
    /// configuration nobody has filled in yet produces a stream of connection
    /// errors that look like a fault rather than an absence, and it produces them
    /// on a timer forever.
    /// </summary>
    public async Task<ApprovalReport> SweepAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            logger.LogDebug("RegLocationEngine sweep is disabled.");
            return new ApprovalReport();
        }

        if (Misconfigured(out var reason))
            return Failed(reason);

        var approved = await registry.GetApprovedTagsAsync(_options.ScopeId, ct);

        // Ordered by tag id so a sweep that stops part-way resumes predictably
        // rather than picking a different arbitrary subset next time.
        var batch = approved
            .OrderBy(t => t.Tag.TagId)
            .Take(_options.MaxTagsPerSweep)
            .ToList();

        return await PublishAsync(batch, "sweep", ct);
    }

    private async Task<ApprovalReport> PublishAsync(
        IReadOnlyList<RegTagDetail> candidates, string decidedBy, CancellationToken ct)
    {
        var (state, etag) = await stateStore.ReadAsync(ct);

        var published = new HashSet<string>(state.PublishedTags);
        var skipped = new List<string>();
        var errors = new List<string>();
        var toPublish = new List<RegTagDetail>();
        var alreadyPublished = 0;

        foreach (var detail in candidates)
        {
            if (!RegLocationSegmentsBuilder.IsPublishable(detail))
            {
                // Not recorded as published: a tag missing its GUID may acquire
                // one, and a proposed tag will be approved. Recording either now
                // would mean the engine never looked at it again on the day it
                // became publishable.
                skipped.Add(detail.Object.Guid is null
                    ? $"{detail.Tag.Code}: has no registry GUID."
                    : $"{detail.Tag.Code}: state is '{detail.Tag.State}', not approved.");
                continue;
            }

            var key = RegLocationEngineState.KeyFor(detail.Object.Guid!.Value, detail.Tag.Revision);

            if (published.Contains(key))
            {
                alreadyPublished++;
                continue;
            }

            toPublish.Add(detail);
        }

        if (toPublish.Count == 0)
        {
            return new ApprovalReport
            {
                TagsSeen = candidates.Count,
                AlreadyPublished = alreadyPublished,
                Skipped = skipped
            };
        }

        var entriesRegistered = 0;
        var publishedCount = 0;

        try
        {
            var channelUri = _options.ChannelUriFor(_options.IModelId);

            // Opened only once there is something to say. A pass that finds
            // nothing should not establish a session against the broker to prove
            // it.
            var sessionId = await isbm.OpenPublicationSessionAsync(channelUri, ct);

            var correlationId = Guid.NewGuid().ToString();
            var content = builder.Build(toPublish, decidedBy, correlationId);

            var messageId = await isbm.PostPublicationAsync(
                sessionId,
                content,
                _options.Topics,
                ct: ct);

            publishedCount = toPublish.Count;

            foreach (var detail in toPublish)
            {
                published.Add(
                    RegLocationEngineState.KeyFor(detail.Object.Guid!.Value, detail.Tag.Revision));
            }

            logger.LogInformation(
                "Published {Count} approved tag(s) as {MessageId} [{CorrelationId}].",
                publishedCount, messageId, correlationId);

            // Registration follows publication rather than preceding it. Both
            // orders leave a window, and this is the better one: a tag on the
            // channel but not yet in CIR is a receiver that has to ask again,
            // whereas a CIRID for something never published is an identity for a
            // location nobody has been told about.
            if (_options.RegisterInCir && !string.IsNullOrWhiteSpace(_options.CirBaseUrl))
            {
                entriesRegistered = await RegisterAsync(toPublish, ct);
            }
        }
        catch (Exception ex)
        {
            // The tags are left unrecorded, so the next pass retries them.
            // Publishing later is recoverable; recording a tag that never reached
            // the channel is not.
            logger.LogError(ex, "Publishing approved tags failed.");
            errors.Add(ex.Message);
        }

        if (publishedCount > 0)
        {
            var next = new RegLocationEngineState { PublishedTags = published };

            if (!await stateStore.TryWriteAsync(next, etag, CancellationToken.None))
            {
                // Harmless. The BOD is a Replace, so the worst case is that the
                // other instance's view wins and these tags are sent once more.
                logger.LogWarning(
                    "Engine state was modified concurrently; this pass's record was discarded. " +
                    "The affected tags will be republished, which is idempotent downstream.");
            }
        }

        return new ApprovalReport
        {
            TagsSeen = candidates.Count,
            TagsPublished = publishedCount,
            AlreadyPublished = alreadyPublished,
            EntriesRegistered = entriesRegistered,
            Errors = errors,
            Skipped = skipped
        };
    }

    /// <summary>
    /// Registers each published tag in CIR under its federation GUID.
    ///
    /// One registry, one category per scope. The CIRID is the registry GUID
    /// rather than one CIR mints, because the whole point is that this is the
    /// same location the design tool already published: letting CIR allocate a
    /// fresh id would create a second identity for a thing that already had one.
    /// </summary>
    private async Task<int> RegisterAsync(IReadOnlyList<RegTagDetail> tags, CancellationToken ct)
    {
        var entries = tags.Select(detail => new CirEntry(
            IdInSource: detail.Tag.TagId.ToString(),
            SourceId: _options.SourceId,
            Cirid: detail.Object.Guid,
            SourceOwnerId: _options.Enterprise,
            Name: detail.Tag.Code,
            Description: new CirLocalizedText(detail.Tag.Name),
            Properties:
            [
                new CirProperty("revision", "int",
                    [new CirPropertyValue("revision", detail.Tag.Revision.ToString())])
            ])).ToList();

        var request = new CreateRegistryRequest(
            [
                new CirRegistry(
                    Id: _options.Enterprise,
                    Description: [new CirLocalizedText("Functional locations")],
                    Categories:
                    [
                        new CirCategory(
                            Id: "FunctionalLocation",
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("REG-LOCATION tags")],
                            Entries: entries)
                    ])
            ],
            // False: the CIRID is supplied above and is the registry's own GUID.
            // Letting CIR mint one would give an already-federated location a
            // second identity.
            CreateCirid: false);

        try
        {
            return await cir.RegisterEntriesAsync(request, ct);
        }
        catch (CirClientException ex)
        {
            // Not fatal to the pass. The publication has already succeeded, and
            // failing here would leave the tags unrecorded and republish them on
            // the next sweep to no benefit -- CIR would still be down.
            logger.LogError(ex, "Registering {Count} approved tag(s) in CIR failed.", tags.Count);
            return 0;
        }
    }

    private bool Misconfigured(out string reason)
    {
        if (string.IsNullOrWhiteSpace(_options.RegLocationBaseUrl))
        {
            reason = "RegLocationEngine__RegLocationBaseUrl is not configured.";
            return true;
        }

        if (_options.IModelId == Guid.Empty)
        {
            reason = "RegLocationEngine__IModelId is not configured.";
            return true;
        }

        if (string.IsNullOrWhiteSpace(_options.Enterprise))
        {
            reason = "RegLocationEngine__Enterprise is not configured.";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static ApprovalReport Failed(string error) => new() { Errors = [error] };
}
