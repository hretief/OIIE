# Personality packs

A **personality** is one simulated participant: a system with its own database
schema, its own reference data, its own codes for things, and its own opinion
about what it received. ENG, REG-LOCATION and MMS are personalities.

Packs live here and are copied to the output directory on build. The path is
overridable with `Sandbox:PersonalitiesPath`, which is how the Azure deployment
points at its own copy.

```
PersonalityPacks/
  eng/
    personality.yaml          identity, channels, ISBM and CIR endpoints
    Fixtures/classes.yaml     the reference data this participant holds
  reg-location/
    personality.yaml
    Fixtures/classes.yaml
  mms/
    personality.yaml          no Fixtures - deliberately
```

## Why the packs differ from each other

The asymmetry is the product, not an oversight.

| | Classes | Property definitions |
|---|---|---|
| ENG | 4, including the leaf `rdl:TemperatureIndicatingController` | 4 |
| REG-LOCATION | 2, no leaf class | 3 |
| MMS | 0 | 0 |

A federation where every participant held the same library would make graceful
degradation untestable, and graceful degradation is the behaviour that argues
for governed reference data in the first place. So:

- ENG classifies a tag against the leaf class and requires a control action.
- REG-LOCATION cannot resolve that leaf, binds at `rdl:Instrument` instead, and
  records the classification **degraded**. It still holds the `rdl:ControlAction`
  *definition*, so the value arrives **mapped** — understood, just filed under a
  coarser class.
- MMS holds no reference data at all, so the same value is retained **unmapped**:
  stored, flagged, never discarded.

That last point is the rule the whole model turns on. A receiver never drops a
value it does not understand. Holding a definition and holding the class that
sanctions it are separate questions, and conflating them is what makes
federation look all-or-nothing when it is not.

## Adding a new personality

### 1. Create the pack

`PersonalityPacks/<participantId>/personality.yaml`:

```yaml
participantId: ops            # must be unique; used as the DI and route key
displayName: OPS - Operations
schema: ops                   # SQL schema name; see step 2
sourceId: OPS                 # what this participant calls itself on the wire
sourceOwnerId: ACME-OPERATIONS
logicalId: urn:oiie-sandbox:ops
accentColour: "#0f766e"
releaseMode: Manual
identifierStyle: numeric

channels:
  - channelUri: /OIIE-SANDBOX/Enterprise/Site/OandM
    role: Subscriber          # Publisher | Subscriber
    topics:
      - Segments
    useNotifications: false

isbm:
  baseUrl: https://isbm-func-44p2f3n6dv7p4.azurewebsites.net/api
  tokenSecretName: sandbox-isbm-token-ops

cir:
  channelUri: /OIIE/CIR/Request
  requestTopic: ws-CIR
  baseUrl: https://cir-func-44p2f3n6.azurewebsites.net/api
  registryId: OIIE-SANDBOX
  identityCacheTtl: 00:05:00
```

`schema` must be a legal SQL identifier. It is the participant's own schema in
the shared database — participants are separated by schema, not by server, and
there are deliberately **no foreign keys between them**. The premise is that
these are independent systems that agree on an identity and on nothing else;
joining them in the database would model a coupling that does not exist.

A `tokenSecretName` must exist in Key Vault before the participant can open an
ISBM session.

### 2. Decide what reference data it holds

Optional. Omit `Fixtures/classes.yaml` entirely and the participant classifies
nothing — everything it receives is retained unmapped. That is a legitimate and
useful configuration: it is exactly what MMS is, and it models a legacy system
with no governed reference data.

Otherwise:

```yaml
propertyDefinitions:
  - key: rdl:ControlAction
    name: Control action
    dataType: Character       # Numeric | Character | Boolean | DateTime
    unitOfMeasure: degC       # optional

classes:
  - key: rdl:Equipment
    name: Equipment
    appliesTo: Segment
    kind: Taxonomy            # Taxonomy | Aspect
    properties: []

  - key: rdl:Instrument
    name: Instrument
    parent: rdl:Equipment
    appliesTo: Segment
    kind: Taxonomy
    properties:
      - definition: rdl:ControlAction
        requirement: Recommended   # Required | Recommended | Optional
        minValue: -273             # optional
        maxValue: 2000             # optional
```

Two rules worth knowing before you author a class:

- **A subclass may narrow an inherited entry but never widen it.** Tighten a
  range, promote Optional to Required, restrict a code list, fix a unit — all
  fine. Loosening any of those is rejected by `NarrowingRules`.
- **Do not redeclare CCOM spine fields as properties.** Identity, short name and
  description travel as `IDInInfoSource`, `ShortName` and `Description`. A class
  that declares them Required would demand values that never arrive as
  properties, because the sender puts them in the spine.

Give a participant *less* than ENG on purpose if you want to demonstrate
degradation. That is what makes the scenario interesting.

### 3. Give it a domain entity

Add a type under `Domain/<Participant>/` and register it in
`ParticipantDbContext.OnModelCreating`. Every participant entity that
represents a real-world thing carries a `FederationId` — the shared identity —
and its own local code, which is *not* the identity:

