using Oiie.Isbm.Client;
using SimHost.Application.Participants;
using SimHost.Infrastructure.Sql;

namespace SimHost.Infrastructure.Isbm;

/// <summary>
/// Resolves the ISBM client for a participant. Each participant authenticates with
/// distinct credentials so the message archive reflects genuinely separate
/// identities and token operations are exercisable.
/// </summary>
public interface IIsbmClientAccessor
{
    IIsbmClient For(string participantId);
}

public interface IIsbmSessionStoreAccessor
{
    IIsbmSessionStore For(string participantId);

    /// <summary>
    /// The session manager, exposed here so callers that already hold the accessor
    /// get open-confirm and stale-session recovery without a second dependency.
    /// </summary>
    IsbmSessionManager Manager { get; }
}

public sealed class IsbmClientAccessor : IIsbmClientAccessor, IIsbmSessionStoreAccessor
{
    private readonly Dictionary<string, IIsbmClient> _clients;
    private readonly Dictionary<string, IIsbmSessionStore> _stores;
    private IsbmSessionManager? _manager;

    /// <summary>
    /// Set after construction: the manager depends on this accessor, so injecting it
    /// through the constructor would be circular.
    /// </summary>
    public IsbmSessionManager Manager
    {
        get => _manager ?? throw new InvalidOperationException("Session manager not attached.");
        set => _manager = value;
    }

    public IsbmClientAccessor(
        ParticipantRegistry registry,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IParticipantDbContextFactory dbFactory,
        ILoggerFactory loggerFactory)
    {
        _clients = registry.All.ToDictionary(
            p => p.ParticipantId,
            p => CreateClient(p, httpClientFactory, configuration, loggerFactory),
            StringComparer.OrdinalIgnoreCase);

        _stores = registry.All.ToDictionary(
            p => p.ParticipantId,
            IIsbmSessionStore (p) => new SqlIsbmSessionStore(
                p.ParticipantId, dbFactory, loggerFactory.CreateLogger<SqlIsbmSessionStore>()),
            StringComparer.OrdinalIgnoreCase);
    }

    public IIsbmClient For(string participantId) =>
        _clients.TryGetValue(participantId, out var client)
            ? client
            : throw new KeyNotFoundException($"No ISBM client for participant '{participantId}'.");

    IIsbmSessionStore IIsbmSessionStoreAccessor.For(string participantId) =>
        _stores.TryGetValue(participantId, out var store)
            ? store
            : throw new KeyNotFoundException($"No ISBM session store for participant '{participantId}'.");

    private static IIsbmClient CreateClient(
        ParticipantContext participant,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILoggerFactory loggerFactory)
    {
        var config = participant.Config.Isbm;

        var options = new IsbmClientOptions
        {
            BaseUrl = config.BaseUrl,
            // Channel authorization token, per participant, resolved from Key Vault
            // the same way SQL passwords are. Never a literal in configuration.
            SecurityToken = config.TokenSecretName is { Length: > 0 }
                ? configuration[config.TokenSecretName]
                : null,
            ApiKey = configuration["Isbm:ApiKey"],
            ListenerUrl = participant.Config.Channels.Any(c => c.UseNotifications)
                ? configuration["Isbm:ListenerBaseUrl"] is { Length: > 0 } baseUrl
                    ? $"{baseUrl.TrimEnd('/')}/isbm/notify/{participant.ParticipantId}"
                    : null
                : null
        };

        var http = httpClientFactory.CreateClient($"isbm:{participant.ParticipantId}");
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

        if (options.ApiKey is { Length: > 0 })
        {
            http.DefaultRequestHeaders.Add("x-functions-key", options.ApiKey);
        }

        return new IsbmRestClient(http, options, loggerFactory.CreateLogger<IsbmRestClient>());
    }
}
