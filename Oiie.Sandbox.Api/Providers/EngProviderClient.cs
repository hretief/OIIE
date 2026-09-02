using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Oiie.Sandbox.Api.Providers;

// The provider's own shapes, restated here rather than shared by project
// reference. EngProvider emulates a customer system: the sandbox is entitled to
// its HTTP contract and nothing else, and a reference would let a change to its
// internals break this host at compile time instead of at the boundary where it
// belongs.
//
// Property names match the JSON exactly. EngProvider serialises with
// JsonSerializerDefaults.Web, so these bind camelCase without attributes.

/// <summary>An iTwin as ENG reports it.</summary>
public sealed record EngITwinDto(
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc,
    string? DisplayName,
    string? Number);

/// <summary>An iModel as ENG reports it.</summary>
public sealed record EngIModelDto(
    Guid IModelId,
    Guid ITwinId,
    string Code,
    string? Description,
    DateTime CreatedUtc);

/// <summary>
/// An element as ENG reports it.
///
/// Note what is absent: no maturity, and none of the instrument attributes the
/// sandbox's own tags carry. That is ENG's position rather than an oversight —
/// see IEngDesignStore — and the adapter reports those fields as null rather
/// than inventing them.
/// </summary>
public sealed record EngElementDto(
    long ECInstanceId,
    Guid IModelId,
    long ECClassId,
    string FullyQualifiedECClassName,
    Guid? FederationGuid,
    string? CodeValue,
    string? UserLabel,
    string? DisplayName,
    int ChangesetIndex,
    DateTime CreatedUtc,
    DateTime ModifiedUtc);

/// <summary>A marker in an iModel's history.</summary>
public sealed record EngNamedVersionDto(
    long NamedVersionId,
    Guid VersionGuid,
    Guid IModelId,
    string Name,
    string? Description,
    string ChangesetId,
    int ChangesetIndex,
    DateTime CreatedUtc,
    int ElementCount);

/// <summary>
/// A marker to create.
///
/// Carries no changeset position. ENG pins the marker at the iModel's current
/// position when it creates it, and a position supplied from outside could name
/// one that had already moved on -- which would silently change what the marker
/// contains, since membership is derived from that position rather than stored.
/// </summary>
public sealed record EngNamedVersionDraftDto(
    Guid IModelId,
    string Name,
    string? Description,
    string? CreatedBy);

/// <summary>A class an element may be created on.</summary>
public sealed record EngClassDto(
    long ECClassId,
    string SchemaName,
    string ClassName,
    string FullyQualifiedName,
    string? DisplayLabel,
    string ClassModifier);

/// <summary>
/// An element the sandbox wants ENG to hold.
///
/// The class is an ECClassId rather than a name: ENG resolves elements against
/// its own metadata, and a name would have to be looked up somewhere anyway.
/// ECInstanceId and ChangesetIndex are absent because ENG allocates both — a
/// caller choosing its own position could place work inside a published
/// baseline.
/// </summary>
public sealed record EngElementUpsertDto(
    long ECClassId,
    string? CodeValue,
    string? UserLabel,
    string? DisplayName,
    Guid? FederationGuid,
    long? ECInstanceId = null,
    long? ParentECInstanceId = null);

/// <summary>What ENG did with one requested element.</summary>
public sealed record EngUpsertedElementDto(long ECInstanceId, string? CodeValue, bool Created);

/// <summary>
/// Why ENG refused one element. Transient marks a fault worth retrying, as
/// opposed to a duplicate code or an abstract class, which will fail the same
/// way every time.
/// </summary>
public sealed record EngUpsertRejectionDto(string Key, string Reason, bool Transient);

/// <summary>
/// The outcome of a batch. ENG answers 200 even when items were refused, so the
/// rejections have to be read rather than inferred from the status.
/// </summary>
public sealed record EngElementUpsertResultDto(
    IReadOnlyList<EngUpsertedElementDto> Elements,
    IReadOnlyList<EngUpsertRejectionDto> Rejections);

