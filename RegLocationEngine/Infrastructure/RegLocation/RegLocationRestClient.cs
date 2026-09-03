using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RegLocationEngine.Infrastructure.RegLocation;

public interface IRegLocationClient
{
    /// <summary>
    /// A single tag with its registry row, or null when REG-LOCATION does not
    /// have it. Null rather than an exception because a tag deleted between the
    /// notification and this read is an ordinary race, not a fault.
    /// </summary>
    Task<RegTagDetail?> GetTagAsync(int tagId, CancellationToken ct);

    /// <summary>
    /// Approved tags, optionally narrowed to one scope.
    ///
    /// This is what the reconciling sweep reads. It exists because notifications
    /// are best-effort: the provider deliberately does not fail an approval when
    /// its listener is unreachable, so something has to notice what was missed.
    /// </summary>
    Task<IReadOnlyList<RegTagDetail>> GetApprovedTagsAsync(int? scopeId, CancellationToken ct);

    /// <summary>
    /// Every tag carrying this federation GUID, empty when none do.
    ///
    /// A list, not a single tag: the GUID identifies the functional location and
    /// survives revision, so one location legitimately has several rows. This is
    /// the inbound leg's idempotency check, which is why it asks the registry
    /// rather than consulting engine state -- the registry is the thing that
    /// actually knows, and its answer stays right if the engine's state is lost
    /// or the same location arrives on two channels.
    /// </summary>
    Task<IReadOnlyList<RegTagDetail>> FindTagsByGuidAsync(Guid guid, CancellationToken ct);

    /// <summary>
    /// Files a new tag proposal.
    ///
    /// The one write this engine performs, and it deliberately cannot approve
    /// anything: it proposes, and a steward disposes.
    /// </summary>
    Task<RegTagDetail> CreateTagAsync(CreateTagRequest request, CancellationToken ct);

    // ---- Sites ------------------------------------------------------------

    /// <summary>
    /// The scope carrying this federation GUID, or null.
    ///
    /// Single-valued, unlike the tag lookup: a scope has no revisions, so one
    /// GUID names at most one scope. This is what makes site ingestion
    /// idempotent without any engine-side state.
    /// </summary>
    Task<RegScope?> FindScopeByGuidAsync(Guid guid, CancellationToken ct);

    Task<RegScope> CreateScopeAsync(CreateScopeRequest request, CancellationToken ct);

    /// <summary>
    /// Removes a scope and everything registered inside it, or null if there was
    /// no such scope.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception for an absent scope, because a redelivered
    /// site deletion necessarily finds the scope the first delivery removed. The
    /// caller treats that as already-done.
    /// </remarks>
    Task<ScopeCascadeResult?> DeleteScopeCascadeAsync(int scopeId, CancellationToken ct);

    /// <summary>
    /// Links a scope to the object whose context it represents.
    /// </summary>
    Task SetScopeContextAsync(int scopeId, SetScopeContextRequest request, CancellationToken ct);

    /// <summary>
    /// The item carrying this federation GUID, or null. This is how a site type
    /// is reused rather than recreated for each site of that type.
    /// </summary>
    Task<RegItem?> FindItemByGuidAsync(Guid guid, CancellationToken ct);

    Task<RegItem> CreateItemAsync(CreateItemRequest request, CancellationToken ct);

    /// <summary>The serial carrying this federation GUID, or null.</summary>
    Task<RegSerialDetail?> FindSerialByGuidAsync(Guid guid, CancellationToken ct);

    Task<RegSerialDetail> CreateSerialAsync(CreateSerialRequest request, CancellationToken ct);
}

