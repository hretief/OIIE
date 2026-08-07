using IsbmProvider.Models;

namespace IsbmProvider.Abstractions;

/// <summary>
/// Tracks active sessions so the notification pipeline and session validation can look up
/// sessions. Populated at session-open time, removed at session-close time.
/// </summary>
public interface ISessionRegistry
{
    void Register(SessionMetadata session);
    void Unregister(string sessionId);

    /// <summary>Get a specific session by ID (for session validation).</summary>
    SessionMetadata? GetSession(string sessionId);

    /// <summary>All active sessions on a channel that have a ListenerURL (for NotifyListener).</summary>
    IReadOnlyList<SessionMetadata> GetNotifiableSessions(string channelUri);

    /// <summary>All active sessions on a channel that have an ExpirationListenerURL.</summary>
    IReadOnlyList<SessionMetadata> GetExpirableSessions(string channelUri);
}
