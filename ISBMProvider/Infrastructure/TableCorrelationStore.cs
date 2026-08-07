using System.Text;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Azure Table Storage implementation of <see cref="ICorrelationStore"/>.
/// Maps request MessageIDs to consumer session IDs so PostResponse can route
/// the response back to the correct consumer.
///
/// Table: IsbmCorrelations
///   PK = "corr"  (fixed — small table, single partition is fine)
///   RK = requestMessageId
///   Properties: ConsumerSessionId
/// </summary>
public sealed class TableCorrelationStore : ICorrelationStore
{
    private const string TableName = "IsbmCorrelations";
    private const string PK = "corr";

    private readonly TableServiceClient _serviceClient;
    private readonly ILogger<TableCorrelationStore> _log;
    private TableClient? _table;

    public TableCorrelationStore(TableServiceClient serviceClient, ILogger<TableCorrelationStore> log)
    {
        _serviceClient = serviceClient;
        _log = log;
    }

    public async Task SetAsync(string requestMessageId, string consumerSessionId, CancellationToken ct = default)
    {
        var table = await GetTableAsync(ct);
        await table.UpsertEntityAsync(new TableEntity(PK, requestMessageId)
        {
            { "ConsumerSessionId", consumerSessionId }
        }, cancellationToken: ct);
        _log.LogDebug("Correlation set: {RequestId} → {SessionId}", requestMessageId, consumerSessionId);
    }

    public async Task<string?> GetAsync(string requestMessageId, CancellationToken ct = default)
    {
        var table = await GetTableAsync(ct);
        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(PK, requestMessageId, cancellationToken: ct);
            return entity.Value.GetString("ConsumerSessionId");
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task RemoveAsync(string requestMessageId, CancellationToken ct = default)
    {
        var table = await GetTableAsync(ct);
        try
        {
            await table.DeleteEntityAsync(PK, requestMessageId, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already removed — idempotent
        }
    }

    private async Task<TableClient> GetTableAsync(CancellationToken ct = default)
    {
        if (_table is not null) return _table;
        _table = _serviceClient.GetTableClient(TableName);
        await _table.CreateIfNotExistsAsync(ct);
        return _table;
    }
}
