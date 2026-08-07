using Oiie.Ccom;
using Oiie.Ccom.Oagis;
using Oiie.Isbm.Client;
using SimHost.Application.Bods;
using SimHost.Application.Participants;
using SimHost.Application.Scenarios;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Blob;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Inbox;

/// <summary>
/// What the pump last did on one binding.
///
/// Without this, "the subscription is open and nothing arrived" is the whole
/// picture, and it cannot distinguish a pump that is polling and finding nothing
/// from one that is not polling at all, or is failing every read. Those have
/// different owners.
/// </summary>
public sealed record InboxBindingStatus
{
    public required string ParticipantId { get; init; }
    public required string ChannelUri { get; init; }
    public required IReadOnlyList<string> Topics { get; init; }

    public DateTimeOffset? LastPollUtc { get; set; }
    public DateTimeOffset? LastMessageUtc { get; set; }
    public string? SessionId { get; set; }

    public long Polls { get; set; }
    public long EmptyReads { get; set; }
    public long MessagesRead { get; set; }
    public long Failures { get; set; }

    public string? LastError { get; set; }
    public DateTimeOffset? LastErrorUtc { get; set; }
}

/// <summary>Shared so diagnostics can read it without holding the pump.</summary>
public sealed class InboxTelemetry
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, InboxBindingStatus> _bindings = new();

    public InboxBindingStatus For(string participantId, string channelUri, IReadOnlyList<string> topics) =>
        _bindings.GetOrAdd($"{participantId}|{channelUri}", _ => new InboxBindingStatus
        {
            ParticipantId = participantId,
            ChannelUri = channelUri,
            Topics = topics
        });

    public IReadOnlyCollection<InboxBindingStatus> All => _bindings.Values.ToList();
}

