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

/// <summary>ISBM Provider Request Service (spec §5.7): the provider side of request-response (Push/Pull).</summary>
public sealed class ProviderRequestFunctions(IChannelStore channels, IMessageBroker broker, IPayloadStore payloads, ISessionRegistry sessionRegistry, TokenValidator tokenValidator)
{
    // POST /provider-request-sessions  — OpenProviderRequestSession (channelUri in body)
    [Function("OpenProviderRequestSession")]
    public async Task<HttpResponseData> Open(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "provider-request-sessions")] HttpRequestData req,
        [DurableClient] DurableTaskClient durable)
    {
        var body = await req.ReadJsonAsync<ProviderRequestOpen>();
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
            SessionId = sessionId, ChannelUri = uri, SessionType = SessionType.ProviderRequest,
            Topics = body?.Topics ?? Array.Empty<string>(), ListenerUrl = body?.ListenerUrl,
            ExpirationListenerUrl = body?.ExpirationListenerUrl, FilterExpressions = body?.FilterExpressions ?? Array.Empty<string>()
        };
        await broker.CreateSubscriptionAsync(meta);
        var confirmedId = await SessionHelper.OpenAndConfirmAsync(durable, meta, sessionRegistry);
        sessionRegistry.Register(meta);
        return await req.JsonAsync(new { sessionId = confirmedId }, System.Net.HttpStatusCode.Created);
    }

    // GET /sessions/{sessionId}/request  — ReadRequest (first request; 404 if none)
    [Function("ReadRequest")]
    public async Task<HttpResponseData> Read(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sessions/{sessionId}/request")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ProviderRequest, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);

        var next = await broker.PeekNextAsync(state.Metadata!, state.Removed);
        if (next is null) return req.NoMessage();

        await broker.SettleAsync(state.Metadata!, next.MessageId!);
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.RecordRead), next.MessageId!);

        var resolved = await payloads.ResolveAsync(next.MessageContent);
        return await req.JsonAsync(new { messageId = next.MessageId, messageContent = resolved, topics = next.Topics, originalMessageId = next.OriginalMessageId });
    }

    // DELETE /sessions/{sessionId}/request  — RemoveRequest
    [Function("RemoveRequest")]
    public async Task<HttpResponseData> Remove(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}/request")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ProviderRequest, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);
        var messageId = state.ReadNotRemoved.FirstOrDefault();
        if (messageId is not null)
            await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.RecordRemoved), messageId);
        return req.NoContent();
    }

    // POST /sessions/{sessionId}/requests/{requestMessageId}/response  — PostResponse
    // Route aligned with ReadResponse so both sides use the same URL pattern.
    [Function("PostResponse")]
    public async Task<HttpResponseData> PostResponse(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/requests/{requestMessageId}/response")] HttpRequestData req,
        string sessionId, string requestMessageId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.ProviderRequest, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);

        var body = await req.ReadJsonAsync<ResponseBody>();
        var content = body?.MessageContent ?? new MessageContent { MediaType = "application/octet-stream" };

        var payloadFault = PayloadValidator.Validate(content);
        if (payloadFault is not null) return await req.FaultAsync(payloadFault);

        var resolved = await payloads.ResolveAsync(content);
        var messageId = await broker.PostResponseAsync(state.Metadata!.ChannelUri, requestMessageId, resolved);
        return await req.JsonAsync(new { messageId }, System.Net.HttpStatusCode.Created);
    }

    // DELETE /provider-request-sessions/{sessionId}  — CloseProviderRequestSession
    [Function("CloseProviderRequestSession")]
    public async Task<HttpResponseData> Close(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "provider-request-sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var entity = await durable.Entities.GetEntityAsync<SessionState>(new EntityInstanceId(nameof(SessionEntity), sessionId));
        if (entity?.State?.Metadata is { } closeMeta) await broker.DeleteSubscriptionAsync(closeMeta);
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.Close));
        sessionRegistry.Unregister(sessionId);
        return req.NoContent();
    }

    /// <summary>PostResponse body — requestMessageId is now in the URL route.</summary>
    public sealed record ResponseBody(MessageContent? MessageContent);

    public sealed record ProviderRequestOpen(
        string? ChannelUri, IReadOnlyList<string> Topics, string? ListenerUrl,
        string? ExpirationListenerUrl, IReadOnlyList<string> FilterExpressions);
}
