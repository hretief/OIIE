# Building an engine

A guide for adding a new participant system to the federation, modelled on
`CmsEngine` — the smallest complete engine in the solution.

Read this alongside the code. Every rule below was learned by getting it wrong,
and the decision register entries cited are where the reasoning lives.

---

## What an engine is

An **engine** is the integration layer for one customer system. It has no
database of its own. It reads from the bus, translates, and calls the system's
REST API — and registers what it learned in CIR so other participants can find
it.

The **provider** is the emulated customer system: its schema, its keys, its REST
API. Engines reach providers only over HTTP, never by project reference, because
a real customer system would not be linkable.

```
  ISBM channel  ──▶  Engine  ──▶  Provider REST API  ──▶  customer database
                       │
                       └────────▶  CIR   (identity federation)
```

`CmsEngine` is the reference to copy: one inbound leg, no outbound, no approval
gate. `RegLocationEngine` adds a second leg that publishes; read it only once the
first leg works.

---

## Before writing code

Answer these. Each one is a decision that is expensive to reverse.

1. **What does the system receive, and what does it assert?**
   CMS receives sites and asserts nothing — a condition monitoring system that
   invented its own plants would be claiming something only the enterprise can.
   If your system only receives, you need one leg. Stop there for the first pass.

2. **What is the system's own key, and can the publisher supply it?**
   If the provider's key is IDENTITY-assigned, the engine cannot supply it on
   first contact and must write it back to CIR afterwards (DR-033). If the
   publisher's federation GUID *is* the provider's key — as with CMS sites — the
   upsert is naturally idempotent and there is no write-back.

3. **Which CIR category does the system's identity belong in?**
   Sites go in `ITWIN-SITE`. Classes go in `RDL-CLASS`. Individual assets go in
   `ASSET`. Do not put two levels in one category (DR-033): a query for classes
   that also returns every asset is a defect you can only fix by reissuing
   CIRIDs other participants already hold.

4. **Does the system model classes as data, or as schema?**
   REG-LOCATION holds a class in a column, so an unknown class can bind to a
   parent. MMS's table names *are* its classes, so an unmapped class must be
   declined — picking a table would be corruption, not degradation (DR-030).

---

## The five files

`CmsEngine` is nine files, of which five carry the pattern.

### 1. `Application/{X}EngineOptions.cs`

Every external dependency is a nullable URL plus an optional key. **An empty URL
disables the leg rather than failing it** — the engine and provider are separate
hosts and either may start first, so treating a missing provider as a
configuration error makes ordinary local startup look broken.

Identity properties every engine needs:

| Property | Meaning |
| --- | --- |
| `Enterprise` | Owns CIR entries and names channels. `acme`. |
| `SourceId` | How the system identifies itself in CIR — its key space. |
| `LogicalId` | How the system identifies itself on the bus. |

Feature flags default to **`true`**, not `false` (DR-025, DR-026). A flag that
defaults off produces a deployed engine that does nothing and reports no error.

### 2. `Application/Incoming{X}Mapper.cs`

**Pure and synchronous.** Every judgement about what an arriving message means
lives here, where it can be tested without a broker or a provider. The service
decides what to *do* with the answer; the mapper only decides what the answer is.

Return a result type carrying either a rejection reason or the mapped values —
never throw for a bad message, and never return null:

```csharp
public sealed record SiteMappingResult(SiteRejection Rejection, /* … */)
{
    public bool IsMapped => Rejection == SiteRejection.None;
    public static SiteMappingResult Rejected(SiteRejection r) => new(r, /* … */);
}
```

Rules for mapping decisions:

- **Never invent a federation identity.** A minted GUID looks like it worked,
  and the same entity then exists twice across the federation with nothing to
  show they were ever one thing. Missing UUID ⇒ reject.
- **Never substitute one key space for another.** CMS refuses to fall back from
  `ShortName` to `IDInInfoSource`, because that would put the publisher's
  internal identifier in a column operators read.
- **Fall back only where the result is still honest.** `FullName` → `ShortName`
  is fine: a site known by its operator code is still a usable site.
- **Leave unasserted fields null** (DR-032). If CCOM carries no parent, country
  or status, write null. Do not populate a column encoding the receiving
  system's own organisation — that invents a fact no publisher asserted, and the
  invention outlives the reasoning behind it.

### 3. `Application/{X}IngestionService.cs`

The drain loop. Four things it must get right:

**Cache the session in a field.** The session is the broker's record of what
this consumer has already seen. Reopening one each pass either replays the
channel or silently skips what arrived in between:

