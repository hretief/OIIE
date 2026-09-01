using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
}

public sealed class CirRestClient(HttpClient http) : ICirClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct)
    {
        var count = request.Registry
            .SelectMany(r => r.Categories)
            .SelectMany(c => c.Entries)
            .Count();

        if (count == 0)
            return 0;

        using var response = await http.PostAsJsonAsync("registries", request, Json, ct);

        if (response.IsSuccessStatusCode)
            return count;

        // CIR answers a repeated entry with a DuplicateEntryFault. That is the
        // normal outcome of republishing a tag the sweep has seen before, so it
        // is reported as nothing-written rather than raised: the identity CIR
        // already holds is the identity this call was trying to establish.
        if (response.StatusCode == HttpStatusCode.Conflict)
            return 0;

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new CirClientException(
            $"CIR returned {(int)response.StatusCode} registering {count} entry(ies): {body}");
    }
}

public sealed class CirClientException(string message, Exception? inner = null)
    : Exception(message, inner);
