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

/// <summary>ISBM Provider Publication Service (spec §5.5): the publisher side of pub-sub.</summary>
public sealed class ProviderPublicationFunctions(IChannelStore channels, IMessageBroker broker, IPayloadStore payloads, ISessionRegistry sessionRegistry, TokenValidator tokenValidator)
{
    // POST /publication-sessions  — OpenPublicationSession (channelUri in body)
    [Function("OpenPublicationSession")]
    public async Task<HttpResponseData> Open(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "publication-sessions")] HttpRequestData req,
        [DurableClient] DurableTaskClient durable)
    {
        var body = await req.ReadJsonAsync<PublicationSessionOpen>();
        if (body?.ChannelUri is null) return await req.FaultAsync(IsbmFaultException.Operation("Missing channelUri."));
        var uri = body.ChannelUri;
        var channel = await channels.GetAsync(uri);
        if (channel is null) return await req.FaultAsync(IsbmFaultException.Channel());
        if (channel.ChannelType != ChannelType.Publication) return await req.FaultAsync(IsbmFaultException.Operation("Not a Publication channel."));

        var fault = await tokenValidator.ValidateAsync(req, uri);
        if (fault is not null) throw fault;

        var sessionId = Guid.NewGuid().ToString();
        var meta = new SessionMetadata { SessionId = sessionId, ChannelUri = uri, SessionType = SessionType.Publication };
        var confirmedId = await SessionHelper.OpenAndConfirmAsync(durable, meta, sessionRegistry);
        return await req.JsonAsync(new { sessionId = confirmedId }, System.Net.HttpStatusCode.Created);
    }

    // POST /sessions/{sessionId}/publications  — PostPublication  (returns only messageId)
    [Function("PostPublication")]
    public async Task<HttpResponseData> Post(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sessions/{sessionId}/publications")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.Publication, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);

        var msg = await req.ReadJsonAsync<IsbmMessage>();
        if (msg is null) return await req.FaultAsync(IsbmFaultException.Session("Missing message.", 422));

        var payloadFault = PayloadValidator.Validate(msg.MessageContent);
        if (payloadFault is not null) return await req.FaultAsync(payloadFault);

        // Claim-check large CCOM BODs to Blob before publishing.
        var content = await payloads.ResolveAsync(msg.MessageContent);
        // TODO: OriginalMessageID forwarding rule — if forwarded, carry/verify OriginalMessageID (spec §5.5.2).
        var messageId = await broker.PublishAsync(state.Metadata!.ChannelUri, msg with { MessageContent = content });
        return await req.JsonAsync(new { messageId }, System.Net.HttpStatusCode.Created);
    }

    // DELETE /sessions/{sessionId}/publications/{messageId}  — ExpirePublication
    [Function("ExpirePublication")]
    public async Task<HttpResponseData> Expire(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sessions/{sessionId}/publications/{messageId}")] HttpRequestData req,
        string sessionId, string messageId,
        [DurableClient] DurableTaskClient durable)
    {
        var (state, fault) = await SessionHelper.GetValidatedSessionAsync(durable, sessionId, SessionType.Publication, sessionRegistry);
        if (fault is not null) return await req.FaultAsync(fault);
        await broker.ExpireAsync(state.Metadata!.ChannelUri, messageId);
        return req.NoContent();
    }

    // DELETE /sessions/{sessionId}  — ClosePublicationSession (shared close route; type-checked here)
    [Function("ClosePublicationSession")]
    public async Task<HttpResponseData> Close(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "publication-sessions/{sessionId}")] HttpRequestData req,
        string sessionId,
        [DurableClient] DurableTaskClient durable)
    {
        // Spec: closing a publication session expires all unexpired messages posted during it.
        await durable.Entities.SignalEntityAsync(new EntityInstanceId(nameof(SessionEntity), sessionId), nameof(SessionEntity.Close));
        return req.NoContent();
    }

    public sealed record PublicationSessionOpen(string? ChannelUri);
}
