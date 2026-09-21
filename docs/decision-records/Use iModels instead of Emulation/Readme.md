# ENG Engine

The ENG Engine is the engineering participant's publisher in the OIIE ecosystem. It
turns design markers into **SyncSegments BODs** on ISBM, where ALIM receives, governs
and approves them before republishing to maintenance systems.

It reads real iModels through the iTwin Platform REST APIs (iModels, Changed
Elements, Grouping & Mapping, Webhooks). It replaces the earlier ENGProvider
emulation, which has been retired as a source. The downstream half is unchanged:
outbound map, RDL validation, SyncSegments construction, ISBM publication and
per-marker idempotency. The design rationale is in
[`docs/adr/ADR-ENG-0001-itwin-reconcile.md`](docs/adr/ADR-ENG-0001-itwin-reconcile.md).

---

## 1. Concepts

**Marker.** A Named Version. It is the unit of publication and idempotency: each
marker publishes once, keyed on its id (the VersionGuid).

**Watermark.** Per iModel, the last marker whose content ISBM accepted. It is stored in
Table Storage and is the only authority on progress.

**Reconcile.** Bringing downstream from the watermark to a target marker. There are two modes:

- **Full** (WF01, baseline or resync): extract everything at the target and derive
  deletes by diffing against the element ledger.
- **Delta** (WF02, update or catch-up): compare changesets from the watermark to the
  target, then extract only the relevant changed elements.

**Nudge.** Any trigger (webhook, poll, HTTP) only *starts* the per-iModel reconcile if it
isn't already running. Nudges carry no data; the reconcile always re-reads the
platform's Named Version list.

**Extraction definition.** A Grouping & Mapping mapping kept as JSON in blob storage and
reconciled onto each iModel. Group names are canonical table names.

**Outbound map.** Canonical rows → CCOM SyncSegments. It is RDL-validated,
connector-agnostic, and kept as JSON in blob storage (attribute-level mapping;
structural changes remain in code).

**Resync generation.** A counter on the watermark row. Publication is idempotent per
(Named Version, generation), so only a resync republishes an already-published marker.

---

## 2. How it works

```
namedVersionCreated ─┐
poll (backstop) ─────┼─► nudge ─► reconcile-{iModelId} (Durable, singleton)
engine/drain ────────┤              │
engine/resync (Full) ┘              ├─ Plan            Named Versions vs watermark; schema changesets?
                                    ├─ Census          refresh cached schema profile if needed
                                    ├─ EnsureMappings  apply applicable definitions (hash-tagged)
                                    ├─ Delta only:     Changed Elements job ──(jobCompleted webhook | poll)
                                    │                  stage ids to blob; filter by change type
                                    ├─ Extract         full (Full mode / changed mappings) or keyed batches
                                    ├─ TransformAndPublish  CDM → canonical → SyncSegments → ISBM; ledger
                                    ├─ AdvanceWatermark
                                    └─ ContinueAsNew   (plan again)
```

Key behaviours:

- **Markers publish in order**, one per Named Version, unless
  `Reconcile__CoalesceNamedVersions=true`.
- **Extraction only moves forward.** Grouping & Mapping skips a mapping already
  extracted at a newer changeset, so targets are monotonic, and changed definitions
  recreate their mapping.
- **Deletes** come from the element ledger: Changed Elements deletes, changed
  elements that left every group's scope, and in Full mode anything published before
  and not seen now.
- **Failure leaves the watermark in place.** The next nudge retries the same step, and
  publication is idempotent per (Named Version, resync generation).
- **Configuration changes never republish by themselves.** A changed extraction
  definition takes effect at the next marker, including a backfill of the recreated
  mapping. A changed outbound map applies from the next marker onward. Run a resync to
  apply either to everything already published.

## 3. Handling different design tools

Each connector writes its own schema. The engine copes in three tiers:

1. **Core definitions** have no `requiresSchemas`. Polymorphic groups on BIS base
   classes cover identity and lifecycle for every element.
