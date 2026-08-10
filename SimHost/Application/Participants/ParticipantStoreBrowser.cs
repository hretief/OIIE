using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Participants;

/// <summary>One table's contents, as read from a participant's own schema.</summary>
/// <param name="TotalRows">
/// The row count before the cap, so a truncated grid can say how much it is not showing.
/// A grid that silently stops at its limit invites the conclusion that the table ends there.
/// </param>
public sealed record StoreTable(
    string TableName,
    string EntityName,
    bool IsInfrastructure,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int TotalRows,
    string? Error)
{
    public bool IsTruncated => TotalRows > Rows.Count;
}

/// <summary>Every table in one participant's schema.</summary>
public sealed record StoreContents(
    string ParticipantId,
    IReadOnlyList<StoreTable> Tables,
    string? Error);

/// <summary>
/// Reads a participant's tables for display, connecting as that participant's own
/// contained user so the browser is subject to the same grants as the participant.
///
/// Reading through a privileged login would have been simpler and would have shown rows
/// no participant can actually see, which is the opposite of what the sandbox exists to
/// demonstrate. If a table is unreadable here, that is a finding rather than a bug to
/// route around.
///
/// Tables and columns come from the EF model rather than a hand-kept list, so a table
/// added to <see cref="ParticipantDbContext"/> appears here without a second edit. The
/// alternative rots quietly: the list stays plausible while the schema moves on.
/// </summary>
public sealed class ParticipantStoreBrowser(
    IParticipantDbContextFactory contextFactory,
    ILogger<ParticipantStoreBrowser> logger)
{
    /// <summary>
    /// Rows read per table. A cap rather than paging: this is an "is it in there" view, and
    /// the tables that grow without bound are the infrastructure ones nobody scrolls.
    /// </summary>
    public const int RowLimit = 100;

    /// <summary>
    /// Namespaces holding the plumbing every participant carries. Everything else is that
    /// participant's own domain, which is what someone opening the expander came to see.
    /// </summary>
    private static readonly string[] InfrastructureNamespaces =
    [
        "SimHost.Domain.Common",
        "SimHost.Application.Identity"
    ];

    public async Task<StoreContents> ReadAsync(string participantId, CancellationToken ct = default)
    {
        try
        {
            await using var db = contextFactory.Create(participantId);
            var schema = db.Schema;

            // Ordered domain-first, then alphabetically, so the tables that answer a
            // question about the scenario are not buried under Message and Outbox.
            var entityTypes = db.Model.GetEntityTypes()
                .Select(e => new
                {
                    Entity = e,
                    Table = e.GetTableName(),
                    IsInfrastructure = IsInfrastructure(e.ClrType)
                })
                .Where(x => x.Table is not null)
                .OrderBy(x => x.IsInfrastructure)
                .ThenBy(x => x.Table, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(ct);

            var tables = new List<StoreTable>();

            foreach (var item in entityTypes)
            {
                var columns = item.Entity.GetProperties()
                    .Select(p => p.GetColumnName())
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();

                if (columns.Count == 0)
                {
                    continue;
                }

                tables.Add(await ReadTableAsync(
                    connection,
                    schema,
                    item.Table!,
                    item.Entity.ClrType.Name,
                    item.IsInfrastructure,
                    columns!,
                    ct));
            }

            return new StoreContents(participantId, tables, null);
        }
        catch (Exception ex)
        {
            // Failing to reach the schema at all is a different finding from a schema that
            // is present and empty, so it is reported separately rather than as no tables.
            logger.LogWarning(ex, "Store browser: {ParticipantId} could not be read.", participantId);
            return new StoreContents(participantId, [], ex.Message);
        }
    }

    private async Task<StoreTable> ReadTableAsync(
        DbConnection connection,
        string schema,
        string table,
        string entityName,
        bool isInfrastructure,
        IReadOnlyList<string> columns,
        CancellationToken ct)
    {
        try
        {
            var columnList = string.Join(", ", columns.Select(Quote));

            // Identifiers come from the EF model, never from a request, so they are quoted
            // rather than parameterised - a parameter cannot stand in for an identifier.
            var sql =
                $"SELECT COUNT_BIG(1) FROM {Quote(schema)}.{Quote(table)}";

            await using var countCommand = connection.CreateCommand();
            countCommand.CommandText = sql;
            var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct) ?? 0);

            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT TOP ({RowLimit}) {columnList} FROM {Quote(schema)}.{Quote(table)}";

            var rows = new List<IReadOnlyList<string?>>();

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[i] = Format(reader, i);
                }

                rows.Add(row);
            }

            return new StoreTable(table, entityName, isInfrastructure, columns, rows, total, null);
        }
        catch (Exception ex)
        {
            // One unreadable table must not cost the rest of the schema its display: a
            // participant denied SELECT on a single table is worth seeing precisely
            // because the grant model is what the sandbox is demonstrating.
            logger.LogWarning(ex, "Store browser: {Schema}.{Table} could not be read.", schema, table);
            return new StoreTable(table, entityName, isInfrastructure, columns, [], 0, ex.Message);
        }
    }

    private static bool IsInfrastructure(Type clrType) =>
        clrType.Namespace is { } ns
        && InfrastructureNamespaces.Any(n => ns.StartsWith(n, StringComparison.Ordinal));

    /// <summary>
    /// Renders a value for a grid cell. Nulls are returned as null rather than as an empty
    /// string so the view can distinguish "no value" from "empty string", which are
    /// different facts about a record and routinely get flattened into one.
    /// </summary>
    private static string? Format(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
            byte[] bytes => $"({bytes.Length} bytes)",
            bool b => b ? "true" : "false",
            var value => value.ToString()
        };
    }

    private static string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
