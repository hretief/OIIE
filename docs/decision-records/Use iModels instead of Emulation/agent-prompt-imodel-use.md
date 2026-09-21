# Task: replace the emulated iModel in the ENG Engine with real iModels

You are working in the existing **ENG Engine** Function App (.NET 10 isolated worker,
Azure Functions). It currently publishes SyncSegments BODs to ISBM from
**ENGProvider**, an emulated iModel. Your job is to **replace that emulation**: the
engine must read **real iModels through the iTwin Platform REST APIs**, using Durable
Functions. ENGProvider is retired as a source. Do not keep it as a switchable
alternative (ADR D12).

The half of the engine downstream of acquisition stays: the outbound map, RDL
validation, SyncSegments construction, ISBM publication, and the published-marker
idempotency set. Its input changes from ENGProvider DTOs to canonical rows.

Two documents accompany this prompt. Add them to the repository and treat them as
authoritative:

- `README.md`: what the engine does, its configuration, triggers and state.
- `docs/adr/ADR-ENG-0001-itwin-reconcile.md`: the decisions (D1–D12) and open
  spikes (S1–S6). When this prompt and the ADR seem to disagree, follow the ADR and
  report the conflict.

---

## 0. Ground rules

1. **Inspect before you change.** Phase 0 is read-only. Stop after it and report.
2. **Replace acquisition; keep publication.** Remove ENGProvider-specific code (client,
   DTOs, settings, inline drain). Keep and reuse the outbound map, RDL validation, BOD
   construction, ISBM publication and the published-marker set. Change their input
   types rather than duplicating them.
3. **Follow the repository's existing layout and naming.** In this project that
   typically means `Application/`, `Domain/`, `Functions/`, `Infrastructure/` under
   the app folder, with **no `src/` level**. Match what you find.
4. **Table Storage is the authority** for progress state. Never read Durable
   orchestration state, and never use Durable Entities, as the source of truth.
5. **Keep existing platform pins**: `Microsoft.ApplicationInsights` 2.x, and a
   `NuGet.config` with `<clear/>`. Use timer triggers, not `IHostedService`.
6. **Durable orchestrator code must be deterministic**: no I/O, no `DateTime.Now`, no
   injected services, no random values. All platform calls happen in activities.
7. **Don't guess API contracts silently.** Where the ADR lists a spike, implement
   behind a clearly named seam, mark it `// SPIKE Sn:`, and write a probe script.
8. **Report per phase**: what changed (files), what was verified and how, what
   remains uncertain. Paste full test output; do not summarize failures away.

---

## Phase 0 — Inventory (read-only, then stop)

**Prerequisites** (confirm they exist, or list what is missing):
- a test iTwin containing a test iModel with at least two Named Versions;
- a Service Application that is a member of that iTwin with iModel read and
  `imodels_write`;
- its credentials in Key Vault.

Produce a short report covering:

- The Function App layout, the options classes and their settings keys, and DI
  registration.
- The three existing triggers (`EngEnginePoll`, `EngEngineDrain`,
  `EngEngineNamedVersionCreated`) and everything `DrainAsync` calls.
- How markers and segments are represented. How the published-marker set is stored
  and keyed. How SyncSegments BODs are built from segments. Where
  `ValidateOutboundMapAsync` and the outbound map live, what input type they take, and
  **where the map's content comes from**: compiled in, an embedded resource, a file, or
  external storage.
- How ISBM publication is done, and whether large segment sets are chunked.
- Existing tests: PowerShell scripts, Bruno collections, unit tests.
- **Every ENGProvider dependency**: client classes, DTOs, settings keys, DI
  registrations, Bicep and app settings, tests, Bruno requests, and Sandbox references.
  Classify each as *remove*, *replace* (with what), or *keep* (why).
- **Your proposed seam.** Name the exact type or method where canonical rows will
  enter the existing transform-and-publish half, and how its input type changes from
  ENGProvider DTOs.

