using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SimHost.Application.Participants;

namespace SimHost.Infrastructure.Sql;

public interface IParticipantSchemaInitializer
{
    Task<bool> EnsureTablesAsync(string participantId, CancellationToken ct = default);

    Task DropTablesAsync(string participantId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListTablesAsync(string participantId, CancellationToken ct = default);
}

/// <summary>
/// Creates each participant's tables directly from the EF model.
///
/// EF migrations are deliberately not used. `dotnet ef migrations add` bakes the
/// schema name into the generated SQL, and this model is parameterised across ten
/// schemas — one migration set cannot serve them all without rewriting migration
/// output at apply time. More to the point, every session starts from reset
/// (spec §10), so there is no data to migrate and no history worth keeping.
///
/// Database.EnsureCreated is also unsuitable: it short-circuits when the database
/// already exists, which it always does here, and would create nothing.
/// </summary>
public sealed class ParticipantSchemaInitializer : IParticipantSchemaInitializer
{
    private readonly IParticipantDbContextFactory _factory;
    private readonly ParticipantRegistry _registry;
    private readonly ILogger<ParticipantSchemaInitializer> _logger;

    /// <summary>Presence of this table is taken to mean the schema is initialised.</summary>
    private const string SentinelTable = "Message";

    public ParticipantSchemaInitializer(
        IParticipantDbContextFactory factory,
        ParticipantRegistry registry,
        ILogger<ParticipantSchemaInitializer> logger)
    {
        _factory = factory;
        _registry = registry;
        _logger = logger;
    }

    /// <returns>True when tables were created; false when they already existed.</returns>
    public async Task<bool> EnsureTablesAsync(string participantId, CancellationToken ct = default)
    {
        var schema = _registry.Get(participantId).Schema;
        await using var context = _factory.Create(participantId);

        if (await TableExistsAsync(context, schema, SentinelTable, ct))
        {
            _logger.LogDebug("Schema {Schema} already initialised", schema);
            return false;
        }

        var creator = context.Database.GetService<IRelationalDatabaseCreator>();
        await creator.CreateTablesAsync(ct);

        var tables = await ListTablesAsync(participantId, ct);
        _logger.LogInformation(
            "Created {Count} tables in schema {Schema} for {ParticipantId}",
            tables.Count, schema, participantId);

        return true;
    }

    /// <summary>
    /// Drops every table in the participant's schema. Foreign keys are dropped first
    /// so table order does not matter — the alternative is a topological sort that
    /// would need maintaining as the model grows.
    /// </summary>
    public async Task DropTablesAsync(string participantId, CancellationToken ct = default)
    {
        var schema = _registry.Get(participantId).Schema;
        await using var context = _factory.Create(participantId);

        const string dropSql = """
            DECLARE @sql NVARCHAR(MAX) = N'';

            SELECT @sql += N'ALTER TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name)
                         + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
            FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema;

            SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema;

            IF LEN(@sql) > 0 EXEC sp_executesql @sql;
            """;

        await context.Database.ExecuteSqlRawAsync(
            dropSql,
            [new SqlParameter("@schema", schema)],
            ct);

        _logger.LogInformation("Dropped all tables in schema {Schema}", schema);
    }

    public async Task<IReadOnlyList<string>> ListTablesAsync(
        string participantId, CancellationToken ct = default)
    {
        var schema = _registry.Get(participantId).Schema;
        await using var context = _factory.Create(participantId);

        return await context.Database
            .SqlQueryRaw<string>(
                "SELECT t.name AS Value FROM sys.tables t " +
                "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE s.name = {0} ORDER BY t.name",
                schema)
            .ToListAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(
        ParticipantDbContext context, string schema, string table, CancellationToken ct)
    {
        var count = await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sys.tables t " +
                "JOIN sys.schemas s ON s.schema_id = t.schema_id " +
                "WHERE s.name = {0} AND t.name = {1}",
                schema, table)
            .SingleAsync(ct);

        return count > 0;
    }
}
