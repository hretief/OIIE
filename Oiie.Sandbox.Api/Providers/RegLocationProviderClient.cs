using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Oiie.Sandbox.Api.Providers;

// As with ENG, the registry's shapes are restated rather than referenced. These
// bind camelCase without attributes because RegLocationProvider serialises with
// JsonSerializerDefaults.Web.

/// <summary>
/// A tag as the registry reports it.
///
/// Seven fields, and none of them describe how the tag was classified on the
/// way in. The sandbox's stewardship row carried requestedClassKey,
/// boundClassKey and the property-mapping counts; the registry has no such
/// notion, because degradation happened in the engine before the tag arrived.
/// </summary>
public sealed record RegTagDto(
    int TagId,
    int ItemId,
    int ClassId,
    string Code,
    int Revision,
    string Name,
    string State);

/// <summary>
/// The registry bookkeeping row paired with every domain row.
///
/// Guid is the federation identity — the link back to the ENG element the tag
/// was raised from — and ScopeId is the site boundary, which is the closest
/// thing the registry has to the sandbox's notion of a twin.
/// </summary>
public sealed record RegObjectDto(
    int ObjectId,
    int ObjectType,
    Guid? Guid,
    int ScopeId,
    DateTime? DateAdded,
    DateTime? DateChanged);

/// <summary>A tag with its registry row, which the provider always returns as a pair.</summary>
public sealed record RegTagDetailDto(RegTagDto Tag, RegObjectDto Object);

/// <summary>Who decided, which the registry requires for an approval.</summary>
public sealed record ApproveTagRequestDto(string DecidedBy);

/// <summary>
/// Reads and approves against the REG-LOCATION registry emulator.
///
/// Deliberately has no Reject method. The registry defines a Rejected state but
/// exposes no route that sets it, and a client method that quietly did
/// something else — a PUT with a state field, say — would misrepresent the
/// registry's authority model. The gap is reported at the endpoint instead.
/// </summary>
public sealed class RegLocationProviderClient(
    HttpClient http, ILogger<RegLocationProviderClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The steward's queue: tags proposed and not yet admitted.
    ///
    /// Takes no filter because the route takes none. Scoping to a twin is done
    /// by the caller against the paired registry row's GUID.
    /// </summary>
    public async Task<IReadOnlyList<RegTagDetailDto>> GetProposedTagsAsync(CancellationToken ct) =>
        await SendAsync<List<RegTagDetailDto>>(HttpMethod.Get, "tags/proposed", null, ct) ?? [];

    /// <summary>
    /// Every tag, or those in one scope. Used to show decided rows beside
    /// outstanding ones, which is what the queue's "all" filter asks for.
    /// </summary>
    public async Task<IReadOnlyList<RegTagDto>> GetTagsAsync(int? scopeId, CancellationToken ct)
    {
        var route = scopeId is null ? "tags" : $"tags?scopeId={scopeId}";

        return await SendAsync<List<RegTagDto>>(HttpMethod.Get, route, null, ct) ?? [];
    }

    /// <summary>
    /// Admits a proposed tag to the registry.
    ///
    /// Nothing is published here. The registry records the decision and the
    /// engine carries it onto a channel, so a caller watching for a downstream
    /// effect is watching the engine, not this call.
    /// </summary>
    public async Task<RegTagDetailDto?> ApproveTagAsync(int tagId, string decidedBy, CancellationToken ct) =>
        await SendAsync<RegTagDetailDto>(
            HttpMethod.Post, $"tags/{tagId}/approve", new ApproveTagRequestDto(decidedBy), ct);

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string route, object? body, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, route);

            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            using var response = await http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "REG-LOCATION provider call to {Route} failed.", route);

            throw new ProviderUnavailableException("REG-LOCATION", route, ex);
        }
    }
}
