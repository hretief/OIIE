using CirProvider.Domain;

namespace CirProvider.Application;

public sealed class CirOptions
{
    public string SqlConnectionString { get; set; } = string.Empty;
    public bool AutoCreateSchema { get; set; } = true;

    /// <summary>When false, CreateRegistry throws CreateRegistryFault for unknown RegistryIds (§3.1.1).</summary>
    public bool AllowNewRegistries { get; set; } = true;

    /// <summary>When false, CreateRegistry throws CreateCategoryFault for unknown Categories (§3.1.1).</summary>
    public bool AllowNewCategories { get; set; } = true;
}

/// <summary>
/// Persistence port for the ws-CIR object registry. Every method is atomic:
/// §3.1 forbids partial creates/updates/deletes when a fault is thrown.
/// </summary>
public interface ICirStore
{
    // --- Command services (§3.1) --------------------------------------------

    Task CreateRegistryAsync(CreateRegistryRequest request, CancellationToken ct = default);

    Task CreateEquivalentEntriesAsync(IReadOnlyList<EquivalentEntryRequest> requests, CancellationToken ct = default);

    Task UpdateRegistryAsync(IReadOnlyList<Registry> registries, CancellationToken ct = default);

    Task UpdateEntryCiridAsync(UpdateEntryCiridRequest request, CancellationToken ct = default);

    Task DeleteRegistryAsync(string registryId, CancellationToken ct = default);

    Task DeleteCategoryAsync(CategoryIdentifier id, CancellationToken ct = default);

    Task DeleteEntriesAsync(IReadOnlyList<EntryIdentifier> ids, CancellationToken ct = default);

    Task DeletePropertiesAsync(IReadOnlyList<PropertyIdentifier> ids, CancellationToken ct = default);

    // --- Query services (§3.2) ----------------------------------------------

    Task<IReadOnlyList<Registry>> GetRegistryAsync(IReadOnlyList<CirFilter> filters, CancellationToken ct = default);

    /// <summary>
    /// Returns the specified Entries together with their equivalents, so the
    /// client can correlate by CIRID (§3.2.2).
    /// </summary>
    Task<IReadOnlyList<Registry>> GetEquivalentEntriesAsync(
        IReadOnlyList<EntryIdentifier> identifiers,
        IReadOnlyList<string> targetSourceIds,
        CancellationToken ct = default);

    /// <summary>
    /// Returns Entries carrying the given CIRID. Unlike GetEquivalentEntries this
    /// excludes nothing — there is no "specified entry" to omit (§3.2.3).
    /// </summary>
    Task<IReadOnlyList<Registry>> GetEntriesByCiridAsync(
        Guid cirid,
        IReadOnlyList<string> targetSourceIds,
        CancellationToken ct = default);

    Task<bool> PingAsync(CancellationToken ct = default);
}