```csharp
private string? _sessionId;

_sessionId ??= await isbm.OpenSubscriptionSessionAsync(
    _options.SitesChannelUri, _options.SitesTopics, ct);
```

This is why the service is registered **singleton** (see `Program.cs` below).

**Distinguish the three outcomes.** Null from the broker means empty, not error:

| Outcome | Action |
| --- | --- |
| Broker returns null | Queue is empty — break |
| Unparseable content | Discard (`return true`) — retrying cannot help, and leaving it blocks everything behind it |
| Unrecognised BOD | Acknowledge and skip — not yours to handle |
| Mapper rejected | Count, log, continue — the message will not improve on redelivery |
| Provider rejected, transient | `return false` — leave on channel, a fix can be deployed |
| Handler threw | `return false` — leave on channel |

**Remove only after the work is done.** A crash before `RemovePublicationAsync`
redelivers the message, which is safe precisely because the upsert is keyed on a
stable identity.

**Match the BOD noun exactly.** `SyncSites` yields noun `Sites`, plural — the
root name with the verb removed. The singular matched nothing, and because an
unrecognised BOD is skipped rather than failed, that leg reported success while
registering nothing at all. Verify against a real message before trusting it.

Return a report object counting every outcome. `messagesRead`, `created`,
`updated`, `rejected`, `cirEntriesRegistered`, `failed` — this is what you will
debug from in production.

### 4. `Infrastructure/{System}/{X}RestClient.cs`

A typed client per external system. Follow the provider's conventions for
partial success: the MMS and CMS providers return per-item rejections with a
`Transient` flag and answer `503` when *all* items failed transiently, so the
ingest leg's retry logic needs no special casing.

### 5. `Functions/{X}EngineFunctions.cs`

Four endpoints. The first three are the minimum; the fourth is mandatory if you
cache anything.

```csharp
[Function("XEngineIngest")]         // TimerTrigger, %FlatSettingName%
[Function("XEngineIngestNow")]      // POST engine/ingest — drain on demand
[Function("XEngineStatus")]         // GET  engine/status — config, no secrets
[Function("XEngineReset")]          // POST engine/reset  — clear state AND caches
```

The timer schedule must be a **flat** setting name: the WebJobs binding resolves
`%...%` against flat configuration, not the bound options section.

`engine/status` returns configuration and readiness, never a key. Include
`{system}Configured` and `cirConfigured` booleans — an unconfigured dependency
is the first thing to check when a leg is silently doing nothing.

---

## CIR registration

Registering is what makes the system's key resolvable by participants that do
not speak its key space. The CIRID is the shared identity; each participant
registers its own identifier against it.

```csharp
new CirCategory(
    Id: "ITWIN-SITE",              // the level — see the category rules above
    SourceId: _options.SourceId,   // the registrar — existing convention
    Entries: entries)
```

**Categories are split by registrar in this solution.** The live `acme` registry
holds `RDL-CLASS`/`ENG` and `RDL-CLASS`/`REG-LOCATION` as separate categories
that federate on a shared CIRID. Follow that convention (DR-033).

**Use `CreateEquivalentEntries` for write-backs.** Its merge rule gives an
existing CIRID precedence, so a late registration adopts the federated identity
rather than displacing it.

**CIR failure is not fatal.** The row is already in the provider; failing here
would leave it unrecorded and blocked behind a registry that is still down. Log
and return zero.

---

## Caching, and the rule that costs the most

If you cache anything resolved from CIR — a class mapping, an identity, a table
name — you must invalidate it (DR-031):

> **Whatever invalidates a source invalidates its caches.**

Day zero drops the CIR registry but restarts nothing, so an in-memory cache
outlives the database it describes. Every lookup is then answered from memory
and the write that would rebuild the row is skipped. Nothing faults, because
from the engine's point of view there is no work to do. This cost days to find.

Expose `ClearCacheAsync` and call it from `engine/reset`. Day zero drops CIR
*before* calling the engine resets, so identity is never left describing rows
that no longer exist.

Add a **TTL as well**. `UpdateEntryCIRID` can collapse two CIRIDs at any time
with no notification, and ws-CIR has no Sync verb, so a cached resolution goes
stale silently whether or not anyone reset anything.

---

## Wiring: `Program.cs`

Copy `CmsEngine/Program.cs`. Three things matter:

**Trailing slash on every base address.** Without it the last path segment is
replaced rather than appended when relative routes resolve:

```csharp
http.BaseAddress = new Uri(options.CmsBaseUrl.TrimEnd('/') + "/");
```

**`IsbmRestClient` needs an extra registration.** It takes `IsbmClientOptions`
directly rather than `IOptions<>`, so the typed-client registration cannot
construct it unaided:

