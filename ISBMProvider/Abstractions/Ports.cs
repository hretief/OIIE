using IsbmProvider.Models;

namespace IsbmProvider.Abstractions;

/// <summary>Channel registry + token administration. Backed by Azure SQL + Key Vault.</summary>
public interface IChannelStore
{
    Task<Channel> CreateAsync(Channel channel, CancellationToken ct = default);
    Task<Channel?> GetAsync(string channelUri, CancellationToken ct = default);
    Task<IReadOnlyList<Channel>> GetAllAsync(CancellationToken ct = default);
    Task DeleteAsync(string channelUri, CancellationToken ct = default);
    Task AddSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default);
    Task RemoveSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default);
}

/// <summary>
/// Transport port over Azure Service Bus. Publication channel = Topic, Request channel = Queue.
/// NOTE the Read/Remove split: PeekNext must NOT hold a broker lock across two HTTP calls —
/// implementations settle on read and rely on the session cursor for Remove.
/// </summary>
public interface IMessageBroker
{
    /// <summary>Publish to a Publication channel (topic). Returns the broker-assigned MessageId.</summary>
    Task<string> PublishAsync(string channelUri, IsbmMessage message, CancellationToken ct = default);

    /// <summary>Enqueue a request onto a Request channel (queue). Returns MessageId.
    /// Takes the consumer session so the response can later be correlated back to it.</summary>
    Task<string> PostRequestAsync(SessionMetadata consumerSession, IsbmMessage message, CancellationToken ct = default);

    /// <summary>Enqueue a response correlated to a request. Returns MessageId.</summary>
    Task<string> PostResponseAsync(string channelUri, string requestMessageId, MessageContent content, CancellationToken ct = default);

    /// <summary>Peek the next message for a session's queue/subscription, honouring the cursor. Null if none.</summary>
    Task<IsbmMessage?> PeekNextAsync(SessionMetadata session, ISet<string> alreadyRemoved, CancellationToken ct = default);

    /// <summary>Settle (complete) a message so it is not redelivered; removal state lives in the cursor.</summary>
    Task SettleAsync(SessionMetadata session, string messageId, CancellationToken ct = default);

    /// <summary>Expire a specific posted message (ExpirePublication / ExpireRequest).</summary>
    Task ExpireAsync(string channelUri, string messageId, CancellationToken ct = default);

    /// <summary>Provision Service Bus entities (topic/queue) for a channel. Called on CreateChannel.</summary>
    Task EnsureChannelEntitiesAsync(string channelUri, ChannelType channelType, CancellationToken ct = default);

    /// <summary>Delete all Service Bus entities (topics/queues) for a channel. Called on DeleteChannel.</summary>
    Task DeleteChannelEntitiesAsync(string channelUri, ChannelType channelType, CancellationToken ct = default);

    Task CreateSubscriptionAsync(SessionMetadata session, CancellationToken ct = default);
    Task DeleteSubscriptionAsync(SessionMetadata session, CancellationToken ct = default);
}

/// <summary>Claim-check store (Azure Blob). Offloads large CCOM BODs; returns a retrievable ref.</summary>
public interface IPayloadStore
{
    Task<string> StoreAsync(MessageContent content, CancellationToken ct = default);
    Task<MessageContent> RetrieveAsync(string payloadRef, CancellationToken ct = default);
    /// <summary>Rehydrate inline content if it was claim-checked; pass-through otherwise.</summary>
    Task<MessageContent> ResolveAsync(MessageContent content, CancellationToken ct = default);
}

/// <summary>Channel security tokens, stored encrypted (Key Vault). Level 2+ requirement.</summary>
public interface ITokenVault
{
    Task<string> StoreTokenAsync(string channelUri, string rawToken, CancellationToken ct = default);
    Task<string?> RemoveTokenAsync(string channelUri, string tokenId, CancellationToken ct = default);
    Task<bool> ValidateAsync(string channelUri, string? presentedToken, CancellationToken ct = default);
}

/// <summary>Body-level content filtering: XPath 1.0 for XML, JSONPath for JSON.</summary>
public interface IFilterEngine
{
    /// <summary>True if the message body satisfies ALL of the session's filter expressions.
    /// <paramref name="namespaces"/> maps XPath prefixes to namespace URIs (from the subscription).</summary>
    bool Matches(MessageContent content, IReadOnlyList<string> filterExpressions,
        IReadOnlyDictionary<string, string> namespaces);
}

/// <summary>Outbound NotifyListener / ExpirationListener webhook dispatch (via Event Grid, mTLS at L3).</summary>
public interface INotificationDispatcher
{
    Task NotifyAsync(string listenerUrl, string sessionId, string messageId, IReadOnlyList<string> topics, string? originalMessageId, CancellationToken ct = default);
    Task NotifyExpiryAsync(string expirationListenerUrl, string sessionId, string messageId, CancellationToken ct = default);
}
