namespace Oiie.Sandbox.Api.Providers;

/// <summary>
/// ENG refused to cut a marker because the iModel already has a named version
/// with that name.
///
/// Distinct from <see cref="ProviderUnavailableException"/>: ENG answered, and
/// answered plainly -- the name collides. That is a fact about the request the
/// caller can act on (pick a different name), not a fault in ENG, so it is
/// carried back to the promote endpoint as a finding rather than surfaced as an
/// unhandled 500.
/// </summary>
public sealed class NamedVersionNameConflictException(Guid iModelId, string name)
    : Exception($"iModel '{iModelId:D}' already has a named version called '{name}'.")
{
    public Guid IModelId { get; } = iModelId;
    public string VersionName { get; } = name;
}
