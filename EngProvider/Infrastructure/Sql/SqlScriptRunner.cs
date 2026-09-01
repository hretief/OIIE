using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EngProvider.Infrastructure.Sql;

/// <summary>
/// Runs an embedded .sql script against ENG's database.
///
/// Extracted from SchemaInitializer once a second caller (the reset path) needed
/// the same two behaviours: reading DDL out of the assembly, and splitting it on
/// GO. GO is a batch separator understood by tools, not T-SQL, so SqlClient
/// rejects a script containing it — a detail worth encoding once rather than
/// rediscovering per caller.
/// </summary>
public static partial class SqlScriptRunner
{
    public static async Task ExecuteAsync(
        string connectionString, string resourceName, CancellationToken ct)
    {
        var ddl = await ReadEmbeddedAsync(resourceName);

        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);

        foreach (var batch in GoSeparator().Split(ddl))
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
