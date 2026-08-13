using Microsoft.EntityFrameworkCore;
using Oiie.Isbm.Client;
using SimHost.Domain.Common;
using SimHost.Infrastructure.Sql;

namespace SimHost.Infrastructure.Isbm;

/// <summary>
/// Persists ISBM sessions for one participant.
///
/// Partitioning is by schema rather than by a ParticipantId column: each
/// participant's sessions live in its own database schema, reached through its own
/// contained user. A shared table would let one participant read another's session
/// ids, which is exactly the coupling the isolation model exists to prevent.
///
/// Sessions are recorded so a restart resumes rather than leaks them. An ISBM
/// session left open holds provider-side state indefinitely, and a simulator that
/// leaks one per restart degrades the very provider it is meant to exercise.
/// </summary>
public sealed class SqlIsbmSessionStore(
    string participantId,
    IParticipantDbContextFactory factory,
    ILogger logger) : IIsbmSessionStore
{
    public async Task<string?> GetAsync(
        IsbmSessionKind kind, string channelUri, CancellationToken ct = default)
    {
        await using var db = factory.Create(participantId);

        return await db.IsbmSessions
            .Where(s => s.Kind == kind.ToString() && s.ChannelUri == channelUri && s.ClosedAt == null)
            .OrderByDescending(s => s.OpenedAt)
            .Select(s => s.SessionId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SaveAsync(
        IsbmSessionKind kind, string channelUri, string sessionId, CancellationToken ct = default)
    {
        await using var db = factory.Create(participantId);

        var record = await db.IsbmSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        if (record is null)
        {
            record = new IsbmSessionRecord { SessionId = sessionId };
            db.IsbmSessions.Add(record);
        }

        record.Kind = kind.ToString();
        record.ChannelUri = channelUri;
        record.ClosedAt = null;

        await db.SaveChangesAsync(ct);

        logger.LogDebug(
            "Saved {Kind} session {SessionId} for {ParticipantId} on {ChannelUri}",
            kind, sessionId, participantId, channelUri);
    }

    /// <summary>
    /// Marks sessions of a kind closed rather than deleting the rows. Reset clears
    /// the schema anyway, and until then the record distinguishes an abandoned
    /// session from one that was never opened — which matters when diagnosing a
    /// provider still holding state the simulator has forgotten.
    /// </summary>
    public async Task ClearAsync(IsbmSessionKind kind, CancellationToken ct = default)
    {
        await using var db = factory.Create(participantId);

        var open = await db.IsbmSessions
            .Where(s => s.Kind == kind.ToString() && s.ClosedAt == null)
            .ToListAsync(ct);

        foreach (var record in open)
        {
            record.ClosedAt = DateTimeOffset.UtcNow;
        }

        if (open.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogDebug(
                "Closed {Count} {Kind} session(s) for {ParticipantId}",
                open.Count, kind, participantId);
        }
    }

    public async Task<IReadOnlyList<(IsbmSessionKind Kind, string ChannelUri, string SessionId, DateTimeOffset OpenedUtc)>>
        ListAsync(CancellationToken ct = default)
    {
        await using var db = factory.Create(participantId);

        var records = await db.IsbmSessions
            .Where(s => s.ClosedAt == null)
            .OrderBy(s => s.OpenedAt)
            .ToListAsync(ct);

        return records
            .Select(r => (Enum.Parse<IsbmSessionKind>(r.Kind), r.ChannelUri, r.SessionId, r.OpenedAt))
            .ToList();
    }

    /// <summary>Last message id handled on a session, for dedup across restarts.</summary>
    public async Task<string?> GetCursorAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = factory.Create(participantId);

        return await db.IsbmSessions
            .Where(s => s.SessionId == sessionId)
            .Select(s => s.LastMessageId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task SetCursorAsync(string sessionId, string messageId, CancellationToken ct = default)
    {
        await using var db = factory.Create(participantId);

        var record = await db.IsbmSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (record is null)
        {
            logger.LogWarning(
                "Cursor set for unknown session {SessionId} on {ParticipantId}", sessionId, participantId);
            return;
        }

        record.LastMessageId = messageId;
        record.LastReadAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
