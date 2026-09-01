using System.Net.Http.Json;
using System.Text.Json;

namespace MmsEngine.Infrastructure.Mms;

/// <summary>
/// The slice of MMS's contract this engine writes.
///
/// Declared locally rather than referenced. MmsProvider emulates a customer
/// system reached over HTTP, and a ProjectReference would let its storage
/// concerns compile into this one -- and would make it possible to call the
/// store directly and never notice the boundary had gone. The duplication is
/// the price of the boundary being real.
/// </summary>
public sealed record SiteUpsert(
    Guid SiteId,
    string SiteCode,
    string SiteName,
    string? Description,
    string? SiteType,
    Guid? ParentSiteId,
    string? Country,
    string? Region,
    string? Status);

public sealed record UpsertedSite(Guid SiteId, string SiteCode, bool Created);

public sealed record UpsertRejection(string Key, string Reason, bool Transient);

public sealed record SiteUpsertResult(
    IReadOnlyList<UpsertedSite> Sites,
    IReadOnlyList<UpsertRejection> Rejections);

public sealed class MmsClientException(string message) : Exception(message);

public interface IMmsClient
{
    /// <summary>
    /// Creates or updates sites in MMS.
    ///
    /// Sites are sent as a batch because the provider accepts them that way and
    /// because one publication commonly carries several.
    /// </summary>
    Task<SiteUpsertResult> UpsertSitesAsync(
        IReadOnlyList<SiteUpsert> sites, CancellationToken ct);
}

public sealed class MmsRestClient(HttpClient http) : IMmsClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SiteUpsertResult> UpsertSitesAsync(
        IReadOnlyList<SiteUpsert> sites, CancellationToken ct)
    {
        if (sites.Count == 0)
        {
            return new SiteUpsertResult([], []);
        }

        using var response = await http.PostAsJsonAsync("sites", sites, Json, ct);

        // 503 is the provider's way of saying every site in the batch failed for
        // a reason that may not recur -- a deadlock, a database still starting.
        // It is raised rather than parsed so the caller leaves the message on the
        // channel and tries again, which is the whole point of the distinction.
        if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
        {
            throw new MmsClientException(
                "MMS reported every site in the batch as transiently rejected.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);

            throw new MmsClientException(
                $"MMS refused the site upsert ({(int)response.StatusCode}): {Excerpt(body)}");
        }

        var result = await response.Content.ReadFromJsonAsync<SiteUpsertResult>(Json, ct);

        return result ?? new SiteUpsertResult([], []);
    }

    private static string Excerpt(string? body) =>
        string.IsNullOrWhiteSpace(body)
            ? "(no body)"
            : body.Length <= 400 ? body.Trim() : body[..400].Trim() + "...";
}
