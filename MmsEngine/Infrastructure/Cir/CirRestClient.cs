using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MmsEngine.Infrastructure.Cir;

/// <summary>
/// The slice of the CIR model this engine writes.
///
/// Declared locally for the same reason the MMS contracts are: CIR is reached
/// over HTTP as a peer system, and a ProjectReference would let its storage
/// concerns compile into this one.
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
/// The shape CIR answers GetEntriesByCIRID with: a registry graph wrapped in a
/// single property. Declared here rather than reusing the request records so a
/// change to what this engine writes cannot silently change what it reads.
/// </summary>
internal sealed record CirRegistryResponse(IReadOnlyList<CirReadRegistry>? Registry);

internal sealed record CirReadRegistry(IReadOnlyList<CirReadCategory>? Categories);

internal sealed record CirReadCategory(IReadOnlyList<CirReadEntry>? Entries);

internal sealed record CirReadEntry(string? IdInSource, string? SourceId, bool? Inactive);

public interface ICirClient
{
    /// <summary>
    /// Registers sites as CIR entries under their federation GUID.
    ///
    /// Returns the number actually written. Entries that were already there are
    /// not an error: republication is expected here, and an ingest that failed
    /// because it found its own previous work would never make progress.
    /// </summary>
    Task<int> RegisterEntriesAsync(CreateRegistryRequest request, CancellationToken ct);

    /// <summary>
    /// The identifier <paramref name="sourceId"/> already holds for the object
    /// with this CIRID, or null if the registry has never been told of one.
    ///
    /// This is the first question the sites leg asks: MMS stores no CIRID of
    /// its own -- SETUP_OWNER has no column for one -- so the registry is the
    /// only place the site GUID is related to an OWNER_ID. Asking it before
    /// matching on name is what lets a renamed site find its existing owner
    /// instead of creating a second one.
    /// </summary>
    Task<string?> FindIdInSourceAsync(Guid cirid, string sourceId, CancellationToken ct);
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
        // normal outcome of re-receiving a site the engine has seen before, so it
        // is reported as nothing-written rather than raised: the identity CIR
        // already holds is the identity this call was trying to establish.
        if (response.StatusCode == HttpStatusCode.Conflict)
            return 0;

        var body = await response.Content.ReadAsStringAsync(ct);

        throw new CirClientException(
            $"CIR returned {(int)response.StatusCode} registering {count} entry(ies): {body}");
    }

    public async Task<string?> FindIdInSourceAsync(
        Guid cirid, string sourceId, CancellationToken ct)
    {
        if (cirid == Guid.Empty)
            return null;

        var uri = $"entries?cirid={cirid:D}&targetSourceId={Uri.EscapeDataString(sourceId)}";

        using var response = await http.GetAsync(uri, ct);

        // A CIRID the registry has never seen is the ordinary case for a site
        // arriving for the first time, not a fault.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new CirClientException(
                $"CIR returned {(int)response.StatusCode} resolving CIRID {cirid}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<CirRegistryResponse>(Json, ct);

        // targetSourceId already constrains the server side; SourceId is
        // rechecked because that filter is a regex match and a source named as a
        // prefix of another would otherwise be accepted.
        return result?.Registry?
            .SelectMany(r => r.Categories ?? [])
            .SelectMany(c => c.Entries ?? [])
            .Where(e => e.Inactive != true)
            .FirstOrDefault(e => string.Equals(
                e.SourceId, sourceId, StringComparison.OrdinalIgnoreCase))
            ?.IdInSource;
    }
}

public sealed class CirClientException(string message, Exception? inner = null)
    : Exception(message, inner);
