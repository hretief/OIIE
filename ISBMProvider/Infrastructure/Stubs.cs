using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

// These are deliberately-thin STUBS so the app wires up and every route is reachable.
// Replace each with the real Azure-backed adapter (Service Bus / Blob / Key Vault / Event Grid).
// Each throws or returns benign placeholders and is annotated with the concrete Azure work to do.

public sealed class StubChannelStore : IChannelStore
{
    // TODO: back with Azure SQL (Channels table). Enforce Level-3 identity check on unauthenticated ops.
    public Task<Channel> CreateAsync(Channel channel, CancellationToken ct = default) => Task.FromResult(channel);
    public Task<Channel?> GetAsync(string channelUri, CancellationToken ct = default) => Task.FromResult<Channel?>(null);
    public Task<IReadOnlyList<Channel>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Channel>>(Array.Empty<Channel>());
    public Task DeleteAsync(string channelUri, CancellationToken ct = default) => Task.CompletedTask;
    public Task AddSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class StubMessageBroker : IMessageBroker
{
    // TODO: Azure.Messaging.ServiceBus. Topic per publication channel, queue per request channel.
    // Set ApplicationProperties["topics"] for broker-side fan-out; TTL from Expiry; claim-check large bodies.
    public Task<string> PublishAsync(string channelUri, IsbmMessage message, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid().ToString());
    public Task<string> PostRequestAsync(SessionMetadata consumerSession, IsbmMessage message, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid().ToString());
    public Task<string> PostResponseAsync(string channelUri, string requestMessageId, MessageContent content, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid().ToString());
    public Task<IsbmMessage?> PeekNextAsync(SessionMetadata session, ISet<string> alreadyRemoved, CancellationToken ct = default) => Task.FromResult<IsbmMessage?>(null);
    public Task SettleAsync(SessionMetadata session, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task ExpireAsync(string channelUri, string messageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task EnsureChannelEntitiesAsync(string channelUri, ChannelType channelType, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteChannelEntitiesAsync(string channelUri, ChannelType channelType, CancellationToken ct = default) => Task.CompletedTask;
    public Task CreateSubscriptionAsync(SessionMetadata session, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteSubscriptionAsync(SessionMetadata session, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class StubPayloadStore : IPayloadStore
{
    // TODO: Azure.Storage.Blobs. Offload bodies over ~200KB; return SAS-scoped ref.
    public Task<string> StoreAsync(MessageContent content, CancellationToken ct = default) => Task.FromResult($"blob://payloads/{Guid.NewGuid()}");
    public Task<MessageContent> RetrieveAsync(string payloadRef, CancellationToken ct = default) => Task.FromResult(new MessageContent { MediaType = "application/xml" });
    public Task<MessageContent> ResolveAsync(MessageContent content, CancellationToken ct = default) => Task.FromResult(content);
}

public sealed class StubTokenVault : ITokenVault
{
    // TODO: Azure.Security.KeyVault.Secrets. Store tokens encrypted; validate presented token/cert.
    public Task<string> StoreTokenAsync(string channelUri, string rawToken, CancellationToken ct = default) => Task.FromResult(Guid.NewGuid().ToString());
    public Task<string?> RemoveTokenAsync(string channelUri, string tokenId, CancellationToken ct = default) => Task.FromResult<string?>(tokenId);
    public Task<bool> ValidateAsync(string channelUri, string? presentedToken, CancellationToken ct = default) => Task.FromResult(true);
}

public sealed class StubFilterEngine : IFilterEngine
{
    // TODO: compile+cache XPath 1.0 (System.Xml.XPath) for XML, JSONPath for JSON, per session.
    public bool Matches(MessageContent content, IReadOnlyList<string> filterExpressions, IReadOnlyDictionary<string, string> namespaces) => true;
}

public sealed class StubNotificationDispatcher : INotificationDispatcher
{
    // TODO: publish to Event Grid topic; Event Grid delivers the webhook to the ListenerURL
    // with retry/backoff/dead-letter. At Level 3 present client cert + validate endpoint cert.
    public Task NotifyAsync(string listenerUrl, string sessionId, string messageId, IReadOnlyList<string> topics, string? originalMessageId, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyExpiryAsync(string expirationListenerUrl, string sessionId, string messageId, CancellationToken ct = default) => Task.CompletedTask;
}
