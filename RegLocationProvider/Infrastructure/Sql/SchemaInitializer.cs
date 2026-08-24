using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RegLocationProvider.Application;

namespace RegLocationProvider.Infrastructure.Sql;

/// <summary>
/// Applies schema.sql, then bootstrap.sql, at startup. Both scripts are
/// re-runnable, so this is safe on every cold start and on scale-out. Requires
/// db_ddladmin.
///
/// Order matters and is not negotiable: the bootstrap seeds rows into tables
/// the schema creates, so applying it first would fail on a fresh database.
/// </summary>
public sealed partial class SchemaInitializer(
    IOptions<RegLocationOptions> options,
    ILogger<SchemaInitializer> logger) : IHostedService
{
    private readonly RegLocationOptions _options = options.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
        {
            logger.LogWarning("RegLocation__SqlConnectionString is not configured; skipping schema initialization.");
            return;
        }

        try
        {
            await using var cn = new SqlConnection(_options.SqlConnectionString);
            await cn.OpenAsync(ct);

            if (_options.AutoCreateSchema)
            {
                await ExecuteScriptAsync(cn, "RegLocationProvider.Infrastructure.Sql.schema.sql", ct);
                logger.LogInformation("REG-LOCATION schema verified.");
            }
            else
            {
                logger.LogInformation("Schema auto-creation disabled.");
            }

            if (_options.AutoBootstrap)
            {
                await ExecuteScriptAsync(cn, "RegLocationProvider.Infrastructure.Sql.bootstrap.sql", ct);
                logger.LogInformation("REG-LOCATION bootstrap verified.");
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

    private static async Task ExecuteScriptAsync(SqlConnection cn, string resourceName, CancellationToken ct)
    {
        var sql = await ReadEmbeddedAsync(resourceName);

        // SqlClient cannot execute GO; it is a batch separator, not T-SQL.
        foreach (var batch in GoSeparator().Split(sql))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            await using var cmd = new SqlCommand(batch, cn) { CommandTimeout = 120 };
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<string> ReadEmbeddedAsync(string name)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
