namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// Where the customer-system provider apps live, and the key to reach them.
///
/// Bound from <c>Providers:Eng</c> and <c>Providers:RegLocation</c>. Both are
/// optional: absent configuration is the signal to keep serving the affected
/// panels from the sandbox's own participants, which is what a fresh clone with
/// no Azure access does. See <see cref="IsConfigured"/>.
///
/// The key is a plain setting rather than a managed identity because the
/// provider apps use <c>AuthorizationLevel.Function</c>, which has no notion of
/// a bearer token. It is held here, server-side, and never reaches the browser.
/// </summary>
public sealed class ProviderEndpointOptions
{
    /// <summary>
    /// The app's root, e.g. <c>https://acme-api-eng-dev.azurewebsites.net/api</c>.
    ///
    /// Includes the <c>/api</c> prefix because that is the Functions route
    /// prefix and the route templates below are written relative to it.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>The value sent as <c>x-functions-key</c>.</summary>
    public string? Key { get; set; }

    /// <summary>
    /// True when this provider can actually be called.
    ///
    /// A base URL without a key is treated as unconfigured rather than as an
    /// attempt worth making: every request would answer 401, and a panel of
    /// authentication errors is a worse diagnostic than the sandbox data the
    /// fallback shows.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// The provider apps the sandbox reads through to.
///
/// Only ENG and REG-LOCATION are here. CMS and MMS panels still read the
/// sandbox's own participants, and giving them settings that nothing consumed
/// would imply otherwise.
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    public ProviderEndpointOptions Eng { get; set; } = new();

    public ProviderEndpointOptions RegLocation { get; set; } = new();
}