```csharp
builder.Services.AddTransient(sp =>
    sp.GetRequiredService<IOptions<IsbmClientOptions>>().Value);
```

**Ingestion services are singletons.** Scoped or transient opens a new session
every timer tick and discards the broker's record of what was already read.

---

## Declaring the participant

Add `Oiie.Sandbox.Core/PersonalityPacks/{id}/personality.yaml`:

```yaml
participantId: x
sourceId: X
channels:
  - channelUri: /acme/enterprise/sites/publication
    role: Subscriber
    topics: [ "oiie:sc01/ccom:SyncSites" ]
cir:
  channelUri: /OIIE/CIR/Request
  publicationChannelUri: /OIIE/CIR/Publication
  registryId: acme
```

**Every channel the system touches must be declared here**, including channels
owned by other systems. Day zero deletes every channel the broker reports but
recreates only what the packs declare — so an undeclared channel is deleted on
every run, taking its sessions with it, and the reset warns about an orphan it
created itself. Channels you do not own are *ensured*, never deleted.

Duplicated URIs between a pack and an engine's settings do not fail loudly: the
engine opens a session on a channel nobody sends to and reports a clean, empty
drain forever. Add a test asserting the two agree, as `RdlPersonalityTests` does.

---

## Verifying

In order. Each step rules out a class of failure the next would otherwise mask.

1. **Build and test.** `dotnet build OpenOM.slnx` and the sandbox suite.
2. **`GET engine/status`.** Confirm every `*Configured` flag is true. A false
   one explains silence immediately.
3. **`POST engine/ingest`.** Expect `messagesRead > 0`. Zero means the session,
   the channel or the topic — not the mapper.
4. **Query the provider.** Confirm the rows landed.
5. **Query `cir.Entry` directly in SQL, not through the provider API.** An
   incidental restart repopulates cached rows and makes the registry look
   correct through the API. Direct reads isolated a defect in one attempt after
   days of ambiguity.
6. **Run day zero, then repeat 3-5.** This is what exercises cache
   invalidation. A warm repeat should *reuse* identities, not create new ones —
   a second entry for the same class means the cross-reference is not being
   consulted.

---

## Failure modes, in the order you will meet them

| Symptom | Cause |
| --- | --- |
| `messagesRead: 0` forever, no error | Wrong topic or noun; or a session naming a subscription that does not exist. Check the BOD noun is plural |
| Leg does nothing, reports success | A feature flag defaulting to `false` |
| Registry empty after day zero, no error | A cache outliving the registry (DR-031) |
| Day zero warns about orphaned channels | A channel configured on a provider but declared in no pack |
| Provider cannot post to a channel | Channel created with the wrong type — derive it from the URI, never assume |
| Rows exist via API, absent in SQL | You are reading a repopulated cache. Trust the database |
| Duplicate identities across systems | Something minted a GUID instead of rejecting a message that lacked one |

The pattern: **the expensive failures are silent ones.** An engine that throws
gets fixed in an hour. An engine that reports success while doing nothing costs
days. When choosing between failing loudly and degrading quietly, fail loudly.

---

# Appendix: the code template

A complete skeleton for `XEngine`, an isolated-worker Function App with one
inbound leg. Replace `X` with the system name and `Thing` with the entity it
receives. The comments are part of the template — they record why the shape is
what it is, and copying the code without them reproduces the structure while
losing the reasoning.

## Project layout

```
XEngine/
├── XEngine.csproj
├── Program.cs
├── host.json
├── local.settings.json
├── Application/
│   ├── XEngineOptions.cs
│   ├── IncomingThingMapper.cs
│   └── ThingIngestionService.cs
└── Infrastructure/
    ├── Cir/CirRestClient.cs        (copy from CmsEngine)
    └── X/XRestClient.cs            (datasource-specific)
```

## `XEngine.csproj`

Functions worker 2.x. `ConfigureFunctionsWebApplication` and the ASP.NET Core
integration types (`HttpRequest`/`IActionResult`) require both `Worker` and
`Worker.Sdk` on 2.x, plus the `AspNetCore` extension.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AzureFunctionsVersion>v4</AzureFunctionsVersion>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker" Version="2.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Sdk" Version="2.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore" Version="2.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Timer" Version="4.*" />
    <PackageReference Include="Microsoft.ApplicationInsights.WorkerService" Version="2.*" />
    <PackageReference Include="Microsoft.Azure.Functions.Worker.ApplicationInsights" Version="2.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Oiie.Ccom\Oiie.Ccom.csproj" />
    <ProjectReference Include="..\Oiie.Isbm.Client\Oiie.Isbm.Client.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="host.json" CopyToOutputDirectory="PreserveNewest" />
    <None Update="local.settings.json"
          CopyToOutputDirectory="PreserveNewest"
          CopyToPublishDirectory="Never" />
  </ItemGroup>

