namespace Oiie.Participant.Engine.Plan;

// ─────────────────────────────────────────────────────────────────────────────
// THE SEAM WHERE THE GENERIC PLAN MEETS THE TYPED DTO.
//
// The WritePlan does not replace MmsProvider's DTOs. `GenericHttpResourceWriter`
// drives them from bindings.yaml for the common case; a participant that wants
// its generated OpenAPI client in the path supplies its own implementation and
// binds LightUnitUpsert by hand. The executor cannot tell the difference.
// ─────────────────────────────────────────────────────────────────────────────

public interface IResourceWriter
{
    Task<ResourceState?> ReadAsync(
        string resource, string matchField, string matchValue, CancellationToken ct);

    Task<ResourceState> WriteAsync(
        string resource, IReadOnlyDictionary<string, string> fields, CancellationToken ct);

    Task DeleteAsync(
        string resource, string matchField, string matchValue, CancellationToken ct);
}

public sealed record ResourceState(IReadOnlyDictionary<string, string> Fields)
{
    public string? this[string field] => Fields.GetValueOrDefault(field);
}
