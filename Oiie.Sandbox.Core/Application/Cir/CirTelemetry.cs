using Microsoft.EntityFrameworkCore;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Cir;

/// <summary>
/// The last CIR exchange per participant, kept in memory.
///
/// When a provider consumes a request and discards it, the only thing that moves
/// the conversation forward is the exact document that was sent — not a description
/// of it. Reconstructing the XML from a description has cost this project real time
/// twice; keeping the literal bytes costs nothing.
/// </summary>
public sealed record CirExchange
{
    /// <summary>
    /// Stable across the three points that persist this exchange, so the post, the
    /// answer and the timeout all update one row rather than appending three.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string ParticipantId { get; init; }
    public required string Bod { get; init; }
    public required string CorrelationId { get; init; }
    public required string ChannelUri { get; init; }
    public required string Topic { get; init; }
    public required string RequestXml { get; init; }

    public string? RequestMessageId { get; set; }

    /// <summary>
    /// The consumer session the request was posted on, and the one the response is
    /// awaited on. The provider posts to its own session keyed on the request message
    /// id, so if a response is written but never readable, these two ids are what the
    /// two sides need to compare.
    /// </summary>
    public string? ConsumerSessionId { get; set; }

    /// <summary>How long the response was waited for before giving up.</summary>
    public int? WaitedSeconds { get; set; }
    public string? ResponseXml { get; set; }
    public string? ResponseVerb { get; set; }
    public IReadOnlyList<string> Faults { get; set; } = [];
    public string? Outcome { get; set; }

    public DateTimeOffset SentUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AnsweredUtc { get; set; }
}

/// <summary>
/// Records CIR exchanges to the participant's own schema, with an in-process copy
/// as a fast path.
///
/// Memory alone was not enough: the Sandbox runs on App Service, and an instance
/// recycle or a scale-out between the registration and the request for evidence
/// leaves the diagnostic endpoint reporting blanks. A blank reads like "nothing was
/// sent", which is the one conclusion the record exists to rule out.
/// </summary>
public sealed class CirTelemetry(
    IParticipantDbContextFactory factory,
    ILogger<CirTelemetry> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CirExchange> _last = new();

    /// <summary>
    /// Upserts the exchange. Called more than once per round trip, as the message id,
    /// then the outcome, become known.
    ///
    /// Never throws: this observes an exchange and must not be able to fail it. A
    /// telemetry write that broke a registration would be worse than the blind spot
    /// it was added to remove.
    /// </summary>
    public async Task SaveAsync(CirExchange exchange, CancellationToken ct = default)
    {
        _last[exchange.ParticipantId] = exchange;

        try
        {
            await using var db = factory.Create(exchange.ParticipantId);

            var row = await db.CirExchanges.FirstOrDefaultAsync(e => e.Id == exchange.Id, ct);

            if (row is null)
            {
                row = new CirExchangeRecord { Id = exchange.Id };
                db.CirExchanges.Add(row);
            }

            row.ParticipantId = exchange.ParticipantId;
            row.Bod = exchange.Bod;
            row.CorrelationId = exchange.CorrelationId;
            row.ChannelUri = exchange.ChannelUri;
            row.Topic = exchange.Topic;
            row.RequestXml = exchange.RequestXml;
            row.RequestMessageId = exchange.RequestMessageId;
            row.ConsumerSessionId = exchange.ConsumerSessionId;
            row.WaitedSeconds = exchange.WaitedSeconds;
            row.ResponseXml = exchange.ResponseXml;
            row.ResponseVerb = exchange.ResponseVerb;
            row.Outcome = exchange.Outcome;
            row.SentUtc = exchange.SentUtc;
            row.AnsweredUtc = exchange.AnsweredUtc;
            row.FaultsJson = exchange.Faults.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(exchange.Faults)
                : null;

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not persist the CIR exchange for {ParticipantId} [{CorrelationId}]. " +
                "The in-memory copy stands, but it will not survive a restart.",
                exchange.ParticipantId, exchange.CorrelationId);
        }
    }

    /// <summary>
    /// The most recent exchanges for a participant, newest first, read from its own
    /// schema so a restart between the request and this call does not erase them.
    /// </summary>
    public async Task<IReadOnlyList<CirExchangeRecord>> RecentAsync(
        string participantId, int take = 5, CancellationToken ct = default)
    {
        try
        {
            await using var db = factory.Create(participantId);

            return await db.CirExchanges
                .AsNoTracking()
                .OrderByDescending(e => e.SentUtc)
                .Take(take)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not read CIR exchanges for {ParticipantId}.", participantId);
            return [];
        }
    }

    /// <summary>In-process copy only. Used by the UI, which is on the same instance.</summary>
    public CirExchange? For(string participantId) =>
        _last.TryGetValue(participantId, out var exchange) ? exchange : null;

    public IReadOnlyCollection<CirExchange> All => _last.Values.ToList();
}
