namespace SimHost.Application;

/// <summary>
/// Where the Sandbox admin API is reachable.
///
/// Needed because the admin surface and the operator UI are no longer the same
/// process. Reset and scenario launch are deliberately driven over HTTP rather than
/// by calling the underlying services — the endpoints are the definition of what a
/// reset means, and the admin-key guard sits on that path — so the caller has to know
/// where to send them. Before the split "here" was a safe answer and the UI passed its
/// own base URI; it is not a safe answer any more.
///
/// Configure <c>Sandbox:ApiBaseUrl</c> on any host that is not itself the API. The API
/// host may leave it unset, in which case callers pass their own base address and the
/// call loops back to the same process, exactly as it did before.
/// </summary>
public sealed class SandboxApiEndpoint(IConfiguration configuration)
{
    /// <summary>Configuration key holding the Sandbox API base URL.</summary>
    public const string ConfigurationKey = "Sandbox:ApiBaseUrl";

    private readonly string? _configured = configuration[ConfigurationKey];

    /// <summary>True when an explicit API base URL is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_configured);

    /// <summary>
    /// The configured API base URL, or <paramref name="fallback"/> when none is set.
    /// </summary>
    /// <param name="fallback">
    /// The caller's own base address, used only when this host is the API.
    /// </param>
    public string Resolve(string fallback) =>
        IsConfigured ? _configured!.TrimEnd('/') + "/" : fallback;
}