2. **Connector definitions** declare `requiresSchemas` and apply automatically to
   any iModel whose cached schema profile contains those schemas.
3. **Fallback.** Elements with no connector definition still flow through Core.

The schema profile comes from a system census mapping. It is cached per iModel and
refreshed only when a schema changeset appears, in Full mode, or when no profile exists.

Adding support for a new tool means adding a definition file. No code changes and no
per-iModel setup are needed.

### Definition file format

One JSON blob per mapping in the `Reconcile__MappingDefinitionsContainer` container:

```json
{
  "mappingName": "EngCore",
  "description": "Tool-agnostic identity for every physical element.",
  "requiresSchemas": [],
  "groups": [
    {
      "groupName": "Segment",
      "description": "All physical elements.",
      "query": "SELECT ECInstanceId, ECClassId FROM BisCore.PhysicalElement",
      "properties": [ { "...": "Create Property request body, verbatim" } ]
    }
  ]
}
```

Rules:

- `groupName` is the canonical table name the outbound map is keyed on.
  Groups with the same name in one mapping union into one table.
- Every group must define `CodeValue` and `FederationGuid` properties. They form
  the ENG key and the CIRID.
- Properties are stored in the exact Create Property request shape and passed
  through unchanged.
- Editing a file changes its hash. On the next reconcile, the mapping is recreated on every
  applicable iModel and backfilled at the current target.
- Mappings are managed only when their description carries a `[def:…]` tag. Hand-made
  mappings are never touched.

**Authoring tip.** Build or refine a mapping interactively in the Grouping & Mapping
widget on a representative iModel, then harvest it into a definition file. The widget
is an authoring tool; the definition file is the source of truth.

---

## 4. Triggers

| Function | Trigger | Route / schedule | Behaviour |
|---|---|---|---|
| `EngEnginePoll` | Timer | `%EngEnginePollSchedule%` | Nudge (backstop for lost notifications). |
| `EngEngineDrain` | HTTP POST | `engine/drain` | Same as poll, on demand. |
| `EngEngineResync` | HTTP POST | `engine/resync` | Records a resync request (increments `ResyncRequested`), then nudges. Always `202`; a running reconcile picks the request up when it re-plans. |
| `EngEngineNamedVersionCreated` | HTTP POST | `events/named-version-created` | Webhook. Verify signature → nudge → `200`. |
| `EngEngineComparisonCompleted` | HTTP POST | `events/changed-elements-completed` | Webhook. Verify signature → raise event → `200`. |
| `ReconcileOrchestrator` + activities | Durable | — | See section 2. |

**Webhook contract.** Handlers always respond within 5 s:

- `401` if the signature is invalid.
- `200` for everything authentic, including unreadable or foreign events, because
  repeated non-200 responses deactivate a Webhooks v2 subscription.

## 5. Configuration

| Setting | Default | Notes |
|---|---|---|
| `EngEngine__Enabled` | — | Existing. |
| `EngEngine__IModelId` | — | Existing. The iModel this engine serves. |
| `EngEngine__Enterprise` | — | Existing. (ENGProvider-only settings such as `EngEngine__EngBaseUrl` are removed.) |
| `EngEngine__WebhookSecret` | empty | Key Vault reference. Empty disables the signature check; use only for local debugging. |
| `EngEnginePollSchedule` | — | Existing. Can relax to hourly, since webhooks carry the low-latency path. |
| `ITwin__ClientId`, `ITwin__ClientSecret` | — | Service Application; the secret is a Key Vault reference. |
| `ITwin__TokenEndpoint` | `https://ims.bentley.com/connect/token` | |
| `Reconcile__CoalesceNamedVersions` | `false` | `true` jumps straight to the latest marker. |
| `Reconcile__ExtractionBatchSize` | `2000` | `ecInstanceIds` per keyed extraction. |
| `Reconcile__RelevantChangeMask` | `47` | TypeOfChange flags: Property 1, Geometry 2, Placement 4, Indirect 8, Parent 32. Excludes Hidden 16. |
| `Reconcile__StagingContainer` | `eng-staging` | Staged comparison id lists. |
| `Reconcile__MappingDefinitionsContainer` | `eng-mappings` | Extraction definitions. |
| `EngEngine__OutboundMapContainer` | `eng-outbound-maps` | Canonical → CCOM map. |

