using System.Net.Http.Json;
using System.Text.Json;

namespace RdlEngine.Infrastructure.Rdl;

/// <summary>
/// A class as RDL publishes it over HTTP.
///
/// Deliberately a separate record from RdlProvider's own, despite the identical
/// shape. This is the engine's view of a remote system's contract, and the two
/// are allowed to drift: RDL may add a column this engine never reads. Sharing
/// a type would make that drift a compile error rather than a non-event.
/// </summary>
public sealed record RdlClassDto(
    int ClassId,
    int GroupId,
    int NamespaceId,
    string Code,
    string Name,
    string? Description,
    int? ParentClassId,
    Guid? Uuid = null);

public interface IRdlClient
{
    /// <summary>
    /// The library, optionally narrowed to one namespace.
    ///
    /// RDL returns the whole set rather than paging — the library is small by
    /// design and consumers cache it whole.
    /// </summary>
    Task<IReadOnlyList<RdlClassDto>> GetClassesAsync(int? namespaceId, CancellationToken ct);
}

/// <summary>
/// Reads the RDL library over its HTTP API.
///
/// HTTP rather than a project reference because RdlProvider emulates a customer
/// reference data library and must stay reachable only the way a real one would
/// be. See RdlEngine.csproj for the full reasoning.
/// </summary>
public sealed class RdlRestClient(HttpClient http) : IRdlClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RdlClassDto>> GetClassesAsync(int? namespaceId, CancellationToken ct)
    {
        var route = namespaceId is { } ns ? $"classes?namespaceId={ns}" : "classes";

        var classes = await http.GetFromJsonAsync<List<RdlClassDto>>(route, Json, ct);

        return classes ?? [];
    }
}
