using System.Net.Http.Json;
using System.Text.Json;

namespace MmsEngine.Infrastructure.Mms;

/// <summary>
/// The slice of MMS's contract this engine writes, over the TAMS lighting
/// domain.
///
/// Declared locally rather than referenced. MmsProvider emulates a customer
/// system reached over HTTP, and a ProjectReference would let its storage
/// concerns compile into this one -- and would make it possible to call the
/// store directly and never notice the boundary had gone. The duplication is
/// the price of the boundary being real.
/// </summary>
public sealed record LightSystemUpsert(
    Guid ExtAssetId,
    string LightSystemName,
    long LightSystemClassCodeId,
    long? LightSystemStatusId,
    long? OwnerId,
    long? SglElecJurOwnerId,
    long? CountyId);

public sealed record UpsertedLightSystem(long LightSystemId, Guid ExtAssetId, bool Created);

public sealed record UpsertRejection(string Key, string Reason, bool Transient);

public sealed record LightSystemUpsertResult(
    IReadOnlyList<UpsertedLightSystem> Systems,
    IReadOnlyList<UpsertRejection> Rejections);

public sealed record MmsLookup(long Id, string Name, bool ActiveFlag);

/// <summary>
/// An owner this engine wants to exist in MMS.
///
/// OwnerId is null on first contact: OWNER_ID is TAMS-minted, so a site that
/// has never been seen has no key to offer. Supplying it makes the call a
/// rename, which is what happens when CIR resolved the site to an owner that
/// upstream has since renamed.
/// </summary>
public sealed record OwnerUpsert(string OwnerName, long? OwnerId = null);

public sealed record UpsertedOwner(long OwnerId, string OwnerName, bool Created);

public sealed record OwnerUpsertResult(
    IReadOnlyList<UpsertedOwner> Owners,
    IReadOnlyList<UpsertRejection> Rejections);

public sealed class MmsClientException(string message) : Exception(message);

public interface IMmsClient
{
    /// <summary>
    /// Creates or updates light systems in MMS.
    ///
    /// Sent as a batch because the provider accepts them that way and because
    /// one publication commonly carries several.
    /// </summary>
    Task<LightSystemUpsertResult> UpsertLightSystemsAsync(
        IReadOnlyList<LightSystemUpsert> systems, CancellationToken ct);

    /// <summary>
    /// TAMS's light system class codes, for resolving the fallback this engine
    /// uses when an incoming site carries no classification of its own.
    /// </summary>
    Task<IReadOnlyList<MmsLookup>> GetLightSystemClassCodesAsync(CancellationToken ct);

    /// <summary>
    /// Creates or renames owners in MMS, returning the OWNER_ID assigned to
    /// each. This is where an incoming site lands: TAMS models a site as an
    /// owner, and the returned id is what gets registered in CIR.
    /// </summary>
    Task<OwnerUpsertResult> UpsertOwnersAsync(
        IReadOnlyList<OwnerUpsert> owners, CancellationToken ct);

    /// <summary>
    /// TAMS's owners, used to match an incoming site's short name against a
    /// SETUP_OWNER row that already exists -- typically one of the customer's
    /// own seeded districts, which this engine adopts rather than duplicates.
    /// </summary>
    Task<IReadOnlyList<MmsLookup>> GetOwnersAsync(CancellationToken ct);
}

public sealed class MmsRestClient(HttpClient http) : IMmsClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<LightSystemUpsertResult> UpsertLightSystemsAsync(
        IReadOnlyList<LightSystemUpsert> systems, CancellationToken ct)
    {
        if (systems.Count == 0)
        {
            return new LightSystemUpsertResult([], []);
        }

        using var response = await http.PostAsJsonAsync("lightsystems", systems, Json, ct);

        // 503 is the provider's way of saying every system in the batch failed
        // for a reason that may not recur -- a deadlock, a database still
        // starting. It is raised rather than parsed so the caller leaves the
        // message on the channel and tries again, which is the whole point of
        // the distinction.
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new MmsClientException(
                "MMS reported every light system in the batch as transiently rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new MmsClientException(
                $"MMS refused the light system upsert ({(int)response.StatusCode}): {Excerpt(body)}");
        }

        var result = await response.Content.ReadFromJsonAsync<LightSystemUpsertResult>(Json, ct);

        return result ?? new LightSystemUpsertResult([], []);
    }

    public async Task<IReadOnlyList<MmsLookup>> GetLightSystemClassCodesAsync(CancellationToken ct)
    {
        var lookups = await http.GetFromJsonAsync<MmsLookup[]>(
            "lookups/lightsystemclasscodes", Json, ct);

        return lookups ?? [];
    }

    public async Task<OwnerUpsertResult> UpsertOwnersAsync(
        IReadOnlyList<OwnerUpsert> owners, CancellationToken ct)
    {
        if (owners.Count == 0)
        {
            return new OwnerUpsertResult([], []);
        }

        using var response = await http.PostAsJsonAsync("owners", owners, Json, ct);

        // Same contract as the light system path: 503 means every owner failed
        // for a reason that may not recur, so the caller leaves the publication
        // on the channel instead of parsing a body describing a temporary state.
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new MmsClientException(
                "MMS reported every owner in the batch as transiently rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new MmsClientException(
                $"MMS refused the owner upsert ({(int)response.StatusCode}): {Excerpt(body)}");
        }

        var result = await response.Content.ReadFromJsonAsync<OwnerUpsertResult>(Json, ct);

        return result ?? new OwnerUpsertResult([], []);
    }

    public async Task<IReadOnlyList<MmsLookup>> GetOwnersAsync(CancellationToken ct)
    {
        var lookups = await http.GetFromJsonAsync<MmsLookup[]>("lookups/owners", Json, ct);

        return lookups ?? [];
    }

    private static string Excerpt(string? body) =>
        string.IsNullOrWhiteSpace(body)
            ? "(no body)"
            : body.Length <= 400 ? body.Trim() : body[..400].Trim() + "...";
}
