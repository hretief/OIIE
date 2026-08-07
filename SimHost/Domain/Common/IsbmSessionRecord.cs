namespace SimHost.Domain.Common;

/// <summary>
/// Durable record of an open ISBM session, so a restart resumes rather than leaks.
///
/// The read cursor lives here rather than in a separate table: it is one value per
/// session, always written in the same operation that consumes a message, and a
/// second table would add a join to the hottest path in the inbox for no benefit.
/// </summary>
public class IsbmSessionRecord
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Publication, Subscription, ConsumerRequest, ProviderRequest.</summary>
    public string Kind { get; set; } = string.Empty;

    public string ChannelUri { get; set; } = string.Empty;

    /// <summary>JSON array. Empty for publication and consumer-request sessions.</summary>
    public string Topics { get; set; } = "[]";

    /// <summary>Set when the session uses push delivery rather than polling.</summary>
    public string? ListenerUri { get; set; }

    /// <summary>
    /// Last ISBM message id handled on this session. Deduplicates across restarts,
    /// where the provider may re-present a message that was read but not removed.
    /// </summary>
    public string? LastMessageId { get; set; }

    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastReadAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => ClosedAt is null;
}