**Platform permissions.** The Service Application must be a member of the iTwin with
roles that grant iModel read and **`imodels_write`**. Run Extraction requires it even
though nothing is written to the iModel.

**Webhook registration.** Two Webhooks v2 subscriptions, scoped to the iTwin:

- `iModels.namedVersionCreated.v1` → `/api/events/named-version-created?code=<function key>`
- `changedElements.jobCompleted.v1` → `/api/events/changed-elements-completed?code=<function key>`

The signing secret goes into Key Vault. The deploy scripts own this registration.

## 6. State

| Store | Name | Key | Purpose |
|---|---|---|---|
| Table | `EngWatermark` | PK `iModelId:N`, RK `current` | Last published marker, changeset, mode, mapping hashes, `ResyncGeneration`, `ResyncRequested`. |
| Table | `EngElementLedger` | PK `iModelId:N`, RK `ECInstanceId` | What was published: `CodeValue`, `FederationGuid`. Source of delete identity. |
| Table | `EngSchemaProfile` | PK `iModelId:N`, RK `current` | Cached schema list and the changeset index it was measured at. |
| Blob | `eng-staging` | `{iModelId}/{versionId}/changes.json` | Filtered Changed Elements ids (kept out of orchestration history). |
| Blob | `eng-mappings` | `*.json` | Extraction definitions (enable blob versioning). |
| Blob | `eng-outbound-maps` | `*.json` | Outbound map (enable blob versioning). |
| Existing | published-marker set | (Named Version id, resync generation) | Per-publication idempotency. |

## 7. Operations

- **First run on an iModel.** No watermark means Full mode, so the baseline happens by
  itself on the first nudge. You can also trigger it with `POST engine/resync`.
- **Suspected drift.** Run `POST engine/resync`. It re-publishes everything at the latest
  marker and issues deletes for published elements that no longer exist.
- **Stuck reconcile.** Inspect `reconcile-{iModelId:N}` in the Durable instance store. A
  failed instance restarts on the next nudge; the watermark guarantees no gap.
- **Adding a derived column or changing output.** No build or deployment:
  1. Add the calculated or custom-calculation property to the extraction definition and
     upload it. Unmapped canonical columns are ignored, so this is safe on its own.
  2. Add the mapping for the new column to the outbound map and upload it.
  3. Either let it take effect from the next Named Version (the changed mapping is
     recreated and backfilled then), or run `POST engine/resync` to republish
     everything now.
- **Rolling back configuration.** Restore the previous blob version, then resync if the
  bad version was already published.
- **Partial extraction.** Logged as a warning and published. A resync repairs any gap.

## 8. Testing

- Round-trip tests run against a dedicated **test iModel** in a test iTwin, with
  scripted edits and Named Versions (via a connector sync or iTwin tooling) as fixtures.
- Unit tests cover the pure logic:
  - opcode and change-type filtering;
  - delete-candidate computation (Full, Delta, left-scope);
  - plan target selection;
  - definition hash and applicability decisions;
  - webhook signature validation;
  - CDM CSV reading.
- Integration: PowerShell scripts and Bruno requests under `Testing/` drive
  resync, drain and the webhook routes against a real iModel.
- Spikes S1–S6 (see the ADR) each get a scripted probe before their code paths are
  relied upon.

## 9. Known open items

See ADR-ENG-0001 §5. The most consequential are the Changed Elements start-changeset
semantics (S1) and whether census queries over ECDbMeta are accepted by Grouping &
Mapping (S4). Separately, the ENG key embeds `CodeValue`, which many connectors leave
null; that convention needs a decision.