using CirProvider.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace CirProvider.Infrastructure.Isbm;

/// <summary>
/// Persists ISBM session ids. One row per session kind, so re-opening replaces
/// rather than accumulates — a leaked session on the broker holds messages that
/// nobody will ever read.
/// </summary>
public sealed class SqlIsbmSessionStore(IOptions<CirOptions> options) : IIsbmSessionStore
{
    private readonly CirOptions _options = options.Value;

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cn = new SqlConnection(_options.SqlConnectionString);
        await cn.OpenAsync(ct);
        return cn;
    }

    public async Task<string?> GetAsync(IsbmSessionKind kind, string channelUri, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT SessionId FROM cir.IsbmSession WHERE SessionKind = @kind AND ChannelUri = @uri", cn);
        cmd.Parameters.AddWithValue("@kind", kind.ToString());
        cmd.Parameters.AddWithValue("@uri", channelUri);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SaveAsync(IsbmSessionKind kind, string channelUri, string sessionId, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            MERGE cir.IsbmSession AS target
            USING (SELECT @kind AS SessionKind) AS source
               ON target.SessionKind = source.SessionKind
            WHEN MATCHED THEN
                UPDATE SET SessionId = @sid, ChannelUri = @uri, OpenedUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (SessionKind, SessionId, ChannelUri) VALUES (@kind, @sid, @uri);
            """, cn);
        cmd.Parameters.AddWithValue("@kind", kind.ToString());
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@uri", channelUri);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearAsync(IsbmSessionKind kind, CancellationToken ct = default)
    {
        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "DELETE FROM cir.IsbmSession WHERE SessionKind = @kind", cn);
        cmd.Parameters.AddWithValue("@kind", kind.ToString());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<(IsbmSessionKind Kind, string ChannelUri, string SessionId, DateTimeOffset OpenedUtc)>>
        ListAsync(CancellationToken ct = default)
    {
        var results = new List<(IsbmSessionKind, string, string, DateTimeOffset)>();

        await using var cn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT SessionKind, ChannelUri, SessionId, OpenedUtc FROM cir.IsbmSession", cn);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add((
                Enum.Parse<IsbmSessionKind>(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        }

        return results;
    }
}