</Project>
```

Note the provider is **not** referenced. Match the exact package versions
against `CmsEngine.csproj` rather than trusting the wildcards above.

## `Program.cs`

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using XEngine.Application;
using XEngine.Infrastructure.Cir;
using XEngine.Infrastructure.X;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Oiie.Isbm.Client;

// FunctionsApplication.CreateBuilder is the 2.x entry point. It wraps
// HostApplicationBuilder, so services are reached directly rather than through
// a ConfigureServices callback. Requires Worker and Worker.Sdk on 2.x.
var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Web defaults give camelCase; enums must be opted in explicitly.
builder.Services.Configure<JsonSerializerOptions>(o =>
{
    o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<XEngineOptions>(
    builder.Configuration.GetSection("XEngine"));

// --- Upstream: X -----------------------------------------------------------
//
// A typed client over HTTP rather than a project reference. XProvider emulates
// a customer system and must stay reachable only the way a real one would be.
builder.Services.AddHttpClient<IXClient, XRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<XEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.XBaseUrl))
    {
        // A trailing slash matters: without it the last path segment is replaced
        // rather than appended when relative routes are resolved.
        http.BaseAddress = new Uri(options.XBaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(options.XApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.XApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// --- Downstream: CIR -------------------------------------------------------

builder.Services.AddHttpClient<ICirClient, CirRestClient>((sp, http) =>
{
    var options = sp.GetRequiredService<IOptions<XEngineOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(options.CirBaseUrl))
    {
        http.BaseAddress = new Uri(options.CirBaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(options.CirApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", options.CirApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// --- Downstream: ISBM ------------------------------------------------------

builder.Services.Configure<IsbmClientOptions>(builder.Configuration.GetSection("Isbm"));

builder.Services.AddHttpClient<IIsbmClient, IsbmRestClient>((sp, http) =>
{
    var isbm = sp.GetRequiredService<IOptions<IsbmClientOptions>>().Value;

    if (!string.IsNullOrWhiteSpace(isbm.BaseUrl))
    {
        http.BaseAddress = new Uri(isbm.BaseUrl.TrimEnd('/') + "/");
    }

    if (!string.IsNullOrWhiteSpace(isbm.ApiKey))
        http.DefaultRequestHeaders.Add("x-functions-key", isbm.ApiKey);

    http.Timeout = TimeSpan.FromSeconds(60);
});

// IsbmRestClient takes IsbmClientOptions directly rather than IOptions<>, so the
// typed-client registration above cannot construct it unaided.
builder.Services.AddTransient(sp =>
    sp.GetRequiredService<IOptions<IsbmClientOptions>>().Value);

// --- Engine ----------------------------------------------------------------

builder.Services.AddSingleton<IncomingThingMapper>();

// Singleton because it holds the ISBM subscription session between polls. A
// scoped or transient ingestor would open a new session on every timer tick,
// and the broker's record of what this consumer has already read would be
// discarded along with it.
builder.Services.AddSingleton<ThingIngestionService>();

builder.Build().Run();
```

## `Application/XEngineOptions.cs`

