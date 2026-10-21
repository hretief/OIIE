using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Oiie.Cir.Client;

namespace Oiie.Sandbox.Api.Providers;

/// <summary>A persisted MMS light unit, retaining MMS's own keys and nullable fields.</summary>
public sealed record MmsLightUnitDto(
    long LightUnitId,
    string LightUnitName,
    long? LightSystemId,
    long? LightUnitClassCodeId,
    long? LightUnitStatusId,
    long? OwnerId,
    Guid? ExtAssetId,
    string? MndotAssetNumber,
    DateTimeOffset? DateUpdate);

/// <summary>Live inventory in one iTwin, scoped by its CIR-resolved MMS owner.</summary>
public sealed record MmsInventoryResponse(
    Guid ITwinId, long? OwnerId, IReadOnlyList<MmsLightUnitDto> Items)
{
    public int Count => Items.Count;
    public bool SiteMapped => OwnerId.HasValue;
}

/// <summary>Read-only access to the customer's MMS inventory.</summary>
public interface IMmsInventorySource
{
    Task<MmsInventoryResponse> ListAsync(Guid twinId, CancellationToken ct);
}

/// <summary>The provider remains an HTTP peer; its function key stays server-side.</summary>
public sealed class MmsProviderClient(HttpClient http, ILogger<MmsProviderClient> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new MmsTimestampConverter() }
    };

    public async Task<IReadOnlyList<MmsLightUnitDto>> ListLightUnitsAsync(CancellationToken ct)
    {
        const string route = "lightunits";
        try
        {
            using var response = await http.GetAsync(route, ct);
            response.EnsureSuccessStatusCode();
            var rows = await response.Content.ReadFromJsonAsync<MmsLightUnitDto[]>(JsonOptions, ct)
                ?? throw new JsonException("MMS returned null instead of an inventory array.");
            if (rows.Any(r => r is null || r.LightUnitId <= 0 || string.IsNullOrWhiteSpace(r.LightUnitName))
                || rows.Select(r => r.LightUnitId).Distinct().Count() != rows.Length)
                throw new JsonException("MMS returned invalid or duplicate inventory rows.");
            return rows;
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException)
            && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "MMS inventory read failed.");
            throw new ProviderUnavailableException("MMS", route, ex);
        }

    }

    private sealed class MmsTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            // SQL stores SYSUTCDATETIME; the provider's DateTime has no offset.
            if (reader.TokenType != JsonTokenType.String
                || !DateTimeOffset.TryParse(reader.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp))
                throw new JsonException("MMS returned an invalid update timestamp.");
            return timestamp;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }
}

/// <summary>Resolves the site through CIR before reading the matching MMS-owned rows.</summary>
public sealed class MmsInventorySource(
    HttpClient cir,
    MmsProviderClient mms,
    IConfiguration configuration,
    ILogger<MmsInventorySource> logger) : IMmsInventorySource
{
    private readonly string _registryId = configuration["Sandbox:CirRegistryId"]?.Trim() is { Length: > 0 } id
        ? id : "acme";

    public async Task<MmsInventoryResponse> ListAsync(Guid twinId, CancellationToken ct)
    {
        if (twinId == Guid.Empty) throw new ArgumentException("A non-empty iTwin UUID is required.", nameof(twinId));
        var owner = await ResolveOwnerAsync(twinId, ct);
        if (owner is null) return new(twinId, null, []);

        var rows = await mms.ListLightUnitsAsync(ct);
        return new(twinId, owner, rows.Where(r => r.OwnerId == owner)
            .OrderBy(r => r.LightUnitName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.LightUnitId).ToArray());
    }

    private async Task<long?> ResolveOwnerAsync(Guid twinId, CancellationToken ct)
    {
        const string route = "queries/registry";
        try
        {
            var query = new GetRegistryRequest(
            [
                new CirFilter
                {
                    RegistryFilter = new() { Id = Regex.Escape(_registryId) },
                    CategoryFilter = new() { Id = "ITWIN-SITE", SourceId = "MMS" },
                    EntryFilter = new() { Cirid = twinId, SourceId = "MMS", Inactive = false }
                }
            ]);
            using var response = await cir.PostAsJsonAsync(route, query, ct);
            response.EnsureSuccessStatusCode();
            var graph = await response.Content.ReadFromJsonAsync<GetRegistryResponse>(ct);
            if (graph?.Registry is null || graph.Registry.Any(r => r is null || r.Categories is null
                || r.Categories.Any(c => c is null || c.Entries is null || c.Entries.Any(e => e is null))))
                throw new JsonException("CIR returned an invalid registry graph.");
            var entries = graph.Registry.Where(r => r.Id == _registryId)
                .SelectMany(r => r.Categories)
                .Where(c => c.Id == "ITWIN-SITE" && c.SourceId == "MMS")
                .SelectMany(c => c.Entries)
                .Where(e => e.SourceId == "MMS" && e.Cirid == twinId && e.Inactive != true).ToArray();
            if (entries.Length == 0) return null;
            if (entries.Length != 1 || !long.TryParse(entries[0].IdInSource, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var owner) || owner <= 0)
                throw new JsonException("CIR's MMS site mapping is ambiguous or has an invalid OWNER_ID.");
            return owner;
        }
        catch (Exception ex) when ((ex is HttpRequestException or JsonException or TaskCanceledException)
            && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "CIR lookup of MMS owner for site {Site} failed.", twinId);
            throw new ProviderUnavailableException("CIR", route, ex);
        }
    }
}
