namespace IsbmProvider.Models;

/// <summary>Immutable identity/config of a session, set at Open* time.</summary>
public sealed record SessionMetadata
{
    public required string SessionId { get; init; }
    public required string ChannelUri { get; init; }
    public required SessionType SessionType { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
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