```csharp
namespace XEngine.Application;

/// <summary>
/// Configuration for the X integration engine.
///
/// One leg: X is told what exists and records it. Add outbound channel, sweep
/// schedule and approval settings only if X genuinely asserts something.
/// </summary>
public sealed class XEngineOptions
{
    // ---- Upstream: X ------------------------------------------------------

    /// <summary>
    /// Where XProvider's REST API lives.
    ///
    /// Empty disables the ingest leg rather than failing it. The engine and the
    /// provider are separate hosts and either may be started first; treating a
    /// missing X as a configuration error would make ordinary local startup
    /// look broken.
    /// </summary>
    public string? XBaseUrl { get; set; }

    /// <summary>The Functions key for XProvider, if it requires one.</summary>
    public string? XApiKey { get; set; }

    // ---- Identity ---------------------------------------------------------

    /// <summary>
    /// The enterprise this engine belongs to, used to name the channel and as
    /// the owner of CIR entries.
    /// </summary>
    public string Enterprise { get; set; } = "acme";

    /// <summary>
    /// How X identifies itself in CIR: its own key space, distinct from every
    /// other participant's. Two systems may hold the same entity, and the
    /// registry is what relates their identifiers.
    /// </summary>
    public string SourceId { get; set; } = "X";

    /// <summary>How X identifies itself on the bus.</summary>
    public string LogicalId { get; set; } = "X";

    // ---- Upstream: the ingest leg -----------------------------------------

    /// <summary>
    /// Whether the engine consumes from the channel. On by default: a flag
    /// defaulting to false produces a deployed engine that does nothing and
    /// reports no error.
    /// </summary>
    public bool ThingIngestEnabled { get; set; } = true;

    /// <summary>Set only when the derived channel URI is not what you need.</summary>
    public string? ThingChannelUriOverride { get; set; }

    /// <summary>Topics subscribed to on the channel.</summary>
    public string[] ThingTopics { get; set; } = ["oiie/ccom:SyncThings"];

    /// <summary>The channel messages are consumed from.</summary>
    public string ThingChannelUri =>
        !string.IsNullOrWhiteSpace(ThingChannelUriOverride)
            ? ThingChannelUriOverride
            : $"/{Enterprise}/enterprise/things/publication";

    /// <summary>
    /// Ceiling on publications drained in one poll, so a backlog is worked
    /// through in bounded batches rather than in one unbounded run.
    /// </summary>
    public int MaxMessagesPerPoll { get; set; } = 25;

    // ---- Downstream: CIR --------------------------------------------------

    /// <summary>
    /// Whether what is written to X is also registered in CIR.
    ///
    /// Registration is what lets another system resolve X's identifier without
    /// knowing X's key space. Without it the row exists but is unreachable by
    /// anything that did not already hold its key.
    /// </summary>
    public bool RegisterInCir { get; set; } = true;

    /// <summary>Where CIR lives. Empty leaves rows written but unregistered.</summary>
    public string? CirBaseUrl { get; set; }

    /// <summary>The Functions key for CIR, if it requires one.</summary>
    public string? CirApiKey { get; set; }
}
```

## `Application/IncomingThingMapper.cs`

```csharp
using Oiie.Ccom.Types;

namespace XEngine.Application;

/// <summary>Why a thing could not be recorded in X.</summary>
public enum ThingRejection
{
    None = 0,

    /// <summary>No federation GUID, so the thing would have no shared identity.</summary>
    NoFederationId,

    /// <summary>No code, so X would have nothing to refer to the thing by.</summary>
    NoCode,

    /// <summary>The class is not mapped to anything X can hold.</summary>
    UnmappedClass
}

/// <summary>
/// The outcome of translating one thing into what XProvider's upsert needs.
/// </summary>
public sealed record ThingMappingResult(
    ThingRejection Rejection,
    Guid ThingId,
    string ThingCode,
    string ThingName,
    string? Description)
{
    public bool IsMapped => Rejection == ThingRejection.None;

    public static ThingMappingResult Rejected(ThingRejection rejection) =>
        new(rejection, Guid.Empty, string.Empty, string.Empty, null);
}

/// <summary>
/// Translates an incoming CCOM Thing into what X needs to hold it.
///
/// Pure and synchronous: every judgement about what an arriving thing means is
/// made here where it can be tested without a broker or a provider, and the
/// service decides what to do with the answer.
/// </summary>
public sealed class IncomingThingMapper
{
    public ThingMappingResult Map(Thing thing)
    {
        // The federated identity, which this leg cannot invent: a minted GUID
        // would look like it worked, and the same thing would exist twice
        // across the federation with nothing to show they were ever one thing.
        if (thing.UUID == Guid.Empty)
        {
            return ThingMappingResult.Rejected(ThingRejection.NoFederationId);
        }

        // No fallback to IDInInfoSource: the code is how an operator refers to
        // this, and quietly substituting the publisher's internal identifier
        // would put a foreign key space in a column people read.
        var code = Trimmed(thing.ShortName);
        if (code is null)
        {
            return ThingMappingResult.Rejected(ThingRejection.NoCode);
        }

        // Falls back to the code rather than being rejected: something known by
        // its operator code but lacking a long-form name is still usable.
        var name = Trimmed(thing.FullName) ?? code;

        return new ThingMappingResult(
            ThingRejection.None,
            ThingId: thing.UUID,
            ThingCode: code,
            ThingName: name,
            Description: Trimmed(thing.Description));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
```

## `Application/ThingIngestionService.cs`

