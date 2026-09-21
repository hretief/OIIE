# ADR-ENG-0001 — Acquiring engineering data from iModels via the iTwin Platform

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-09-21 |
| **Component** | ENG Engine (Function App) |
| **Supersedes** | ENGProvider (emulated iModel) as the ENG Engine's data source |
| **Related** | SyncSegments workflow |

> Renumber to fit the repository's ADR sequence if one already exists.

## 1. Context

The ENG Engine publishes engineering data (SyncSegments BODs) onto ISBM so that
downstream participants (ALIM, then MMS/CMS) stay aligned with design. Today its
only source is **ENGProvider**, a function app that emulates an iModel: elements
change, a Named Version is declared as a *marker*, and the engine drains markers
(poll, on-demand HTTP, or a `namedVersionCreated` notification) and publishes the
segments each marker carries. Idempotency is keyed on the marker's VersionGuid.

That emulation served its purpose: it proved the SyncSegments handover chain end to
end. This ADR **replaces it with real iModels** read through the iTwin Platform. The
emulated source is retired, not kept alongside. Two workflows are required:

- **WF01, baseline and catch-up.** With nothing published yet, or when systems have
  drifted, publish a complete snapshot.
- **WF02, update.** When a Named Version is created, publish what changed since the
  previous one.

Constraints and drivers:

1. iModels are reachable **only through official iTwin Platform REST APIs**. There is
   no iTwin.js backend, no iModel opened locally, and no ad-hoc ECSQL execution.
2. **Efficiency.** Avoid re-extracting a whole iModel per Named Version.
3. **Heterogeneous schemas.** Each contributing design tool's connector writes
   different leaf classes and properties. The flow must run with **no human in the
   loop**. Humans may curate mappings asynchronously ("on the loop").
4. **Tech-preview APIs are acceptable.** This is a next-generation interop solution.
5. Existing conventions hold. Table Storage is the authority for state. The ENG key is
   `iModelId::ECInstanceId::CodeValue`. FederationGuid is the CIRID. Channels are named
   on immutable iTwin FederationIds. Each engine cancels only its own CIR entries.

## 2. Decisions

### D1 — Use four platform APIs

| API | Role |
|---|---|
| **iModels v2** | Authoritative list of Named Versions (markers) and changesets. `containingChanges` flags schema changesets. |
| **Changed Elements v2** | Delta engine: element ids, opcodes (insert 18 / update 23 / delete 9) and a change-type bit-flag between two changesets. |
| **Grouping & Mapping v1** | Extractor: server-side ECSQL groups and properties, materialized per changeset, read back as a CDM with CSV partitions. Supports `changesetId` pinning and `ecInstanceIds` restriction. |
| **Webhooks v2** | Triggers: `iModels.namedVersionCreated.v1` and `changedElements.jobCompleted.v1`. Requires a Service Application. |

ECSQL appears only as declarative group definitions that Bentley executes server-side.
That is within constraint 1.

### D2 — WF01 and WF02 are one operation: *reconcile to a Named Version*

Each iModel has a **watermark**: the last Named Version whose content ISBM accepted.

- No watermark, or a pending resync request (D13), means **Full** mode. Extract everything at the latest
  Named Version, and derive deletes by diffing against what was previously published.
- Otherwise it is **Delta** mode. Compare changesets from the watermark to the target,
  then extract only relevant changed instances at the target changeset.

Catch-up is Delta from a stale watermark. A single comparison spans any number of
missed Named Versions. There is one code path, and the platform's Named Version list
is always the input. The triggering event's payload is never trusted.

### D3 — A Named Version is a marker; markers publish in order by default

Real Named Versions take over the role of ENGProvider's emulated markers, including
idempotency by VersionGuid. By default each Named Version is published in order,
preserving the semantics the emulation established, in which each marker is a governed
baseline. A setting,
`CoalesceNamedVersions`, allows jumping straight to the latest Named Version when
catch-up speed matters more than per-marker fidelity.

