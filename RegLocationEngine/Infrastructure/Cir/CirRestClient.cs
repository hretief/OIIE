using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RegLocationEngine.Infrastructure.Cir;

/// <summary>
/// The slice of the CIR model this engine writes.
///
/// Declared locally for the same reason the REG-LOCATION contracts are: CIR is
/// reached over HTTP as a peer system, and a ProjectReference would let its
/// storage concerns compile into this one.
/// </summary>
public sealed record CirPropertyValue(string? Key, string Value, string? UnitOfMeasure = null);

public sealed record CirProperty(string Id, string? DataType, IReadOnlyList<CirPropertyValue> PropertyValue);

public sealed record CirLocalizedText(string Value, string? LanguageId = null);

public sealed record CirEntry(
    string IdInSource,
    string SourceId,
    Guid? Cirid,
    string? SourceOwnerId,
    string? Name,
    CirLocalizedText? Description,
    IReadOnlyList<CirProperty> Properties);

public sealed record CirCategory(
    string Id,
    string SourceId,
    IReadOnlyList<CirLocalizedText> Description,
    IReadOnlyList<CirEntry> Entries);

public sealed record CirRegistry(
    string Id,
    IReadOnlyList<CirLocalizedText> Description,
    IReadOnlyList<CirCategory> Categories);

public sealed record CreateRegistryRequest(IReadOnlyList<CirRegistry> Registry, bool CreateCirid);

/// <summary>
/// Names one CIR entry by the five-part composite key CIR matches on.
/// </summary>
/// <remarks>
/// Every part is required. Getting one wrong does not match a different entry,
/// it matches none, so a cancel built from the wrong field silently removes
/// nothing.
///
/// The trap is <paramref name="EntryIdInSource"/>: for the entries this engine
/// writes it is the REG-LOCATION integer id -- the scopeId or itemId -- not the
/// federation GUID. The GUID lives in the entry's Cirid.
/// </remarks>
public sealed record CirEntryIdentifier(
    string RegistryId,
    string CategoryId,
    string CategorySourceId,
    string EntryIdInSource,
    string EntrySourceId);

public interface ICirClient
{
    /// <summary>
    /// Registers approved tags as CIR entries under their federation GUID.
    ///
    /// Returns the number actually written. Entries that were already there are
    /// not an error: republication is expected here, and a sweep that failed
    /// because it found its own previous work would never make progress.
    /// </summary>
    Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct);

    /// <summary>
    /// Removes CIR entries, and reports how many were actually there to remove.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="RegisterEntriesAsync"/>, and tolerant in the same
    /// way: an entry already gone is the state the caller wanted, so it counts as
    /// nothing-removed rather than raising.
    ///
    /// That tolerance is required, not merely convenient. A delete can fail
    /// partway and be redelivered, and the second pass necessarily finds the
    /// entries the first one removed.
    /// </remarks>
    Task<int> CancelEntriesAsync(IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct);
}

public sealed class CirRestClient(HttpClient http, ILogger<CirRestClient> logger) : ICirClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Posted one category at a time rather than as the single batch the request
    /// describes.
    ///
    /// CIR rejects a whole request with one Conflict when any entry in it is
    /// already registered, and callers routinely mix the two: a site publication
    /// carries a new ITWIN-SITE entry next to the SITE-TYPE entry that every
    /// previous site already established. Sent together, the duplicate type
    /// suppressed the new site -- CIR wrote nothing and the engine reported
    /// nothing written, which is indistinguishable from success at the call
    /// site. Split, the duplicate category is the only one that conflicts and
    /// the new one still lands.
    /// </summary>
    public async Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct)
    {
        var written = 0;

        foreach (var registry in request.Registry)
        {
            foreach (var category in registry.Categories)
            {
                if (category.Entries.Count == 0)
                    continue;

                written += await RegisterCategoryAsync(registry, category, request.CreateCirid, ct);
            }
        }

        return written;
    }

    private async Task<int> RegisterCategoryAsync(
        CirRegistry registry, CirCategory category, bool createCirid, CancellationToken ct)
    {
        var single = new CreateRegistryRequest(
            [new CirRegistry(registry.Id, registry.Description, [category])],
            createCirid);

        var count = category.Entries.Count;

        using var response = await http.PostAsJsonAsync("registries", single, Json, ct);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "CIR accepted {Count} entry(ies) for {Registry}/{Category} ({Status}).",
                count, registry.Id, category.Id, (int)response.StatusCode);

            return count;
        }

        // CIR answers a repeated entry with a DuplicateEntryFault. That is the
        // normal outcome of republishing a tag the sweep has seen before, so it
        // is reported as nothing-written rather than raised: the identity CIR
        // already holds is the identity this call was trying to establish.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            logger.LogInformation(
                "CIR already holds the {Count} entry(ies) for {Registry}/{Category}; nothing written.",
                count, registry.Id, category.Id);

            return 0;
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new CirClientException(
            $"CIR returned {(int)response.StatusCode} registering {count} entry(ies) " +
            $"for {registry.Id}/{category.Id}: {body}");
    }

    /// <summary>
    /// Sent one entry per request, though the route accepts a list.
    /// </summary>
    /// <remarks>
    /// CIR deletes a batch in a single transaction and walks to each entry as it
    /// goes, so the first identifier that is already gone raises
    /// EntryNotFoundFault and rolls back every deletion in the request. A batch
    /// is therefore all-or-nothing in exactly the case that matters: a redelivered
    /// delete, where some entries were removed by the earlier attempt and some
    /// were not. Batched, that request can never succeed and the entries that
    /// remain are stranded permanently.
    ///
    /// One request each costs N round trips and makes every one independently
    /// retryable. This is the same reasoning that split the registration path.
    /// </remarks>
    public async Task<int> CancelEntriesAsync(
        IReadOnlyList<CirEntryIdentifier> entries, CancellationToken ct)
    {
        var removed = 0;

        foreach (var entry in entries)
        {
            if (await CancelEntryAsync(entry, ct))
                removed++;
        }

        return removed;
    }

    private async Task<bool> CancelEntryAsync(CirEntryIdentifier entry, CancellationToken ct)
    {
        // The wire contract is a list even for one entry: this is the batch route,
        // and CIR reads the body as an array.
        using var response = await http.PostAsJsonAsync(
            "entries/batch-delete", new[] { entry }, Json, ct);

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "CIR removed {Registry}/{Category} entry '{Entry}'.",
                entry.RegistryId, entry.CategoryId, entry.EntryIdInSource);

            return true;
        }

        // 404 covers the entry, its category and its registry alike, and all three
        // mean the same thing here: what this call was asked to remove is not
        // there. Distinguishing them would only let a caller act on a difference
        // that does not exist.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation(
                "CIR holds no {Registry}/{Category} entry '{Entry}'; nothing removed.",
                entry.RegistryId, entry.CategoryId, entry.EntryIdInSource);

            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new CirClientException(
            $"CIR returned {(int)response.StatusCode} removing " +
            $"{entry.RegistryId}/{entry.CategoryId} entry '{entry.EntryIdInSource}': {body}");
    }
}

public sealed class CirClientException(string message, Exception? inner = null)
    : Exception(message, inner);
