using EngProvider.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EngProvider.Infrastructure.Sql;

/// <summary>
/// Applies schema.sql, then bootstrap.sql, at startup. Both scripts are
/// idempotent, so this is safe on every cold start and on scale-out. Requires
/// db_ddladmin.
///
/// Order matters and is not negotiable: the bootstrap seeds rows into tables
/// the schema creates, so applying it first would fail on a fresh database.
/// </summary>
public sealed class SchemaInitializer(
    IOptions<EngOptions> options,
    ILogger<SchemaInitializer> logger) : IHostedService
{
    private readonly EngOptions _options = options.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
        {
            logger.LogWarning("Eng__SqlConnectionString is not configured; skipping schema initialization.");
            return;
        }

        try
        {
            if (_options.AutoCreateSchema)
            {
                await SqlScriptRunner.ExecuteAsync(
                    _options.SqlConnectionString, "EngProvider.Infrastructure.Sql.schema.sql", ct);

                logger.LogInformation("ENG schema verified.");
            }
            else
            {
                logger.LogInformation("Schema auto-creation disabled.");
            }

            if (_options.AutoBootstrap)
            {
                await SqlScriptRunner.ExecuteAsync(
                    _options.SqlConnectionString, "EngProvider.Infrastructure.Sql.bootstrap.sql", ct);

                logger.LogInformation("ENG class catalog verified.");
            }
            else
            {
                logger.LogInformation("Bootstrap seeding disabled.");
            }
        }
        catch (Exception ex)
        {
            // Don't take the host down; the health endpoint will surface the problem.
            logger.LogError(ex, "Schema initialization failed.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