### D4 — Durable Functions: one singleton orchestration per iModel, driven by nudges

- The instance id is deterministic: `reconcile-{iModelId:N}`.
- Webhooks, the poll and the manual triggers only **nudge**. A nudge starts the
  instance if it isn't running and does nothing otherwise.
- Each pass runs Plan → EnsureMappings → Compare/Extract → TransformAndPublish →
  AdvanceWatermark, then continues-as-new to plan again. Named Versions created
  mid-pass are picked up. Bursts coalesce and passes serialize per iModel, with no
  queue or session infrastructure.
- **Table Storage remains the authority** for the watermark, the element ledger and
  the schema profile. Orchestration history is never read as truth, and Durable
  Entities are not used. This matches the lesson from the ISBM provider.
- The timer poll is retained as the backstop for lost notifications, and for the
  narrow window between a pass's final "up to date" plan and its completion.

### D5 — Two transformation definitions, with different owners and change rates

1. **Extraction definitions** (iModel → canonical tables): Grouping & Mapping
   mappings. They absorb connector heterogeneity. Group names are the **canonical
   table names** (e.g. `Segment`, `PayItem`). Groups that share a name within one
   mapping concatenate into one CDM entity, which is the normalization mechanism.
2. **Outbound map** (canonical → CCOM SyncSegments): the existing map with its RDL
   validation. It now consumes canonical rows and never sees a connector-specific
   schema.

Both are versioned. BOD provenance records the Named Version, the changeset, the
mapping-definition hashes and the outbound-map version.

### D6 — Extraction definitions are configuration-as-code, reconciled onto every iModel

- **Storage.** The source of truth is JSON definitions in blob storage, one per mapping,
  authored in the G&M API's request shapes.
- **Reconciliation.** A reconciler makes each iModel's G&M state an exact image of the
  applicable definitions. It detects drift with a definition hash embedded in the
  mapping description (`[def:<hash>]`). It **recreates** a mapping on change rather
  than patching it. A mapping still under construction carries `[def:pending]`, so a
  half-built one is rebuilt on the next pass.
- **Scope of management.** The reconciler touches only mappings carrying a `[def:` tag.
  Human-made mappings are left alone.
- **Why not Copy Mapping at runtime.** G&M definitions live in the G&M service, not in the
  iModel, and Copy Mapping produces an unlinked snapshot, so changes would not
  propagate. Copy Mapping and the G&M widget remain useful for **authoring**. A
  harvest step can read a hand-built template mapping via the API and commit it as a
  definition.
- `extractionEnabled` is always `false`. Extraction follows Named Versions, not every
  changeset group.

### D7 — Definitions declare their schema applicability, and a cached census decides

- A definition may declare `requiresSchemas`. Core definitions have none and apply
  everywhere; connector definitions apply only where their schemas are present.
- Each iModel's **schema profile** comes from a system-managed census mapping (a group
  rooted on `bis.Element` joined to ECDbMeta). It is **cached per iModel** in Table
  Storage and refreshed only when a plan reports a schema changeset
  (`containingChanges` bit 1), in Full mode, or when no profile exists.
- The first iModel to show a schema that a definition covers starts using it
  automatically. This is "define once per schema, reuse across iModels" with no
  per-iModel setup.

### D8 — Tiered handling of heterogeneous schemas

1. **Core**: polymorphic groups on BIS base classes give identity, lifecycle and
   provenance for every element from every connector.
2. **Connector definitions**: per-class groups aliasing tool-specific properties onto
   canonical columns.
3. **Fallback**: an element with no connector definition still flows through Core. A
   missing mapping reduces semantic richness; it never blocks the flow.

Semantic equivalence of dynamic, project-authored classes (DGN item types,
Revit-style dynamic classes) is **not** claimed to be automatable. Humans curate
definitions on the loop, and each curated definition upgrades a whole class of future
content.

