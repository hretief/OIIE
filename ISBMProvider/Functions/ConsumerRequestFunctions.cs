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

/// <summary>ISBM Consumer Request Service (spec §5.8): the consumer side of request-response (Push/Pull).</summary>
public sealed class ConsumerRequestFunctions(IChannelStore channels, IMessageBroker broker, IPayloadStore payloads, ISessionRegistry sessionRegistry, TokenValidator tokenValidator)
{
    // POST /consumer-request-sessions  — OpenConsumerRequestSession (channelUri in body)
    [Function("OpenConsumerRequestSession")]
    public async Task<HttpResponseData> Open(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "consumer-request-sessions")] HttpRequestData req,
        [DurableClient] DurableTaskClient durable)
    {
        var body = await req.ReadJsonAsync<ConsumerRequestOpen>();
        if (body?.ChannelUri is null) return await req.FaultAsync(IsbmFaultException.Operation("Missing channelUri."));
        var uri = body.ChannelUri;
        var channel = await channels.GetAsync(uri);
        if (channel is null) return await req.FaultAsync(IsbmFaultException.Channel());
        if (channel.ChannelType != ChannelType.Request) return await req.FaultAsync(IsbmFaultException.Operation("Not a Request channel."));

        var fault = await tokenValidator.ValidateAsync(req, uri);
        if (fault is not null) throw fault;

        var sessionId = Guid.NewGuid().ToString();
        var meta = new SessionMetadata
        {
            SessionId = sessionId, ChannelUri = uri, SessionType = SessionType.ConsumerRequest,
            ListenerUrl = body?.ListenerUrl
        };
        await broker.CreateSubscriptionAsync(meta);   // response-topic subscription filtered to this session
        var confirmedId = await SessionHelper.OpenAndConfirmAsync(durable, meta, sessionRegistry);
        sessionRegistry.Register(meta);
        return await req.JsonAsync(new { sessionId = confirmedId }, System.Net.HttpStatusCode.Created);
    }

    // POST /sessions/{sessionId}/requests  — PostRequest (single Topic; returns messageId)
    [Function("PostRequest")]
    public async Task<HttpResponseData> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/requests")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ConsumerRequest, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);

        var msg = await req.ReadJsonAsync<IsbmMessage>();
        if (msg is null || msg.Topics.Count != 1) return await req.FaultAsync(IsbmFaultException.Session("Request requires exactly one Topic.", 422));

        var payloadFault = PayloadValidator.Validate(msg.MessageContent);
        if (payloadFault is not null) return await req.FaultAsync(payloadFault);

        var content = await payloads.ResolveAsync(msg.MessageContent);
        var messageId = await broker.PostRequestAsync(state.Metadata!, msg with { MessageContent = content });
        return await req.JsonAsync(new { messageId }, System.Net.HttpStatusCode.Created);
    }

    // DELETE /sessions/{sessionId}/requests/{messageId}  — ExpireRequest
    [Function("ExpireRequest")]
    public async Task<HttpResponseData> Expire(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}/requests/{messageId}")] HttpRequestData req,
        string sessionId, string messageId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, faultR) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ConsumerRequest, sessionRegistry);
        if (faultR is not null) return await req.FaultAsync(faultR);
        await broker.ExpireAsync(state.Metadata!.ChannelUri, messageId);
        return req.NoContent();
    }

    // GET /sessions/{sessionId}/requests/{requestMessageId}/response  — ReadResponse
    [Function("ReadResponse")]
    public async Task<HttpResponseData> Read(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/requests/{requestMessageId}/response")] HttpRequestData req,
        string sessionId, string requestMessageId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, faultR) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ConsumerRequest, sessionRegistry);
        if (faultR is not null) return await req.FaultAsync(faultR);

        // TODO: fetch the response correlated to requestMessageId from the broker; 404 if not yet available.
        var next = await broker.PeekNextAsync(state.Metadata!, state.Removed);
        if (next is null) return req.NoMessage();

        await broker.SettleAsync(state.Metadata!, next.MessageId!);
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.RecordRead), next.MessageId!);
        var resolved = await payloads.ResolveAsync(next.MessageContent);
        return await req.JsonAsync(new { messageId = next.MessageId, messageContent = resolved });
    }

    // DELETE /sessions/{sessionId}/requests/{requestMessageId}/response  — RemoveResponse
    [Function("RemoveResponse")]
    public async Task<HttpResponseData> Remove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}/requests/{requestMessageId}/response")] HttpRequestData req,
        string sessionId, string requestMessageId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, faultR) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ConsumerRequest, sessionRegistry);
        if (faultR is not null) return await req.FaultAsync(faultR);
        var messageId = state.ReadNotRemoved.FirstOrDefault();
        if (messageId is not null)
            await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.RecordRemoved), messageId);
        return req.NoContent();
    }

    // DELETE /consumer-request-sessions/{sessionId}  — CloseConsumerRequestSession
    [Function("CloseConsumerRequestSession")]
    public async Task<HttpResponseData> Close(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "consumer-request-sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        // Spec: closing expires all unexpired requests posted during the session and fires the
        // Expiration Listener for any provider sessions that registered one.
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.Close));
        sessionRegistry.Unregister(sessionId);
        return req.NoContent();
    }

    public sealed record ConsumerRequestOpen(string? ChannelUri, string? ListenerUrl);
}