/// <summary>
/// Polls each participant's subscription channels, archives what arrives, and
/// dispatches it to a handler.
///
/// A message with no registered handler is still archived and marked Pending rather
/// than dropped: a participant is expected to receive BODs it cannot act on, and
/// the wire view has to show them or the ecosystem looks like it is losing traffic.
/// </summary>
public sealed class InboxPump(
    InboxTelemetry telemetry,
    ParticipantRegistry registry,
    IParticipantDbContextFactory dbFactory,
    IsbmSessionManager sessions,
    IIsbmClientAccessor clients,
    IIsbmSessionStoreAccessor stores,
    IPayloadStore payloads,
    BodValidator validator,
    IEnumerable<IBodHandler> handlers,
    ScenarioRunContext runContext,
    ILogger<InboxPump> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private const int MaxMessagesPerPoll = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var participant in registry.All)
            {
                foreach (var binding in participant.Config.Channels
                             .Where(c => c.Role == ChannelRole.Subscriber))
                {
                    var status = telemetry.For(
                        participant.ParticipantId, binding.ChannelUri, binding.Topics);

                    status.Polls++;
                    status.LastPollUtc = DateTimeOffset.UtcNow;

                    try
                    {
                        await DrainAsync(participant, binding, status, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        status.Failures++;

                        // EF wraps the useful part: the outer message is only ever
                        // "See the inner exception for details", which is not details.
                        status.LastError = string.Join(" -> ",
                            Flatten(ex).Select(e => $"{e.GetType().Name}: {e.Message}"));
                        status.LastErrorUtc = DateTimeOffset.UtcNow;

                        logger.LogError(ex,
                            "Inbox drain failed for {ParticipantId} on {ChannelUri}",
                            participant.ParticipantId, binding.ChannelUri);
                    }
                }
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DrainAsync(
        ParticipantContext participant,
        ChannelBinding binding,
        InboxBindingStatus status,
        CancellationToken ct)
    {
        var client = clients.For(participant.ParticipantId);
        var store = stores.For(participant.ParticipantId);

        // Read through the session manager, not directly.
        //
        // A subscription can die under a running pump — the channel is recreated, the
        // provider is redeployed — and the read then fails with a Session fault. Read
        // directly and that fault propagates as an error every three seconds forever;
        // read through the manager and the session is discarded and re-opened once.
        var sessionId = string.Empty;

        for (var i = 0; i < MaxMessagesPerPoll; i++)
        {
            var message = await sessions.WithSessionAsync(
                participant.ParticipantId,
                IsbmSessionKind.Subscription,
                binding.ChannelUri,
                binding.Topics,
                async (session, token) =>
                {
                    sessionId = session;
                    return await client.ReadPublicationAsync(session, token);
                },
                ct);

            status.SessionId = sessionId;

            if (message is null)
            {
                status.EmptyReads++;
                return;
            }

            status.MessagesRead++;
            status.LastMessageUtc = DateTimeOffset.UtcNow;

            var lastHandled = await store.GetCursorAsync(sessionId, ct);
            if (lastHandled == message.MessageId)
            {
                // Read but not removed on a previous run. Removing it here rather
                // than reprocessing is what stops a restart from duplicating work.
                logger.LogDebug("Skipping already-handled message {MessageId}", message.MessageId);
                await client.RemovePublicationAsync(sessionId, ct);
                continue;
            }

            await HandleAsync(participant, binding, sessionId, message, ct);

            await store.SetCursorAsync(sessionId, message.MessageId, ct);
            await client.RemovePublicationAsync(sessionId, ct);
        }
    }

    private async Task HandleAsync(
        ParticipantContext participant,
        ChannelBinding binding,
        string sessionId,
        IsbmMessage message,
        CancellationToken ct)
    {
        await using var db = dbFactory.Create(participant.ParticipantId);

        var record = new MessageRecord
        {
            Direction = MessageDirection.Inbound,
            Pattern = MessagePattern.Publication,
            ChannelUri = binding.ChannelUri,
            IsbmMessageId = message.MessageId,
            IsbmSessionId = sessionId,
            Topic = message.Topics.FirstOrDefault(),
            ContentBytes = System.Text.Encoding.UTF8.GetByteCount(message.RawContent),

            // Attributed to whichever run is in flight when the message is archived.
            //
            // The run id is not on the wire, so this is the receiving end guessing from
            // local state rather than reading an identifier the sender set. It holds
            // because only one scenario runs at a time, and a scenario's own traffic is
            // what arrives while it runs. It does not hold for a message that crosses a
            // run boundary — a delayed or replayed publication is attributed to whatever
            // is running when it lands, or to nothing. Scenarios that deliberately
            // exercise expiry, duplicate delivery or abandoned sessions will need the run
            // id carried on the envelope instead.
            ScenarioRunId = runContext.CurrentRunId,

            OccurredAt = DateTimeOffset.UtcNow
        };

        if (message.Content is null)
        {
            // Archived deliberately. An unparseable message that vanishes looks
            // identical to one that was never sent.
            record.Verb = "Unknown";
            record.Noun = "Unknown";
            record.BodId = message.MessageId;
            record.CorrelationId = message.MessageId;
            record.ValidationStatus = nameof(BodValidationStatus.Invalid);
            record.ValidationDetail = "Payload could not be parsed as XML.";
            record.ProcessingStatus = ProcessingStatus.Failed;
            record.ContentRef = await SafeStoreAsync(participant, message.MessageId, message.RawContent, ct);

            db.Messages.Add(record);
            await db.SaveChangesAsync(ct);
            return;
        }

        var document = new System.Xml.Linq.XDocument(message.Content);
        BodEnvelope envelope;

        try
        {
            envelope = BodEnvelope.Parse(document);
        }
        catch (BodFormatException ex)
        {
            record.Verb = message.Content.Name.LocalName;
            record.Noun = "Unknown";
            record.BodId = message.MessageId;
            record.CorrelationId = message.MessageId;
            record.ValidationStatus = nameof(BodValidationStatus.Invalid);
            record.ValidationDetail = ex.Message;
            record.ProcessingStatus = ProcessingStatus.Failed;
            record.ContentRef = await SafeStoreAsync(participant, message.MessageId, message.RawContent, ct);

            db.Messages.Add(record);
            await db.SaveChangesAsync(ct);
            return;
        }

        record.Verb = envelope.Verb;
        record.Noun = envelope.Noun;
        record.BodId = envelope.BodId ?? message.MessageId;

        // The correlation id travels in BODID, so one query reconstructs the whole
        // exchange across participants and providers.
        record.CorrelationId = envelope.BodId ?? message.MessageId;

        var validation = validator.Validate(document);
        record.ValidationStatus = validation.Status.ToString();
        record.ValidationDetail = validation.Detail;

        record.ContentRef = await SafeStoreAsync(
            participant, record.CorrelationId, message.RawContent, ct);

        db.Messages.Add(record);
        await db.SaveChangesAsync(ct);

        var candidates = handlers
            .Where(h => h.Handles.Verb == envelope.Verb && h.Handles.Noun == envelope.Noun)
            .ToList();

        var handler = candidates.FirstOrDefault(h =>
                string.Equals(h.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(h => h.ParticipantId is null);

        if (handler is null)
        {
            var result = BodHandlingResult.NoHandler(envelope.Verb, envelope.Noun);
            record.ProcessingStatus = result.Status;
            record.ProcessingDetail = result.Detail;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "{ParticipantId} received {Verb}{Noun} with no handler [{CorrelationId}]",
                participant.ParticipantId, envelope.Verb, envelope.Noun, record.CorrelationId);
            return;
        }

        try
        {
            var result = await handler.HandleAsync(participant, db, envelope, record.MessageId, ct);
            record.ProcessingStatus = result.Status;
            record.ProcessingDetail = result.Detail;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "{ParticipantId} handled {Verb}{Noun}: {Entities} entities, {Mapped} mapped, " +
                "{Unmapped} unmapped [{CorrelationId}]",
                participant.ParticipantId, envelope.Verb, envelope.Noun,
                result.EntitiesAffected, result.PropertiesMapped, result.PropertiesUnmapped,
                record.CorrelationId);
        }
        catch (Exception ex)
        {
            record.ProcessingStatus = ProcessingStatus.Failed;
            record.ProcessingDetail = ex.Message;
            await db.SaveChangesAsync(ct);

            logger.LogError(ex,
                "{ParticipantId} failed handling {Verb}{Noun} [{CorrelationId}]",
                participant.ParticipantId, envelope.Verb, envelope.Noun, record.CorrelationId);
        }
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    /// <summary>
    /// A storage failure must not lose the message. The archive row is worth more
    /// than the payload body, and the reference records why the body is missing.
    /// </summary>
    private async Task<string> SafeStoreAsync(
        ParticipantContext participant, string correlationId, string content, CancellationToken ct)
    {
        try
        {
            return await payloads.SaveAsync(participant.ParticipantId, correlationId, content, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to store payload for {CorrelationId}", correlationId);
            return $"unstored:{ex.GetType().Name}";
        }
    }
}