```csharp
public class OperatingPoint
{
    public Guid FederationId { get; set; }   // adopted, not minted
    public string PointCode { get; set; } = string.Empty;
    // ...
}
```

Only ENG mints. Issuing a code is not minting an identity, and conflating the
two is the error the whole model exists to expose.

### 4. Handle inbound BODs

Implement `IBodHandler` under `Personalities/<Participant>/`:

```csharp
public sealed class OpsSegmentsHandler(
    CcomAttributeMapperFactory mappers,
    ILogger<OpsSegmentsHandler> logger) : IBodHandler
{
    public (string Verb, string Noun) Handles => ("Sync", "Segments");
    public string? ParticipantId => "ops";

    public async Task<BodHandlingResult> HandleAsync(
        ParticipantContext participant,
        ParticipantDbContext db,
        BodEnvelope envelope,
        Guid messageId,
        CancellationToken ct)
    {
        // ...
    }
}
```

`ParticipantId` scopes the handler; returning `null` would make it apply to
every participant.

**Ingest the properties.** This is the step most easily forgotten, and forgetting
it silently discards everything beyond the CCOM spine:

```csharp
var mapper = mappers.For(participant);
var (incoming, classKeys) = mapper.Extract(segment);

// Transport metadata describes how to read the segment, not the thing itself.
var ingestible = incoming
    .Where(p => !p.DefinitionKey.StartsWith("sandbox:", StringComparison.Ordinal))
    .ToList();

var ingestion = participant.Ingestor.Ingest(
    nameof(OperatingPoint), pointCode, ingestible,
    effectiveSet,          // EffectivePropertySet.Empty if you classify nothing
    fromParticipant, messageId, DateTimeOffset.UtcNow);

foreach (var definition in ingestion.InferredDefinitions)
{
    db.PropertyDefinitions.Add(definition);   // guard against duplicates
}

foreach (var value in ingestion.Values)
{
    db.PropertyValues.Add(value);
}
```

Pass `EffectivePropertySet.Empty` if the participant genuinely classifies
nothing. Fabricating a set would report understanding the system does not have.

### 5. Publish, if it publishes

Implement `IBodBuilder`, scoped the same way. If the participant forwards data
it received, re-emit the retained property values or the chain breaks at that
participant:

```csharp
var values = await db.PropertyValues
    .AsNoTracking()
    .Where(v => v.EntityType == nameof(OperatingPoint)
        && v.EntityKey == point.PointCode
        && v.ValidTo == null)
    .ToListAsync(ct);

mapper.Apply(segment, values, EffectivePropertySet.Empty, participant.Config.SourceId);
```

Forward mapped and unmapped alike. Forwarding only what you understood makes the
participant a filter, and a downstream system may hold a definition you lack.

### 6. Register it

In `Program.cs`, next to the existing personalities:

```csharp
builder.Services.AddSingleton<IBodHandler, OpsSegmentsHandler>();
builder.Services.AddSingleton<IBodBuilder, OpsSegmentsBuilder>();   // if publishing
builder.Services.AddSingleton<OpsService>();                        // if it has actions
```

The pack itself needs no registration — `PersonalityLoader.LoadAll` discovers
every directory under the packs root.

### 7. Make it visible in the UI

Two switches key on participant id and will otherwise silently skip the new
participant:

- `MessageTransformService.ReadRecordsAsync` — projects the entity to labelled
  fields for the record cards. Without a case here the participant renders as
  "holds no record", which reads as a finding rather than an omission.
- `IdentityLineageService.ReadAsync` and its `FlowOrder` array — puts the
  participant in the identity chain, in the position the data actually travels.

### 8. Provision the schema

```pwsh
Invoke-RestMethod -Method Post https://localhost:7180/admin/schema/reset
```

`EnsureTablesAsync` checks only for a sentinel table, so it cannot add tables to
an existing schema. A drifted schema needs `reset`, not `init` — which is safe
here and nowhere else.

### 9. Add it to a scenario

```yaml
participants:
  - eng
  - ops

setup:
  channels:
    - uri: /OIIE-SANDBOX/Enterprise/Site/OandM
      type: Publication
      subscribers:
        - ops
```

Then assert on rows, not on log lines. `store_contains` against the new entity
with `FederationId <> '00000000-...'` is the assertion that proves the identity
survived the trip.

## Checklist

- [ ] `personality.yaml` with a unique `participantId` and a legal `schema`
- [ ] Key Vault secret matching `tokenSecretName`
- [ ] `Fixtures/classes.yaml`, or a deliberate decision to hold no reference data
- [ ] Domain entity with `FederationId`, registered in `ParticipantDbContext`
- [ ] `IBodHandler` that **ingests properties**, not just the spine
- [ ] `IBodBuilder` that **re-emits retained values**, if it publishes
- [ ] Registrations in `Program.cs`
- [ ] Case in `MessageTransformService.ReadRecordsAsync`
- [ ] Entry in `IdentityLineageService.FlowOrder`
- [ ] `/admin/schema/reset`
- [ ] A scenario that asserts on its rows