Do not modify code in Phase 0.

---

## Phase 1 — Contracts, options, clients (no behaviour change)

**Options.** Add these settings, all optional, with defaults matching README §5:
`EngEngine__WebhookSecret`, `ITwin__ClientId`, `ITwin__ClientSecret`,
`ITwin__TokenEndpoint`, `Reconcile__CoalesceNamedVersions`,
`Reconcile__ExtractionBatchSize`, `Reconcile__RelevantChangeMask`,
`Reconcile__StagingContainer`, `Reconcile__MappingDefinitionsContainer`.

**Contracts.** All of these cross Durable boundaries, so they must be plain serializable
records. Computed members are methods, not properties.

- `ReconcileInput(IModelId)`
- `NamedVersionRef(Id, DisplayName, ChangesetId, ChangesetIndex)`
- `ChangesetRef(Id, Index, ContainingChanges)`
- `ReconcilePlan(IModelId, ITwinId, Mode, Target, StartChangesetId, SchemaChanged, Generation, UpToDate)`
- `MappingState(Id, Name, DefinitionHash, Changed)` and `MappingSet`
- `ComparisonStatus(JobId, Status, ComparisonHref)`
- `StagedChangeSet(Upserts, Deletes)` (the blob body) and `StagedChanges(BlobName, counts, BatchCount)`
- `ExtractionRequest(IModelId, ChangesetId, MappingIds, StagedBlob?, BatchIndex)` and `ExtractionRef(ExtractionId, MappingIds, Partial)`
- `CanonicalElement(Table, ECInstanceId, ECClassId, Values)`
  - `Table` is the CDM entity name, which equals the G&M group name.
- `EngKey(IModelId, ECInstanceId, CodeValue, FederationGuid)`
  - Its string form is `iModelId::ECInstanceId::CodeValue`.
- `Provenance(NamedVersionId, ChangesetId, ChangesetIndex, Mode, Mappings)`
- `MarkerContent(IModelId, Marker, Upserts, Deletes, Provenance)`

**HTTP clients.** Typed `HttpClient`s share one delegating handler that obtains a
client-credentials token (scope `itwin-platform`) and caches it until two minutes
before expiry. Do not retry 429s inside the handler; activity retry policies handle
them. Every non-success response must throw with the status, method, URL **and
response body**.

- **`IModelsClient`** (Accept `application/vnd.bentley.itwin-platform.v2+json`):
  - `GetITwinIdAsync`: `GET /imodels/{id}`.
  - `GetNamedVersionsAfterAsync(iModelId, afterIndex)`: ordered by changesetIndex
    ascending, paged via `_links.next`, `Prefer: return=representation`. Exclude hidden
    versions and versions without a changesetId.
  - `GetChangesetsAsync(iModelId, afterIndex, lastIndex)`: with
    `Prefer: return=representation` so `containingChanges` is present.
- **`ChangedElementsClient`** (Accept v2):
  - `CreateOrGetJobAsync`: `POST /changedelements/comparisonjob`. On `409`, fetch the
    existing job. The job id is `{start}-{end}`.
  - `GetJobAsync`: `GET /changedelements/comparisonjob/{jobId}/itwin/{iTwinId}/imodel/{iModelId}`
    with `Prefer: return=minimal`.
  - `DownloadAsync(href)`: parse the `elements`, `opcodes` and `type` arrays. They may be
    wrapped in `changedElements`.
- **`GroupingMappingClient`** (Accept v1; root
  `https://api.bentley.com/grouping-and-mapping/datasources/imodel-mappings`):
  - `GetMappingsAsync(iModelId)`: paged.
  - `CreateMappingAsync`: **always `extractionEnabled: false`**.
  - `UpdateMappingDescriptionAsync`: `PATCH`.
  - `DeleteMappingAsync`: a 404 counts as success.
  - `CreateGroupAsync`.
  - `CreatePropertyAsync`: posts a JSON body **verbatim**.
  - `RunExtractionAsync(iModelId, changesetId, mappingIds, ecInstanceIds?)`.
  - `GetExtractionStateAsync`: Queued, Running, Succeeded, PartiallySucceeded or Failed.
  - `GetCdmAsync`: a `404` throws `MappingAheadOfTargetException`, explained in ADR D10.
  - `OpenPartitionAsync`: streams CSV via `.../cdm/partitions?location=`.

