namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// A provider app could not be reached, or answered something unusable.
///
/// Distinct from "the provider answered, and the answer was empty". A panel
/// showing no segments because the design is empty and a panel showing no
/// segments because ENG is down look identical to a viewer, and the difference
/// is the whole diagnostic. The admin endpoints turn this into a 502 naming the
/// provider, so the failure is attributed to the system that actually failed
/// rather than to the sandbox.
/// </summary>
public sealed class ProviderUnavailableException(string provider, string route, Exception inner)
    : Exception($"The {provider} provider did not answer '{route}'.", inner)
{
    /// <summary>Which provider failed, for the message shown to the operator.</summary>
    public string Provider { get; } = provider;

    /// <summary>The route attempted, for the log rather than the response.</summary>
    public string Route { get; } = route;
}
