namespace Oiie.Isbm.Abstractions;

/// <summary>
/// Durable record of open sessions so a restart can resume rather than leak
/// them. Partitioned by participant — each Sandbox participant is an
/// independent ISBM client with its own credentials.
/// </summary>
public interface IIsbmSessionStore
{
    Task<IsbmSession?> FindAsync(
        string participantId,
        IsbmSessionKind kind,
        string channelUri,
        CancellationToken ct = default);

    Task SaveAsync(string participantId, IsbmSession session, CancellationToken ct = default);

    Task<IReadOnlyList<IsbmSession>> ListAsync(string participantId, CancellationToken ct = default);

    Task RemoveAsync(string participantId, string sessionId, CancellationToken ct = default);

    /// <summary>Cursor of the last message id handled, for dedup across restarts.</summary>
    Task<string?> GetCursorAsync(string participantId, string sessionId, CancellationToken ct = default);

    Task SetCursorAsync(string participantId, string sessionId, string messageId, CancellationToken ct = default);
}
