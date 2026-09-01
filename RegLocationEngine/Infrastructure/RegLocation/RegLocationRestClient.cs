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
}

/// <summary>
/// Reads REG-LOCATION over its REST surface.
///
/// Reads only. The engine never writes to the registry: an approval is a
/// steward's act, and an integration component able to record one could
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
}

public sealed class RegLocationClientException(string message, Exception? inner = null)
    : Exception(message, inner);
