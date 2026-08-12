using Microsoft.EntityFrameworkCore;
using Oiie.Isbm.Client;
using SimHost.Application.Bods;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Blob;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Outbox;

/// <summary>
/// Global and per-participant pause. Pausing lets several changes be entered and
/// then released as a visible burst, which is the demo pacing control — and it
/// also proves the outbox is real rather than decorative (spec §6.3).
/// </summary>
public sealed class DispatcherControl
{
    private readonly HashSet<string> _pausedParticipants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public bool PausedGlobally { get; private set; }

    public void PauseAll() { lock (_gate) { PausedGlobally = true; } }

    public void ResumeAll() { lock (_gate) { PausedGlobally = false; _pausedParticipants.Clear(); } }

    public void Pause(string participantId)
    {
        lock (_gate) { _pausedParticipants.Add(participantId); }
    }

    public void Resume(string participantId)
    {
        lock (_gate) { _pausedParticipants.Remove(participantId); }
    }

    public bool IsPaused(string participantId)
    {
        lock (_gate) { return PausedGlobally || _pausedParticipants.Contains(participantId); }
    }
}

public sealed class OutboxDispatcher : BackgroundService
{
    private readonly ParticipantRegistry _registry;
    private readonly IParticipantDbContextFactory _dbFactory;
    private readonly IIsbmClientAccessor _isbm;
    private readonly IIsbmSessionStoreAccessor _sessions;
    private readonly IPayloadStore _payloads;
    private readonly IEnumerable<IBodBuilder> _builders;
    private readonly DispatcherControl _control;
    private readonly ILogger<OutboxDispatcher> _logger;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int MaxAttempts = 5;

    public OutboxDispatcher(
        ParticipantRegistry registry,
        IParticipantDbContextFactory dbFactory,
        IIsbmClientAccessor isbm,
        IIsbmSessionStoreAccessor sessions,
        IPayloadStore payloads,
        IEnumerable<IBodBuilder> builders,
        DispatcherControl control,
        ILogger<OutboxDispatcher> logger)
    {
        _registry = registry;
        _dbFactory = dbFactory;
        _isbm = isbm;
        _sessions = sessions;
        _payloads = payloads;
        _builders = builders;
        _control = control;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var participant in _registry.All)
            {
                if (_control.IsPaused(participant.ParticipantId))
                {
                    continue;
                }

                try
                {
                    await DrainAsync(participant, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Outbox drain failed for participant {ParticipantId}",
                        participant.ParticipantId);
                }
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DrainAsync(ParticipantContext participant, CancellationToken ct)
    {
        await using var db = _dbFactory.Create(participant.ParticipantId);

        var pending = await db.Outbox
            .Where(o => o.State == OutboxState.Pending && o.Attempts < MaxAttempts)
            .OrderBy(o => o.CreatedAt)
            .Take(20)
            .ToListAsync(ct);

        foreach (var item in pending)
        {
            await PostAsync(participant, db, item, ct);
        }
    }

    private async Task PostAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        OutboxItem item,
        CancellationToken ct)
    {
        item.State = OutboxState.Building;
        item.Attempts++;
        await db.SaveChangesAsync(ct);

        try
        {
            // A participant-specific builder wins over a generic one, so a shared
            // fallback can coexist with personality-specific mappings.
            var candidates = _builders
                .Where(b => b.Handles.Verb == item.Verb && b.Handles.Noun == item.Noun)
                .ToList();

            var builder = candidates.FirstOrDefault(b =>
                    string.Equals(b.ParticipantId, participant.ParticipantId, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(b => b.ParticipantId is null)
                ?? throw new InvalidOperationException(
                    $"No BOD builder registered for {item.Verb}{item.Noun}.");

            // The builder reads through a context scoped to the item's twin, so entity
            // keys resolve within the plant they were published from -- two twins may
            // use the same key and the builder has no way to tell them apart.
            //
            // Only the builder gets the scoped context. Outbox state stays on `db`,
            // which is tracking `item`; saving it through a second context would
            // silently persist nothing.
            await using var reads = item.ITwinId == Guid.Empty
                ? null
                : _dbFactory.Create(participant.ParticipantId, item.ITwinId);

            var document = await builder.BuildAsync(participant, reads ?? db, item, ct);
            var xml = document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);

            var client = _isbm.For(participant.ParticipantId);
            var content = document.Root
                ?? throw new InvalidOperationException("Built BOD has no root element.");
            IReadOnlyList<string> topics = item.Topic is null ? [] : [item.Topic];

            var kind = item.Pattern == MessagePattern.Publication
                ? IsbmSessionKind.Publication
                : IsbmSessionKind.ConsumerRequest;

            // Through the session manager rather than opening directly, so a stored
            // session the provider no longer recognises is discarded and re-opened
            // instead of failing every attempt until the item is abandoned.
            //
            // Stale ids are routine here, not exceptional: deleting a channel
            // invalidates its sessions, and reset does that on every run.
            string sessionId = string.Empty;

            var isbmMessageId = await _sessions.Manager.WithSessionAsync(
                participant.ParticipantId,
                kind,
                item.ChannelUri,
                topics,
                async (session, token) =>
                {
                    sessionId = session;

                    return kind == IsbmSessionKind.Publication
                        ? await client.PostPublicationAsync(session, content, topics, null, token)
                        : await client.PostRequestAsync(session, content, topics, null, token);
                },
                ct);

            var contentRef = await _payloads.SaveAsync(
                participant.ParticipantId, item.CorrelationId, xml, ct);

            var record = new MessageRecord
            {
                Direction = MessageDirection.Outbound,
                Pattern = item.Pattern,
                ChannelUri = item.ChannelUri,
                Topic = item.Topic,
                Verb = item.Verb,
                Noun = item.Noun,
                BodId = item.CorrelationId,
                IsbmMessageId = isbmMessageId,
                IsbmSessionId = sessionId,
                ScenarioRunId = item.ScenarioRunId,
                CorrelationId = item.CorrelationId,
                ContentRef = contentRef,
                ContentBytes = System.Text.Encoding.UTF8.GetByteCount(xml),
                ProcessingStatus = ProcessingStatus.Applied,
                OccurredAt = DateTimeOffset.UtcNow
            };

            db.Messages.Add(record);

            item.State = OutboxState.Posted;
            item.MessageId = record.MessageId;
            item.PostedAt = DateTimeOffset.UtcNow;
            item.LastError = null;

            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Posted {Verb}{Noun} from {ParticipantId} to {ChannelUri} [{CorrelationId}]",
                item.Verb, item.Noun, participant.ParticipantId, item.ChannelUri, item.CorrelationId);
        }
        catch (Exception ex)
        {
            item.LastError = ex.Message;
            item.State = item.Attempts >= MaxAttempts ? OutboxState.Failed : OutboxState.Pending;
            await db.SaveChangesAsync(ct);

            _logger.LogWarning(ex,
                "Outbox item {Id} failed (attempt {Attempts}) for {ParticipantId}",
                item.Id, item.Attempts, participant.ParticipantId);
        }
    }
}
