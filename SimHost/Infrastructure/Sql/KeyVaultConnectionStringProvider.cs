using Microsoft.Data.SqlClient;
using SimHost.Application.Participants;

namespace SimHost.Infrastructure.Sql;

public interface IParticipantConnectionStringProvider
{
    string For(string participantId);

    /// <summary>Orchestrator and tower are not participants but need connections.</summary>
    string ForService(string serviceName);
}

/// <summary>
/// Builds one connection string per participant from Key Vault.
///
/// Each participant connects as its own contained database user, so the schema
/// grants provisioned by deploy/provision.ps1 are actually in force at runtime.
/// A shared login would leave the isolation model theoretical: a cross-schema read
/// would succeed, nobody would notice, and the demonstration would stop proving
/// that the participants are independent systems.
///
/// Secrets arrive through IConfiguration because AddAzureKeyVault projects them as
/// configuration keys — no separate SecretClient needed.
/// </summary>
public sealed class KeyVaultConnectionStringProvider : IParticipantConnectionStringProvider
{
    private readonly IConfiguration _configuration;
    private readonly ParticipantRegistry _registry;
    private readonly ILogger<KeyVaultConnectionStringProvider> _logger;

    public KeyVaultConnectionStringProvider(
        IConfiguration configuration,
        ParticipantRegistry registry,
        ILogger<KeyVaultConnectionStringProvider> logger)
    {
        _configuration = configuration;
        _registry = registry;
        _logger = logger;
    }

    public string For(string participantId) =>
        Build(participantId, $"sb_{_registry.Get(participantId).Schema}");

    /// <summary>
    /// Orchestrator and tower are not participants but need connections.
    ///
    /// Their login is named after the service rather than after a schema, because
    /// neither is confined to one: sb_orchestrator is db_owner so that reset can
    /// truncate every participant schema, and sb_tower reads across all of them.
    /// Deriving the name from a default schema would ask for sb_sandbox, which
    /// provisioning never creates.
    /// </summary>
    public string ForService(string serviceName) => Build(serviceName, $"sb_{serviceName}");

    private string Build(string principalKey, string userName)
    {
        var environment = Required("Sandbox:Environment");
        var server = Required("Sandbox:SqlServer");
        var database = Required("Sandbox:Database");
        var secretName = $"sandbox-sql-{environment}-{principalKey}";
        var password = _configuration[secretName];

        if (string.IsNullOrWhiteSpace(password))
        {
            // Falling back to a shared login here would silently disable the grant
            // model, so this is a hard failure rather than a warning.
            throw new InvalidOperationException(
                $"No Key Vault secret '{secretName}'. Run deploy/provision.ps1 for the " +
                $"'{environment}' environment, and confirm the signed-in identity can read " +
                "secrets from the vault.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{server}.database.windows.net,1433",
            InitialCatalog = database,
            UserID = userName,
            Password = password,
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = 60,
            MultipleActiveResultSets = false,
            ApplicationName = $"OiieSandbox/{principalKey}"
        };

        _logger.LogDebug(
            "Connection for {Principal} as {UserName} on {Database}",
            principalKey, userName, database);

        return builder.ConnectionString;
    }

    private string Required(string key)
    {
        var value = _configuration[key];

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Configuration '{key}' is not set.");
        }

        // An unreplaced placeholder is worse than a missing value: a wrong database
        // name surfaces as "Login failed for user", because contained-user
        // authentication happens inside the target database and the server has no
        // way to report which part was wrong.
        if (value.Contains("REPLACE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration '{key}' still holds the template placeholder '{value}'. " +
                "Set it in appsettings.Development.json — deploy/provision.ps1 prints " +
                "the correct values at the end of a run.");
        }

        return value;
    }
}