**Acceptance.** It builds. The clients are exercised by probe scripts against the test
iModel. ENGProvider code is not removed yet; that happens in Phase 7, once the
replacement path works end to end.

---

## Phase 2 — State stores

- **`WatermarkStore`** (table `EngWatermark`): PK `iModelId:N`, RK `current`. It holds
  NamedVersionId, name, ChangesetId, ChangesetIndex, Mode, mapping states as JSON,
  PublishedOn, **`ResyncGeneration`** and **`ResyncRequested`** (integers, default 0).
  - `AdvanceAsync(plan, …)` writes the position and sets `ResyncGeneration =
    plan.Generation`. It must not overwrite a `ResyncRequested` written concurrently:
    use ETag-conditional merge and retry on conflict.
  - `RequestResyncAsync(iModelId)` increments `ResyncRequested` with an ETag-conditional
    update and retries on conflict. It creates the row if absent.
- **`ElementLedger`** (table `EngElementLedger`): PK `iModelId:N`, RK `ECInstanceId`.
  It holds CodeValue and FederationGuid.
  - `GetAllAsync` returns a case-insensitive dictionary.
  - `ApplyAsync(upserts, removedIds)` batches upserts in transactions of 100, with
    upserts pre-deduplicated by ECInstanceId. It deletes individually and ignores 404.
- **`SchemaProfileStore`** (table `EngSchemaProfile`): PK `iModelId:N`, RK `current`.
  It holds the schema names and the changeset index they were measured at.
- Blob containers `eng-staging`, `eng-mappings` and `eng-outbound-maps` are created if
  missing. Enable blob versioning on the last two.

**Acceptance.** Unit or integration tests round-trip each store.

---

## Phase 3 — Extraction definitions, census, reconciler

**Definition format** (README §3):

```
{ mappingName, description, requiresSchemas?: string[],
  groups: [ { groupName, description, query, properties: [ <Create Property body> ] } ] }
```

**Census.** Add a system-managed census mapping, not stored as a user definition.
Its group is rooted on `bis.Element` and joined to `meta.ECClassDef` and
`meta.ECSchemaDef`, returning the schema name per class in use.

- `// SPIKE S4:` If G&M rejects ECDbMeta joins, fall back as follows: apply connector
  definitions optimistically, and when a connector mapping's extraction fails with a
  schema or class error, record that definition as not applicable for this iModel in
  the schema profile. Log the decision.

**Profile refresh.** Refresh the schema profile only when:

- no profile exists, or
- the plan is Full, or
- the plan reports `SchemaChanged` (any changeset in range has `containingChanges & 1`).

To refresh, extract the census mapping at the target changeset and read its CDM.
Otherwise use the cached profile.

**`MappingReconciler.EnsureAsync(iModelId, schemaProfile)`**:

1. Load definitions from blob storage and hash each: the first 12 lowercase hex characters
   of SHA-256 over the raw bytes.
2. A definition is **applicable** if every entry in `requiresSchemas` is in the profile.
3. For each applicable definition:
   - If an existing mapping with the same name has a description ending in
     `[def:<hash>]`, mark it `Changed=false`.
   - Otherwise delete any existing mapping of that name, then create a new one with
     description `<description> [def:pending]`. Create all its groups and properties,
     then PATCH the description to `<description> [def:<hash>]`. Mark it `Changed=true`.
4. Delete **managed** mappings (description contains `[def:`) whose definition is gone or
   no longer applicable. Never touch unmanaged mappings.
