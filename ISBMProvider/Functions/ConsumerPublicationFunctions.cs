using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Entities;
using IsbmProvider.Abstractions;
using IsbmProvider.Durable;
using IsbmProvider.Http;
using IsbmProvider.Models;

namespace IsbmProvider.Functions;

/// <summary>ISBM Consumer Publication Service (spec §5.6): the subscriber side of pub-sub.</summary>
public sealed class ConsumerPublicationFunctions(IChannelStore channels, IMessageBroker broker, IPayloadStore payloads, IFilterEngine filters, ISessionRegistry sessionRegistry, TokenValidator tokenValidator)
{
    // POST /subscription-sessions  — OpenSubscriptionSession (channelUri in body)
    [Function("OpenSubscriptionSession")]
    public async Task<HttpResponseData> Open(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "subscription-sessions")] HttpRequestData req,
        [DurableClient] DurableTaskClient durable)
    {
        var body = await req.ReadJsonAsync<SubscriptionOpen>();
        if (body?.ChannelUri is null) return await req.FaultAsync(IsbmFaultException.Operation("Missing channelUri."));
        var uri = body.ChannelUri;
        var channel = await channels.GetAsync(uri);
        if (channel is null) return await req.FaultAsync(IsbmFaultException.Channel());
        if (channel.ChannelType != ChannelType.Publication) return await req.FaultAsync(IsbmFaultException.Operation("Not a Publication channel."));

        var fault = await tokenValidator.ValidateAsync(req, uri);
        if (fault is not null) throw fault;

        if (body.Topics.Count == 0) return await req.FaultAsync(IsbmFaultException.Operation("At least one Topic is required."));
        // TODO: NamespaceFault if filter prefixes collide (spec §5.6.1).

        var sessionId = Guid.NewGuid().ToString();
        var meta = new SessionMetadata
        {
            SessionId = sessionId, ChannelUri = uri, SessionType = SessionType.Subscription,
            Topics = body.Topics, ListenerUrl = body.ListenerUrl,
            ExpirationListenerUrl = body.ExpirationListenerUrl, FilterExpressions = body.FilterExpressions,
            FilterNamespaces = body.FilterNamespaces ?? new Dictionary<string, string>()
        };
        await broker.CreateSubscriptionAsync(meta);
        var confirmedId = await SessionHelper.OpenAndConfirmAsync(durable, meta, sessionRegistry);
        sessionRegistry.Register(meta);
        return await req.JsonAsync(new { sessionId = confirmedId }, System.Net.HttpStatusCode.Created);
    }

    // GET /sessions/{sessionId}/publication  — ReadPublication (first non-removed match; 404 if none)
    [Function("ReadPublication")]
    public async Task<HttpResponseData> Read(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/publication")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.Subscription, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);

        var next = await broker.PeekNextAsync(state.Metadata!, state.Removed);
        if (next is null) return req.NoMessage();                                   // 404 == empty queue (spec)

        // Resolve claim-checked payload BEFORE filtering so XPath/JSONPath see the full body.
        var resolved = await payloads.ResolveAsync(next.MessageContent);

        // Body-level filter (XPath 1.0 / JSONPath). Topics returned = intersection with subscription.
        if (!filters.Matches(resolved, state.Metadata.FilterExpressions, state.Metadata.FilterNamespaces))
            return req.NoMessage();

        // SETTLE ON READ: complete the broker message and record the read in the cursor —
        // do NOT hold a broker lock across the later Remove call.
        await broker.SettleAsync(state.Metadata, next.MessageId!);
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.RecordRead), next.MessageId!);

        var topics = next.Topics.Intersect(state.Metadata.Topics).ToArray();
        return await req.JsonAsync(new { messageId = next.MessageId, messageContent = resolved, topics, originalMessageId = next.OriginalMessageId });
    }

    // DELETE /sessions/{sessionId}/publication  — RemovePublication (removes first message in queue)
    [Function("RemovePublication")]
    public async Task<HttpResponseData> Remove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}/publication")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.Subscription, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);

        var messageId = state.ReadNotRemoved.FirstOrDefault();
        if (messageId is not null)
            await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.RecordRemoved), messageId);
        return req.NoContent();
    }

    // DELETE /subscription-sessions/{sessionId}  — CloseSubscriptionSession
    [Function("CloseSubscriptionSession")]
    public async Task<HttpResponseData> Close(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "subscription-sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var entity = await durable.Entities.GetEntityAsync<SessionState>(new EntityInstanceId(nameof(SessionEntity), sessionId));
        if (entity?.State?.Metadata is { } closeMeta) await broker.DeleteSubscriptionAsync(closeMeta);
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.Close));
        sessionRegistry.Unregister(sessionId);
        return req.NoContent();
    }

    public sealed record SubscriptionOpen(
        string? ChannelUri, IReadOnlyList<string> Topics, string? ListenerUrl,
        string? ExpirationListenerUrl, IReadOnlyList<string> FilterExpressions,
        IReadOnlyDictionary<string, string>? FilterNamespaces);
}
