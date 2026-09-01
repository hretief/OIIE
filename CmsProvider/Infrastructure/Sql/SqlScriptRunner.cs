using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace CmsProvider.Infrastructure.Sql;

/// <summary>
/// Runs an embedded .sql script against CMS's database.
///
/// Exists because two callers need the same two behaviours: reading DDL out of
/// the assembly, and splitting it on GO. GO is a batch separator understood by
/// tools, not T-SQL, so SqlClient rejects a script containing it -- a detail
/// worth encoding once rather than rediscovering per caller.
///
/// Deliberately a per-project copy rather than a shared package. This app
/// emulates a customer system and takes no ProjectReference on anything in the
/// solution; a shared helper would be the first crack in that isolation, and
/// the file is small enough that duplication is the cheaper price.
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