/// <summary>
/// Reads the ENG customer-system emulator over HTTP.
///
/// One method per question the admin endpoints ask, rather than a general
/// "get" helper: the routes differ in how they are scoped — iModels by query
/// string, elements by iModel — and hiding that behind a single call would make
/// the scoping look optional when it is not.
///
/// Every method surfaces failure as <see cref="ProviderUnavailableException"/>
/// rather than returning empty. An empty design and an unreachable provider
/// look identical in a UI panel, and only one of them is worth waking someone
/// for.
/// </summary>
public sealed class EngProviderClient(HttpClient http, ILogger<EngProviderClient> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<EngITwinDto>> GetITwinsAsync(CancellationToken ct) =>
        await GetAsync<List<EngITwinDto>>("itwins", ct) ?? [];

    public async Task<IReadOnlyList<EngIModelDto>> GetIModelsAsync(Guid? iTwinId, CancellationToken ct)
    {
        var route = iTwinId is null ? "imodels" : $"imodels?iTwinId={iTwinId}";

        return await GetAsync<List<EngIModelDto>>(route, ct) ?? [];
    }

    public async Task<IReadOnlyList<EngClassDto>> GetClassesAsync(CancellationToken ct) =>
        await GetAsync<List<EngClassDto>>("classes", ct) ?? [];

    /// <summary>
    /// The elements in one iModel.
    ///
    /// iModelId is required by this overload even though the route allows it to
    /// be omitted: an unscoped call returns every model's elements, which is
    /// never what a twin-scoped panel wants.
    /// </summary>
    public async Task<IReadOnlyList<EngElementDto>> GetElementsAsync(Guid iModelId, CancellationToken ct) =>
        await GetAsync<List<EngElementDto>>($"elements?iModelId={iModelId}", ct) ?? [];

    public async Task<IReadOnlyList<EngNamedVersionDto>> GetNamedVersionsAsync(Guid iModelId, CancellationToken ct) =>
        await GetAsync<List<EngNamedVersionDto>>($"named-versions?iModelId={iModelId}", ct) ?? [];

    /// <summary>
    /// The elements a marker covers.
    ///
    /// This is what stands in for maturity. ENG stores no per-element status, so
    /// "published" is answered by asking which elements fall inside a marker
    /// rather than by reading a field that would eventually disagree with the
    /// markers.
    /// </summary>
    public async Task<IReadOnlyList<EngElementDto>> GetNamedVersionElementsAsync(
        long namedVersionId, CancellationToken ct) =>
        await GetAsync<List<EngElementDto>>($"named-versions/{namedVersionId}/elements", ct) ?? [];

    /// <summary>
    /// Creates or updates elements in one iModel.
    ///
    /// The iModel is a route parameter rather than a per-element field, so a
    /// batch cannot span models and half-succeed.
    ///
    /// A 200 does not mean everything persisted: ENG refuses items individually
    /// so one bad row does not discard a good batch, and the caller has to read
    /// Rejections. Only a batch where nothing persisted and every rejection was
    /// transient comes back 503, which is the one case worth retrying.
    /// </summary>
    public async Task<EngElementUpsertResultDto> UpsertElementsAsync(
        Guid iModelId, IReadOnlyList<EngElementUpsertDto> elements, CancellationToken ct)
    {
        var route = $"imodels/{iModelId}/elements";

        try
        {
            using var response = await http.PostAsJsonAsync(route, elements, Json, ct);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<EngElementUpsertResultDto>(Json, ct)
                   ?? new EngElementUpsertResultDto([], []);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "ENG provider write to {Route} failed.", route);

            throw new ProviderUnavailableException("ENG", route, ex);
        }
    }

    /// <summary>
    /// Creates a marker, which is ENG's release act.
    ///
    /// Nothing about membership is sent. ENG derives what a marker contains from
    /// its changeset position, so the release is expressed entirely by when the
    /// marker is cut -- there is no list of elements to get wrong.
    /// </summary>
    public async Task<EngNamedVersionDto> CreateNamedVersionAsync(
        EngNamedVersionDraftDto draft, CancellationToken ct)
    {
        const string route = "named-versions";

        try
        {
            using var response = await http.PostAsJsonAsync(route, draft, Json, ct);

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<EngNamedVersionDto>(Json, ct)
                   ?? throw new ProviderUnavailableException(
                       "ENG", route, new InvalidOperationException(
                           "ENG accepted the marker but returned no body."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "ENG provider write to {Route} failed.", route);

            throw new ProviderUnavailableException("ENG", route, ex);
        }
    }

    private async Task<T?> GetAsync<T>(string route, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(route, ct);

            // A missing collection is an empty one. ENG answers 404 for an
            // unknown single item, and treating that as a fault would turn a
            // twin with no models into an error page.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return default;
            }

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<T>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "ENG provider call to {Route} failed.", route);

            throw new ProviderUnavailableException("ENG", route, ex);
        }
    }
}
