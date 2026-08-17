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

## Two kinds of participant

Before writing any code, decide which of these you are building. The difference
is not cosmetic: it determines whether the participant may store a shared
identity at all, and that decision is very hard to reverse once rows exist.

**Sandbox-native.** The schema is ours to design. ENG, REG-LOCATION and CMS are
these. Entities carry a `FederationId` holding the shared identity directly, and
identity resolution is a local column read.

**Mapped to a real system.** The schema belongs to somebody else and arrived as
a given. MMS is this: `LIGHT_SYSTEM_INVENTORY`, `SETUP_OWNER` and the rest are
the customer's tables, reproduced column for column.

For a mapped participant, three rules follow, and they are not negotiable:

- **No new columns.** Not a `FederationId`, not a `Cirid`, not a nullable one
  "just for us". A column we add is a second identity competing with the
  system's own, inside a schema we do not own. This is DR-008.
- **Identity lives in ws-CIR.** The correspondence between the local key and
  everyone else's is registered in the registry and resolved on read. There is
  no local shortcut, and adding one is the mistake the rule exists to prevent.
- **Context is resolved, never joined.** MMS has no iTwin column, so a
  twin-scoped read resolves the twin to an `OWNER_ID` through CIR *first*, then
  filters on that. See `MmsContextResolver`.

A mapped participant costs a registry round trip on every context resolution.
That is the price of not owning the schema, and it is worth paying: it is what
lets the real system be swapped in for the sandbox one without a migration.

If a table has no counterpart in the real system yet \u2014 as MMS's `WorkOrder` and
`EquipmentRecord` do not \u2014 mark it clearly in the entity's doc comment as
sandbox-only, so nobody later mistakes it for customer schema.

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
`ParticipantDbContext.OnModelCreating`.

Which shape you use depends on whether the participant is **sandbox-native** or
**mapped to a real system's schema**. Read [Two kinds of
participant](#two-kinds-of-participant) before choosing — getting this wrong is
the most expensive mistake on this page, because it is the one that is hardest
to undo later.

A sandbox-native entity carries a `FederationId` — the shared identity — and its
own local code, which is *not* the identity:

```csharp
public class OperatingPoint
{
    public Guid FederationId { get; set; }   // adopted, not minted
    public string PointCode { get; set; } = string.Empty;
    // ...
}
```

A participant mapped to a real system's schema has **no such column**, because
the real system has none and you may not add one. Its identity correspondence
lives in ws-CIR instead. See `Domain/Mms/MmsEntities.cs` for a worked example.

Only ENG mints. Issuing a code is not minting an identity, and conflating the
two is the error the whole model exists to expose.

### 3b. If it maps a real system's schema

Skip this if the participant is sandbox-native.

Map the real tables and columns exactly, using the owner's names. In
`ParticipantDbContext`, add a `Configure<Participant>` method and a case in
`ConfigurePersonality`, then name every table and column explicitly:

```csharp
entity.ToTable("LIGHT_SYSTEM_INVENTORY");
entity.HasKey(e => e.LightSystemId);
entity.Property(e => e.LightSystemId)
    .HasColumnName("LIGHT_SYSTEM_ID")
    .ValueGeneratedNever();
```

Leave the SQL schema to `HasDefaultSchema` rather than pinning `dbo`. In the
owner's database these live in `dbo`; here each participant is isolated into its
own schema and connects as a contained user granted only on that schema, so
pinning `dbo` would fail on permissions. The *names* are what fidelity requires;
the schema is deployment context.

Then write a context resolver — model it on `MmsContextResolver` — answering
"which local context key corresponds to this iTwin?" through CIR. Return a
result that distinguishes *resolved*, *no context asserted*, and *asserted but
unresolvable*. Collapsing those into a nullable loses the distinction between a
legitimately context-less row and a missing registry relation, and they need
different handling.

**An unresolved context must never degrade to "no filter".** Returning every row
because a twin could not be resolved shows one owner's data under another
owner's selection. Return empty, with the reason attached.

#### Do not join across the two categories

Every participant schema holds two kinds of table: the participant **spine**
(`Outbox`, `Provenance`, `CodeAssignment`, `IdentityMap`, `CirExchange`,
`Message`, `IsbmSession`, `PendingWork`, classification) which every participant
gets automatically, and the participant's own **domain** tables.

No query may join one category to the other. Read each separately and correlate
in C#, carrying the local key as a *value*:

```csharp
var row = await db.Set<LightSystemInventory>()...;   // domain
db.Codes.Add(identities.RegisterCode(                // spine, keyed by value
    segment.UUID, participantId, row.LightSystemId.ToString()));
```

Two reasons. A join binds your adapter to the owner's physical schema, which is
exactly the coupling that stops the real system being swapped in. And a join
between a domain table and `IdentityMap` reconstructs, in SQL, the shared
identity that the no-new-columns rule forbids storing — outside ws-CIR, where no
registry relink can invalidate it.

**This rule is convention, not enforcement.** Both categories are mapped in the
single `ParticipantDbContext`, so nothing stops you writing a join across them:
it compiles, and against a mapped schema like MMS it emits a cross-category join
into tables the sandbox does not own. Separating the two into distinct contexts
was considered and deliberately deferred — it touches every participant service —
so for now the reviewer is the check. If you find yourself reaching for a join
here, that is the signal to re-open that decision rather than to write it.

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

In `SandboxCoreRegistration.AddSandboxCore`, next to the existing personalities
(**not** in `Program.cs` — that file only calls `AddSandboxCore`):

```csharp
services.AddSingleton<IBodHandler, OpsSegmentsHandler>();
services.AddSingleton<IBodBuilder, OpsSegmentsBuilder>();   // if publishing
services.AddSingleton<OpsService>();                        // if it has actions
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

- [ ] Decided **sandbox-native or mapped** (see [Two kinds of
      participant](#two-kinds-of-participant)) — do this first
- [ ] `personality.yaml` with a unique `participantId` and a legal `schema`
- [ ] Key Vault secret matching `tokenSecretName`
- [ ] `Fixtures/classes.yaml`, or a deliberate decision to hold no reference data
- [ ] Domain entity, registered in `ParticipantDbContext`
      - sandbox-native: carries `FederationId`
      - mapped: owner's table and column names, **no added columns**
- [ ] Mapped only: context resolver returning resolved / no-context / unresolvable
- [ ] No query joins a domain table to a spine table
- [ ] `IBodHandler` that **ingests properties**, not just the spine
- [ ] `IBodBuilder` that **re-emits retained values**, if it publishes
- [ ] Registrations in `SandboxCoreRegistration.AddSandboxCore`
- [ ] Case in `MessageTransformService.ReadRecordsAsync`
- [ ] Entry in `IdentityLineageService.FlowOrder`
- [ ] `/admin/schema/reset`
- [ ] A scenario that asserts on its rows
