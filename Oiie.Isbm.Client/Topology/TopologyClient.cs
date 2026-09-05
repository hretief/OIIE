using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Oiie.Isbm.Client.Topology;

/// <summary>
/// One channel as the sandbox resolved it.
/// </summary>
public sealed class TopologyChannel
{
    public string Uri { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
    public string Type { get; set; } = "Publication";
    public string Publisher { get; set; } = string.Empty;
    public List<string> Subscribers { get; set; } = [];
    public List<string> Topics { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public bool IsPerITwin { get; set; }
}

/// <summary>One scenario and the channels it runs on.</summary>
public sealed class TopologyScenario
{
    public string ScenarioId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Scenario { get; set; }
    public string UseCase { get; set; } = string.Empty;
    public List<TopologyChannel> Channels { get; set; } = [];
}

/// <summary>The whole topology document.</summary>
public sealed class TopologyDocument
{
    public string Enterprise { get; set; } = string.Empty;
    public List<TopologyScenario> Scenarios { get; set; } = [];
}

/// <summary>
/// Reads the channel topology the sandbox publishes.
///
/// Over HTTP because the engines are separate function apps: they cannot take a
/// project reference on the sandbox, and a copy of the topology deployed beside
/// each engine would be a copy that goes stale — which is the drift this whole
/// mechanism exists to remove.
///
/// Every lookup is nullable and every caller keeps its configured default. An
/// engine that cannot reach the sandbox goes on publishing to the channel it was
/// configured with, rather than stopping: the topology is the better answer, not
/// the only one.
/// </summary>
public sealed class TopologyClient(HttpClient http, ILogger<TopologyClient> logger)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Cached with a TTL rather than fetched per publish or read once at start.
    // Per call would put the sandbox in the path of every message; once at start
    // would mean a topology edit needs an engine restart, and "restart the
    // engines" is close enough to a redeployment to be worth avoiding.
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private TopologyDocument? _cached;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<TopologyDocument?> GetAsync(
        Guid? iTwinId = null, CancellationToken ct = default)
    {
        if (http.BaseAddress is null)
        {
            return null;
        }

        // The twin is part of the resolved URIs, so a document fetched for one
        // twin cannot answer for another. Caching applies to the twin-less
        // shape only; per-twin lookups are few and happen off the message path.
        if (iTwinId is not null)
        {
            return await FetchAsync(iTwinId, ct);
        }

        if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAt < CacheFor)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);

        try
        {
            if (_cached is not null && DateTimeOffset.UtcNow - _fetchedAt < CacheFor)
            {
                return _cached;
            }

            var fetched = await FetchAsync(null, ct);

            if (fetched is not null)
            {
                _cached = fetched;
                _fetchedAt = DateTimeOffset.UtcNow;
            }

            // On failure the previous document is returned rather than null: a
            // sandbox that is briefly down should not change where messages go.
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The channel a participant publishes to for a scenario, or null when the
    /// topology does not say — in which case the caller keeps its own default.
    /// </summary>
    public async Task<TopologyChannel?> FindPublicationAsync(
        string scenarioId, string participantId, Guid? iTwinId = null,
        CancellationToken ct = default)
    {
        var document = await GetAsync(iTwinId, ct);

        return document?.Scenarios
            .FirstOrDefault(s => string.Equals(
                s.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase))
            ?.Channels
            .FirstOrDefault(c => string.Equals(
                c.Publisher, participantId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The channel a participant subscribes to for a scenario, or null when the
    /// topology does not say.
    /// </summary>
    public async Task<TopologyChannel?> FindSubscriptionAsync(
        string scenarioId, string participantId, Guid? iTwinId = null,
        CancellationToken ct = default)
    {
        var document = await GetAsync(iTwinId, ct);

        return document?.Scenarios
            .FirstOrDefault(s => string.Equals(
                s.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase))
            ?.Channels
            .FirstOrDefault(c => c.Subscribers.Any(s => string.Equals(
                s, participantId, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<TopologyDocument?> FetchAsync(Guid? iTwinId, CancellationToken ct)
    {
        var path = iTwinId is { } id
            ? $"admin/topology?iTwinId={id:D}"
            : "admin/topology";

        try
        {
            return await http.GetFromJsonAsync<TopologyDocument>(path, Json, ct);
        }
        catch (Exception ex)
        {
            // Debug, not warning. The fallback is a correct configured value, so
            // this is not a fault worth alerting on; logging it louder would
            // train operators to ignore it on every deployment that has no
            // sandbox configured.
            logger.LogDebug(ex, "Channel topology unavailable; using configured channels.");
            return null;
        }
    }
}
