# Pipeline Abstraction — Pluggable Ingest and Dispatch Stages

**Status:** Draft for implementation
**Scope:** The internal structure of a participant engine (`MmsEngine`, `CmsEngine`,
`RegLocationEngine`), below the ISBM boundary established by DR-015 and
`participant-abstraction-spec.md`.
**Does not change:** channel topology, the engine/provider split (§5.2), ISBM as the
prescribed interface, or the rule that a participant may not call another participant.
**Worked nouns:** `SyncSites` and `SyncSegments`.

> **Purpose of this document.** `participant-abstraction-spec.md` says what a participant
> is. This says what is *inside* one, so that the transform step — the only step a human
> domain expert authors — is a substitutable artifact rather than a hand-written service
> class. Everything else is engine code, identical for every participant.

---

## 1. The claim this document makes

> Adding a participant, or adding a noun to an existing participant, is **a transform
> artifact plus a manifest**. No engine code changes.

**Acceptance test.** The engine assembly contains no participant name and no CCOM noun
name. Grep for `mms`, `cms`, `reglocation`, `Segment`, `Site`, `LIGHT_UNIT` in
`Oiie.Participant.Engine` and find nothing. One `if (participant == …)` means the manifest
schema is missing something; fix the schema, not the engine.

This is stricter than C3 in the participant spec and deliberately so. C3 forbids sharing a
dispatcher. This forbids the engine knowing what it is dispatching.

---

## 2. Stage decomposition

### 2.1 Ingest

```
  ISBM subscription session
          │
          ▼
┌─────────────────────────────────────────────────────────────────┐
│ E1  TRANSPORT            engine   session, poll, webhook, remove │
│ E2  DEDUPLICATE          engine   on BODID                       │
│ E3  VALIDATE             engine   XSD + Schematron(RDL)          │
│ E4  RESOLVE              engine   executes declared lookups      │
│ P1  TRANSFORM         ◄─ PLUG     (BOD, Context) → WritePlan     │
│ E5  EXECUTE              engine   ordered, upsert-only           │
│ E6  REGISTER             engine   native keys → CIR              │
│ E7  VERDICT              engine   Applied|Rejected|Failed        │
│ E8  REMOVE               engine   only after E7 succeeds         │
└─────────────────────────────────────────────────────────────────┘
```

Two plug points only: **P1** (the transform) and the manifests that feed E4, E5 and E7.

### 2.2 Dispatch

Dispatch is **not** the mirror of ingest and must not be built as one. Publication channels
return no verdict, and the trigger is not a message.

```
┌─────────────────────────────────────────────────────────────────┐
│ D1  TRIGGER              engine   SEAM — see §7                  │
│ D2  HARVEST              engine   native reads, per manifest     │
│ D3  RESOLVE OUT          engine   native key → CIRID, class → RDL│
│ P2  TRANSFORM         ◄─ PLUG     (Aggregate, Context) → CCOM    │
│ D4  VALIDATE OUT         engine   XSD + Schematron(RDL)          │
│ D5  ENVELOPE             engine   BOD + verb + topic             │
│ D6  PUBLISH              engine   outbox, receipt, retry         │
└─────────────────────────────────────────────────────────────────┘
```

D4 is not optional. A publisher that emits a non-conformant BOD puts a false statement on
the bus and every subscriber records it as fact. Validate outbound as hard as inbound.

---

## 3. The transform contract (P1 / P2)

The whole design turns on one constraint:

> **The transform performs no I/O.** It is a function of the BOD and a context the engine
> has already populated.

This is what makes the artifact substitutable — an XSLT stylesheet cannot open a connection
even if its author wants it to — and it is what turns C4 from a recommendation into a
structural property.

### 3.1 Interfaces

```csharp
public interface IInboundTransform
{
    // Declared statically, read by the engine BEFORE the transform runs.
    ResolutionPlan Resolutions { get; }

    WritePlan Apply(XDocument bod, TransformContext context);
}

public interface IOutboundTransform
{
    ResolutionPlan Resolutions { get; }

    XDocument Apply(NativeAggregate aggregate, TransformContext context);
}

public sealed record TransformContext(
    IReadOnlyDictionary<string, string> Resolved,   // key → resolved value
    IReadOnlyCollection<string>          Unresolved, // keys with a definite miss
    DateTimeOffset                       AsOfUtc);
```