### D9 — Deletes are resolved from an engine-owned ledger, not extracted and not read from the CIR

Deleted elements cannot be extracted at the target changeset. The engine keeps an
**element ledger** of what it has published (`ECInstanceId → CodeValue,
FederationGuid`) and recovers ENG keys from it. The ledger is also:

- the Full-mode diff set: published before and not seen now means a delete;
- the source for **left-scope** deletes: changed elements that no longer match any group.

The ledger is owned by the engine, so a delete never waits on ALIM having registered
the ENG key in the CIR.

### D10 — Extraction only moves forward

Grouping & Mapping skips a mapping that has already been extracted from a newer
changeset. Targets are therefore monotonic per iModel (enforced by D2 and D3).
Definition changes recreate mappings (D6), and a new mapping has no history. A
pinned extraction whose CDM is absent is raised as a distinct error, never treated
as empty success.

### D11 — Webhook contract

- Return **200 within 5 s**. Handlers verify the HMAC signature, parse, nudge and return.
  They never do work inline.
- Answer an **authentic but unreadable** event with **200** and log it. Any non-200
  from a genuine delivery counts toward webhook deactivation.
- Answer a failed signature with 401. The request is not from Bentley, so no retry
  budget is at stake.

### D12 — ENGProvider is retired as the engine's source

The ENG Engine reads only from the iTwin Platform. What is removed and what is kept:

- **Removed:** the ENGProvider client, its DTOs, its settings (such as the ENGProvider
  base URL), the inline drain, and every branch that existed only to serve the
  emulation.
- **Kept:** the half of the engine downstream of acquisition: the outbound map, RDL
  validation, SyncSegments construction, ISBM publication and the published-marker
  idempotency set. Its input changes from ENGProvider DTOs to canonical rows.
- **Testing:** round-trip tests and the Sandbox's ENG participant move to a dedicated test
  iModel in a test iTwin.
- **Out of scope:** decommissioning the ENGProvider Function App itself is a separate
  follow-up.

### D13 — Idempotency is keyed on (Named Version, resync generation); only a resync republishes

The watermark row carries two counters: `ResyncGeneration` (last completed) and
`ResyncRequested`.

- **Requesting a resync.** `engine/resync` increments `ResyncRequested` in Table
  Storage, then nudges. The request is durable state, not an orchestration input. It
  survives failed passes and host restarts, and it is honoured even if a reconcile is
  already running, because that reconcile re-plans before it finishes.
- **Planning.** A plan is Full whenever `ResyncRequested > ResyncGeneration`, and it
  carries the requested generation. `AdvanceWatermark` sets `ResyncGeneration` to it.
- **The key.** The published-marker set is keyed on **(Named Version id, generation)**.
  An activity retry, a replay or a duplicate nudge produces the same key and is
  suppressed. A resync produces a new key, so an already-published marker legitimately
  republishes. SyncSegments Replace semantics make that harmless downstream.
- **Hashes are provenance, not identity.** Definition and outbound-map hashes are
  recorded on every BOD but are deliberately **not** part of the key. Changing a
  definition or the map never republishes the current marker by itself:
  - Changed *extraction definitions* take effect at the next marker. The reconciler
    recreates the mapping, and that marker includes its backfill.
  - A changed *outbound map* applies to elements published from the next marker onward.
  - To apply either change to everything already published, run a resync.

### D14 — The outbound map is external configuration

Like the extraction definitions (D6), the canonical → CCOM map lives in blob storage
(`eng-outbound-maps`). An app setting only names the container. The map is loaded and
RDL-validated at the start of each reconcile pass, and its hash goes into provenance.
Blob versioning provides history and rollback.

The map is declarative at **attribute level**: canonical column → CCOM attribute or
AttributeType, value translations, RDL code lookups and constants. **Structural**
changes stay in code: a new node in the Segment → MaterialItemOnSegment →
MaterialItem → MaterialMasterItem chain, a new BOD noun, or a new relationship pattern.
A map expressive enough for structural change would become a programming language
to maintain.

