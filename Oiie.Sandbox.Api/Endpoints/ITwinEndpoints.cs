using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Oiie.Sandbox.Api.Endpoints;

/// <summary>
/// What the UI supplies when it adds an iTwin that already exists on the
/// platform.
///
/// The fields are named as the iTwin platform names them. They are carried
/// rather than looked up because the caller has just read them from the
/// platform, and the sandbox has no credentials of its own to read them again.
/// Type in particular matters downstream: the ENG engine refuses to publish a
/// site whose type it does not know, since the type is what REG-LOCATION
/// classifies the site by.
/// </summary>
public sealed record AddITwinRequest(
    Guid ITwinId,
    string? DisplayName = null,
    string? Number = null,
    string? Description = null,
    string? Class = null,
    string? SubClass = null,
    string? Type = null,
    string? Status = null,
    Guid? ParentITwinId = null);

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
/// The outcome of deleting an iTwin, reported in the same two parts as
/// <see cref="AddITwinResult"/> and for the same reason.
///
/// Removal and announcement fail independently, and the distinction matters more
/// on the way out than on the way in: a twin removed from ENG but never announced
/// leaves REG-LOCATION holding a scope, and its CIR entries, for a site that no
/// longer exists. Re-announcing is the repair, and a caller can only know to
/// attempt it if the two are reported separately.
/// </summary>
public sealed record DeleteITwinResult(
    Guid ITwinId,
    /// <summary>True when ENG no longer holds the twin.</summary>
    bool Removed,
    /// <summary>True when the SyncSites deletion was triggered.</summary>
    bool Announced,
    /// <summary>Why the removal or announcement did not happen, when it did not.</summary>
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
    /// <summary>
    /// The site type a twin registers under when the platform names none.
    ///
    /// Type names the boundary of the digital twin -- what it is drawn around
    /// -- and the iTwin Platform exposes no way to set it, so in practice it is
    /// always absent and this is the value every twin in the demo carries.
    /// "District" is the boundary a DOT manages by, which is what this demo
    /// models; another owner might scope twins to a Plant or a Highway instead.
    ///
    /// SubClass is not used as a fallback: it answers a different question
    /// (asset versus the endeavour delivering it), so borrowing it would file
    /// twins under a boundary named "Asset".
    ///
    /// ENG mints the site type UUID from this string and reuses it, so a
    /// constant here is what makes all demo sites classify alike downstream.
    /// </summary>
    private const string DefaultSiteType = "District";

    // Each base URL accepts two setting names, and this is not redundancy for
    // its own sake. Sandbox:*ProviderBaseUrl was the reset's own set;
    // Providers:*:BaseUrl is what the panels read and what deploy.ps1 has always
    // written. Only ENG was ever given both, so day zero skipped REG-LOCATION
    // while the REG-LOCATION panel worked -- a reset that reported success and
    // left the data alone. Reading both means one configured provider is
    // configured for every purpose.
    private readonly string? _providerBaseUrl =
        Trimmed(configuration["Sandbox:EngProviderBaseUrl"])
        ?? Trimmed(configuration["Providers:Eng:BaseUrl"]);

    private readonly string? _engineBaseUrl =
        Trimmed(configuration["Sandbox:EngEngineBaseUrl"]);

    private readonly string? _regLocationBaseUrl =
        Trimmed(configuration["Sandbox:RegLocationProviderBaseUrl"])
        ?? Trimmed(configuration["Providers:RegLocation:BaseUrl"]);

    // The REG-LOCATION engine is its own Functions host, separate from the
    // REG-LOCATION provider it reads. Its published-tag set is what made
    // publishedTags keep counting across day zero: the registry was emptied
    // while the engine kept believing it had already sent everything.
    private readonly string? _regLocationEngineBaseUrl =
        Trimmed(configuration["Sandbox:RegLocationEngineBaseUrl"]);

    private readonly string? _mmsBaseUrl =
        Trimmed(configuration["Sandbox:MmsProviderBaseUrl"])
        ?? Trimmed(configuration["Providers:Mms:BaseUrl"]);

    private readonly string? _cmsBaseUrl =
        Trimmed(configuration["Sandbox:CmsProviderBaseUrl"])
        ?? Trimmed(configuration["Providers:Cms:BaseUrl"]);

    /// <summary>
    /// The CIR the engines register in, which is not necessarily the one the
    /// sandbox participants use.
    /// </summary>
    /// <remarks>
    /// Configured separately because the two are genuinely different
    /// deployments today: the personality packs point at their own CIR with the
    /// registry OIIE-SANDBOX, while the engines write to whatever
    /// RegLocationEngine__CirBaseUrl names, under a registry taken from their
    /// Enterprise setting. Clearing one does not clear the other, and it is the
    /// engine-written entries that break the next run.
    /// </remarks>
    private readonly string? _cirBaseUrl =
        Trimmed(configuration["Sandbox:CirProviderBaseUrl"])
        ?? Trimmed(configuration["Providers:Cir:BaseUrl"]);

    /// <summary>
    /// The registry the engines write into -- the enterprise id, not a
    /// per-participant value.
    /// </summary>
    /// <remarks>
    /// Defaults to the enterprise the engines are deployed with
    /// (<c>{prefix}__Enterprise=acme</c> in deploy-engine.ps1) rather than to
    /// null. An absent registry id used to mean CIR was skipped entirely, so the
    /// one setting nobody knew to write was also the one that silently left
    /// stale CIRIDs behind. Defaulting to the value the engines actually use
    /// makes the common deployment correct without configuration.
    ///
    /// This drops only the engine-written registry. The personality packs
    /// register under OIIE-SANDBOX, which is a different deployment and is not
    /// cleared here.
    /// </remarks>
    private readonly string _cirRegistryId =
        Trimmed(configuration["Sandbox:CirRegistryId"]) ?? "acme";

    // The provider and engine are separate Functions hosts with separate host
    // keys. A single default header on the HttpClient can only ever satisfy one
    // of them, so the key travels with the request instead. Each falls back to
    // the shared Sandbox:EngFunctionsKey for deployments where one key does
    // cover both.
    private readonly string? _providerKey =
        Trimmed(configuration["Providers:Eng:Key"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    private readonly string? _engineKey =
        Trimmed(configuration["Sandbox:EngEngineKey"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    private readonly string? _regLocationKey =
        Trimmed(configuration["Providers:RegLocation:Key"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    private readonly string? _regLocationEngineKey =
        Trimmed(configuration["Sandbox:RegLocationEngineKey"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    private readonly string? _mmsKey =
        Trimmed(configuration["Providers:Mms:Key"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    private readonly string? _cmsKey =
        Trimmed(configuration["Providers:Cms:Key"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    private readonly string? _cirKey =
        Trimmed(configuration["Providers:Cir:Key"])
        ?? Trimmed(configuration["Sandbox:EngFunctionsKey"]);

    /// <summary>
    /// Sends a request carrying the key for the host being addressed, rather
    /// than the one key the client was constructed with.
    /// </summary>
    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? key, object? body, CancellationToken ct)
    {
        var message = new HttpRequestMessage(method, url);

        if (!string.IsNullOrWhiteSpace(key))
        {
            message.Headers.Remove("x-functions-key");
            message.Headers.Add("x-functions-key", key);
        }

        if (body is not null)
        {
            message.Content = JsonContent.Create(body, body.GetType());
        }

        return http.SendAsync(message, ct);
    }

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
    /// Each provider decides for itself what a reset means. They drop, recreate
    /// and re-seed, so bootstrapped reference data -- classes, namespaces, units
    /// -- comes back while transactional rows do not. Nothing here needs to know
    /// that distinction.
    ///
    /// An unconfigured provider is a failure, not a skip. It reads as one system
    /// being untouched while the rest were rebuilt, which is the state day zero
    /// exists to rule out: ids restart from 1 in the systems that were reset and
    /// collide with the rows still held by the one that was not. Silence here
    /// previously turned that into a green reset over stale data.
    ///
    /// Returns the reasons rather than throwing. Day zero has already torn down
    /// channels and participant schemas by the time this runs, and failing the
    /// whole call because one Functions host is not running would leave the
    /// caller unsure which half happened.
    /// </summary>
    public async Task<IReadOnlyList<string>> ResetAsync(CancellationToken ct)
    {
        var problems = new List<string>();

        var targets = new List<(string Label, string? Url, string? Key, string Setting)>
        {
            ("ENG provider", _providerBaseUrl is null ? null : $"{_providerBaseUrl}/eng/reset", _providerKey, "Sandbox:EngProviderBaseUrl or Providers:Eng:BaseUrl"),
            ("ENG engine", _engineBaseUrl is null ? null : $"{_engineBaseUrl}/engine/reset", _engineKey, "Sandbox:EngEngineBaseUrl"),
            ("REG-LOCATION provider", _regLocationBaseUrl is null ? null : $"{_regLocationBaseUrl}/reglocation/reset", _regLocationKey, "Sandbox:RegLocationProviderBaseUrl or Providers:RegLocation:BaseUrl"),
            ("REG-LOCATION engine", _regLocationEngineBaseUrl is null ? null : $"{_regLocationEngineBaseUrl}/engine/reset", _regLocationEngineKey, "Sandbox:RegLocationEngineBaseUrl"),
            ("MMS provider", _mmsBaseUrl is null ? null : $"{_mmsBaseUrl}/mms/reset", _mmsKey, "Sandbox:MmsProviderBaseUrl or Providers:Mms:BaseUrl"),
            ("CMS provider", _cmsBaseUrl is null ? null : $"{_cmsBaseUrl}/cms/reset", _cmsKey, "Sandbox:CmsProviderBaseUrl or Providers:Cms:BaseUrl"),
        };

        foreach (var (label, url, key, setting) in targets)
        {
            if (url is null)
            {
                problems.Add(
                    $"{label} was NOT reset: no base URL is configured. Set {setting}. " +
                    "Its data is still that of the previous run, while the systems that " +
                    "were reset have restarted their ids from 1.");
                continue;
            }

            try
            {
                var response = await SendAsync(HttpMethod.Post, url, key, null, ct);

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

    /// <summary>
    /// Drops the registry the engines register identities in.
    /// </summary>
    /// <remarks>
    /// Over the CIR provider's REST surface rather than an ISBM CancelRegistry
    /// BOD. Day zero deletes channels and destroys the sessions on them, so the
    /// bus is at its least trustworthy exactly when this runs -- and
    /// CancelRegistry declares no response, so a delete that never arrived would
    /// be indistinguishable from one that worked.
    ///
    /// This matters more than tidiness. ITWIN-SITE entries key on the integer
    /// scopeId, and day zero rebuilds REG-LOCATION so ids restart at 1. A
    /// registry left populated therefore collides with the first sites created
    /// after the reset, and the failure surfaces as a duplicate-entry conflict
    /// on a greenfield run -- which reads as a bug in registration rather than
    /// as leftover state.
    ///
    /// Deleting the registry takes its categories with it. That is correct here:
    /// the next registration recreates both, and a category surviving without
    /// its entries would describe a shape nothing occupies.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ResetCirAsync(CancellationToken ct)
    {
        if (_cirBaseUrl is null)
        {
            // Reported rather than skipped. CIR holds the identities of rows the
            // other systems are about to renumber from 1, so leaving it
            // untouched is the specific failure that surfaces later as a
            // duplicate-entry conflict on what looks like a greenfield run.
            return ["The CIR registry was NOT dropped: no base URL is configured. " +
                    "Set Sandbox:CirProviderBaseUrl or Providers:Cir:BaseUrl. Its entries " +
                    "still identify rows from the previous run, and will collide with the " +
                    "first sites created after this reset."];
        }

        try
        {
            var response = await SendAsync(
                HttpMethod.Delete,
                $"{_cirBaseUrl}/registries/{Uri.EscapeDataString(_cirRegistryId)}",
                _cirKey,
                body: null,
                ct);

            // Already absent is the desired end state, not a failure. A
            // greenfield environment has no registry to drop.
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return await ResetCirSessionsAsync(ct);
            }

            return [$"CIR refused to drop registry '{_cirRegistryId}' " +
                    $"({(int)response.StatusCode}): " +
                    Excerpt(await response.Content.ReadAsStringAsync(ct))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Dropping CIR registry {RegistryId} failed.", _cirRegistryId);

            return [$"Could not reach the CIR provider to drop registry " +
                    $"'{_cirRegistryId}': {ex.Message}"];
        }
    }

    /// <summary>
    /// Tells the CIR provider to close and forget its ISBM sessions.
    /// </summary>
    /// <remarks>
    /// Day zero deletes the channels the provider is subscribed to, which
    /// destroys the sessions on them. The provider keeps polling the stored
    /// session ids regardless, and the broker no longer recognises them, so
    /// without this it never receives another BOD until someone restarts it.
    ///
    /// Called after the registry drop rather than before, because reopening a
    /// session against a channel that is about to be deleted would leave the
    /// provider holding a stale id again.
    ///
    /// A failure here is reported, not thrown: the registry has already been
    /// dropped by this point, and losing that outcome to a session problem
    /// would leave the caller believing nothing happened.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ResetCirSessionsAsync(CancellationToken ct)
    {
        try
        {
            var response = await SendAsync(
                HttpMethod.Post,
                $"{_cirBaseUrl}/isbm/reset",
                _cirKey,
                body: null,
                ct);

            if (response.IsSuccessStatusCode)
            {
                return [];
            }

            return [$"The CIR registry was dropped, but its ISBM sessions were NOT " +
                    $"reset ({(int)response.StatusCode}): " +
                    Excerpt(await response.Content.ReadAsStringAsync(ct)) +
                    " It will keep polling a session the broker has forgotten until " +
                    "it is restarted."];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Resetting CIR ISBM sessions failed.");

            return [$"The CIR registry was dropped, but its ISBM sessions could not be " +
                    $"reset: {ex.Message} It will keep polling a session the broker has " +
                    "forgotten until it is restarted."];
        }
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
            // Relayed under the platform's own field names, which is what the
            // provider now stores. There is no code: the platform never carried
            // one, and the provider no longer invents it.
            var upsert = await SendAsync(
                HttpMethod.Post,
                $"{_providerBaseUrl}/itwins",
                _providerKey,
                new
                {
                    iTwinId = request.ITwinId,
                    description = request.Description,
                    displayName = request.DisplayName,
                    number = request.Number,
                    @class = request.Class,
                    subClass = request.SubClass,

                    // The engine will not publish a site with no type, and the
                    // platform has no UI for one, so it is effectively always
                    // absent. This default is a demo convention shared with the
                    // UI -- keeping the two in step matters because a different
                    // fallback here would classify a twin added through the API
                    // differently from one added in the app.
                    type = request.Type ?? DefaultSiteType,

                    status = request.Status,
                    parentITwinId = request.ParentITwinId,
                },
                ct);

            if (!upsert.IsSuccessStatusCode)
            {
                return $"ENG provider refused the iTwin ({(int)upsert.StatusCode}): " +
                       Excerpt(await upsert.Content.ReadAsStringAsync(ct));
            }

            var announce = await SendAsync(
                HttpMethod.Post,
                $"{_engineBaseUrl}/bootstrap/itwin-created",
                _engineKey,
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

    /// <summary>
    /// Removes an iTwin from ENG and announces the removal.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="BootstrapAsync"/>, and ordered the same way and
    /// for the same reason: the provider is the system of record, so it goes
    /// first and the announcement follows. Announcing first would tell the
    /// ecosystem to tear down a site ENG might then refuse to delete.
    ///
    /// A 404 from the provider stops the flow rather than continuing to the
    /// announcement. Publishing a deletion for a twin ENG never held would ask
    /// every subscriber to remove something they were never told about, and on a
    /// reused scope id that is not merely a no-op.
    /// </remarks>
    public async Task<string?> TeardownAsync(Guid iTwinId, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            return "Sandbox:EngProviderBaseUrl and Sandbox:EngEngineBaseUrl are not configured, " +
                   "so the twin was not removed.";
        }

        try
        {
            var removed = await SendAsync(
                HttpMethod.Delete,
                $"{_providerBaseUrl}/itwins/{iTwinId:D}",
                _providerKey,
                body: null,
                ct);

            if (!removed.IsSuccessStatusCode)
            {
                return $"ENG provider refused the deletion ({(int)removed.StatusCode}): " +
                       Excerpt(await removed.Content.ReadAsStringAsync(ct));
            }

            var announce = await SendAsync(
                HttpMethod.Post,
                $"{_engineBaseUrl}/bootstrap/itwin-deleted",
                _engineKey,
                new ITwinDeletedEvent(iTwinId),
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
            logger.LogWarning(ex, "Tearing down iTwin {ITwinId:D} through ENG failed.", iTwinId);

            return $"Could not reach the ENG Functions host: {ex.Message}";
        }
    }

    /// <summary>The platform's iTwinDeleted notification, shaped as the real webhook is.</summary>
    private sealed record ITwinDeletedEvent(
        [property: JsonPropertyName("iTwinId")] Guid ITwinId,
        [property: JsonPropertyName("eventType")] string EventType = "iTwins.iTwinDeleted.v1");

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

        var response = await SendAsync(
            HttpMethod.Get,
            $"{_providerBaseUrl}/imodels?iTwinId={iTwinId:D}",
            _providerKey,
            null,
            ct);

        response.EnsureSuccessStatusCode();

        var models = await response.Content
            .ReadFromJsonAsync<List<EngIModelSummary>>(ct);

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
            var response = await SendAsync(
                HttpMethod.Post,
                $"{_providerBaseUrl}/imodels",
                _providerKey,
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
