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

    /// <summary>
    /// How many sites' channels were polled.
    ///
    /// Reported because zero is the reading that used to be indistinguishable
    /// from a working engine with nothing to do: when the site list is empty,
    /// no message can arrive no matter what anyone publishes.
    /// </summary>
    public int SitesPolled { get; set; }

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

    /// <summary>
    /// The stored session id named a subscription the broker no longer had, so
    /// it was reopened during this drain.
    ///
    /// Reported because reopening is not free: a new subscription starts empty,
    /// so anything published before it was opened is not delivered. Seeing this
    /// set explains an otherwise inexplicable gap between what was published and
    /// what arrived.
    /// </summary>
    public bool SessionReopened { get; set; }

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
    /// Subscription sessions, cached across polls and keyed by site.
    ///
    /// Reused because a session is the broker's record of what this consumer has
    /// already seen. Opening a fresh one each pass would either replay the
    /// channel from the start or silently skip what arrived in between,
    /// depending on the broker -- and both look like the integration working
    /// until someone counts the rows.
    ///
    /// Keyed by site rather than held as a single field because this engine
    /// follows every site it knows about, not one configured twin.
    /// </summary>
    private readonly Dictionary<Guid, string> _sessions = [];

    public async Task<IngestionReport> DrainAsync(CancellationToken ct)
    {
        var report = new IngestionReport();

        // Scope resolutions do not survive the drain. A deferred segment is
        // waiting for a scope to appear, and a cached miss held across polls
        // would make it wait forever.
        _scopeCache.Clear();

        if (string.IsNullOrWhiteSpace(_options.RegLocationBaseUrl))
        {
            report.Note = "RegLocationBaseUrl is not configured, so proposals cannot be filed.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        var sites = await DiscoverSitesAsync(ct);

        if (sites.Count == 0)
        {
            // Not an error, and deliberately not silent. Before this engine
            // followed sites it read one channel derived from a configured
            // federation id, and when day zero replaced the twin that id named,
            // it polled a channel nobody published to and reported success
            // forever. An empty channel and a channel for a twin that no longer
            // exists produce the same zero, so the distinction has to be stated
            // rather than inferred.
            report.Note =
                "No engineering channels found on the broker, so there is nothing to ingest. " +
                "Sites are established by SyncSites, which provisions the channel this reads.";

            logger.LogInformation("{Note}", report.Note);
            return report;
        }

        report.SitesPolled = sites.Count;

        foreach (var site in sites)
        {
            if (report.MessagesRead >= _options.MaxMessagesPerPoll)
            {
                break;
            }

            await DrainSiteAsync(site, report, ct);
        }

        if (report.MessagesRead > 0)
        {
            logger.LogInformation(
                "Inbound drain read {Messages} message(s) across {Sites} site(s): {Proposed} proposed, " +
                "{Known} already known, {Rejected} rejected, {Failed} left on the channel.",
                report.MessagesRead, report.SitesPolled, report.TagsProposed,
                report.AlreadyKnown, report.Rejected, report.Failed);
        }

        return report;
    }

    /// <summary>
    /// The sites this engine should be reading segments for.
    ///
    /// Taken from the broker's channel list rather than configuration. The
    /// engineering channel for a site is provisioned by SyncSites ingestion
    /// (see <c>SiteIngestionService.BootstrapChannelsAsync</c>), so its existence
    /// is the same fact as the site being established -- which makes the broker
    /// the one place that already knows the answer, and avoids a second list
    /// that can disagree with it.
    ///
    /// The alternative was a configured federation id, which is what this
    /// replaced: it survived day zero while the twin it named did not.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> DiscoverSitesAsync(CancellationToken ct)
    {
        // An explicit override still wins, so a deployment can be pinned to one
        // twin deliberately -- for a focused test, say. What is gone is the
        // requirement to set it.
        if (_options.ITwinFederationId != Guid.Empty)
        {
            return [_options.ITwinFederationId];
        }

        IReadOnlyList<IsbmChannel> channels;

        try
        {
            channels = await isbm.GetChannelsAsync(ct);
        }
        catch (Exception ex)
        {
            // Enumeration failing is not the same as there being no sites, and
            // treating it as such would drain nothing while reporting a clean
            // pass. Logged and reported as an empty list, which the caller turns
            // into a stated note rather than silence.
            logger.LogError(ex, "Could not enumerate channels, so no site could be polled this pass.");
            return [];
        }

        var sites = new List<Guid>();

        foreach (var channel in channels)
        {
            if (TryReadSite(channel.ChannelUri, out var siteGuid) && !sites.Contains(siteGuid))
            {
                sites.Add(siteGuid);
            }
        }

        return sites;
    }

    /// <summary>
    /// The site a channel URI belongs to, if it is this engine's inbound channel
    /// for one.
    ///
    /// Matched by rebuilding the URI from the candidate GUID and comparing,
    /// rather than by parsing segments or a regular expression. That way the
    /// convention lives in exactly one place -- <see
    /// cref="RegLocationEngineOptions.InboundChannelUriFor"/> -- and a change to
    /// it cannot leave a matcher here silently recognising nothing.
    /// </summary>
    private bool TryReadSite(string? channelUri, out Guid siteGuid)
    {
        siteGuid = Guid.Empty;

        if (string.IsNullOrWhiteSpace(channelUri))
        {
            return false;
        }

        foreach (var segment in channelUri.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Guid.TryParse(segment, out var candidate))
            {
                continue;
            }

            if (string.Equals(
                    _options.InboundChannelUriFor(candidate),
                    channelUri,
                    StringComparison.OrdinalIgnoreCase))
            {
                siteGuid = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drains one site's engineering channel into the shared report.
    /// </summary>
    private async Task DrainSiteAsync(Guid siteGuid, IngestionReport report, CancellationToken ct)
    {
        // The subscriber end of the same declaration ENG publishes from, so the
        // two cannot drift: previously each derived this URI independently and
        // agreement rested on both settings files saying the same thing.
        var declared = await topology.FindSubscriptionAsync(
            _options.ScenarioId, _options.ParticipantId, siteGuid, ct);

        var channelUri = declared?.Uri ?? _options.InboundChannelUriFor(siteGuid);

        var topics = declared is { Topics.Count: > 0 }
            ? declared.Topics.ToArray()
            : _options.InboundTopics;

        // Scoped to the iTwin as well as the role, because this channel is
        // per-iTwin: a single 'segments' id would collide across twins and let
        // two engines consume each other's messages. Same durability argument as
        // the sites leg -- the id must survive a restart or the backlog is
        // abandoned with the subscription.
        var subscriberId = $"{_options.SourceId}:segments:{siteGuid:D}";

        if (!_sessions.TryGetValue(siteGuid, out var sessionId))
        {
            sessionId = await isbm.OpenSubscriptionSessionAsync(channelUri, topics, ct, subscriberId);
            _sessions[siteGuid] = sessionId;
        }

        while (report.MessagesRead < _options.MaxMessagesPerPoll)
        {
            IsbmMessage? message;

            try
            {
                message = await isbm.ReadPublicationAsync(sessionId, ct);
            }
            catch (IsbmException ex) when (ex.IsSessionProblem)
            {
                // The cached id names a subscription the broker no longer has --
                // normally because day zero deleted and recreated the channel
                // underneath it, which takes its subscriptions with it.
                //
                // Reopened once rather than thrown, because the id is the only
                // thing that is stale: the channel exists, the publication is
                // waiting on it, and failing here means this engine never reads
                // another message until someone restarts it. That is precisely
                // the silent stall this catch exists to end.
                logger.LogWarning(
                    ex,
                    "Inbound session {SessionId} is unusable ({Fault}); reopening on {Channel}.",
                    sessionId, ex.Fault ?? ex.Status.ToString(), channelUri);

                sessionId = await isbm.OpenSubscriptionSessionAsync(
                    channelUri, topics, ct, subscriberId);

                _sessions[siteGuid] = sessionId;
                report.SessionReopened = true;

                // Reopening loses whatever the dead subscription still held, so
                // the backlog published before this point is not recoverable
                // here. Reported rather than hidden -- see IngestionReport.
                message = await isbm.ReadPublicationAsync(sessionId, ct);
            }

            // Null is the broker saying the queue is empty, not an error.
            if (message is null)
            {
                break;
            }

            report.MessagesRead++;

            // Captured before handling so a deferral can be told apart from a
            // failure below. Comparing against the running total rather than a
            // flag would misread a deferral on an earlier site as one here.
            var deferralsBefore = report.SiteUnknown;

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
                // A deferral already counted itself as SiteUnknown, and it is not
                // a failure: the message is well-formed and will file itself once
                // the site arrives. Counting it as Failed too would report a fault
                // for the normal case of segments outrunning their SyncSites.
                if (report.SiteUnknown == deferralsBefore)
                {
                    report.Failed++;
                }

                // Stop rather than skip ahead. ISBM delivers in order, and a
                // reader that steps over a failure would process later segments
                // that may depend on it. Only this site's channel stops -- the
                // remaining sites are independent and are still polled.
                break;
            }

            // Removed only now, after every segment in it has been filed. A crash
            // before this point redelivers the message, which is why the GUID
            // check exists: reprocessing must be harmless.
            await isbm.RemovePublicationAsync(sessionId, ct);
        }
    }

    /// <summary>
    /// Files every segment in one publication. Returns false when the message
    /// should stay on the channel.
    /// </summary>
    private async Task<bool> HandleMessageAsync(IsbmMessage message, IngestionReport report, CancellationToken ct)
    {
        // Set when a segment named a site REG-LOCATION holds no scope for. It
        // decides the return value below, because a deferral is the one outcome
        // that is neither success nor failure: nothing was filed, and nothing is
        // wrong with the message.
        var deferred = false;

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
                    deferred = true;

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
            // The newest revision on file, which is what the sender's publication
            // is really being compared against. Redelivery is judged against this
            // rather than against the revision slot the segment arrived in: once a
            // correction has been filed as revision 2, the same publication
            // arriving again still names revision 0, and matching only that slot
            // would find the superseded row, see a difference, and mint revision
            // 3 -- then 4, on every poll thereafter.
            var latest = existing.Count == 0
                ? null
                : existing.MaxBy(t => t.Tag.Revision);

            if (latest is not null)
            {
                // Identical to what is already on file -- a redelivery, not a
                // correction. Writing it again would just restamp date_changed
                // for no reason, and would count as a false correction.
                var unchanged =
                    latest.Tag.ClassId == request.ClassId &&
                    string.Equals(latest.Tag.Code, request.Code, StringComparison.Ordinal) &&
                    string.Equals(latest.Tag.Name, request.Name, StringComparison.Ordinal);

                if (unchanged)
                {
                    report.AlreadyKnown++;

                    logger.LogDebug(
                        "Segment {Guid} matches tag {TagId} rev {Revision} already on file; left untouched.",
                        request.Guid, latest.Tag.TagId, latest.Tag.Revision);

                    continue;
                }

                if (!string.Equals(latest.Tag.State, RegTagStates.Proposed, StringComparison.OrdinalIgnoreCase))
                {
                    // A steward has already decided this revision, so it is never
                    // overwritten: doing so would let an unreviewed correction
                    // masquerade as the decision that was actually made, and would
                    // erase the record of what was approved.
                    //
                    // The correction is filed as a NEW revision instead, carrying
                    // the same federation GUID -- the GUID identifies the location
                    // and deliberately survives revision, so several rows sharing
                    // one is the intended shape rather than a duplicate. It enters
                    // Proposed, which puts the edit in front of a steward instead
                    // of past one.
                    var next = request with
                    {
                        Revision = latest.Tag.Revision + 1,
                        State = RegTagStates.Proposed
                    };

                    var superseding = await regLocation.CreateTagAsync(next, ct);
                    report.TagsProposed++;

                    logger.LogInformation(
                        "Segment {Guid} corrects tag {PriorTagId}, which is already {State}; " +
                        "filed as tag {TagId} '{Code}' rev {Revision} awaiting approval [{CorrelationId}].",
                        request.Guid, latest.Tag.TagId, latest.Tag.State,
                        superseding.Tag.TagId, superseding.Tag.Code, superseding.Tag.Revision,
                        envelope.BodId);

                    continue;
                }

                var corrected = await regLocation.UpdateTagAsync(
                    latest.Tag.TagId,
                    new UpdateTagRequest(request.ClassId, request.Code, latest.Tag.Revision, request.Name),
                    ct);

                if (corrected is null)
                {
                    // Removed from the registry between the lookup and the write --
                    // an ordinary race, not a fault. Falling through would try to
                    // create it fresh below.
                    logger.LogDebug(
                        "Tag {TagId} for {Guid} vanished before its correction could be applied; proposing anew.",
                        latest.Tag.TagId, request.Guid);
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

            var created = await regLocation.CreateTagAsync(request, ct);
            report.TagsProposed++;

            logger.LogInformation(
                "Proposed tag {TagId} '{Code}' from {Guid} [{CorrelationId}].",
                created.Tag.TagId, created.Tag.Code, request.Guid, envelope.BodId);
        }

        // False when any segment was deferred, which keeps the publication on the
        // channel. Returning true here was the defect behind "ENG published but
        // nothing arrived": the caller removes a handled message, so a segment
        // that arrived before its SyncSites was logged as deferred and then
        // destroyed, and no later drain could ever recover it.
        return !deferred;
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
