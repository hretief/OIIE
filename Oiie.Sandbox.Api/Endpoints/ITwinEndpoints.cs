using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Oiie.Sandbox.Api.Endpoints;

/// <summary>
/// What the UI supplies when it adds an iTwin that already exists on the
/// platform.
///
/// The classification fields are carried rather than looked up because the
/// caller has just read them from the platform, and the sandbox has no
/// credentials of its own to read them again. TwinType in particular matters
/// downstream: the ENG engine refuses to publish a site whose type it does not
/// know, since the type is what REG-LOCATION classifies the site by.
/// </summary>
public sealed record AddITwinRequest(
    Guid ITwinId,
    string? DisplayName = null,
    string? Number = null,
    string? Description = null,
    string? TwinClass = null,
    string? SubClass = null,
    string? TwinType = null);

/// <summary>
/// The outcome of adding an iTwin, reported in two parts.
///
/// Registration and announcement are separate acts and fail independently, so
/// collapsing them into one boolean would leave the caller unable to tell a
/// twin that is in the sandbox but unannounced from one that never arrived.
/// The first is retryable by adding the twin again; the second is not.
/// </summary>
public sealed record AddITwinResult(
    Guid ITwinId,
    string Code,
    string Name,
    /// <summary>True when ENG now holds the twin.</summary>
    bool Registered,
    /// <summary>True when the SyncSites publication was triggered.</summary>
    bool Announced,
    /// <summary>Why the announcement did not happen, when it did not.</summary>
    string? Detail = null);

/// <summary>
/// The sandbox's link to the ENG-side Functions apps.
///
/// The UI talks only to this API, so the two-hop bootstrap -- record the twin
/// in the ENG provider, then ask the ENG engine to announce it -- happens here
/// rather than in the browser. That keeps the function keys server-side and
/// means the browser cannot perform half of the sequence and stop.
///
/// Both hops are optional at configuration level. A workstation running the UI
/// against the sandbox alone, with no Functions host, is the common case during
/// front-end work; being told the twin was registered but not announced is far
/// more useful there than a connection error that looks like a bug.
/// </summary>
public sealed class EngEngineClient(
    HttpClient http, IConfiguration configuration, ILogger<EngEngineClient> logger)
{
    private readonly string? _providerBaseUrl =
        Trimmed(configuration["Sandbox:EngProviderBaseUrl"]);

    private readonly string? _engineBaseUrl =
        Trimmed(configuration["Sandbox:EngEngineBaseUrl"]);

    /// <summary>Whether the bootstrap can run at all.</summary>
    public bool IsConfigured => _providerBaseUrl is not null && _engineBaseUrl is not null;

    /// <summary>
    /// Records the twin with the ENG provider and asks the engine to publish it
    /// as a SyncSites BOD.
    ///
    /// Ordered, not concurrent: the engine deliberately publishes what the
    /// provider holds rather than trusting the event's copy of a twin's detail,
    /// so announcing before recording would publish a twin the provider cannot
    /// describe.
    ///
    /// Returns the reason on failure rather than throwing. The caller has
    /// already registered the twin in its own store by this point, and turning a
    /// broker or host problem into a failed request would hide that.
    /// </summary>
    public async Task<string?> BootstrapAsync(AddITwinRequest request, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return "Sandbox:EngProviderBaseUrl and Sandbox:EngEngineBaseUrl are not configured, " +
                   "so the twin was recorded but SyncSites was not published.";
        }

        try
        {
            // The provider names the twin's code itself when one is not supplied,
            // falling back through number and display name, so nothing is
            // invented here for a column the platform never carried.
            var upsert = await http.PostAsJsonAsync(
                $"{_providerBaseUrl}/itwins",
                new
                {
                    iTwinId = request.ITwinId,
                    code = request.Number ?? request.DisplayName,
                    description = request.Description,
                    displayName = request.DisplayName,
                    number = request.Number,
                    twinClass = request.TwinClass,
                    subClass = request.SubClass,

                    // The engine will not publish a site with no type. SubClass is
                    // the platform's nearest equivalent, so it stands in when the
                    // caller names nothing more specific.
                    twinType = request.TwinType ?? request.SubClass,
                },
                ct);

            if (!upsert.IsSuccessStatusCode)
            {
                return $"ENG provider refused the iTwin ({(int)upsert.StatusCode}): " +
                       Excerpt(await upsert.Content.ReadAsStringAsync(ct));
            }

            var announce = await http.PostAsJsonAsync(
                $"{_engineBaseUrl}/bootstrap/itwin-created",
                new ITwinCreatedEvent(request.ITwinId),
                ct);

            if (!announce.IsSuccessStatusCode)
            {
                return $"ENG engine refused the event ({(int)announce.StatusCode}): " +
                       Excerpt(await announce.Content.ReadAsStringAsync(ct));
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Bootstrapping iTwin {ITwinId:D} through ENG failed.", request.ITwinId);

            return $"Could not reach the ENG Functions host: {ex.Message}";
        }
    }

    /// <summary>
    /// The platform's iTwinCreated notification, reduced to what the engine
    /// reads. Shaped as the real webhook is, so moving from this bootstrap to a
    /// live subscription is a URL change rather than a code change.
    /// </summary>
    private sealed record ITwinCreatedEvent(
        [property: JsonPropertyName("iTwinId")] Guid ITwinId,
        [property: JsonPropertyName("eventType")] string EventType = "iTwins.iTwinCreated.v1");

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');

    private static string Excerpt(string body) =>
        body.Length <= 300 ? body : body[..300];
}