```csharp
using XEngine.Infrastructure.Cir;
using XEngine.Infrastructure.X;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using Oiie.Isbm.Client;

namespace XEngine.Application;

/// <summary>What one drain of the channel did.</summary>
public sealed class ThingIngestionReport
{
    /// <summary>Publications read from the channel.</summary>
    public int MessagesRead { get; set; }

    /// <summary>Things found across those publications.</summary>
    public int ThingsSeen { get; set; }

    /// <summary>Things newly created in X.</summary>
    public int Created { get; set; }

    /// <summary>Things that already existed and were updated.</summary>
    public int Updated { get; set; }

    /// <summary>Things the mapper refused.</summary>
    public int Rejected { get; set; }

    /// <summary>CIR entries written.</summary>
    public int CirEntriesRegistered { get; set; }

    /// <summary>Messages left on the channel because handling them failed.</summary>
    public int Failed { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// Receives SyncThings and records them in X.
///
/// This is how a thing comes to exist in X. Nothing else creates one: XProvider
/// is reachable only through its own REST API, so the only route from "the
/// enterprise published it" to "X knows about it" is this service.
/// </summary>
public sealed class ThingIngestionService(
    IIsbmClient isbm,
    IXClient x,
    ICirClient cir,
    IncomingThingMapper mapper,
    IOptions<XEngineOptions> options,
    ILogger<ThingIngestionService> logger)
{
    private readonly XEngineOptions _options = options.Value;

    /// <summary>
    /// The subscription session, cached across polls. A session is the broker's
    /// record of what this consumer has already seen, and reopening one each
    /// pass either replays the channel or silently skips what arrived in
    /// between.
    /// </summary>
    private string? _sessionId;

    public async Task<ThingIngestionReport> DrainAsync(CancellationToken ct)
    {
        var report = new ThingIngestionReport();

        if (!_options.ThingIngestEnabled)
        {
            report.Note = "ThingIngestEnabled is off.";
            return report;
        }

        if (string.IsNullOrWhiteSpace(_options.XBaseUrl))
        {
            report.Note = "XBaseUrl is not configured, so things cannot be recorded.";
            logger.LogWarning("{Note}", report.Note);
            return report;
        }

        _sessionId ??= await isbm.OpenSubscriptionSessionAsync(
            _options.ThingChannelUri, _options.ThingTopics, ct);

        while (report.MessagesRead < _options.MaxMessagesPerPoll)
        {
            var message = await isbm.ReadPublicationAsync(_sessionId, ct);

            // Null is the broker saying the queue is empty, not an error.
            if (message is null)
            {
                break;
            }

            report.MessagesRead++;

            bool handled;
            try
            {
                handled = await HandleMessageAsync(message, report, ct);
            }
            catch (Exception ex)
            {
                // Left on the channel rather than discarded: a fix can be
                // deployed and the message reprocessed.
                logger.LogError(
                    ex,
                    "Failed to handle publication {MessageId}; leaving it on the channel.",
                    message.MessageId);

                handled = false;
            }

            if (!handled)
            {
                report.Failed++;
                break;
            }

            // Removed only after every thing in it has been recorded. A crash
            // before this point redelivers the message, which is safe because
            // the upsert is keyed on the thing's own GUID.
            await isbm.RemovePublicationAsync(_sessionId, ct);
        }

        if (report.MessagesRead > 0)
        {
            logger.LogInformation(
                "Thing drain read {Messages} message(s): {Created} created, {Updated} updated, " +
                "{Rejected} rejected, {Cir} CIR entry(ies), {Failed} left on the channel.",
                report.MessagesRead, report.Created, report.Updated,
                report.Rejected, report.CirEntriesRegistered, report.Failed);
        }

        return report;
    }

    /// <summary>
    /// Records every thing in one publication. Returns false when the message
    /// should stay on the channel.
    /// </summary>
    private async Task<bool> HandleMessageAsync(
        IsbmMessage message, ThingIngestionReport report, CancellationToken ct)
    {
        if (message.Content is null)
        {
            // Unparseable, and retrying will not change that. The one case where
            // discarding is right: leaving it would block everything behind it.
            logger.LogError(
                "Publication {MessageId} carried no parseable XML; discarding it.",
                message.MessageId);

            return true;
        }

        var envelope = BodEnvelope.Parse(message.Content.ToString());

        // Plural: the noun is the BOD's root name with the verb removed, so
        // SyncThings yields Things. Verify against a real message -- the
        // singular matches nothing, and an unrecognised BOD is skipped rather
        // than failed, so the leg would report success while registering
        // nothing at all.
        if (!envelope.Is("Sync", "Things"))
        {
            logger.LogDebug(
                "Publication {MessageId} was {Verb}{Noun}, which this leg does not handle.",
                message.MessageId, envelope.Verb, envelope.Noun);

            return true;
        }

        var mapped = new List<ThingMappingResult>();

        foreach (var thing in envelope.NounsAs(e => new Thing(e)))
        {
            report.ThingsSeen++;

            var result = mapper.Map(thing);

            if (!result.IsMapped)
            {
                // Counted and logged, not retried: the message will not improve
                // on redelivery.
                report.Rejected++;

                logger.LogWarning(
                    "Thing '{Name}' in publication {MessageId} was rejected: {Reason}.",
                    thing.ShortName ?? thing.FullName ?? "(unnamed)",
                    message.MessageId,
                    result.Rejection);

                continue;
            }

            mapped.Add(result);
        }

        if (mapped.Count == 0)
        {
            // Nothing usable, but the message was understood. Acknowledged so
            // the channel is not blocked by a publication that will never
            // succeed.
            return true;
        }

        var upserts = mapped
            .Select(m => new ThingUpsert(
                ThingId: m.ThingId,
                ThingCode: m.ThingCode,
                ThingName: m.ThingName,
                Description: m.Description,

                // Left null because the publisher asserted nothing here.
                // Inventing values would put facts in X that no publisher ever
                // stated, and the invention outlives the reasoning behind it.
                Status: null))
            .ToList();

        var upsertResult = await x.UpsertThingsAsync(upserts, ct);

        report.Created += upsertResult.Things.Count(t => t.Created);
        report.Updated += upsertResult.Things.Count(t => !t.Created);

        foreach (var rejection in upsertResult.Rejections)
        {
            logger.LogWarning(
                "X rejected thing {Key}: {Reason} (transient: {Transient}).",
                rejection.Key, rejection.Reason, rejection.Transient);
        }

        // Something X refused for a reason that may pass is grounds to keep the
        // message: acknowledging it would lose the thing permanently.
        if (upsertResult.Rejections.Any(r => r.Transient))
        {
            return false;
        }

        report.CirEntriesRegistered += await RegisterInCirAsync(mapped, ct);

        return true;
    }

    /// <summary>
    /// Registers the things in CIR under X's own key space.
    ///
    /// This is what makes X's identifier resolvable by systems that do not speak
    /// X. The CIRID is the federation GUID, the same one other participants
    /// registered against their own identifiers -- which is precisely how the
    /// registry relates them without either system learning the other's keys.
    /// </summary>
    private async Task<int> RegisterInCirAsync(
        IReadOnlyList<ThingMappingResult> mapped, CancellationToken ct)
    {
        if (!_options.RegisterInCir || string.IsNullOrWhiteSpace(_options.CirBaseUrl))
        {
            return 0;
        }

        var entries = mapped
            .Select(m => new CirEntry(
                // X's own identifier. Where the provider assigns its own key,
                // this is where that key goes -- and the write-back must happen
                // after the upsert returns it, using CreateEquivalentEntries.
                IdInSource: m.ThingId.ToString(),
                SourceId: _options.SourceId,
                Cirid: m.ThingId,
                SourceOwnerId: _options.Enterprise,
                Name: m.ThingCode,
                Description: new CirLocalizedText(m.Description ?? m.ThingName),
                Properties: []))
            .ToList();

        var request = new CreateRegistryRequest(
            [
                new CirRegistry(
                    Id: _options.Enterprise,
                    Description: [new CirLocalizedText("Things")],
                    Categories:
                    [
                        // The category is the level: classes in RDL-CLASS,
                        // individual assets in ASSET, sites in ITWIN-SITE. Do
                        // not put two levels in one category.
                        new CirCategory(
                            Id: "ASSET",
                            SourceId: _options.SourceId,
                            Description: [new CirLocalizedText("X things")],
                            Entries: entries)
                    ])
            ],
            CreateCirid: false);

        try
        {
            return await cir.RegisterEntriesAsync(request, ct);
        }
        catch (CirClientException ex)
        {
            // Not fatal. The thing is already in X, and failing here would leave
            // it unrecorded and blocked behind a CIR that is still down.
            logger.LogError(ex, "Registering {Count} X thing(s) in CIR failed.", entries.Count);
            return 0;
        }
    }
}
```