With D6 and D14 together, adding a derived column is two file uploads and no deployment:
first the extraction definition (unmapped canonical columns are ignored, so this is
safe on its own), then the map entry that consumes it.

## 3. Options considered and rejected

| Option | Reason rejected |
|---|---|
| iTwin.js backend / direct ECSQL | Violates constraint 1. |
| Full extraction per Named Version, diffed locally | Simpler, but cost scales with iModel size rather than change size. Retained as the fallback if Changed Elements proves unsuitable. |
| Hand-rolled state machine on scheduled Service Bus messages | Workable, but reimplements correlation, timers and external events that Durable provides. The watermark design carries over if Durable is ever removed. |
| Durable Entities for progress state | Eventually consistent; the ISBM provider hit a race with exactly this. |
| Copy Mapping from a template iModel at runtime | Unlinked snapshots don't propagate changes; definitions would sit outside version control. |
| Resolving deletes via CIR lookup | Couples deletes to ALIM's registration timing. |
| `extractionEnabled = true` | Extracts on every changeset group, not on governed markers. |
| Idempotency on Named Version id alone | A resync of an already-published marker would be silently suppressed, making drift repair impossible. |
| Definition/map hashes in the idempotency key | A configuration upload would republish the current marker without anyone deciding to. Republication should be a deliberate act. |
| Extraction definitions or outbound map in app settings | Every change restarts the app; no version history; large JSON in environment variables is fragile. |
| Keeping ENGProvider as a second, switchable source | Adds a branch to every trigger and keeps an emulation whose semantics would drift from the platform's. The emulation has done its job. |

## 4. Consequences

**Positive**
- One reconcile path covers baseline, update and catch-up. It is self-healing after lost
  events, redeliveries and host restarts.
- Connector heterogeneity is contained in versioned definitions. The outbound map and
  ISBM publishing are source-agnostic.
- Every BOD is traceable to (Named Version, changeset, mapping definition, outbound map).
- Output changes at attribute level (derived columns, new attribute mappings) need no
  build or deployment. Republishing existing data with them is an explicit resync.

**Negative / risks**
- Depends on tech-preview APIs (Grouping & Mapping carries a "do not use in
  production" banner) whose contracts may change.
- More Azure state: three tables and two blob containers.
- Testing and the Sandbox now depend on a real test iModel, a Service Application and
  platform availability. There is no local emulation to fall back on.
- The ENG key embeds `CodeValue`, which many connectors leave null. That key convention
  needs a decision independent of this ADR.
- Long publications may exceed Consumption-plan activity limits. Monitor them, and fan out
  per CDM partition if needed.

## 5. Open items (spikes)

| # | Question | Fallback if the answer is unfavorable |
|---|---|---|
| S1 | Is Changed Elements `startChangesetId` inclusive? Is `start == end` accepted for a one-changeset step? | Adjust range computation; for a single changeset use the watermark changeset as start and de-duplicate. |
| S2 | Exact format of the Webhooks v2 `Signature` header. | Adjust the parser; the HMAC over the raw body is unchanged. |
| S3 | Field names in `changedElements.jobCompleted.v1` content. | The orchestrator polls anyway; the event only shortens latency. |
| S4 | Does G&M accept census groups joining ECDbMeta (`meta.ECSchemaDef`, `meta.ECClassDef`)? | Treat an extraction failure on a connector mapping as "not applicable" and cache that per iModel. |
| S5 | Create Property request shape; whether CDM partitions carry a header row. | Definitions are stored in API shape, so only the definition files change. The reader handles both header cases. |
| S6 | Does G&M support ECSQL instance access (`$`, `$->Prop`) and surface such a column in the CDM? | If yes, Tier 2 could collapse into two fixed groups plus normalization in the engine. Revisit D5/D8. |