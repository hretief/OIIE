namespace Oiie.Participant.Engine.Plan;

/// §4.4 — resource → endpoint. A PARTICIPANT artifact, deployed with the
/// participant. The prior generation put the BOD-to-stored-procedure mapping in
/// orchestration config, so a change to the customer's schema became a change to
/// orchestration. Putting this file in shared config recreates that leak.
public sealed class ResourceBindings
{
    private readonly IReadOnlyDictionary<string, ResourceBinding> _byName;

    public ResourceBindings(IReadOnlyDictionary<string, ResourceBinding> byName) => _byName = byName;

    public bool Knows(string resource) => _byName.ContainsKey(resource);

    public ResourceBinding Get(string resource) =>
        _byName.TryGetValue(resource, out var b)
            ? b
            // No fallback, by design. Writing a light unit into an arbitrary
            // table is corruption, not degraded behaviour (DR-030).
            : throw new UnboundResourceException(resource);
}

public sealed record ResourceBinding(
    string Resource,
    RouteSpec Upsert,
    RouteSpec? Read,
    RouteSpec? Delete);

public sealed record RouteSpec(string Method, string Path, string? MatchOn);

public sealed class UnboundResourceException(string resource)
    : Exception($"No binding declared for resource '{resource}'.")
{
    public string Resource { get; } = resource;
}
