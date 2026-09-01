namespace RegLocationProvider.Application;

/// <summary>
/// Raised when a write would break a registry rule the caller could have
/// avoided -- a duplicate code, a missing parent, a half-filled context pair.
///
/// Distinct from an unexpected SqlException so the HTTP layer can answer 409 or
/// 400 rather than 500. A constraint doing its job is not a server fault.
/// </summary>
public sealed class RegistryConflictException(string message) : Exception(message);

/// <summary>
/// The REG-LOCATION registry store.
///
/// Expressed in the registry's own vocabulary -- object, scope, namespace,
/// class, item, tag. It knows nothing of CCOM, Segments, BODs or stewardship
/// queues. A functional location registry does not know it is being integrated
/// with; translation is the integrator's job.
///
/// Note what is absent: nothing here publishes. A tag created or retired stops
/// at the database. Carrying that change onto a channel is the engine's work,
/// and it lives outside this project.
/// </summary>
public interface IRegLocationStore
{
    // ---- Scopes -----------------------------------------------------------

    Task<IReadOnlyList<RegScope>> GetScopesAsync(CancellationToken ct);

    Task<RegScope?> FindScopeAsync(int scopeId, CancellationToken ct);

    Task<RegScope> CreateScopeAsync(CreateScopeRequest request, CancellationToken ct);

    Task<bool> DeleteScopeAsync(int scopeId, CancellationToken ct);

    // ---- Catalogue --------------------------------------------------------

    Task<IReadOnlyList<RegNamespace>> GetNamespacesAsync(CancellationToken ct);

    Task<IReadOnlyList<RegClass>> GetClassesAsync(int? namespaceId, CancellationToken ct);

    Task<IReadOnlyList<RegUnit>> GetUnitsAsync(CancellationToken ct);

    // ---- Items ------------------------------------------------------------

    Task<IReadOnlyList<RegItem>> GetItemsAsync(int? namespaceId, CancellationToken ct);

    Task<RegItem?> FindItemAsync(int itemId, CancellationToken ct);

    Task<RegItem> CreateItemAsync(CreateItemRequest request, CancellationToken ct);

    Task<bool> DeleteItemAsync(int itemId, CancellationToken ct);

    // ---- Tags -------------------------------------------------------------

    Task<IReadOnlyList<RegTagDetail>> GetTagsAsync(int? itemId, int? scopeId, CancellationToken ct);

    Task<RegTagDetail?> FindTagAsync(int tagId, CancellationToken ct);

    /// <summary>
    /// Finds tags by the code a person would read off a plate.
    ///
    /// Returns a list, not a single tag: code is unique only per item and
    /// revision, so the same code legitimately appears on several rows. A
    /// single-result signature here would force an arbitrary winner.
    /// </summary>
    Task<IReadOnlyList<RegTagDetail>> FindTagsByCodeAsync(string code, int? revision, CancellationToken ct);

    /// <summary>
    /// Finds tags by their federation GUID.
    ///
    /// This is the lookup for a caller holding an identifier minted elsewhere.
    /// It returns a list, not a single tag, and that is not a hedge: the GUID
    /// identifies the functional location itself and deliberately survives
    /// revision, so every revision of one tag carries the same GUID. The
    /// bootstrap seeds exactly that case for TIC-101. A single-valued signature
    /// would silently drop revisions.
    /// </summary>
    Task<IReadOnlyList<RegTagDetail>> FindTagsByGuidAsync(Guid guid, CancellationToken ct);

    Task<RegTagDetail> CreateTagAsync(CreateTagRequest request, CancellationToken ct);

    Task<RegTagDetail?> UpdateTagAsync(int tagId, UpdateTagRequest request, CancellationToken ct);

    /// <summary>
    /// Finds tags awaiting a steward's decision.
    ///
    /// This is the queue a steward works, so it is a first-class query rather
    /// than a filter the caller applies after fetching everything: the proposed
    /// rows are a small minority, and reading the whole registry to find them
    /// gets slower exactly as the registry succeeds.
    /// </summary>
    Task<IReadOnlyList<RegTagDetail>> FindTagsByStateAsync(string state, CancellationToken ct);

    /// <summary>
    /// Records a steward's approval, admitting a proposed tag to the registry.
    ///
    /// Returns null when there is no such tag, and throws
    /// <see cref="RegistryConflictException"/> when the tag is not in a state an
    /// approval can act on. The two are different answers: the first says the
    /// caller is talking about nothing, the second says it is talking about
    /// something whose decision has already been made. Collapsing them would let
    /// a double approval look like a missing tag.
    ///
    /// Approving is deliberately not <see cref="UpdateTagAsync"/> with a state
    /// field. An edit and a decision have different authority behind them, and a
    /// route that could do either would let anything able to rename a tag also
    /// release it to operations.
    /// </summary>
    Task<RegTagDetail?> ApproveTagAsync(int tagId, ApproveTagRequest request, CancellationToken ct);

    Task<bool> DeleteTagAsync(int tagId, CancellationToken ct);

    // ---- Health -----------------------------------------------------------

    /// <summary>
    /// Reports whether the registry invariant actually holds right now.
    ///
    /// Connectivity alone is a weak health signal: the schema can be reachable
    /// and still be wrong, and the failure that matters here is a domain row
    /// with no dbo.objects row behind it. That is exactly what the foreign keys
    /// and delete triggers exist to prevent, so a non-zero orphan count means
    /// something disabled or bypassed them.
    /// </summary>
    Task<RegistryHealth> GetHealthAsync(CancellationToken ct);
}

/// <summary>
/// The registry's own account of itself. UntrustedConstraints counts foreign
/// keys that are disabled or not trusted -- a constraint SQL Server is no
/// longer enforcing looks identical to a healthy one until something violates it.
/// </summary>
public sealed record RegistryHealth(
    int Scopes,
    int Tags,
    int Items,
    int OrphanedRows,
    int UntrustedConstraints)
{
    public bool IsHealthy => OrphanedRows == 0 && UntrustedConstraints == 0;
}
