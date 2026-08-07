using System.Collections.Concurrent;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Process-local session registry. Tracks sessions for notification dispatch.
/// TODO: back with Azure SQL for persistence across restarts and scale-out.
/// </summary>
public sealed class InMemorySessionRegistry : ISessionRegistry
{
    private readonly ConcurrentDictionary<string, SessionMetadata> _sessions = new();

    public void Register(SessionMetadata session) => _sessions[session.SessionId] = session;

    public void Unregister(string sessionId) => _sessions.TryRemove(sessionId, out _);

    public SessionMetadata? GetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public IReadOnlyList<SessionMetadata> GetNotifiableSessions(string channelUri)
        => _sessions.Values
            .Where(s => s.ChannelUri == channelUri && !string.IsNullOrEmpty(s.ListenerUrl))
            .ToList();

    public IReadOnlyList<SessionMetadata> GetExpirableSessions(string channelUri)
        => _sessions.Values
            .Where(s => s.ChannelUri == channelUri && !string.IsNullOrEmpty(s.ExpirationListenerUrl))
            .ToList();
}
