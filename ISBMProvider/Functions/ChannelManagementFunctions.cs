using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using IsbmProvider.Abstractions;
using IsbmProvider.Http;
using IsbmProvider.Models;

namespace IsbmProvider.Functions;

/// <summary>ISBM Channel Management Service (spec §5.2). Admin of the channel topology + tokens.</summary>
public sealed class ChannelManagementFunctions(
    IChannelStore channels, ITokenVault tokens, IMessageBroker broker, TokenValidator tokenValidator)
{
    // POST /channels  — CreateChannel (with optional initial securityTokens)
    [Function("CreateChannel")]
    public async Task<HttpResponseData> CreateChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "channels")] HttpRequestData req)
    {
        var body = await req.ReadJsonAsync<CreateChannelBody>();
        if (body is null) return await req.FaultAsync(IsbmFaultException.Operation("Missing channel body."));

        var channel = new Channel
        {
            ChannelUri = body.ChannelUri!,
            ChannelType = body.ChannelType,
            Description = body.Description
        };
        var created = await channels.CreateAsync(channel);

        // Provision Service Bus entities (topic for Publication, queue+resp-topic for Request)
        await broker.EnsureChannelEntitiesAsync(channel.ChannelUri, channel.ChannelType);

        // Store initial security tokens (spec: tokens assigned at creation time → channel is secured)
        if (body.SecurityTokens is { Count: > 0 })
        {
            foreach (var token in body.SecurityTokens)
            {
                var serialized = JsonSerializer.Serialize(token);
                var tokenId = await tokens.StoreTokenAsync(body.ChannelUri!, serialized);
                await channels.AddSecurityTokenAsync(body.ChannelUri!, tokenId);
            }
        }

        // Re-fetch to include token IDs in the response
        var result = await channels.GetAsync(body.ChannelUri!) ?? created;
        return await req.JsonAsync(result, System.Net.HttpStatusCode.Created);
    }

    // GET /channels/{*channelUri}  — GetChannels / GetChannel
    [Function("GetChannels")]
    public async Task<HttpResponseData> GetChannels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "channels/{*channelUri}")] HttpRequestData req,
        string? channelUri)
    {
        if (string.IsNullOrEmpty(channelUri))
            return await req.JsonAsync(await channels.GetAllAsync());

        var uri = Responses.DecodeChannelUri(channelUri);
        var channel = await channels.GetAsync(uri);
        return channel is null
            ? await req.FaultAsync(IsbmFaultException.Channel())
            : await req.JsonAsync(channel);
    }

    // DELETE /channels/{*channelUri}  — DeleteChannel (token required if secured)
    [Function("DeleteChannel")]
    public async Task<HttpResponseData> DeleteChannel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "channels/{*channelUri}")] HttpRequestData req,
        string channelUri)
    {
        var uri = Responses.DecodeChannelUri(channelUri);

        // Token validation — secured channels require authorization to delete
        var fault = await tokenValidator.ValidateAsync(req, uri);
        if (fault is not null) throw fault;

        var channel = await channels.GetAsync(uri);
        if (channel is not null)
            await broker.DeleteChannelEntitiesAsync(uri, channel.ChannelType);
        await channels.DeleteAsync(uri);
        return req.NoContent();
    }

    // POST /security-tokens  — AddSecurityTokens (channelUri + securityTokens in body)
    [Function("AddSecurityToken")]
    public async Task<HttpResponseData> AddSecurityToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "security-tokens")] HttpRequestData req)
    {
        var payload = await req.ReadJsonAsync<SecurityTokenPayload>();
        if (payload?.ChannelUri is null || payload.SecurityTokens is not { Count: > 0 })
            return await req.FaultAsync(IsbmFaultException.Operation("Missing channelUri or securityTokens."));

        var channel = await channels.GetAsync(payload.ChannelUri);
        if (channel is null) return await req.FaultAsync(IsbmFaultException.Channel());

        // Token validation — if channel is already secured, caller must present a valid token
        if (channel.SecurityTokenIds.Count > 0)
        {
            var fault = await tokenValidator.ValidateAsync(req, payload.ChannelUri);
            if (fault is not null) throw fault;
        }

        foreach (var token in payload.SecurityTokens)
        {
            var serialized = JsonSerializer.Serialize(token);
            var tokenId = await tokens.StoreTokenAsync(payload.ChannelUri, serialized);
            await channels.AddSecurityTokenAsync(payload.ChannelUri, tokenId);
        }
        return req.CreateResponse(System.Net.HttpStatusCode.Created);
    }

    // DELETE /security-tokens  — RemoveSecurityTokens (channelUri + securityTokens in body)
    [Function("RemoveSecurityToken")]
    public async Task<HttpResponseData> RemoveSecurityToken(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "security-tokens")] HttpRequestData req)
    {
        var payload = await req.ReadJsonAsync<SecurityTokenPayload>();
        if (payload?.ChannelUri is null || payload.SecurityTokens is not { Count: > 0 })
            return await req.FaultAsync(IsbmFaultException.Operation("Missing channelUri or securityTokens."));

        var channel = await channels.GetAsync(payload.ChannelUri);
        if (channel is null) return await req.FaultAsync(IsbmFaultException.Channel());

        // Token validation — must present a valid token to remove tokens
        var fault = await tokenValidator.ValidateAsync(req, payload.ChannelUri);
        if (fault is not null) throw fault;

        foreach (var token in payload.SecurityTokens)
        {
            var serialized = JsonSerializer.Serialize(token);
            var resolvedId = await tokens.RemoveTokenAsync(payload.ChannelUri, serialized);

            var storedId = resolvedId is not null && channel.SecurityTokenIds.Contains(resolvedId)
                ? resolvedId
                : channel.SecurityTokenIds.FirstOrDefault(id => id == resolvedId);

            if (storedId is not null)
                await channels.RemoveSecurityTokenAsync(payload.ChannelUri, storedId);
        }
        return req.NoContent();
    }

    public sealed record UsernameToken(string Username, string Password);
    public sealed record CreateChannelBody(string? ChannelUri, ChannelType ChannelType, string? Description,
        IReadOnlyList<UsernameToken>? SecurityTokens);
    public sealed record SecurityTokenPayload(string? ChannelUri, IReadOnlyList<UsernameToken>? SecurityTokens);
}
