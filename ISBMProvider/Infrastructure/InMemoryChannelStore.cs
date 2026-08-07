using System.Collections.Concurrent;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Process-local channel store. Channels survive for the lifetime of the host process —
/// good enough for local dev and integration testing. Replace with Azure SQL for production
/// (persistence across restarts, multi-instance consistency, queryable channel registry).
/// </summary>
public sealed class InMemoryChannelStore : IChannelStore
{
    private readonly ConcurrentDictionary<string, Channel> _channels = new();

    public Task<Channel> CreateAsync(Channel channel, CancellationToken ct = default)
    {
        if (!_channels.TryAdd(channel.ChannelUri, channel))
            throw IsbmFaultException.Operation($"Channel '{channel.ChannelUri}' already exists.");
        return Task.FromResult(channel);
    }

    public Task<Channel?> GetAsync(string channelUri, CancellationToken ct = default)
        => Task.FromResult(_channels.TryGetValue(channelUri, out var ch) ? ch : null);

    public Task<IReadOnlyList<Channel>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Channel>>(_channels.Values.ToList());

    public Task DeleteAsync(string channelUri, CancellationToken ct = default)
    {
        if (!_channels.TryRemove(channelUri, out _))
            throw IsbmFaultException.Channel($"Channel '{channelUri}' does not exist.");
        return Task.CompletedTask;
    }

    public Task AddSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default)
    {
        _channels.AddOrUpdate(channelUri,
            _ => throw IsbmFaultException.Channel(),
            (_, existing) => existing with
            {
                SecurityTokenIds = existing.SecurityTokenIds.Append(tokenId).ToList()
            });
        return Task.CompletedTask;
    }

    public Task RemoveSecurityTokenAsync(string channelUri, string tokenId, CancellationToken ct = default)
    {
        _channels.AddOrUpdate(channelUri,
            _ => throw IsbmFaultException.Channel(),
            (_, existing) => existing with
            {
                SecurityTokenIds = existing.SecurityTokenIds.Where(t => t != tokenId).ToList()
            });
        return Task.CompletedTask;
    }
}
