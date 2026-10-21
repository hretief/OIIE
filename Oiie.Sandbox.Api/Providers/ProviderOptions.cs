namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// Where the customer-system provider apps live, and the key to reach them.
///
/// Bound from <c>Providers</c>. Live panels require configured endpoints;
/// they do not substitute sandbox or sample data when a provider is missing.
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
    /// A base URL without a key cannot authenticate to a provider.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Key);
}

/// <summary>
/// The provider apps the sandbox reads through to.
///
/// ENG, REG-LOCATION and MMS back live panels. CIR resolves the MMS site's
/// native owner key. Day zero uses the same provider settings for reset.
/// </summary>
public sealed class ProviderOptions
{
    public const string SectionName = "Providers";

    public ProviderEndpointOptions Eng { get; set; } = new();

    public ProviderEndpointOptions RegLocation { get; set; } = new();

    public ProviderEndpointOptions Mms { get; set; } = new();

    public ProviderEndpointOptions Cir { get; set; } = new();
}
