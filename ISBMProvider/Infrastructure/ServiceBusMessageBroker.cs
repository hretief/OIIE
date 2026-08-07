using System.Collections.Concurrent;
using System.Text.Json;
using System.Xml;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Azure Service Bus implementation of <see cref="IMessageBroker"/>.
///
/// Mapping:
///   Publication channel  -> Topic  (pub-*).  Subscription session -> Subscription named by SessionID,
///                                             with a SQL rule filtering on promoted ISBM topics.
///   Request channel      -> Queue  (req-*)   for requests (provider competes to read)  +
///                           Topic  (resp-*)  for responses, one Subscription per consumer session.
///
/// Semantics preserved:
///   * Expiry (xs:duration) -> ServiceBusMessage.TimeToLive.
///   * Topics promoted to an ApplicationProperty ("isbm.topics" = "|A|B|") for broker-side fan-out.
///   * Settle-on-read: PeekNext receives under peek-lock and Settle completes it, both within the
///     SAME HTTP call, so no broker lock is held across the later Remove call (removal lives in the
///     Durable Entity cursor). Large CCOM BODs are claim-checked to Blob before send.
/// </summary>
public sealed class ServiceBusMessageBroker : IMessageBroker, IAsyncDisposable
{
    // Keep bodies under the Standard-tier 256 KB ceiling; offload larger CCOM BODs to Blob.
    private const int ClaimCheckThresholdBytes = 192 * 1024;

    private readonly ServiceBusClient _client;
    private readonly ServiceBusAdministrationClient _admin;
    private readonly IPayloadStore _payloads;
    private readonly ICorrelationStore _correlation;
    private readonly ILogger<ServiceBusMessageBroker> _log;

    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
    private readonly ConcurrentDictionary<string, ServiceBusReceiver> _receivers = new();
    private readonly ConcurrentDictionary<string, bool> _ensured = new();
    // Bridges PeekNext -> Settle within one request (same singleton instance).
    private readonly ConcurrentDictionary<string, (ServiceBusReceiver Receiver, ServiceBusReceivedMessage Msg)> _inFlight = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ServiceBusMessageBroker(
        ServiceBusClient client, ServiceBusAdministrationClient admin,
        IPayloadStore payloads, ICorrelationStore correlation,
        ILogger<ServiceBusMessageBroker> log)
    {
        _client = client; _admin = admin; _payloads = payloads; _correlation = correlation; _log = log;
    }

    // ---- Publish (pub-sub) -------------------------------------------------

    public async Task<string> PublishAsync(string channelUri, IsbmMessage message, CancellationToken ct = default)
    {
        var topic = EntityNaming.PublicationTopic(channelUri);
        await EnsureTopicAsync(topic, ct);
        var content = await OffloadIfLargeAsync(message.MessageContent, ct);
        var sb = BuildMessage(content, message.Topics, message.OriginalMessageId, message.Expiry);
        await SenderFor(topic).SendMessageAsync(sb, ct);
        await PublishNotificationEventAsync(channelUri, sb.MessageId, message.Topics, message.OriginalMessageId, ct);
        return sb.MessageId;
    }

    // ---- Request / response -----------------------------------------------

    public async Task<string> PostRequestAsync(SessionMetadata consumerSession, IsbmMessage message, CancellationToken ct = default)
    {
        var queue = EntityNaming.RequestQueue(consumerSession.ChannelUri);
        await EnsureQueueAsync(queue, ct);
        var content = await OffloadIfLargeAsync(message.MessageContent, ct);
        var sb = BuildMessage(content, message.Topics, message.OriginalMessageId, message.Expiry);
        // Remember where the response should go once the provider answers this request.
        await _correlation.SetAsync(sb.MessageId, consumerSession.SessionId, ct);
        await SenderFor(queue).SendMessageAsync(sb, ct);
        await PublishNotificationEventAsync(consumerSession.ChannelUri, sb.MessageId, message.Topics, message.OriginalMessageId, ct);
        return sb.MessageId;
    }