`Unresolved` is separate from a missing dictionary entry on purpose. A definite miss ("no
such site") and an unknown ("CIR did not answer") must not be conflated — that collapse is
LTP-4. The engine never constructs a `TransformContext` in the unknown case; it fails the
message as transient before the transform is reached (§6).

### 3.2 Why the transform declares its lookups

A transform that could call CIR itself would be impure, untestable without a broker, and
impossible to express as a stylesheet. So it declares what it needs and the engine batches
the resolution.

```yaml
# MmsEngine / segments / resolutions.yaml
resolutions:
  - key: targetTable
    source: cir
    category: RDL-CLASS
    cirIdFrom: /SyncSegments//Segment/Type/UUID
    onMiss: skip-item          # DR-030: never guess a table

  - key: ownerId
    source: cir
    category: ITWIN-SITE
    cirIdFrom: /SyncSegments//Segment/RegistrationSite/UUID
    onMiss: skip-item
```

`onMiss` is one of `skip-item` (record and continue), `reject-message`, or `use-default`
with a stated default. There is no implicit default. An unresolvable RDL class in MMS means
choosing a table, and writing a light unit into an arbitrary table is corruption rather than
degradation (DR-030, DR-033).

### 3.3 Two implementations, one interface

| Implementation | Authored by | Artifact | When |
| --- | --- | --- | --- |
| `XsltInboundTransform` | domain expert, via mapping tool or workbook | `.xslt` + `resolutions.yaml` | default |
| compiled `IInboundTransform` | developer | C# class | escape hatch |

The engine resolves the implementation from the manifest and does not know which it has.
The XSLT receives `Resolved` entries as stylesheet parameters — which is the only way values
reach it, enforcing §3's constraint mechanically.

Runtime: XSLT 3.0 via SaxonCS-HE 13 (free from 13.0, May 2026, .NET 8+). Confirm HE
sufficiency against a real generated map before committing — HE excludes schema awareness
and streaming.

---

## 4. The WritePlan

The inbound transform's output is **not** a native payload. It is an ordered set of typed
operations. Grouping the mapped fields by resource *is* the API dispatch decision, so the
domain expert makes it without ever being asked "which endpoint?".

### 4.1 Schema

```xml
<WritePlan participant="mms" noun="SyncSegments" bodId="{BODID}">

  <Item sourceRef="{Segment.UUID}">          <!-- unit of partial failure -->

    <Op id="unit"
        resource="{$targetTable}"            <!-- from resolution, not literal -->
        mode="upsert"
        match="EXT_ASSET_ID">
      <Field name="EXT_ASSET_ID"     onUpdate="never">{Segment.UUID}</Field>
      <Field name="LIGHT_UNIT_NAME"  onUpdate="set">{Segment.ShortName}</Field>
      <Field name="OWNER_ID"         onUpdate="set" ref="$ownerId"/>
      <Field name="DATE_UPDATE"      onUpdate="set">{$asOfUtc}</Field>
      <!-- LIGHT_SYSTEM_ID deliberately absent: DR-032 -->
      <Returns name="LIGHT_UNIT_ID" as="nativeKey"/>
    </Op>

    <Register cirId="{Segment.UUID}"
              category="ASSET"
              idInSource="unit.nativeKey"
              merge="adopt-existing"/>       <!-- CreateEquivalentEntries -->

  </Item>
</WritePlan>
```

### 4.2 Field-level update policy

`onUpdate` is required on every field. Three values:

| Value | Meaning |
| --- | --- |
| `set` | Write on insert and update |
| `setIfAbsent` | Write on insert; leave alone on update |
| `never` | Match key or immutable; write on insert only |

This is how §6.1's "never overwrite `AssetClassID`, criticality, serial or manufacturer"
becomes enforceable instead of remembered. **Omitting a field entirely is different from
writing null**, and the distinction is load-bearing: `LIGHT_SYSTEM_ID` is absent from the
plan because it is MMS's to populate, and a plan that wrote null would erase whatever MMS
users had chosen (DR-032).

The engine MUST reject a plan at validation time if any field lacks `onUpdate`.

### 4.3 Rules the executor enforces

1. **`mode="upsert"` requires `match`.** A plan with an unconditional insert fails
   validation and never runs. This discharges §6.1.1 structurally: retries are harmless only
   because the match key is unique.
2. **Operations are topologically sorted** by `ref="$op.field"` dependencies. Document order
   is not execution order — a hand-ordered list breaks silently when a row is added.
3. **`<Item>` is the unit of partial failure.** One unresolvable segment does not fail the
   message; it is skipped, recorded, and reported in the verdict. One *transient* failure
   fails the whole message, because replay is per-message.
4. **The whole plan is replayable.** No checkpointing. Since every op is an upsert on a
   declared key and `Register` uses adopt-existing merge, re-running the plan from the top is
   safe. This is the only posture compatible with at-least-once delivery across a customer
   boundary where no transaction exists.
5. **`<Register>` runs after the op it names**, because the native key is IDENTITY-assigned
   and known only from the response. Until CIR holds it, MMS has a row no other participant
   can name (DR-033).

### 4.4 Resource → endpoint is a separate manifest

```yaml
# MmsEngine / bindings.yaml — a participant artifact, versioned with the participant
resources:
  LIGHT_UNIT_INVENTORY:
    upsert: { method: POST, path: /light-units, matchOn: EXT_ASSET_ID }
    read:   { method: GET,  path: /light-units/{EXT_ASSET_ID} }
  LIGHT_SYSTEM_INVENTORY:
    upsert: { method: POST, path: /light-systems, matchOn: EXT_ASSET_ID }
```

**This file ships with the participant, never with the engine.** The prior generation put
the BOD-to-stored-procedure mapping in orchestration config, so a change to the customer's
schema became a change to orchestration. Keeping bindings in shared configuration recreates
that leak with nicer syntax.

---

## 5. Worked example: `SyncSites` into MMS

The simplest case. One item, one op, no late-bound key, no CIR resolution.

```yaml
# resolutions.yaml — empty; the site leg needs no lookups
resolutions: []
```

```xml
<WritePlan participant="mms" noun="SyncSites" bodId="{BODID}">
  <Item sourceRef="{Site.UUID}">
    <Op id="system" resource="LIGHT_SYSTEM_INVENTORY" mode="upsert" match="EXT_ASSET_ID">
      <Field name="EXT_ASSET_ID"       onUpdate="never">{Site.UUID}</Field>
      <Field name="LIGHT_SYSTEM_NAME"  onUpdate="set">{Site.ShortName}</Field>
      <Field name="CLASSIFICATION"     onUpdate="setIfAbsent">Undetermined</Field>
    </Op>
  </Item>
</WritePlan>
```

`CLASSIFICATION` is `setIfAbsent` because MMS reclassifies later and a republished site must
not reset it. That single attribute is the sort of decision that currently lives in a mapper
class and is invisible to whoever owns the data. Here it is on the face of the artifact.

The `oa:Delete` verb selects a different transform for the same noun, emitting ops with
`mode="delete"` plus `<Deregister>` entries. The delete plan's ops must key on exactly what
the add plan wrote — that symmetry is the correctness test, and it is checkable by diffing
the two artifacts rather than by reading two code paths.

---

## 6. Failure classification

`PersistOutcome` is `Applied | Rejected | Failed(Transient: bool)`, and `Transient` is
**required** when `Failed`.

**The default is transient.** A condition not explicitly declared terminal is transient.
This is the inverse of the natural implementation and it is deliberate: a `Rejected` verdict
is never retried, so a transient condition misreported as `Rejected` destroys data, while a
terminal condition misreported as transient merely retries until a human looks.

```yaml
# MmsEngine / failures.yaml
terminal:
  - http: 400          # malformed request — replay will not help
  - http: 409
  - sqlError: 547      # FK violation
# everything else, including every unmapped condition, is transient
```

**Never classify from an exception message string.** Message text is locale-dependent and
changes between provider versions. Classify from HTTP status, exception type, or SQL error
number only. The engine MUST reject a `failures.yaml` entry keyed on message text.

The three-outcome table applies at every lookup, not just site resolution:

| Registry / provider says | Meaning | Action |
| --- | --- | --- |
| no such relation | genuinely absent | per `onMiss` — usually skip the item |
| unreachable / timeout | nothing is known | leave queued, retry, **do not transform** |
| answered | proceed | resolve and continue |

---

## 7. Dispatch, and why D1 is a seam

D1 has no generic form. Each participant's trigger is different in kind:

| Participant | Trigger | Shape |
| --- | --- | --- |
| ENG | Named Version committed | webhook in production; polled change detection in the sandbox |
| REG-LOCATION | steward approval | outward-facing notification seam, defaulting to null |
| MMS | maintenance event | not yet specified |

The polled change detector is explicitly the component a real deployment replaces, and its
envelope must match what the real webhook emits so adoption is a transport swap. Do not
attempt to make D1 configurable — make it an interface with one implementation per
participant and mark it as the seam.

**Consequences for D2–D6 that ingest does not have:**

- The event carries identity and intent only. D2 fetches full state from the native API. A
  fat event goes stale between publication and consumption.
- Transient failures on the publish side are **rethrown**, not recorded as a verdict — there
  is no message row to record against. This asymmetry with ingest is correct and must be
  deliberate rather than accidental.
- D6 needs an outbox with a receipt, and the checkpoint must not advance past anything still
  pending, or a crash between publish and checkpoint loses the change permanently.
- D3 validates the outbound class key against the RDL library before publishing. An unmapped
  class publishes **no** classification rather than a fabricated one.
- The channel is derived (`Options.ChannelUriFor(guid)`), never spelled out. A literal
  channel string in a transform artifact is a defect.

---

## 8. Test obligations

Non-negotiable before this is considered done.

1. **Golden-file transforms.** Fixture BOD in, expected `WritePlan` out. No broker, no
   database. This is the regression suite the domain expert's edits run against.
2. **Plan validation.** Reject: upsert without `match`; field without `onUpdate`; cyclic
   `ref`; `failures.yaml` keyed on message text; a `Register` naming an op that does not
   return the field.
3. **Replay.** Execute the same plan twice against a live provider; assert row counts,
   native keys and CIR entries are unchanged. This is the only real test of §4.3 rule 4.
4. **Round trip.** `SyncSegments` → WritePlan → provider → harvest → outbound transform →
   `SyncSegments`. Semantically stable over the RDL-governed subset. This is what catches a
   lossy mapping before a customer does.
5. **Add/delete symmetry.** Whatever the add plan wrote, the delete plan removes, keyed the
   same way. Then re-create the same site: a reused id colliding with a leftover is what
   actually catches a missed deregistration.
6. **Transient path.** Point the engine at an unreachable provider. Assert the message stays
   on the channel and the verdict is `Failed(Transient: true)`. This path cannot be reached
   from the happy path by construction, so its absence of failures says nothing.

---

## 9. Build order

Each step de-risks the next.

1. `WritePlan` schema, validator, and the golden-file harness — **before any executor**.
2. Executor against an in-memory fake provider. Tests 2 and 3 pass.
3. `MmsEngine` `SyncSites` as a compiled `IInboundTransform`. Existing behaviour, new shape,
   no new semantics. Proves the contract against a working leg.
4. `MmsEngine` `SyncSegments` — first use of `ResolutionPlan` (CIR table resolution, owner
   resolution) and of `<Register>` write-back. This is the leg DR-033 scopes and where the
   design earns its keep.
5. Swap step 3's compiled transform for an XSLT artifact. Same golden files must pass. This
   is the moment the "pluggable" claim becomes true rather than asserted.
6. `CmsEngine` `SyncSegments` authored **as an artifact only**, with no engine change. If
   this needs a code change, the design has failed and should be revised, not patched.
7. Dispatch: D2–D6 for one participant, with D1 hand-written.

Do not start at step 7. Do not build dispatch and ingest in parallel — the `WritePlan`
validator and the resolution mechanism are shared, and building them twice under different
names is how the two legs drift.

---

## 10. Open, and deliberately not decided here

- **The CCOM XSDs remain an open dependency** (§12.4). Schematron generation from the RDL
  applicability matrix is blocked on them, and so is any mapping tool that needs a source
  schema to open.
- **Whether the transform artifact is authored as XSLT directly or generated from an RDL
  mapping workbook.** The interface is the same either way; this spec deliberately does not
  choose. Decide it after step 5, with a real artifact to look at.
- **Concurrency on `MAX(id) + 1`.** Inherited, not introduced here, and still not safe
  against a live customer system. Must not reach production without a sequence.
- **Publishing participants beyond ENG and REG-LOCATION.** §7 is specified thinly on purpose;
  it firms up when a third publisher exists.
