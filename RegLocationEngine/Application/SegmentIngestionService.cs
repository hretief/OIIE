using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;
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
    IOptions<RegLocationEngineOptions> options,
    ILogger<SegmentIngestionService> logger)
{
    private readonly RegLocationEngineOptions _options = options.Value;

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

        if (_options.IModelId == Guid.Empty)
        {
            report.Note = "IModelId is not configured, so the inbound channel cannot be derived.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        if (string.IsNullOrWhiteSpace(_options.RegLocationBaseUrl))
        {
            report.Note = "RegLocationBaseUrl is not configured, so proposals cannot be filed.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        var channelUri = _options.InboundChannelUriFor(_options.IModelId);
        _sessionId ??= await isbm.OpenSubscriptionSessionAsync(channelUri, _options.InboundTopics, ct);

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

            // The registry is asked, rather than engine state consulted. First
            // proposal wins: a location the registry already holds is left
            // exactly as it is, whatever state it is in. That is what makes a
            // redelivered message harmless, and it means a re-publication can
            // never reopen a decision a steward has already made.
            var existing = await regLocation.FindTagsByGuidAsync(request.Guid!.Value, ct);

            if (existing.Count > 0)
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
}