5. Return a `MappingSet`.

**`CdmReader.ReadAsync(mappingId, extractionId)`.** For each entity, locate the
`ECInstanceId` and `ECClassId` columns by attribute name. Stream every partition with a
real CSV parser (CsvHelper). Skip a leading header row if one is present
(`// SPIKE S5:`). Yield `CanonicalElement`s, with empty strings becoming null.

**Seed definition.** Add `EngCore.json`: one group `Segment` =
`SELECT ECInstanceId, ECClassId FROM BisCore.PhysicalElement`, with `CodeValue` and
`FederationGuid` properties. Mark the property body shape `// SPIKE S5:` in the
accompanying notes.

**Acceptance.** Unit tests cover hash stability, applicability, unchanged/changed/pending
decisions, and managed-only deletion. A probe script creates the core mapping on a test
iModel, extracts it and prints the first rows.

---

## Phase 4 — Orchestration

Add `ReconcileFunctions` with a static orchestrator and instance activities.

**Instance and retry.**
- Instance id: `reconcile-{iModelId:N}`.
- Activity retry: 5 attempts, first interval 15 s, backoff ×2.

**Orchestrator, one pass:**

1. `PlanReconcile(ReconcileInput)`:
   - Read the watermark. Mode is Full if there is no watermark, or if
     `ResyncRequested > ResyncGeneration`. Otherwise it is Delta.
   - `Generation` = `ResyncRequested` when a resync is pending, else `ResyncGeneration`
     (0 when there is no row).
   - A pending resync is never `UpToDate`, even if the watermark already equals the latest
     Named Version.
   - List Named Versions after the watermark index (all of them for Full). If none, return
     `UpToDate`.
   - Target = latest for Full or when coalescing; otherwise the **next** one in order.
   - For Delta, list changesets in `(watermark, target]`. Start = the first changeset in
     that range. `SchemaChanged` = any `containingChanges & 1`.
   - `// SPIKE S1:` Is start inclusive, and is start == end accepted?
2. `RefreshSchemaProfile` if needed, per Phase 3, then `EnsureMappings`.
3. **Full extraction** at the target changeset (no `ecInstanceIds`) for all mappings in
   Full mode, or for the `Changed` mappings in Delta mode. The latter is the backfill.
4. **Delta only**:
   - `StartComparison` (idempotent create).
   - If the job is not terminal, `WaitForExternalEvent("ComparisonCompleted", 20 min)`.
     On timeout, log and continue.
   - Then poll `GetComparison` every minute on durable timers, up to 2 h. Always confirm
     status by GET.
   - A job ending in `Failed` fails the pass.
   - `StageChangedElements`:
     - opcode 9 → deletes;
     - opcode 18, or `(type & RelevantChangeMask) != 0` → upserts;
     - de-duplicate;
     - write `StagedChangeSet` to `eng-staging/{iModelId:N}/{versionId}/changes.json`;
     - return counts and `BatchCount = ceil(upserts / ExtractionBatchSize)`.
   - For each batch, run a keyed extraction over the **unchanged** mappings.
     `StartExtraction` slices the staged list by batch index. Groups act as the class
     filter; ids no group selects yield nothing.
5. **Awaiting any extraction.** Poll `GetExtractionState` on durable timers: 15 s doubling
   to a 120 s cap, with a 2 h deadline. `Succeeded` and `PartiallySucceeded` return, the
   latter flagged. `Failed` throws.
6. `TransformAndPublish(PublishRequest)`:
   - Read all CDMs into a dictionary keyed by `(Table, ECInstanceId)`, last row wins.
     `seen` = the set of ECInstanceIds read.
   - Load the ledger. Delete candidates:
     - **Full**: every ledger id.
     - **Delta**: staged deletes ∪ staged upserts.
   - `removed` = candidates not in `seen` and present in the ledger. This covers true
     deletes and elements that **left scope**.
   - Build `MarkerContent` with `EngKey`s from the ledger rows and `Provenance`.
   - Call the publisher's new `PublishMarkerAsync` (Phase 5).
   - **Only if the report has no errors**, apply the ledger: upsert seen rows (first per
     ECInstanceId, CodeValue or empty) and remove `removed`.
   - Return the report.
