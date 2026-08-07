using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Azure Table Storage implementation of <see cref="IChannelStore"/>.
/// Uses your existing storage account — zero additional resources.
///
/// Table design:
///   IsbmChannels — PK="channels", RK=encoded(channelUri)
///     Properties: ChannelType, Description
///
///   IsbmTokens — PK=encoded(channelUri), RK=tokenId
///     (existence-only; the token value lives in Key Vault)
///
/// Encoding: channelURIs are Base64-URL-encoded for use as RowKeys
/// (Table Storage keys can't contain / \ # ?).
/// </summary>
public sealed class TableChannelStore : IChannelStore
{
    private const string ChannelsTable = "IsbmChannels";
    private const string TokensTable = "IsbmTokens";
    private const string ChannelsPK = "channels";

    private readonly TableServiceClient _serviceClient;
    private readonly ILogger<TableChannelStore> _log;
    private TableClient? _channels;
    private TableClient? _tokens;

    public TableChannelStore(TableServiceClient serviceClient, ILogger<TableChannelStore> log)
    {
        _serviceClient = serviceClient;
        _log = log;
    }

    public async Task<Channel> CreateAsync(Channel channel, CancellationToken ct = default)
    {
        var channels = await GetChannelsTableAsync(ct);
        var key = EncodeKey(channel.ChannelUri);

        try
        {
            await channels.AddEntityAsync(new TableEntity(ChannelsPK, key)
            {
                { "ChannelUri", channel.ChannelUri },
                { "ChannelType", channel.ChannelType.ToString() },
                { "Description", channel.Description ?? "" }
            }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw IsbmFaultException.Operation($"Channel '{channel.ChannelUri}' already exists.");
        }

        _log.LogInformation("Channel created: {Uri} ({Type})", channel.ChannelUri, channel.ChannelType);
        return channel;
    }

    public async Task<Channel?> GetAsync(string channelUri, CancellationToken ct = default)
    {
        var channels = await GetChannelsTableAsync(ct);
        var key = EncodeKey(channelUri);

        try
        {
            var entity = await channels.GetEntityAsync<TableEntity>(ChannelsPK, key, cancellationToken: ct);
            var tokenIds = await GetTokenIdsAsync(channelUri, ct);
            return ToChannel(entity.Value, tokenIds);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Channel>> GetAllAsync(CancellationToken ct = default)
    {
        var channels = await GetChannelsTableAsync(ct);
        var result = new List<Channel>();

        await foreach (var entity in channels.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{ChannelsPK}'", cancellationToken: ct))
        {
            var uri = entity.GetString("ChannelUri");
            var tokenIds = await GetTokenIdsAsync(uri, ct);
            result.Add(ToChannel(entity, tokenIds));
        }

        return result;
    }

    public async Task DeleteAsync(string channelUri, CancellationToken ct = default)
    {
        var channels = await GetChannelsTableAsync(ct);
        var tokens = await GetTokensTableAsync(ct);
        var key = EncodeKey(channelUri);

        // Delete all tokens for this channel
        await foreach (var tokenEntity in tokens.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{key}'", cancellationToken: ct))
        {
            await tokens.DeleteEntityAsync(tokenEntity.PartitionKey, tokenEntity.RowKey, cancellationToken: ct);
        }

        // Delete the channel
        try
        {
            await channels.DeleteEntityAsync(ChannelsPK, key, cancellationToken: ct);
            _log.LogInformation("Channel deleted: {Uri}", channelUri);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw IsbmFaultException.Channel($"Channel '{channelUri}' does not exist.");
        }
    }

    public async Task AddSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default)
    {
        var tokens = await GetTokensTableAsync(ct);
        var key = EncodeKey(channelUri);

        // Verify channel exists
        if (await GetAsync(channelUri, ct) is null)
            throw IsbmFaultException.Channel();

        await tokens.UpsertEntityAsync(new TableEntity(key, tokenId), cancellationToken: ct);
        _log.LogInformation("Token added: {TokenId} on channel {Uri}", tokenId, channelUri);
    }

    public async Task RemoveSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default)
    {
        var tokens = await GetTokensTableAsync(ct);
        var key = EncodeKey(channelUri);

        try
        {
            await tokens.DeleteEntityAsync(key, tokenId, cancellationToken: ct);
            _log.LogInformation("Token removed: {TokenId} from channel {Uri}", tokenId, channelUri);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _log.LogWarning("Token not found in Table Storage for removal: {TokenId} on {Uri}", tokenId, channelUri);
        }
    }

    // ---- Helpers ----

    private async Task<List<string>> GetTokenIdsAsync(string channelUri, CancellationToken ct)
    {
        var tokens = await GetTokensTableAsync(ct);
        var key = EncodeKey(channelUri);
        var result = new List<string>();

        await foreach (var entity in tokens.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{key}'", cancellationToken: ct))
        {
            result.Add(entity.RowKey);
        }
        return result;
    }

    private static Channel ToChannel(TableEntity entity, List<string> tokenIds) => new()
    {
        ChannelUri = entity.GetString("ChannelUri"),
        ChannelType = Enum.Parse<ChannelType>(entity.GetString("ChannelType")),
        Description = entity.GetString("Description"),
        SecurityTokenIds = tokenIds
    };

    /// <summary>
    /// Base64-URL-encode a channelUri for use as a Table Storage key.
    /// Keys can't contain / \ # ? so raw URIs like "/Enterprise/Site/Area" aren't valid.
    /// </summary>
    private static string EncodeKey(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private async Task<TableClient> GetChannelsTableAsync(CancellationToken ct)
    {
        if (_channels is not null) return _channels;
        _channels = _serviceClient.GetTableClient(ChannelsTable);
        await _channels.CreateIfNotExistsAsync(ct);
        return _channels;
    }

    private async Task<TableClient> GetTokensTableAsync(CancellationToken ct)
    {
        if (_tokens is not null) return _tokens;
        _tokens = _serviceClient.GetTableClient(TokensTable);
        await _tokens.CreateIfNotExistsAsync(ct);
        return _tokens;
    }
}