## `Functions/XEngineFunctions.cs`

```csharp
using XEngine.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace XEngine.Functions;

/// <summary>
/// The X engine's HTTP and timer surface.
///
/// One leg only: X consumes and publishes nothing. Add a sweep and an approval
/// endpoint only if X genuinely proposes something.
/// </summary>
public sealed class XEngineFunctions(
    ThingIngestionService thingIngestion,
    IOptions<XEngineOptions> options,
    ILogger<XEngineFunctions> logger)
{
    private readonly XEngineOptions _options = options.Value;

    /// <summary>
    /// Reads things off the channel.
    ///
    /// Flat setting name for the schedule because the WebJobs binding resolves
    /// %...% against the flat configuration, not the bound options section.
    /// </summary>
    [Function("XEngineIngestThings")]
    public async Task XEngineIngestThings(
        [TimerTrigger("%XEngineThingIngestSchedule%")] TimerInfo timer,
        CancellationToken ct)
    {
        if (!_options.ThingIngestEnabled) return;

        var report = await thingIngestion.DrainAsync(ct);

        if (report.Failed > 0)
        {
            logger.LogError(
                "Thing drain left {Failed} message(s) on the channel after {Read} read.",
                report.Failed, report.MessagesRead);
        }
    }

    /// <summary>Drains the channel on demand, without waiting for the timer.</summary>
    [Function("XEngineIngestThingsNow")]
    public async Task<IActionResult> XEngineIngestThingsNow(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/ingest-things")] HttpRequest req,
        CancellationToken ct)
        => new OkObjectResult(await thingIngestion.DrainAsync(ct));

    /// <summary>Configuration and readiness, without exposing any key.</summary>
    [Function("XEngineStatus")]
    public IActionResult XEngineStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "engine/status")] HttpRequest req)
        => new OkObjectResult(new
        {
            thingIngestEnabled = _options.ThingIngestEnabled,
            thingChannelUri = _options.ThingChannelUri,
            thingTopics = _options.ThingTopics,
            enterprise = _options.Enterprise,
            sourceId = _options.SourceId,
            xConfigured = !string.IsNullOrWhiteSpace(_options.XBaseUrl),
            registerInCir = _options.RegisterInCir,
            cirConfigured = !string.IsNullOrWhiteSpace(_options.CirBaseUrl),
            maxMessagesPerPoll = _options.MaxMessagesPerPoll
        });

    /// <summary>
    /// Clears cached state. Mandatory if the engine caches anything resolved
    /// from CIR: day zero drops the registry but restarts nothing, so a cache
    /// would otherwise outlive the database it describes and every lookup would
    /// be answered from memory while the rows no longer exist.
    ///
    /// Omit only if the engine caches nothing but its session.
    /// </summary>
    [Function("XEngineReset")]
    public async Task<IActionResult> XEngineReset(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "engine/reset")] HttpRequest req,
        CancellationToken ct)
    {
        // await classResolver.ClearCacheAsync(ct);
        await Task.CompletedTask;
        return new OkObjectResult(new { reset = true });
    }
}
```

