using System.Collections.Concurrent;
using IsbmProvider.Abstractions;

namespace IsbmProvider.Infrastructure;

/// <summary>Process-local correlation store. TODO: replace with Azure SQL/Table for scale-out.</summary>
public sealed class InMemoryCorrelationStore : ICorrelationStore
{
    private readonly ConcurrentDictionary<string, string> _map = new();

    public Task SetAsync(string requestMessageId, string consumerSessionId, CancellationToken ct = default)
    { _map[requestMessageId] = consumerSessionId; return Task.CompletedTask; }

    public Task<string?> GetAsync(string requestMessageId, CancellationToken ct = default)
        => Task.FromResult(_map.TryGetValue(requestMessageId, out var v) ? v : null);

    public Task RemoveAsync(string requestMessageId, CancellationToken ct = default)
    { _map.TryRemove(requestMessageId, out _); return Task.CompletedTask; }
}
