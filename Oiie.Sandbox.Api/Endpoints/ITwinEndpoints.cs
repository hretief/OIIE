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
    string? Detail = null,
    /// <summary>The twin's publication channel, once provisioned.</summary>
    string? ChannelUri = null,
    /// <summary>Why the channel could not be created, when it could not.</summary>
    string? ChannelError = null);

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

    private readonly string? _regLocationBaseUrl =
        Trimmed(configuration["Sandbox:RegLocationProviderBaseUrl"]);

    private readonly string? _mmsBaseUrl =
        Trimmed(configuration["Sandbox:MmsProviderBaseUrl"]);

    private readonly string? _cmsBaseUrl =
        Trimmed(configuration["Sandbox:CmsProviderBaseUrl"]);

    /// <summary>Whether the bootstrap can run at all.</summary>
    public bool IsConfigured => _providerBaseUrl is not null && _engineBaseUrl is not null;

    /// <summary>
    /// Empties every emulated system the sandbox can reach: ENG's tables, the
    /// engine's watermark, and the REG-LOCATION, MMS and CMS databases.
    ///
    /// ENG's two halves are both cleared, not either: the engine remembers
    /// published versions by VersionGuid so that its memory survives ENG being
    /// rebuilt, which after a reset means it would decline to republish anything
    /// it recognised. Dropping ENG's rows without clearing that memory produces
    /// an engine that publishes nothing and reports no error.
    ///
    /// The other three each own their database, so emptying them is a call to
    /// the system that owns the data rather than a table drop from here. That
    /// keeps the emulation honest -- the sandbox has no more access to a
    /// customer system's schema than a real integrator would.
    ///
    /// An unconfigured base URL is a skip, not a failure. Running only part of
    /// the stack is the normal case during front-end work, and reporting "MMS
    /// was not reachable" for a host nobody started would bury the failures that
    /// do matter.
    ///
    /// Returns the reasons rather than throwing. Day zero has already torn down
    /// channels and participant schemas by the time this runs, and failing the
    /// whole call because one Functions host is not running would leave the
    /// caller unsure which half happened.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResetAsync(CancellationToken ct)
    {
        var problems = new List<string>();

        var targets = new List<(string Label, string? Url)>
        {
            ("ENG provider", _providerBaseUrl is null ? null : $"{_providerBaseUrl}/eng/reset"),
            ("ENG engine", _engineBaseUrl is null ? null : $"{_engineBaseUrl}/engine/reset"),
            ("REG-LOCATION provider", _regLocationBaseUrl is null ? null : $"{_regLocationBaseUrl}/reglocation/reset"),
            ("MMS provider", _mmsBaseUrl is null ? null : $"{_mmsBaseUrl}/mms/reset"),
            ("CMS provider", _cmsBaseUrl is null ? null : $"{_cmsBaseUrl}/cms/reset"),
        };

        if (!IsConfigured)
        {
            problems.Add("Sandbox:EngProviderBaseUrl and Sandbox:EngEngineBaseUrl are not configured, " +
                         "so ENG's own data was left as it was.");
        }

        foreach (var (label, url) in targets)
        {
            if (url is null)
            {
                continue;
            }

            try
            {
                var response = await http.PostAsync(url, null, ct);

                if (!response.IsSuccessStatusCode)
                {
                    problems.Add($"{label} refused the reset ({(int)response.StatusCode}): " +
                                 Excerpt(await response.Content.ReadAsStringAsync(ct)));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Resetting {Label} failed.", label);
                problems.Add($"Could not reach {label}: {ex.Message}");
            }
        }

        return problems;
    }


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

    /// <summary>Whether the provider hop alone can run.</summary>
    public bool IsProviderConfigured => _providerBaseUrl is not null;

    /// <summary>
    /// The iModels ENG holds for one twin.
    ///
    /// The browser reads iModels from the platform directly, but authoring an
    /// element against one requires the provider to know it, so this is what the
    /// picker lists: what ENG can actually accept, not what the platform has.
    /// </summary>
    public async Task<IReadOnlyList<EngIModelSummary>> GetIModelsAsync(
        Guid iTwinId, CancellationToken ct)
    {
        if (_providerBaseUrl is null) return [];

        var models = await http.GetFromJsonAsync<List<EngIModelSummary>>(
            $"{_providerBaseUrl}/imodels?iTwinId={iTwinId:D}", ct);

        return models ?? [];
    }

    /// <summary>
    /// Records an iModel the browser read from the platform.
    ///
    /// Returns the reason on failure rather than throwing, matching
    /// <see cref="BootstrapAsync"/>: an unreachable Functions host during
    /// front-end work should be reported, not mistaken for a bad request.
    /// </summary>
    public async Task<string?> SyncIModelAsync(
        Guid iModelId, Guid iTwinId, string? code, string? description, CancellationToken ct)
    {
        if (_providerBaseUrl is null)
        {
            return "Sandbox:EngProviderBaseUrl is not configured, so the iModel was not recorded.";
        }

        try
        {
            var response = await http.PostAsJsonAsync(
                $"{_providerBaseUrl}/imodels",
                new { iModelId, iTwinId, code, description },
                ct);

            if (!response.IsSuccessStatusCode)
            {
                return $"ENG provider refused the iModel ({(int)response.StatusCode}): " +
                       Excerpt(await response.Content.ReadAsStringAsync(ct));
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Recording iModel {IModelId:D} in ENG failed.", iModelId);

            return $"Could not reach the ENG Functions host: {ex.Message}";
        }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.TrimEnd('/');

    private static string Excerpt(string body) =>
        body.Length <= 300 ? body : body[..300];
}

/// <summary>
/// An iModel as the ENG provider returns it, reduced to what the picker shows.
/// </summary>
public sealed record EngIModelSummary(
    Guid IModelId,
    Guid ITwinId,
    string Code,
    string? Description);

/// <summary>
/// What the UI supplies when it records an iModel it has read from the platform.
///
/// Code and DisplayName are both carried because the platform names the field
/// displayName while ENG stores a Code, and neither is guaranteed to be present.
/// </summary>
public sealed record SyncIModelRequest(
    Guid IModelId,
    Guid ITwinId,
    string? Code = null,
    string? DisplayName = null,
    string? Description = null);
