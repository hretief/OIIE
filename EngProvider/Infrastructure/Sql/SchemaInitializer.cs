using EngProvider.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EngProvider.Infrastructure.Sql;

/// <summary>
/// Applies schema.sql at startup. The DDL is idempotent, so this is safe on every
/// cold start and on scale-out. Requires db_ddladmin.
/// </summary>
public sealed class SchemaInitializer(
    IOptions<EngOptions> options,
    ILogger<SchemaInitializer> logger) : IHostedService
{
    private readonly EngOptions _options = options.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.AutoCreateSchema)
        {
            logger.LogInformation("Schema auto-creation disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
        {
            logger.LogWarning("Eng__SqlConnectionString is not configured; skipping schema initialization.");
            return;
        }

        try
        {
            await SqlScriptRunner.ExecuteAsync(
                _options.SqlConnectionString, "EngProvider.Infrastructure.Sql.schema.sql", ct);

            logger.LogInformation("ENG schema verified.");
        }
        catch (Exception ex)
        {
            // Don't take the host down; the health endpoint will surface the problem.
            logger.LogError(ex, "Schema initialization failed.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
