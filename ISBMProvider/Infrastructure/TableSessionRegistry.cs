using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using IsbmProvider.Abstractions;
using IsbmProvider.Models;

namespace IsbmProvider.Infrastructure;

/// <summary>
/// Azure Table Storage implementation of <see cref="ISessionRegistry"/>.
/// Persists session metadata so notifications survive host restarts and work across scale-out.
///
/// Table: IsbmSessions
///   PK = encoded(channelUri)  — efficient partition scan for "all sessions on this channel"
///   RK = sessionId
///   Properties: SessionType, Topics (JSON), ListenerUrl, ExpirationListenerUrl,
///               FilterExpressions (JSON), FilterNamespaces (JSON)
/// </summary>
public sealed class TableSessionRegistry : ISessionRegistry
{
    private const string TableName = "IsbmSessions";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TableServiceClient _serviceClient;
    private readonly ILogger<TableSessionRegistry> _log;
    private TableClient? _table;

    public TableSessionRegistry(TableServiceClient serviceClient, ILogger<TableSessionRegistry> log)
    {
        _serviceClient = serviceClient;
        _log = log;
    }

    public void Register(SessionMetadata session)
    {
        // Fire-and-forget async write from a sync interface — use GetAwaiter().GetResult()
        // to keep the ISessionRegistry interface simple. Table writes are fast (<10ms).
        RegisterAsync(session).GetAwaiter().GetResult();
    }

    public void Unregister(string sessionId)
    {
        UnregisterAsync(sessionId).GetAwaiter().GetResult();
    }

    public SessionMetadata? GetSession(string sessionId)
    {
        return GetSessionAsync(sessionId).GetAwaiter().GetResult();
    }

    public IReadOnlyList<SessionMetadata> GetNotifiableSessions(string channelUri)
    {
        return GetNotifiableSessionsAsync(channelUri).GetAwaiter().GetResult();
    }

    public IReadOnlyList<SessionMetadata> GetExpirableSessions(string channelUri)
    {
        return GetExpirableSessionsAsync(channelUri).GetAwaiter().GetResult();
    }

    // ---- Async implementations ----

    private async Task<SessionMetadata?> GetSessionAsync(string sessionId)
    {
        var table = await GetTableAsync();
        // SessionId is the RowKey; we don't know the PK, so scan for it
        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"RowKey eq '{EscapeFilter(sessionId)}'"))
        {
            return ToSessionMetadata(entity);
        }
        return null;
    }

    private async Task RegisterAsync(SessionMetadata session)
    {
        var table = await GetTableAsync();
        var pk = EncodeKey(session.ChannelUri);

        var entity = new TableEntity(pk, session.SessionId)
        {
            { "ChannelUri", session.ChannelUri },
            { "SessionType", session.SessionType.ToString() },
            { "Topics", JsonSerializer.Serialize(session.Topics, Json) },
            { "ListenerUrl", session.ListenerUrl ?? "" },
            { "ExpirationListenerUrl", session.ExpirationListenerUrl ?? "" },
            { "FilterExpressions", JsonSerializer.Serialize(session.FilterExpressions, Json) },
            { "FilterNamespaces", JsonSerializer.Serialize(session.FilterNamespaces, Json) }
        };

        await table.UpsertEntityAsync(entity);
        _log.LogInformation("Session registered: {SessionId} on {Channel} ({Type})",
            session.SessionId, session.ChannelUri, session.SessionType);
    }

    private async Task UnregisterAsync(string sessionId)
    {
        var table = await GetTableAsync();

        // SessionId is the RowKey, but we don't know the PK (channelUri). Scan for it.
        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"RowKey eq '{EscapeFilter(sessionId)}'"))
        {
            await table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
            _log.LogInformation("Session unregistered: {SessionId}", sessionId);
            return;
        }
        _log.LogDebug("Session not found for unregister: {SessionId}", sessionId);
    }

    private async Task<IReadOnlyList<SessionMetadata>> GetNotifiableSessionsAsync(string channelUri)
    {
        var sessions = await GetSessionsByChannelAsync(channelUri);
        return sessions.Where(s => !string.IsNullOrEmpty(s.ListenerUrl)).ToList();
    }

    private async Task<IReadOnlyList<SessionMetadata>> GetExpirableSessionsAsync(string channelUri)
    {
        var sessions = await GetSessionsByChannelAsync(channelUri);
        return sessions.Where(s => !string.IsNullOrEmpty(s.ExpirationListenerUrl)).ToList();
    }

    private async Task<List<SessionMetadata>> GetSessionsByChannelAsync(string channelUri)
    {
        var table = await GetTableAsync();
        var pk = EncodeKey(channelUri);
        var result = new List<SessionMetadata>();

        await foreach (var entity in table.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{pk}'"))
        {
            result.Add(ToSessionMetadata(entity));
        }
        return result;
    }

    private static SessionMetadata ToSessionMetadata(TableEntity entity) => new()
    {
        SessionId = entity.RowKey,
        ChannelUri = entity.GetString("ChannelUri"),
        SessionType = Enum.Parse<SessionType>(entity.GetString("SessionType")),
        Topics = JsonSerializer.Deserialize<List<string>>(entity.GetString("Topics") ?? "[]", Json) ?? new(),
        ListenerUrl = NullIfEmpty(entity.GetString("ListenerUrl")),
        ExpirationListenerUrl = NullIfEmpty(entity.GetString("ExpirationListenerUrl")),
        FilterExpressions = JsonSerializer.Deserialize<List<string>>(entity.GetString("FilterExpressions") ?? "[]", Json) ?? new(),
        FilterNamespaces = JsonSerializer.Deserialize<Dictionary<string, string>>(entity.GetString("FilterNamespaces") ?? "{}", Json) ?? new()
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private static string EscapeFilter(string value) => value.Replace("'", "''");
    private static string EncodeKey(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private async Task<TableClient> GetTableAsync()
    {
        if (_table is not null) return _table;
        _table = _serviceClient.GetTableClient(TableName);
        await _table.CreateIfNotExistsAsync();
        return _table;
    }
}
