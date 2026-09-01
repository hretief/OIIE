using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EngEngine.Infrastructure.Eng;

using EngEngine.Application;

public interface IEngClient
{
    /// <summary>
    /// The iModel and the iTwin that owns it, or null when ENG does not have it.
    ///
    /// The engine needs this to find out where to publish: the channel is rooted
    /// in the iTwin's federation id, and only ENG knows which twin a model
    /// belongs to.
    /// </summary>
    Task<EngIModel?> GetIModelAsync(Guid iModelId, CancellationToken ct);

    /// <summary>
    /// The iTwins ENG holds.
    ///
    /// SyncSites publishes the site itself rather than something inside it, so
    /// unlike every other read here the twin is the subject and not the route to
    /// one.
    /// </summary>
    Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct);

    /// <summary>
    /// The stable UUID for a site type. ENG mints it on first request and
    /// returns the same value thereafter.
    ///
    /// The engine asks rather than decides, because the answer has to outlive
    /// any one engine process: a UUID invented here would differ on the next
    /// deployment, and REG-LOCATION would then hold two site types that are
    /// really one.
    /// </summary>
    Task<Guid> ResolveSiteTypeUuidAsync(string typeName, CancellationToken ct);

    /// <summary>
    /// Markers in an iModel changed at or after <paramref name="modifiedSince"/>,
    /// oldest first.
    /// </summary>
    Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(
        Guid iModelId, DateTime? modifiedSince, CancellationToken ct);

    /// <summary>The elements a marker contains, derived by ENG on read.</summary>
    Task<IReadOnlyList<EngElement>> GetNamedVersionElementsAsync(
        long namedVersionId, CancellationToken ct);
}

/// <summary>
/// Reads ENG over its REST surface.
///
/// Only two routes are used, and both are reads. The engine never writes to ENG:
/// a design tool is not corrected by the integration layer that carries its
/// output, and an engine that could write would be able to fabricate a handover
/// no engineer authored.
/// </summary>
public sealed class EngRestClient(HttpClient http) : IEngClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<EngIModel?> GetIModelAsync(Guid iModelId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"imodels/{iModelId:D}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new EngClientException(
                $"ENG returned {(int)response.StatusCode} for iModel '{iModelId:D}': {body}");
        }

        return await response.Content.ReadFromJsonAsync<EngIModel>(Json, ct);
    }

    public async Task<IReadOnlyList<EngITwin>> GetITwinsAsync(CancellationToken ct) =>
        await GetListAsync<EngITwin>("itwins", ct);

    public async Task<Guid> ResolveSiteTypeUuidAsync(string typeName, CancellationToken ct)
    {
        var url = $"itwin-types/{Uri.EscapeDataString(typeName)}";

        using var response = await http.PostAsync(url, content: null, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new EngClientException(
                $"ENG returned {(int)response.StatusCode} for '{url}': {body}");
        }

        var resolved = await response.Content.ReadFromJsonAsync<SiteTypeResolution>(Json, ct);

        return resolved is null || resolved.TypeUuid == Guid.Empty
            ? throw new EngClientException($"ENG returned no UUID for site type '{typeName}'.")
            : resolved.TypeUuid;
    }

    private sealed record SiteTypeResolution(string TypeName, Guid TypeUuid);

    public async Task<IReadOnlyList<EngNamedVersion>> GetNamedVersionsAsync(
        Guid iModelId, DateTime? modifiedSince, CancellationToken ct)
    {
        var url = $"named-versions?iModelId={iModelId:D}";

        if (modifiedSince is { } since)
        {
            // Round-trip format. ENG parses this as UTC, and a local-time string
            // would shift the window by the host's offset -- silently skipping
            // markers on a machine east of UTC.
            url += $"&modifiedSince={Uri.EscapeDataString(
                since.ToUniversalTime().ToString("O"))}";
        }

        return await GetListAsync<EngNamedVersion>(url, ct);
    }

    public async Task<IReadOnlyList<EngElement>> GetNamedVersionElementsAsync(
        long namedVersionId, CancellationToken ct) =>
        await GetListAsync<EngElement>($"named-versions/{namedVersionId}/elements", ct);

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            // The body carries ENG's problem detail. Losing it would reduce every
            // upstream refusal to a bare status code, which is not enough to tell
            // a misconfigured iModel id from an expired key.
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new EngClientException(
                $"ENG returned {(int)response.StatusCode} for '{url}': {body}");
        }

        return await response.Content.ReadFromJsonAsync<List<T>>(Json, ct) ?? [];
    }
}

public sealed class EngClientException(string message, Exception? inner = null)
    : Exception(message, inner);