/// <summary>
/// Reads REG-LOCATION over its REST surface, and files proposals into it.
///
/// The only write is <see cref="CreateTagAsync"/>, which creates tags in the
/// Proposed state. The engine has no route to approval and that absence is the
/// point: an integration component able to record a steward's decision could
/// manufacture a release nobody authorised.
/// </summary>
public sealed class RegLocationRestClient(HttpClient http) : IRegLocationClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<RegTagDetail?> GetTagAsync(int tagId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"tags/{tagId}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} for tag '{tagId}': {body}");
        }

        return await response.Content.ReadFromJsonAsync<RegTagDetail>(Json, ct);
    }

    public async Task<IReadOnlyList<RegTagDetail>> GetApprovedTagsAsync(int? scopeId, CancellationToken ct)
    {
        // The registry has no by-state list route for approved tags -- approved
        // is the normal state, so filtering happens here rather than adding a
        // route whose answer is 'nearly everything'.
        var url = scopeId is { } scope ? $"tags?scopeId={scope}" : "tags";

        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
        {
            // The body carries REG-LOCATION's problem detail. Losing it would
            // reduce every upstream refusal to a bare status code, which cannot
            // tell a bad scope id from an expired key.
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} for '{url}': {body}");
        }

        var all = await response.Content.ReadFromJsonAsync<List<RegTagDetail>>(Json, ct) ?? [];

        return [.. all.Where(t => string.Equals(t.Tag.State, "Approved", StringComparison.OrdinalIgnoreCase))];
    }

    public async Task<IReadOnlyList<RegTagDetail>> FindTagsByGuidAsync(Guid guid, CancellationToken ct)
    {
        using var response = await http.GetAsync($"tags?guid={guid:D}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} for guid '{guid:D}': {body}");
        }

        return await response.Content.ReadFromJsonAsync<List<RegTagDetail>>(Json, ct) ?? [];
    }

    public async Task<RegTagDetail> CreateTagAsync(CreateTagRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("tags", request, Json, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            // 409 is included rather than swallowed. The caller has already
            // checked the GUID, so a conflict here means a registry rule was
            // broken that the check does not cover -- a duplicate
            // code/revision under the same item, most likely. Treating it as
            // success would file nothing and report that it had.
            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} creating tag '{request.Code}': {body}");
        }

        return await response.Content.ReadFromJsonAsync<RegTagDetail>(Json, ct)
            ?? throw new RegLocationClientException(
                $"REG-LOCATION accepted tag '{request.Code}' but returned no body.");
    }

    // ---- Sites ------------------------------------------------------------

    public async Task<RegScope?> FindScopeByGuidAsync(Guid guid, CancellationToken ct) =>
        (await GetListAsync<RegScope>($"scopes?guid={guid:D}", $"scope guid '{guid:D}'", ct))
            .FirstOrDefault();

    public async Task<RegScope> CreateScopeAsync(CreateScopeRequest request, CancellationToken ct) =>
        await PostAsync<CreateScopeRequest, RegScope>(
            "scopes", request, $"scope '{request.Name}'", ct);

    public async Task<ScopeCascadeResult?> DeleteScopeCascadeAsync(int scopeId, CancellationToken ct)
    {
        using var response = await http.DeleteAsync($"scopes/{scopeId}/cascade", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            // A 409 reaches here when the cascade could not empty the scope. It
            // is deliberately not softened into null: the scope still exists, and
            // reporting it as already-gone would let the caller carry on and
            // delete the channels of a site that is still registered.
            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} deleting scope '{scopeId}': {body}");
        }

        return await response.Content.ReadFromJsonAsync<ScopeCascadeResult>(Json, ct)
            ?? throw new RegLocationClientException(
                $"REG-LOCATION deleted scope '{scopeId}' but returned no body.");
    }

    public async Task SetScopeContextAsync(
        int scopeId, SetScopeContextRequest request, CancellationToken ct)
    {
        using var response = await http.PutAsJsonAsync($"scopes/{scopeId}/context", request, Json, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} linking scope '{scopeId}': {body}");
        }
    }

    public async Task<RegItem?> FindItemByGuidAsync(Guid guid, CancellationToken ct) =>
        (await GetListAsync<RegItem>($"items?guid={guid:D}", $"item guid '{guid:D}'", ct))
            .FirstOrDefault();

    public async Task<RegItem> CreateItemAsync(CreateItemRequest request, CancellationToken ct) =>
        await PostAsync<CreateItemRequest, RegItem>(
            "items", request, $"item '{request.Code}'", ct);

    public async Task<RegSerialDetail?> FindSerialByGuidAsync(Guid guid, CancellationToken ct) =>
        (await GetListAsync<RegSerialDetail>($"serials?guid={guid:D}", $"serial guid '{guid:D}'", ct))
            .FirstOrDefault();

    public async Task<RegSerialDetail> CreateSerialAsync(
        CreateSerialRequest request, CancellationToken ct) =>
        await PostAsync<CreateSerialRequest, RegSerialDetail>(
            "serials", request, $"serial '{request.Name}'", ct);

    // The GUID lookups all return a list even when at most one row can match,
    // so a caller polling for something not yet created reads an empty array
    // rather than distinguishing 404-means-absent from 404-means-wrong-route.
    private async Task<IReadOnlyList<T>> GetListAsync<T>(
        string url, string subject, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} for {subject}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<List<T>>(Json, ct) ?? [];
    }

    private async Task<TResult> PostAsync<TRequest, TResult>(
        string url, TRequest request, string subject, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(url, request, Json, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new RegLocationClientException(
                $"REG-LOCATION returned {(int)response.StatusCode} creating {subject}: {body}");
        }

        return await response.Content.ReadFromJsonAsync<TResult>(Json, ct)
            ?? throw new RegLocationClientException(
                $"REG-LOCATION accepted {subject} but returned no body.");
    }
}

public sealed class RegLocationClientException(string message, Exception? inner = null)
    : Exception(message, inner);
