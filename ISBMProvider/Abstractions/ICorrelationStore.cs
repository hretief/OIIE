namespace IsbmProvider.Abstractions;

/// <summary>
/// Remembers which consumer request session a request came from, so PostResponse can route the
/// response back. Keyed by the ISBM request MessageID. Back this with Azure SQL/Table in production;
/// the in-memory default is process-local and does not survive scale-out.
/// </summary>
public interface ICorrelationStore
{
    Task SetAsync(string requestMessageId, string consumerSessionId, CancellationToken ct = default);
    Task<string?> GetAsync(string requestMessageId, CancellationToken ct = default);
    Task RemoveAsync(string requestMessageId, CancellationToken ct = default);
}