7. If the report has errors, **throw**. The watermark stays put.
8. `AdvanceWatermark`, then `ContinueAsNew(new ReconcileInput(iModelId),
   preserveUnprocessedEvents: false)`.

Keep id lists and CDM rows **out of orchestration history**: blob references in, and
counts or ids out.

**Acceptance.** Unit tests cover plan target selection (in-order, coalesced, Full,
nothing-to-do), staging filters, and delete-candidate computation (Full, Delta,
left-scope). An integration run against a test iModel publishes a baseline, then a
delta after a new Named Version.

---

## Phase 5 — Publisher entry point

Add `PublishMarkerAsync(MarkerContent, ct)`. It becomes the publisher's only entry
point; `DrainAsync`'s ENGProvider acquisition is removed in Phase 7. Reuse:

1. the published-marker idempotency set, **re-keyed to (Named Version id, generation)**
   (ADR D13). Return an "already published" report if the key is present. Migrate
   existing entries as generation 0. Definition and map hashes go into provenance only,
   **never** into the key;
2. `ValidateOutboundMapAsync` (advisory, as today);
3. the outbound map, now fed `CanonicalElement`s keyed by canonical table. Change its
   input type from ENGProvider DTOs; do not fork it or keep both inputs.
   **Externalize its content** (ADR D14) if Phase 0 found it is not already external:
   - Load it from `EngEngine__OutboundMapContainer` once per reconcile pass.
   - RDL-validate it as today.
   - Record its hash in provenance.
   - Attribute-level rules (column → CCOM attribute or AttributeType, value translations,
     RDL lookups, constants) move to the file. Structural logic (chain nodes, BOD nouns,
     relationships) stays in code.
   - An unknown canonical column is ignored with a debug log, never an error;
4. deletes as identity-only segments (ENG key + FederationGuid), using the existing delete
   action code;
5. existing ISBM publication, **chunked** if it isn't already. Put provenance on every
   BOD.

Record the marker as published only when there are no errors.

**Acceptance.** A canonical-row fixture produces the same BOD shape the ENGProvider
path produced for equivalent segments. Capture a golden BOD from the current
implementation **before** changing it, and compare against that.

---

## Phase 6 — Triggers

**`ReconcileNudger`**:
- `NudgeAsync(durable, iModelId, full)`: if the instance is
  Running/Pending/Suspended, return `Started=false`. Otherwise schedule it. If scheduling
  throws, re-check: if the instance is now active, return `Started=false`; otherwise
  rethrow.
- `RaiseComparisonCompletedAsync`: raise only to an active instance.

**`WebhookSignature.IsValid(header, rawBody, secret)`**:
- An empty secret means valid (the Sandbox case).
- Accept `sha256=<hex>` or bare hex. Constant-time comparison.
- `// SPIKE S2:`

**Changes:**
- `EngEnginePoll`, `EngEngineDrain`: nudge instead of draining. The timer remains the
  backstop for lost notifications.
- `EngEngineNamedVersionCreated`, **altered**. The contract follows ADR D11.
  1. Read the raw body and verify the signature: `401` on failure.
  2. Parse the Webhooks v2 envelope (`content.imodelId`, `content.versionId`,
     `eventType`). ENGProvider's flat event format is no longer supported.
  3. If the event is unreadable, return `200 {accepted:false}` and log a warning.
  4. If it is for a different iModel, return `200 {accepted:false}`, as today.
  5. Nudge and return `200` with the instance id.
  6. **Never do platform work inline.**