    public async Task<string> PostResponseAsync(string channelUri, string requestMessageId, MessageContent content, CancellationToken ct = default)
    {
        var consumerSessionId = await _correlation.GetAsync(requestMessageId, ct)
            ?? throw new InvalidOperationException($"No consumer session correlated to request {requestMessageId}.");
        var topic = EntityNaming.ResponseTopic(channelUri);
        await EnsureTopicAsync(topic, ct);
        var resolved = await OffloadIfLargeAsync(content, ct);
        var sb = BuildMessage(resolved, Array.Empty<string>(), requestMessageId, expiry: null);
        sb.CorrelationId = requestMessageId;                       // lets ReadResponse match its request
        sb.ApplicationProperties["isbm.consumerSession"] = consumerSessionId;
        await SenderFor(topic).SendMessageAsync(sb, ct);
        await PublishNotificationEventAsync(channelUri, sb.MessageId, Array.Empty<string>(), requestMessageId, ct);
        return sb.MessageId;
    }

    // ---- Read (settle-on-read) --------------------------------------------

    public async Task<IsbmMessage?> PeekNextAsync(SessionMetadata session, ISet<string> alreadyRemoved, CancellationToken ct = default)
    {
        var receiver = ReceiverFor(session);
        while (true)
        {
            var recv = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), ct);
            if (recv is null) return null;                         // empty queue -> caller returns 404
            if (alreadyRemoved.Contains(recv.MessageId))
            {
                await receiver.CompleteMessageAsync(recv, ct);     // drop a message the cursor already removed
                continue;
            }
            _inFlight[InFlightKey(session.SessionId, recv.MessageId)] = (receiver, recv);
            return await ToIsbmMessageAsync(recv, ct);
        }
    }

    public async Task SettleAsync(SessionMetadata session, string messageId, CancellationToken ct = default)
    {
        if (_inFlight.TryRemove(InFlightKey(session.SessionId, messageId), out var e))
            await e.Receiver.CompleteMessageAsync(e.Msg, ct);
    }

    // ---- Expiry ------------------------------------------------------------

    public Task ExpireAsync(string channelUri, string messageId, CancellationToken ct = default)
    {
        // Service Bus has no "delete arbitrary message by id". Natural expiry is handled by TTL;
        // explicit ExpirePublication/ExpireRequest is enforced at the cursor/SQL layer (Read filters
        // it out) rather than in the broker. Left as an intentional no-op with a trace.
        _log.LogInformation("ExpireAsync requested for {MessageId} on {Channel} (enforced via cursor).", messageId, channelUri);
        return Task.CompletedTask;
    }

    // ---- Subscription lifecycle -------------------------------------------

    public async Task CreateSubscriptionAsync(SessionMetadata session, CancellationToken ct = default)
    {
        switch (session.SessionType)
        {
            case SessionType.Subscription:
            {
                var topic = EntityNaming.PublicationTopic(session.ChannelUri);
                await EnsureTopicAsync(topic, ct);
                await EnsureSubscriptionAsync(topic, session.SessionId, TopicRule(session.Topics), ct);
                break;
            }
            case SessionType.ConsumerRequest:
            {
                var topic = EntityNaming.ResponseTopic(session.ChannelUri);
                await EnsureTopicAsync(topic, ct);
                var rule = new CreateRuleOptions("session",
                    new SqlRuleFilter($"[isbm.consumerSession] = '{Escape(session.SessionId)}'"));
                await EnsureSubscriptionAsync(topic, session.SessionId, rule, ct);
                break;
            }
            case SessionType.ProviderRequest:
                await EnsureQueueAsync(EntityNaming.RequestQueue(session.ChannelUri), ct);
                break;
        }
    }

    public async Task DeleteSubscriptionAsync(SessionMetadata session, CancellationToken ct = default)
    {
        string? topic = session.SessionType switch
        {
            SessionType.Subscription    => EntityNaming.PublicationTopic(session.ChannelUri),
            SessionType.ConsumerRequest => EntityNaming.ResponseTopic(session.ChannelUri),
            _ => null
        };
        if (topic is null) return;
        if (await _admin.SubscriptionExistsAsync(topic, session.SessionId, ct))
            await _admin.DeleteSubscriptionAsync(topic, session.SessionId, ct);
    }

    // ---- Helpers -----------------------------------------------------------

    private ServiceBusSender SenderFor(string entity) => _senders.GetOrAdd(entity, _client.CreateSender);

    private ServiceBusReceiver ReceiverFor(SessionMetadata session)
    {
        var (entity, subscription) = session.SessionType switch
        {
            SessionType.Subscription    => (EntityNaming.PublicationTopic(session.ChannelUri), session.SessionId),
            SessionType.ConsumerRequest => (EntityNaming.ResponseTopic(session.ChannelUri), session.SessionId),
            SessionType.ProviderRequest => (EntityNaming.RequestQueue(session.ChannelUri), (string?)null),
            _ => throw new InvalidOperationException("Session type cannot read messages.")
        };
        var key = subscription is null ? entity : $"{entity}/{subscription}";
        return _receivers.GetOrAdd(key, _ =>
        {
            var opts = new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.PeekLock };
            return subscription is null
                ? _client.CreateReceiver(entity, opts)
                : _client.CreateReceiver(entity, subscription, opts);
        });
    }

    private ServiceBusMessage BuildMessage(MessageContent content, IReadOnlyList<string> topics, string? originalMessageId, string? expiry)
    {
        var envelope = JsonSerializer.SerializeToUtf8Bytes(content, Json);
        var msg = new ServiceBusMessage(BinaryData.FromBytes(envelope))
        {
            MessageId = Guid.NewGuid().ToString(),
            ContentType = content.MediaType
        };
        if (topics.Count > 0) msg.ApplicationProperties["isbm.topics"] = "|" + string.Join("|", topics) + "|";
        if (!string.IsNullOrEmpty(originalMessageId)) msg.ApplicationProperties["isbm.originalMessageId"] = originalMessageId;
        var ttl = ParseExpiry(expiry);
        if (ttl is { } t) msg.TimeToLive = t;
        return msg;
    }

    private async Task<IsbmMessage> ToIsbmMessageAsync(ServiceBusReceivedMessage recv, CancellationToken ct)
    {
        var content = JsonSerializer.Deserialize<MessageContent>(recv.Body.ToArray(), Json)
                      ?? new MessageContent { MediaType = recv.ContentType ?? "application/octet-stream" };
        var topics = recv.ApplicationProperties.TryGetValue("isbm.topics", out var t) && t is string s
            ? s.Trim('|').Split('|', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        var original = recv.ApplicationProperties.TryGetValue("isbm.originalMessageId", out var o) ? o as string : null;
        await Task.CompletedTask;
        return new IsbmMessage { MessageId = recv.MessageId, MessageContent = content, Topics = topics, OriginalMessageId = original };
    }

    private async Task<MessageContent> OffloadIfLargeAsync(MessageContent content, CancellationToken ct)
    {
        if (content.InlineContent is { } body && System.Text.Encoding.UTF8.GetByteCount(body) > ClaimCheckThresholdBytes)
        {
            var payloadRef = await _payloads.StoreAsync(content, ct);
            return content with { InlineContent = null, PayloadRef = payloadRef };
        }
        return content;
    }

    private static string InFlightKey(string sessionId, string messageId) => $"{sessionId}:{messageId}";

    private static CreateRuleOptions TopicRule(IReadOnlyList<string> topics)
    {
        if (topics.Count == 0) return new CreateRuleOptions("all", new TrueRuleFilter());
        // Match if any subscription topic appears in the publication's promoted "|A|B|" topic list.
        var clauses = topics.Select(t => $"[isbm.topics] LIKE '%|{Escape(t)}|%'");
        return new CreateRuleOptions("topics", new SqlRuleFilter(string.Join(" OR ", clauses)));
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static TimeSpan? ParseExpiry(string? expiry)
    {
        if (string.IsNullOrWhiteSpace(expiry)) return null;
        try
        {
            var ts = XmlConvert.ToTimeSpan(expiry);           // xs:duration, e.g. "P7D", "PT1H"
            return ts <= TimeSpan.Zero ? null : ts;           // spec: negative/blank expiry == default
        }
        catch (FormatException) { return null; }
    }

    private async Task EnsureTopicAsync(string topic, CancellationToken ct)
    {
        if (_ensured.ContainsKey(topic)) return;
        if (!await _admin.TopicExistsAsync(topic, ct)) await _admin.CreateTopicAsync(topic, ct);
        _ensured[topic] = true;
    }

    private async Task EnsureQueueAsync(string queue, CancellationToken ct)
    {
        if (_ensured.ContainsKey(queue)) return;
        if (!await _admin.QueueExistsAsync(queue, ct)) await _admin.CreateQueueAsync(queue, ct);
        _ensured[queue] = true;
    }

    private async Task EnsureSubscriptionAsync(string topic, string subscription, CreateRuleOptions rule, CancellationToken ct)
    {
        if (await _admin.SubscriptionExistsAsync(topic, subscription, ct)) return;
        await _admin.CreateSubscriptionAsync(new CreateSubscriptionOptions(topic, subscription), rule, ct);
    }

    // ---- Channel entity provisioning ------------------------------------------

    public async Task EnsureChannelEntitiesAsync(string channelUri, ChannelType channelType, CancellationToken ct = default)
    {
        if (channelType == ChannelType.Publication)
        {
            await EnsureTopicAsync(EntityNaming.PublicationTopic(channelUri), ct);
        }
        else
        {
            await EnsureQueueAsync(EntityNaming.RequestQueue(channelUri), ct);
            await EnsureTopicAsync(EntityNaming.ResponseTopic(channelUri), ct);
        }
        _log.LogInformation("Service Bus entities ensured for channel {Uri} ({Type})", channelUri, channelType);
    }

    // ---- Channel entity cleanup -----------------------------------------------

    public async Task DeleteChannelEntitiesAsync(string channelUri, ChannelType channelType, CancellationToken ct = default)
    {
        if (channelType == ChannelType.Publication)
        {
            var topic = EntityNaming.PublicationTopic(channelUri);
            await DeleteTopicIfExistsAsync(topic, ct);
        }
        else
        {
            var queue = EntityNaming.RequestQueue(channelUri);
            await DeleteQueueIfExistsAsync(queue, ct);
            var respTopic = EntityNaming.ResponseTopic(channelUri);
            await DeleteTopicIfExistsAsync(respTopic, ct);
        }
        _ensured.Clear(); // invalidate cache since entities were removed
        _log.LogInformation("Service Bus entities deleted for channel {Uri} ({Type})", channelUri, channelType);
    }

    private async Task DeleteTopicIfExistsAsync(string topic, CancellationToken ct)
    {
        try
        {
            if (await _admin.TopicExistsAsync(topic, ct))
                await _admin.DeleteTopicAsync(topic, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete topic {Topic}", topic); }
    }

    private async Task DeleteQueueIfExistsAsync(string queue, CancellationToken ct)
    {
        try
        {
            if (await _admin.QueueExistsAsync(queue, ct))
                await _admin.DeleteQueueAsync(queue, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete queue {Queue}", queue); }
    }

    // ---- Notification event publishing ----------------------------------------

    /// <summary>
    /// Publishes a lightweight notification event to the isbm-notifications topic.
    /// The NotifyOnMessage trigger picks this up and dispatches to subscriber ListenerURLs.
    /// </summary>
    public async Task PublishNotificationEventAsync(string channelUri, string messageId,
        IReadOnlyList<string> topics, string? originalMessageId, CancellationToken ct = default)
    {
        const string notifyTopic = "isbm-notifications";
        try
        {
            await EnsureTopicAsync(notifyTopic, ct);
            var msg = new ServiceBusMessage(BinaryData.FromString("notification"))
            {
                MessageId = Guid.NewGuid().ToString(),
                ContentType = "application/json"
            };
            msg.ApplicationProperties["isbm.channelUri"] = channelUri;
            msg.ApplicationProperties["isbm.messageId"] = messageId;
            if (topics.Count > 0) msg.ApplicationProperties["isbm.topics"] = "|" + string.Join("|", topics) + "|";
            if (!string.IsNullOrEmpty(originalMessageId)) msg.ApplicationProperties["isbm.originalMessageId"] = originalMessageId;
            await SenderFor(notifyTopic).SendMessageAsync(msg, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to publish notification event for message {MessageId}", messageId);
            // Non-fatal: the message is already published, notification is best-effort.
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _senders.Values) await s.DisposeAsync();
        foreach (var r in _receivers.Values) await r.DisposeAsync();
        await _client.DisposeAsync();
    }
}