## `local.settings.json`

The `//key` entries are comments — the Functions host ignores unknown values,
and they are the only documentation a new developer gets when reading settings
in the portal.

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",

    "//x": "XProvider is reached over HTTP as a peer system. Leaving the base URL empty disables the ingest leg rather than failing it, so the engine can run before X is up.",
    "XEngine__XBaseUrl": "http://localhost:7076/api",
    "XEngine__XApiKey": "",

    "XEngine__Enterprise": "acme",
    "XEngine__SourceId": "X",
    "XEngine__LogicalId": "X",

    "//things": "The only leg. Each subscriber holds its own session, so every subscriber receives every message rather than competing for them.",
    "XEngine__ThingIngestEnabled": "true",
    "XEngine__ThingTopics__0": "oiie/ccom:SyncThings",
    "XEngine__MaxMessagesPerPoll": "25",

    "//cir": "Registering the identity so other systems can resolve X's key space. Empty CirBaseUrl leaves things written but unregistered.",
    "XEngine__RegisterInCir": "true",
    "XEngine__CirBaseUrl": "http://localhost:7072/api",
    "XEngine__CirApiKey": "",

    "Isbm__BaseUrl": "https://acme-api-isbm-dev.azurewebsites.net/api",
    "Isbm__ApiKey": "",

    "XEngineThingIngestSchedule": "0 */2 * * * *"
  }
}
```

Note the double underscore binds to the options section, while the schedule is
deliberately flat.

## `host.json`

```json
{
  "version": "2.0",
  "logging": {
    "applicationInsights": {
      "samplingSettings": { "isEnabled": true, "excludedTypes": "Request" }
    }
  }
}
```

## Wiring it up

1. Add the project to `OpenOM.slnx`.
2. Copy `Infrastructure/Cir/CirRestClient.cs` from `CmsEngine` unchanged, adjusting
   only the namespace.
3. Write `Infrastructure/X/XRestClient.cs` against the provider's API, following
   the `Transient` rejection convention so the ingest leg's retry logic needs no
   special casing.
4. Add the personality pack, declaring **every** channel the system touches.
5. Add the engine to `deploy/engines/deploy-engine.ps1`, shipping enabled.
6. Work through the verification sequence in the main document.
