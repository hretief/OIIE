using System.Reflection;
using System.Text.RegularExpressions;
using CirProvider.Application;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CirProvider.Infrastructure.Sql;

/// <summary>
/// Applies schema.sql at startup. The DDL is idempotent, so this is safe on every
/// cold start and on scale-out. Requires db_ddladmin, which deploy.ps1 grants.
/// </summary>
public sealed partial class SchemaInitializer(
    IOptions<CirOptions> options,
    ILogger<SchemaInitializer> logger) : IHostedService
{
    private readonly CirOptions _options = options.Value;

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_options.AutoCreateSchema)
        {
            logger.LogInformation("Schema auto-creation disabled.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.SqlConnectionString))
        {
            logger.LogWarning("Cir__SqlConnectionString is not configured; skipping schema initialization.");
            return;
        }

        try
        {
            var ddl = await ReadEmbeddedAsync("CirProvider.Infrastructure.Sql.schema.sql");

            await using var cn = new SqlConnection(_options.SqlConnectionString);
            await cn.OpenAsync(ct);

            // SqlClient cannot execute GO; it is a batch separator, not T-SQL.
            foreach (var batch in GoSeparator().Split(ddl))
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;
                await using var cmd = new SqlCommand(batch, cn) { CommandTimeout = 120 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            logger.LogInformation("CIR schema verified.");
        }
        catch (Exception ex)
        {
            // Don't take the host down; the health endpoint will surface the problem.
            logger.LogError(ex, "Schema initialization failed.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

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