- `EngEngineComparisonCompleted`, **new** (`POST events/changed-elements-completed`):
  verify the signature, parse `content.iModelId`/`content.jobId` case-insensitively
  (`// SPIKE S3:`), raise the event, and always return `200` for authentic requests.
- `EngEngineResync`, **new** (`POST engine/resync`): call
  `WatermarkStore.RequestResyncAsync`, then nudge. Always return `202` with the new
  requested generation and the instance id. A running reconcile picks the request up
  when it re-plans. `NudgeAsync` no longer takes a `full` parameter.

**Acceptance.** Bruno requests exist for every route, including a signed and an
unsigned webhook. Unit tests cover signature validation and the v2 envelope,
including unreadable and foreign-iModel events.

---

## Phase 7 — Retire the ENGProvider dependency

Only after Phases 1–6 pass their acceptance criteria against the test iModel:

- **Remove from the ENG Engine** the ENGProvider client, its DTOs, its settings (e.g.
  `EngEngine__EngBaseUrl`, after confirming nothing else uses it), its DI registrations,
  the ENGProvider acquisition inside `DrainAsync`, and any dead code left behind. If
  `DrainAsync` has no remaining purpose, delete it.
- **Remove** the corresponding app settings and Bicep parameters.
- **Convert tests.** ENGProvider-driven round-trip tests and Bruno requests become
  equivalents against the test iModel. Preserve the scenarios; change only the fixture
  mechanism. Test iModel edits and Named Versions come from a connector sync or iTwin
  tooling.
- **Update the docs** that describe the Sandbox's ENG participant, so they point at the
  test iModel.
- **Do not modify or delete the ENGProvider Function App itself.** Its decommissioning is a
  separate follow-up. List in your report anything still calling it.

**Acceptance.** The solution contains no references to ENGProvider except in historical
docs and the ADR. All converted tests pass.

---

## Phase 8 — Deployment and docs

- **Storage**: add the `eng-outbound-maps` container and blob versioning for it and
  `eng-mappings` to Bicep, plus the `EngEngine__OutboundMapContainer` setting.
- **App settings and secrets**: add the new settings to Bicep with Key Vault references
  for `ITwin__ClientSecret` and `EngEngine__WebhookSecret`, plus the Durable Task
  extension configuration.
- **Webhook registration**: a PowerShell script that registers two Webhooks v2
  subscriptions scoped to the iTwin (`iModels.namedVersionCreated.v1` and
  `changedElements.jobCompleted.v1`). Callback URLs include the function key. Store the
  signing secret in Key Vault; confirm how Webhooks v2 issues it and record the
  answer in the ADR.
- **Service Application**: document the required iTwin membership and roles, including
  `imodels_write` for Run Extraction.
- **Probes**: one script per open spike (S1–S6) under `Testing/`. Each prints what it
  observed and whether the ADR assumption holds.
- **Docs**: update `README.md` and the ADR with the spike findings. Change an ADR decision
  only by adding a dated amendment, never by silently editing it.

---

## Definition of done

- The ENGProvider round-trip scenarios pass as converted tests against the test iModel,
  and the ENG Engine has no remaining dependency on ENGProvider.
- Against a test iModel, the following all work:
  - baseline (Full) publishes;
  - a new Named Version publishes a delta in order;
  - deleting an element publishes a delete;
  - moving an element out of every group's scope publishes a delete;
  - changing a definition file recreates and backfills that mapping;
  - `engine/resync` repairs deliberately induced drift, including republishing a
    marker that was already published;
  - a derived column added by uploading a definition and a map entry, with no
    deployment, appears on the next Named Version, and appears everywhere after a
    resync;
  - uploading a definition or map change alone does **not** republish the current
    marker;
  - a resync requested while a reconcile is running is honoured when that reconcile
    re-plans.
- Killing the host mid-pass and nudging again completes without gaps or duplicates.
- Every spike has a probe result recorded in the ADR.
- `README.md` accurately reflects configuration, triggers and state.