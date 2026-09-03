namespace IsbmProvider.Models;

/// <summary>Immutable identity/config of a session, set at Open* time.</summary>
public sealed record SessionMetadata
{
    public required string SessionId { get; init; }
    public required string ChannelUri { get; init; }
    public required SessionType SessionType { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Stable identity of the *subscriber*, as opposed to this one session.
    /// </summary>
    /// <remarks>
    /// A session id is minted per Open call and lives as long as the caller
    /// remembers it -- which, for a Functions host caching it in a field, means
    /// until the next restart. Naming the Service Bus subscription after it
    /// therefore abandons a subscription per process lifetime, and because a
    /// topic fans out to every subscription that exists when a message is
    /// published, those abandoned subscriptions keep receiving copies nothing
    /// will ever read. Eighteen such subscriptions had accumulated 306 messages
    /// before this was found.
    ///
    /// A subscriber id is supplied by the consumer and does not change across
    /// restarts, so reopening finds the same subscription with its backlog
    /// intact. It identifies a role on a channel -- "the REG-LOCATION site
    /// ingester" -- not a process or an instance.
    ///
    /// Optional: when absent the session id is used, preserving the old
    /// ephemeral behaviour for callers that genuinely want a throwaway
    /// subscription, such as a one-off probe.
    /// </remarks>
    public string? SubscriberId { get; init; }

    public string? ListenerUrl { get; init; }
    public string? ExpirationListenerUrl { get; init; }
    /// <summary>XPath 1.0 / JSONPath body filters (subscription sessions).</summary>
    public IReadOnlyList<string> FilterExpressions { get; init; } = Array.Empty<string>();
    /// <summary>XPath namespace prefixes -> URIs, from the subscription's XPathNamespace pairs.</summary>
    public IReadOnlyDictionary<string, string> FilterNamespaces { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Durable-entity state for a session: the authoritative-in-memory read cursor.
/// Read/removed tracking here is what enforces ISBM read-then-remove and the
/// "expired-but-already-read stays visible to that reader" rule. Mirrored to Azure SQL.
/// </summary>
public sealed class SessionState
{
    public SessionMetadata? Metadata { get; set; }
    public bool IsOpen { get; set; }
    /// <summary>MessageIds this session has Read but not yet Removed.</summary>
    public HashSet<string> ReadNotRemoved { get; set; } = new();
    /// <summary>MessageIds this session has Removed (idempotency + audit).</summary>
    public HashSet<string> Removed { get; set; } = new();
}
