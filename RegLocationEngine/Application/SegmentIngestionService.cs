using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;
using Oiie.Isbm.Client.Topology;
using RegLocationEngine.Infrastructure.RegLocation;

namespace RegLocationEngine.Application;

/// <summary>What one drain of the inbound channel did.</summary>
public sealed class IngestionReport
{
    public int MessagesRead { get; set; }
    public int SegmentsSeen { get; set; }
    public int TagsProposed { get; set; }

    /// <summary>Segments whose federation GUID the registry already holds.</summary>
    public int AlreadyKnown { get; set; }

    /// <summary>Segments that could not become a proposal.</summary>
    public int Rejected { get; set; }

    /// <summary>
    /// Segments whose registration site REG-LOCATION holds no scope for.
    ///
    /// Counted apart from <see cref="Rejected"/> because the remedy is different:
    /// these are waiting on a SyncSites that has not arrived, not on anyone
    /// fixing the message.
    /// </summary>
    public int SiteUnknown { get; set; }

    /// <summary>Messages left on the channel because handling them failed.</summary>
    public int Failed { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Receives SyncSegments from ENG and files them as tag proposals.
///
/// This is the inbound half of SC01. ENG releases elements through a named
/// version, EngEngine wraps them as SyncSegments and publishes; this service
/// reads them off the channel, unpacks the segments, and creates a Proposed tag
/// in REG-LOCATION for each. A steward then approves, which the outbound half of
/// this engine reacts to.
///
/// It polls rather than being pushed. The reasoning is EngEngine's: a
/// notification that is missed is missed forever, whereas a reader that is
/// behind catches up on its next pass. ISBM webhook delivery is intended to
/// arrive later as an optimisation on top of this, not as a replacement for it.
/// </summary>
public sealed class SegmentIngestionService(
    IIsbmClient isbm,
    IRegLocationClient regLocation,
    IncomingSegmentMapper mapper,
    TopologyClient topology,
    IOptions<RegLocationEngineOptions> options,
    ILogger<SegmentIngestionService> logger)
{
    private readonly RegLocationEngineOptions _options = options.Value;

    /// <summary>
    /// Registration site GUID to REG-LOCATION scope, for the current drain only.
    ///
    /// Cleared at the start of each drain rather than held: a site that had no
    /// scope on one pass may well have one on the next, which is precisely the
    /// case a deferred segment is waiting for.
    /// </summary>
    private readonly Dictionary<Guid, int?> _scopeCache = [];

    /// <summary>
    /// The subscription session, cached across polls.
    ///
    /// Reused because a session is the broker's record of what this consumer has
    /// already seen. Opening a fresh one each pass would either replay the
    /// channel from the start or silently skip what arrived in between,
    /// depending on the broker -- and both look like the integration working
    /// until someone counts the rows.
    /// </summary>
    private string? _sessionId;

    public async Task<IngestionReport> DrainAsync(CancellationToken ct)
    {
        var report = new IngestionReport();

        // Scope resolutions do not survive the drain. A deferred segment is
        // waiting for a scope to appear, and a cached miss held across polls
        // would make it wait forever.
        _scopeCache.Clear();

        if (_options.ITwinFederationId == Guid.Empty)
        {
            report.Note = "ITwinFederationId is not configured, so the inbound channel cannot be derived.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        if (string.IsNullOrWhiteSpace(_options.RegLocationBaseUrl))
        {
            report.Note = "RegLocationBaseUrl is not configured, so proposals cannot be filed.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        // The subscriber end of the same declaration ENG publishes from, so the
        // two cannot drift: previously each derived this URI independently and
        // agreement rested on both settings files saying the same thing.
        //
        // Resolved before the session is opened, and only once, because the
        // session is cached across polls -- a channel that changed underneath a
        // live session would not be picked up until the engine restarts, which
        // is the accepted cost of a durable subscription.
        var declared = await topology.FindSubscriptionAsync(
            _options.ScenarioId, _options.ParticipantId, _options.ITwinFederationId, ct);

        var channelUri = declared?.Uri
            ?? _options.InboundChannelUriFor(_options.ITwinFederationId);

        var topics = declared is { Topics.Count: > 0 }
            ? declared.Topics.ToArray()
            : _options.InboundTopics;

        // Scoped to the iTwin as well as the role, because this channel is
        // per-iTwin: a single 'segments' id would collide across twins and let
        // two engines consume each other's messages. Same durability argument as
        // the sites leg -- the id must survive a restart or the backlog is
        // abandoned with the subscription.
        _sessionId ??= await isbm.OpenSubscriptionSessionAsync(
            channelUri, topics, ct,
            subscriberId: $"{_options.SourceId}:segments:{_options.ITwinFederationId:D}");

        while (report.MessagesRead < _options.MaxMessagesPerPoll)
        {
            var message = await isbm.ReadPublicationAsync(_sessionId, ct);

            // Null is the broker saying the queue is empty, not an error.
            if (message is null)
            {
                break;
            }

            report.MessagesRead++;

            bool handled;
            try
            {
                handled = await HandleMessageAsync(message, report, ct);
            }
            catch (Exception ex)
            {
                // One poisonous message must not stop the drain, but it also must
                // not be removed: leaving it on the channel means a fix can be
                // deployed and the message reprocessed. The trade is that a
                // permanently bad message is retried forever, which is visible in
                // the logs -- unlike data that was quietly discarded.
                logger.LogError(
                    ex,
                    "Failed to handle publication {MessageId}; leaving it on the channel.",
                    message.MessageId);

                handled = false;
            }

            if (!handled)
            {
                report.Failed++;

                // Stop rather than skip ahead. ISBM delivers in order, and a
                // reader that steps over a failure would process later segments
                // that may depend on it.
                break;
            }

            // Removed only now, after every segment in it has been filed. A crash
            // before this point redelivers the message, which is why the GUID
            // check exists: reprocessing must be harmless.
            await isbm.RemovePublicationAsync(_sessionId, ct);
        }

        if (report.MessagesRead > 0)
        {
            logger.LogInformation(
                "Inbound drain read {Messages} message(s): {Proposed} proposed, {Known} already known, " +
                "{Rejected} rejected, {Failed} left on the channel.",
                report.MessagesRead, report.TagsProposed, report.AlreadyKnown, report.Rejected, report.Failed);
        }

        return report;
    }

    /// <summary>
    /// Files every segment in one publication. Returns false when the message
    /// should stay on the channel.
    /// </summary>
    private async Task<bool> HandleMessageAsync(IsbmMessage message, IngestionReport report, CancellationToken ct)
    {
        if (message.Content is null)
        {
            // Unparseable, and retrying will not change that. Removing it is the
            // one case where dropping is right: leaving it would block every
            // message behind it forever.
            logger.LogError(
                "Publication {MessageId} carried no parseable XML; discarding it.",
                message.MessageId);

            return true;
        }

        var envelope = BodEnvelope.Parse(message.Content.ToString());

        if (!envelope.Is("Sync", "Segments"))
        {
            // Subscribed by topic, so this should not happen -- but a channel is
            // shared, and a participant receiving a BOD it has no handler for is
            // expected to ignore it rather than fail.
            logger.LogDebug(
                "Publication {MessageId} was {Verb}{Noun}, which this engine does not handle.",
                message.MessageId, envelope.Verb, envelope.Noun);

            return true;
        }

        foreach (var segment in envelope.NounsAs(e => new Segment(e)))
        {
            report.SegmentsSeen++;

            var result = mapper.Map(segment);

            if (!result.IsMapped)
            {
                report.Rejected++;

                logger.LogWarning(
                    "Segment '{Name}' in {MessageId} rejected: {Reason} [{CorrelationId}].",
                    segment.ShortName ?? segment.IDInInfoSource ?? "(unnamed)",
                    message.MessageId,
                    result.Rejection,
                    envelope.BodId);

                continue;
            }

            var request = result.Request!;

            // The scope comes from the site the sender named, not from engine
            // configuration. A configured scope would be a second opinion about
            // where a location belongs, and the two can disagree: this engine
            // serves every iModel under every twin it subscribes to, so a single
            // configured value would collect several plants' locations into one.
            //
            // Looked up and never created. SyncSites is what establishes a scope,
            // and creating one here would race it -- both legs would mint a scope
            // for the same twin and the plant would end up with two.
            if (result.SiteGuid is { } siteGuid)
            {
                var scopeId = await ResolveScopeAsync(siteGuid, ct);

                if (scopeId is null)
                {
                    report.SiteUnknown++;

                    // Left unproposed rather than filed somewhere plausible. The
                    // message stays on the channel, so once the site is ingested
                    // the next drain files these against the right scope.
                    logger.LogWarning(
                        "Segment '{Name}' names site {SiteGuid}, which REG-LOCATION has " +
                        "no scope for; deferred until the site is ingested [{CorrelationId}].",
                        segment.ShortName ?? segment.IDInInfoSource ?? "(unnamed)",
                        siteGuid,
                        envelope.BodId);

                    continue;
                }

                request = request with { ScopeId = scopeId.Value };
            }

            // The registry is asked, rather than engine state consulted, because
            // it is the registry that actually knows what it holds and whether
            // a steward has already acted on it.
            var existing = await regLocation.FindTagsByGuidAsync(request.Guid!.Value, ct);

            // Same GUID, same inbound revision slot: this is the row this leg
            // itself proposed earlier, so a later publication for it is a
            // correction, not a duplicate. Matched by revision because a GUID
            // legitimately carries several real revisions (see
            // docs/FederationId/federation-guid-guideline.md) and only the one
            // this leg owns may be overwritten in place.
            var priorProposal = existing.FirstOrDefault(t => t.Tag.Revision == request.Revision);

            if (priorProposal is not null)
            {
                // Identical to what is already on file -- a redelivery, not a
                // correction. Writing it again would just restamp date_changed
                // for no reason, and would count as a false correction.
                var unchanged =
                    priorProposal.Tag.ClassId == request.ClassId &&
                    string.Equals(priorProposal.Tag.Code, request.Code, StringComparison.Ordinal) &&
                    string.Equals(priorProposal.Tag.Name, request.Name, StringComparison.Ordinal);

                if (unchanged)
                {
                    report.AlreadyKnown++;

                    logger.LogDebug(
                        "Segment {Guid} rev {Revision} matches tag {TagId} already on file; left untouched.",
                        request.Guid, request.Revision, priorProposal.Tag.TagId);

                    continue;
                }

                if (!string.Equals(priorProposal.Tag.State, RegTagStates.Proposed, StringComparison.OrdinalIgnoreCase))
                {
                    // A steward has already decided this revision. Overwriting it
                    // here would let an unreviewed correction masquerade as the
                    // decision that was actually made, so it is left untouched --
                    // the correction waits for a fresh revision or a steward.
                    report.AlreadyKnown++;

                    logger.LogDebug(
                        "Segment {Guid} rev {Revision} is already {State}; left untouched.",
                        request.Guid, request.Revision, priorProposal.Tag.State);

                    continue;
                }

                var corrected = await regLocation.UpdateTagAsync(
                    priorProposal.Tag.TagId,
                    new UpdateTagRequest(request.ClassId, request.Code, request.Revision, request.Name),
                    ct);

                if (corrected is null)
                {
                    // Removed from the registry between the lookup and the write --
                    // an ordinary race, not a fault. Falling through would try to
                    // create it fresh below.
                    logger.LogDebug(
                        "Tag {TagId} for {Guid} vanished before its correction could be applied; proposing anew.",
                        priorProposal.Tag.TagId, request.Guid);
                }
                else
                {
                    report.TagsProposed++;

                    logger.LogInformation(
                        "Corrected tag {TagId} '{Code}' from {Guid} [{CorrelationId}].",
                        corrected.Tag.TagId, corrected.Tag.Code, request.Guid, envelope.BodId);

                    continue;
                }
            }
            else if (existing.Count > 0)
            {
                report.AlreadyKnown++;

                logger.LogDebug(
                    "Segment {Guid} is already registered as tag(s) {TagIds}; left untouched.",
                    request.Guid,
                    string.Join(", ", existing.Select(t => t.Tag.TagId)));

                continue;
            }

            var created = await regLocation.CreateTagAsync(request, ct);
            report.TagsProposed++;

            logger.LogInformation(
                "Proposed tag {TagId} '{Code}' from {Guid} [{CorrelationId}].",
                created.Tag.TagId, created.Tag.Code, request.Guid, envelope.BodId);
        }

        return true;
    }

    /// <summary>
    /// The scope REG-LOCATION holds for a registration site, or null if it holds
    /// none.
    ///
    /// Cached for the life of the drain because every segment in a publication
    /// carries the same site: without it, a marker of two hundred elements would
    /// ask the registry the same question two hundred times.
    /// </summary>
    private async Task<int?> ResolveScopeAsync(Guid siteGuid, CancellationToken ct)
    {
        if (_scopeCache.TryGetValue(siteGuid, out var cached))
        {
            return cached;
        }

        var scope = await regLocation.FindScopeByGuidAsync(siteGuid, ct);

        // The miss is cached too. A site that is absent now stays absent for the
        // rest of this drain, and re-asking would not change the answer.
        _scopeCache[siteGuid] = scope?.ScopeId;

        return scope?.ScopeId;
    }
}
