# Decision register

Running record of design decisions, the evidence behind them, and what remains open.
Entries are append-only: when a decision is revised, the original stays and a superseding
entry is added, so the reasoning trail survives.

Status values: `Decided`, `Provisional` (working assumption, revisit when evidence arrives),
`Open` (actively undecided), `Superseded`.

---

## DR-001 — ENG unpublished-element extraction: how to compute the delta

**Status:** Open
**Date:** 2026-08-13
**Context:** OIIE Scenario 1 (ENG to REG-LOCATION). A named-version notification must lead to
promoting only those elements not yet published. Requirement confirmed with the user as
changeset-accurate (not a timestamp approximation) and inclusive of deletions.

### Candidate approaches

| | Two-point ECSQL diff | Changed Elements API V3 |
|---|---|---|
| Mechanism | Query briefcase at two changesets, diff client-side | Server-side diff job, poll, download result |
| Historical briefcase | Required | Not required |
| Cost driver | Distance from tip to the older changeset | Job duration |
| Returns property values | Yes | No — IDs, class, opcode only |
| Deletions | Yes, by set difference on element IDs | Yes, via opcode |
| Orchestration | None built in (but 200s+ HTTP calls need it anyway) | Job/polling model built in |
| Maturity | Stable | Technical Preview |

A hybrid remains plausible and is currently the most attractive shape: Changed Elements to
compute the delta across the changeset range without acquiring a historical briefcase, then a
single ECSQL call at tip to fetch property values for the resulting (small) element ID set.
Each mechanism does what it is good at, and no old briefcase is ever downloaded.

**Not yet decided.** The deciding measurement is the cost of acquiring a briefcase at an
*adjacent* named version — see OQ-001. A test run against changeset
`a9aa00c6ed6480ffdb23c6c4d7a7dbf49ceddeae` was started but cut off before completing.

### Superseded reasoning (kept deliberately)

Position moved several times during investigation as measurements arrived. Recording this
because the intermediate reasoning was wrong in instructive ways:

1. *Initial:* prefer ECSQL, defer Changed Elements — based on the stated "tip queries first"
   priority.
2. *After learning deletions matter:* claimed ECSQL structurally cannot see deletions. **Wrong** —
   a two-point query recovers them by set difference. Corrected.
3. *After measuring 3.28M elements and a 202s cold call:* declared the two-point diff infeasible.
   **Over-corrected** — the 202s is one-time briefcase acquisition, not per-row, and class
   filtering reduces the working set by orders of magnitude.
4. *After the ~6-year tip-vs-named-version gap emerged:* swung back toward Changed Elements.
   That gap is an artefact of a dormant reference model, not of the live process, which diffs
   adjacent named versions.

---

## DR-002 — Class-key filtering is mandatory

**Status:** Decided
**Date:** 2026-08-13

`SELECT COUNT(*) FROM bis.Element` on the Appomattox iModel
(`af33261c-2067-4fc4-8fb4-22230092a930`) returns **3,280,925** elements. The original working
assumption was "thousands" — three orders of magnitude out.

Element counts are dominated by non-promotable content. The top three classes alone
(`ProcessPidGraphical:PidTextAnnotation` 399,962; `ProcessPidGraphical:PidGraphic` 374,464;
`BisCore:GeometryPart` 251,156) account for roughly a third of the model and are drawing
graphics and render geometry, not assets. Connectivity and fastener detail (`FLUID_PORT`,
`JOINT`, `WELD`, `BOLT`, `TERMINAL`) adds several hundred thousand more.

Plausible REG-LOCATION candidates are far smaller: `PIPING_NETWORK_SYSTEM` (17,653),
`PIPING_NETWORK_SEGMENT` (51,427), `GENERIC_INSTRUMENT` (11,001), `BALL_VALVE` (10,865),
`NOZZLE` (9,799).

**Decision:** the existing design in which the user selects the class key from a list is
confirmed as necessary, not cosmetic. It is the mechanism that makes the volume tractable.
No approach that materialises the full element set is viable.

Schema families present: `ProcessPidGraphical`, `ProcessFunctional`, `ProcessPhysical`,
`PDMxPlant_Appomattox`.

---

## DR-003 — `LastMod` cannot identify unpublished work

**Status:** Decided
**Date:** 2026-08-13

`LastMod` is queryable and filterable (Julian Date float, e.g. `2459125.176` ≈ 2020-10-07), so a
`WHERE LastMod > <julian>` filter is mechanically available. This would have permitted a single
cheap query at tip instead of any diff.

It is not usable. All 17,653 `PIPING_NETWORK_SYSTEM` elements carry timestamps falling on only
**4 distinct days**, clustered milliseconds apart. That is the signature of a bulk synchroniser
import: `LastMod` records when the connector last wrote the element, not when an engineer
changed it. A bulk re-sync would restamp everything, so the filter would return either nothing
or the entire class — never the actual delta.

**Decision:** the single-query shortcut is closed. A genuine changeset-based diff is required.

**Caution for later:** it was briefly inferred from this clustering that the whole iModel was a
static import. That was wrong — it generalised a single-class observation. The iModel has 5000+
changesets and 5-10 named versions.

---

## DR-004 — Promotion must not run over synchronous HTTP

**Status:** Decided
**Date:** 2026-08-13

Measured against `hr-imodel-ccom-api`:

| Call | Time |
|---|---|
| `/health` | 0.9s |
| First real query (cold, briefcase download) | **202.2s** |
| Subsequent queries (warm cache) | 2.5-3.6s |

The cold path exceeds the Azure Functions default HTTP timeout of 230s by a narrow margin, and
202s was the *cheapest* case — a briefcase at tip. Function instances recycle and the briefcase
cache is instance-local, so any promotion run can land on a cold instance.

**Decision:** the promotion flow must be queue- or event-triggered, not a synchronous HTTP
request. This holds regardless of which approach DR-001 settles on. Note this also weakens the
"avoid orchestration complexity" argument for the ECSQL route: a 200s+ synchronous call is
already a long-running operation, just without a job model to manage it.

---

## DR-005 — Use `/query`, never `/assets`, for extraction

**Status:** Decided
**Date:** 2026-08-13

`Bentley.Interoperability.iTwin.API` route inventory:

| Route | Method | Purpose |
|---|---|---|
| `health` | GET | Liveness, anonymous |
| `itwins/{iTwinId}/imodels/{iModelId}/query` | POST | Arbitrary ECSQL at tip |
| `itwins/{iTwinId}/imodels/{iModelId}/changesets/{changesetId}/query` | POST | Arbitrary ECSQL at a changeset |
| `itwins/{iTwinId}/imodels/{iModelId}/assets` | POST | **Write** — upsert `SyncSegments` |
| `itwins/{iTwinId}/imodels/{iModelId}/map` | POST | **Write** — create relationships |
| `itwins/{iTwinId}/imodels/{iModelId}/relationships` | POST | **Write** |
| `itwins/{iTwinId}/imodels/{iModelId}/schemas/{schemaName}` | POST | **Write** |

`/assets` (`upsertAsset.ts`) was initially proposed as the extraction endpoint. It is the
opposite: it opens in `ReadMode.ReadWrite`, calls `insertAsset`, `mapSimilarElements`, and
`saveAndPush` — **pushing a changeset to iModelHub**. It is the inbound REG-ASSET to iModel path.

**Decision:** extraction uses `/query` and `/changesets/{changesetId}/query` only. Both accept
arbitrary ECSQL plus bindings, so extraction is schema-agnostic by construction, and both return
actual property values.

---

## DR-006 — Grouping & Mapping rejected for this workflow

**Status:** Decided
**Date:** 2026-08-13

The approach described in `docs/how-to-query-an-imodel.txt` (mappings, groups, per-property
creation, extraction runs, partition CSV download) is an ETL pipeline. The requirement here is an
event-driven promotion triggered by a named-version notification, not a batch extract.

**Decision:** retained as background reference only. Not the mechanism for this scenario.

---

## Defects found in `Bentley.Interoperability.iTwin.API`

Not decisions, but discovered during investigation and worth not losing.

### DEF-001 — `getAssetLatest` can serve stale data

`iModelService.ts:206-215` calls `pullChanges()` **only** when `mode === ReadMode.ReadWrite`.
`getAsset.ts:34` initialises with `ReadMode.ReadOnly`, so the read path never pulls.

Combined with `briefcase.ts:20-29`, which returns the most recent *cached* briefcase when no
`changesetId` is supplied — with a log message reading "to be updated to latest" describing an
update that never happens — `getAssetLatest` can silently return whatever changeset that
instance last downloaded.

Cold instance: correct (fresh download). Warm instance: potentially stale. Intermittent and
invisible, and for "detect newly unpublished elements" it could report no change when change
occurred.

Ironically the write path (`upsertAsset`, ReadWrite) does pull correctly.

*Fix options:* pull in ReadOnly as well, or resolve tip explicitly via the iModels API and always
use the changeset route. The second is preferable — it makes "latest" explicit and immutable and
sidesteps the cache question. `/changesets/{changesetId}/query` is unaffected: a specific
changeset is immutable, so the exact-match cache lookup at `briefcase.ts:13` is sound.

### DEF-002 — ECSQL does not support `COUNT(DISTINCT ...)`

Returns HTTP 500. Use a grouped subquery instead:
`SELECT COUNT(*) FROM (SELECT x FROM ... GROUP BY x)`.

---

## Open questions

**OQ-001 (blocks DR-001) — cost of a briefcase at an adjacent named version.**
Everything hinges on this. If acquiring a briefcase at the previous named version is cheap, the
two-point ECSQL diff wins on simplicity and returns property values directly. If expensive,
Changed Elements wins. Test in flight when work stopped; adjacent named-version changesets
supplied by the user:
- `a9aa00c6ed6480ffdb23c6c4d7a7dbf49ceddeae`
- `918f6339ec99a4291fe622a8cb4656c985ba486e`

**OQ-002 — does Changed Elements V3 complete acceptably on this iModel?**
Still Technical Preview. Worth proving before committing to it.

**OQ-003 — typical changeset distance between consecutive named versions in a live ENG repo.**
Determines whether OQ-001's answer generalises or is specific to this dataset.

**OQ-004 — is there a queryable lifecycle/`PUBLISHED` status?**
`syncsegments.ts:103` references "the lifecycle status of the segment". If that status is
queryable on elements, a single filtered ECSQL query at tip could replace the diff entirely.
Deferred at the user's request; worth reopening, as it would simplify the design substantially.

---

## Test environment

| Item | Value |
|---|---|
| Function app | `https://hr-imodel-ccom-api.azurewebsites.net/api` |
| iTwin | `50b0eec3-3ed3-468b-b410-538dba4f8263` |
| iModel | `af33261c-2067-4fc4-8fb4-22230092a930` (Appomattox) |
| Elements | 3,280,925 |
| Changesets | 5000+ |
| Named versions | 5-10, latest 2020-10-02 |
| Source | `D:\Working\iTwin\Bentley.Interoperability\Bentley.Interoperability.iTwin.API\src` |

The tip-versus-latest-named-version gap is roughly six years. This is a dormant reference model,
**not** representative of the live process, which diffs adjacent named versions. Do not size the
design against this gap.

The function key is passed as a `?code=` query parameter and is deliberately not recorded here.
It has been exposed in conversation and should be rotated.

### Changed Elements V3 reference

Extracted from the local tutorial to `docs/ce-v3-extract.txt`. V3 differs materially from the V2
implementation in `D:\Working\iTwin\Bentley.Interoperability\iTwinEventListener`:

| | V2 (existing code) | V3 |
|---|---|---|
| Create | `POST /changedelements/comparisonjob` | `POST /changedelements/diff` |
| Accept | `...itwin-platform.v2+json` | `...itwin-platform.v3+json` |
| Range | `startChangeSetId` / `endChangeSetId` (string IDs) | `startChangesetIndex` / `endChangesetIndex` (numeric) |
| Strategy | n/a | `diffingPlan.strategy`: `VersionCompare` or `Basic` |
| Poll | `GET /comparisonjob/{jobId}/itwin/{id}/imodel/{id}` | `GET /diff/{jobId}?iTwinId=..&iModelId=..` |
| Result | `comparisonJob.comparison.href` | `job.href`, plus `completedAgents`/`totalAgents` |

`VersionCompare` returns the same `ChangedElements` parallel-array shape as V2, so
`iTwinEventListener/Models/ChangedElements.cs` stays accurate. `Basic` returns a compact array of
`{ id, classFullName, operation }` — smaller and faster, with `classFullName` directly available,
but IDs are in Big Integer format and need conversion to hex.

If V3 is adopted, `Oiie.ITwin/ITwinQueryClient.GetTipChangesetIdAsync` needs extending to surface
the changeset **index**, not just the ID.

---

## DR-007 — Twin context travels as CCOM `RegistrationSite`, not in the BOD envelope

**Status:** Decided
**Date:** 2026-08-14
**Context:** CMS (formerly OM-RELIABILITY) had to become twin-scoped so its records could be
filtered to the active iTwin. The obvious place to carry the twin was the BOD envelope.

### Why the envelope was rejected

`BodEnvelope` exposes only `BodId`, `SenderLogicalId`, `SenderReferenceId`, `ActionCode` and
`CreationDateTime`; `ApplicationArea.Sender` adds `LogicalID`, `ComponentID`, `TaskID`,
`ReferenceID` and `AuthorizationID`. None of these is a twin. `InboxPump.HandleAsync` also calls
`dbFactory.Create(participantId)` with no `twinId`, so every handler runs unscoped. Adding the twin
to the envelope would have meant inventing a sandbox-specific extension to the OAGIS
`ApplicationArea` — a private field on a standard structure, which every conforming receiver would
ignore.

### Decision

The twin travels as `Segment.RegistrationSite`, which CCOM already defines (`CCOM.xsd:1116`,
type `Site`, and `CCOM.xsd:838` for `Asset`). It was present in the schema and in the
`SyncSegmentsWithAttributes` fixture but was not modelled in the C# types, so three things were
added:

- `Site` in `Oiie.Ccom/Types/Nouns.cs`, as the CCOM specialisation of `Segment` it actually is.
- `RegistrationSite` on both `Segment` and `Asset`, at the schema's element order.
- Stamping in `SyncSegmentsBuilder`, resolving `Tag.ITwinId` to the `ITwin` row and sending
  the twin's own identity as the `Site` UUID rather than minting a second one.

CMS reads the twin from the **segment**, not the asset: an asset moves between plants over its
life, whereas a functional location does not.

### Consequences

- The twin is a property of the payload, expressed in the standard's own vocabulary, so any
  CCOM-conforming receiver can read it.
- `Site`'s registered-content members (`RegisteredSegment`, `RegisteredAsset`, …) are deliberately
  not modelled. A site referenced as context should identify itself and nothing more; carrying its
  inventory inside every noun pointing at it would nest the whole plant in each message.
- `ITwinId` is nullable everywhere in CMS, and the `/admin/cms/*` twin filter applies only when a
  twin is supplied. A publisher asserting no `RegistrationSite` leaves records visibly unscoped
  rather than silently invisible.

### Open

MMS does not persist a twin on `FunctionalLocationRecord` or `EquipmentRecord`, so
`MmsAssetSegmentEventsBuilder` cannot yet re-stamp `RegistrationSite` when it publishes Scenario 11
events. Until it does, CMS records ingested via SC11 will have a null `ITwinId`. Fixing this means
adding an `ITwinId` column to the two MMS records and populating it in `MmsSegmentsHandler` from the
inbound `RegistrationSite`.

> **Superseded in part by DR-008.** The transport decision here stands — `RegistrationSite` remains
> the correct carrier. What was wrong was persisting it as a native `ITwinId` column inside CMS, and
> the "Open" item above proposed extending that same mistake to MMS. Do not add `ITwinId` to the MMS
> records.

---

## DR-008 — Context ownership is resolved through ws-CIR, never copied between schemas

**Status:** Decided
**Date:** 2026-08-15
**Supersedes:** the persistence half of DR-007

**Context:** DR-007 gave CMS a nullable `ITwinId` column on its three tables and filtered
`/admin/cms/*` on it directly. That worked, which is precisely the problem. A condition monitoring
system has no iTwin column and never will — the twin GUID is Bentley's context key. Storing it
natively in a foreign O&M schema taught CMS to speak iTwin and bypassed the registry entirely.
`deploy/sandbox/NAMING.md:60` warns about exactly this: "resolve a foreign identifier instead of a
CIR call — it will work, nobody will notice."

### What the real systems actually hold

MMS keys its context owners in `dbo.SETUP_OWNER (OWNER_ID, OWNER_NAME)` with local integers 1–11:
`7000 - Metro District`, `9600 - District 6`, `MnDOT`, and so on. The same organisational domain
appears in CMS, ENG-LOCATION and ENG-ASSET, but each system keys it its own way. Three key spaces,
one reality, no common structure — an integer, an iTwin GUID, and a CMS-local code have nothing to
join on.

### Decision

Context owners are registered with ws-CIR under a new category, `ContextOwner`, kept distinct from
`Segment` so a steward cannot accidentally relate a pump to a district. Each participant registers
its own key space:

- ENG registers `SourceID=ENG, IDInSource=<twin guid>` for each `ITwin` row.
- CMS registers `SourceID=CMS, IDInSource=OWN-07` for each `ContextOwnerRecord`.
- MMS would register `SourceID=MMS, IDInSource=7`.

A steward then asserts equivalence via `CirRegistrationService.RelateCmsOwnerAsync`, and the
resulting CIRID is read back from the registry — not invented locally — and written onto the CMS
owner row.

CMS's seeded codes are deliberately `OWN-01`…`OWN-11` rather than the integers MMS uses. Identical
keys would make a cross-schema join appear to work and the sandbox would demonstrate the opposite of
its thesis. `ContextOwnershipTests` asserts the two sets are disjoint.

### Consequences

- The three `ITwinId` columns are gone from CMS. What arrives is stored as
  `ForeignOwnerSourceId`/`ForeignOwnerIdInSource` — kept raw and uninterpreted, exactly as CMS
  already treats foreign location and asset identifiers.
- Resolution is on read, not on ingest. A CIRID stamped at ingest is a snapshot of what the registry
  said at that moment, and a later equivalence correction would leave it silently wrong. The
  `IdentityMapEntry` cache keeps this to one round trip per TTL, and invalidated or stale entries are
  skipped so a correction takes effect immediately.
- An unresolvable twin returns an empty result with an explicit `unresolvedContext` reason. Returning
  every row because the filter could not be resolved would present another district's assets as
  belonging to the one asked for.
- Until a steward relates the owners, every CMS record is unresolved. This is truthful and is the
  state the registry exists to remedy; the UI must show it rather than render an empty list.
- `ContextOwnershipTests` locks the regression out structurally: no CMS entity may expose any
  property whose name contains "Twin".


## DR-009 — MMS is modelled on the customer's real schema, which cannot be extended

**Decision.** MMS maps `dbo.LIGHT_SYSTEM_INVENTORY`, `dbo.LIGHT_SYSTEM_CLASS_CODE`,
`dbo.SETUP_ASSET_STATUS` and `dbo.SETUP_OWNER` exactly as the customer defined them. No table and no
column may be added. Everything the sandbox previously stored about federated identity is removed
from MMS and lives only in ws-CIR.

**Context.** The sandbox's earlier MMS model was invented: `FunctionalLocationRecord` carried
`FederationId`, `Cirid`, `ForeignSourceId`, `ForeignIdInSource`, `CostCentre` and `PlannerGroup`, none
of which exist in the customer's database. It worked because nothing tested it against reality.

**Why the constraint bites.** Removing those columns removes real capability, and it is worth being
explicit about what was lost rather than pretending the change was cosmetic:

- **MMS cannot record what it was told.** There is nowhere to put the sender's identifier, so on
  re-receipt it can only match on `LIGHT_SYSTEM_NAME`, the alternate key. If a sender renames
  something, MMS will create a duplicate. Previously it matched on the foreign identifier and would
  not have.
- **MMS cannot cache a resolution.** CMS writes the CIRID onto its own `ContextOwnerRecord` row;
  MMS has no such column, so `MmsContextResolver` re-reads the owner from the registry's equivalence
  set on every call, backed only by the `IdentityMapEntry` TTL. MMS is therefore harder down if
  ws-CIR is unavailable. This is a genuine operational cost of the constraint.
- **Identity lineage cannot include MMS.** `IdentityLineageService` groups by a locally held
  `FederationId`. MMS has none, so it is omitted rather than shown with empty identities, which
  would misreport it as holding nothing identifiable.
- **Two scenario assertions were weakened.** `sc02` could previously assert `Cirid IS NULL` to prove
  a row arrived unresolved; there is no such column, so unresolvedness is now tested via
  `cir_registered` instead. `sc01-greenfield` asserted that a carried identity survived into MMS;
  that now has to be asserted against the code assignment.

**Consequences.**
- `OWNER_ID` is MMS's context key, the counterpart of an iTwin in ENG. It is resolved through ws-CIR
  by `MmsContextResolver`, never stored alongside a twin. `RelateMmsOwnerAsync` asserts the
  equivalence and, unlike the CMS path, writes nothing back — the registry is the sole record.
- `OWNER_ID` is nullable, so unowned light systems exist and can never resolve to a twin. They are
  surfaced as `"no owner"` rather than filtered away, because silently dropping them would
  misreport the inventory as smaller than it is.
- Inserts allocate `MAX(LIGHT_SYSTEM_ID) + 1` inside the insert transaction and then register the key
  with the CIR (`MmsInventoryWriter`). This is **not** concurrency-safe against a live customer
  system and must not reach production without a sequence or an allocation procedure.
- `EquipmentRecord`, `WorkOrder` and `LocationRelationshipRecord` are retained but explicitly marked
  sandbox-only. No customer table has been supplied for any of them, and Scenario 11's
  install/removal semantics have nothing in `LIGHT_SYSTEM_INVENTORY` to hang on. They are the first
  thing to revisit when the real work-order tables are known.
- Scope is deliberately LIGHT_SYSTEM only. The customer models every segment/asset type as its own
  table, and the mapping to the CCOM RDL is a separate exercise.
- `ContextOwnershipTests` guards this structurally: no MMS entity may declare an identity column, the
  mapping must use the customer's names, `LIGHT_SYSTEM_INVENTORY` must have exactly five columns, and
  `OWNER_ID` must stay nullable.

---

## DR-010 — Scoped MMS reads cost one ISBM long-poll, and the cost is the provider's receive timeout

**Status:** Open
**Date:** 2026-08-14
**Context:** The MMS inventory panel took ~1.9s to return 9 rows, and the question was raised as to
whether `LIGHT_SYSTEM_INVENTORY` needed an index.

### Evidence

Measured against the running sandbox, same table and same SQL:

| Call | Rows | Time |
|---|---|---|
| `/admin/mms/locations` (unscoped, no CIR resolve) | 13 | ~450 ms |
| `/admin/mms/locations?twin=…` (scoped, CIR resolve) | 9 | ~1890 ms |

The unscoped call returns *more* rows in a quarter of the time, so the table and its indexing are not
the cause. At 13 rows the query is a trivial scan and no index would change it.

The ~1.4s delta is the ws-CIR resolution. `CirClient.ResolveAsync` calls `FindEquivalentsAsync` even
on a cache hit — deliberately, per DR-008, because MMS cannot cache a CIRID locally and the
equivalence set is the only place the twin→`OWNER_ID` link exists. That is one ISBM round trip per
scoped read.

The round trip itself was then traced to `ISBMProvider/Infrastructure/ServiceBusMessageBroker.cs`:

```csharp
var recv = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), ct);
```

An Azure Service Bus long-poll. On an empty queue it waits the full 2s before answering 404. This is
confirmed by the asymmetry between a real and a nonexistent session: a nonexistent session fails
validation *before* reaching the broker and returns in ~280ms, while a real session with an empty
queue pays ~2.28s (2s wait + ~280ms overhead).

### Ruled out

- **An MMS index.** Disproved by the unscoped call being faster with more rows.
- **Client-side poll interval.** `CirClient` used a 1s `Task.Delay` between polls; replacing it with
  50ms-and-backoff changed nothing (~1890ms → ~1900ms), because the time is spent *inside* a single
  HTTP call, not between polls. The change was reverted.
- **A provider wait/timeout query parameter.** `timeout`, `waitTime`, `wait`, `maxWait`,
  `timeoutSeconds`, `pollTimeout` and `longPoll` were all probed against the deployed function and
  all returned an identical ~2.27s. The timeout is a hardcoded literal with no override via query
  string, header, or `host.json`.

### Why this is not simply lowered

The 2s wait is *correct* for genuine asynchronous consumers. `InboxPump` and `IsbmBodListener`
long-poll deliberately; a short wait would turn efficient blocking reads into a busy spin against
Service Bus, raising cost and throttling risk. Lowering it globally optimises the synchronous CIR
request/response path at the expense of every subscriber.

### Options, none yet taken

1. Make the receive timeout configurable, defaulting to the current 2s, and pass a short wait only on
   the consumer-request read path. Correct fix; spans two deployables and requires redeploying
   `ISBMProvider`.
2. Leave the provider alone and mask the latency in the UI. No redeploy, no correctness risk.
3. Short-TTL cache on the equivalence set in `MmsContextResolver`. Removes the round trip without
   touching the provider, but reintroduces the staleness window that caused the resolved-twin-reads-
   as-unrelated bug, so it trades a known-fixed correctness bug for latency.

**Open question for the user.** Recorded here rather than acted on, because option 1 changes a
separate deployed service and option 3 knowingly reopens a bug that was just closed.

---

## DR-011 — Outbox publication is made idempotent by treating the outbound message record as a receipt

**Status:** Decided
**Date:** 2026-08-15
**Context:** `OutboxDispatcher` retried `Pending` items until `MaxAttempts`, but the ISBM post and the
`MessageRecord` that records it are two separate writes. A process that died between them left the item
`Pending` with the BOD already on the channel, so the retry published it a second time. Retry without an
idempotency check is not safe merely because attempts are bounded.

### Decision

Before building or posting, `PostAsync` looks for an existing **outbound** `MessageRecord` for the item. If
one exists, the post already succeeded and only the bookkeeping was lost: the item is closed out against
that record (`State = Posted`, carrying the original `MessageId` and `OccurredAt`) and logged at warning
level, rather than resent.

### The match is on verb and noun, not correlation id alone

This is the part worth remembering. `CorrelationId` looks like the natural dedupe key — it is already
assigned per item and already indexed — but it is **not unique per publication**. One business event
legitimately produces several outbox items under a single correlation id: `RegLocationService` queues
segments and then the connections between them that way, deliberately, because a receiver cannot store an
edge whose endpoints it has not yet been told about (the same ordering rule as DR-007's context handling).

Matching on correlation id alone would therefore classify the connections publication as a duplicate of the
segments publication and silently drop every edge. The guard matches on `CorrelationId` + `Direction` +
`Verb` + `Noun`, and `ParticipantDbContext` carries a composite index over exactly that tuple, since the
lookup now runs before every publication attempt.

### What this does not cover

A crash *during* the post — after the broker accepted the message but before the call returns — is still
indistinguishable from a failed send, because no receipt was written either way. Closing that fully requires
the **receiver** to dedupe on `BODID`, which is a change on the consuming side and is not done. The guard
narrows the window; it does not eliminate it.

### Caveats carried forward

- **Untested.** There is no harness for the dispatcher: `Oiie.Sandbox.Tests` builds `ParticipantDbContext`
  against a dummy connection string for model inspection only, and there are no fakes for
  `IIsbmClientAccessor`, `IBodBuilder` or `IIsbmSessionStoreAccessor`. The guard is unexercised on the happy
  path by construction, since it only fires on a retry after a lost confirmation.
- **The index needs a day-zero reset to exist.** `ParticipantSchemaInitializer` short-circuits on the
  `Message` sentinel table, so the composite index is absent from any database created before this change.
  The guard still works — it is a query, not a schema dependency — but is not index-backed until then.

## DR-012 — A registry that does not answer is not a registry that says no

**Status:** Decided
**Date:** 2026-08-19
**Context:** An approved location (`LTP-4`, twin `c86c9c10-…725c`) reached CMS but never appeared in MMS. The
MMS message log said `context … is not related to any MMS owner: The registry did not respond.` — which reads
as a stewardship problem and is not one. The relation existed and resolved correctly; ws-CIR had simply been
cold when the segment arrived. The BOD was recorded `Rejected`, which nothing ever revisits, so a correct
message was lost to a service that was merely asleep.

### The failure was a conflation, repeated three times

Nothing here was a wrong answer. Each layer discarded the distinction between *"the registry answered, and
knows of no such relation"* and *"the registry did not answer"*:

- `ResolutionResult` had no field to carry it, so `ResolveAsync`'s timeout branch was indistinguishable from
  a definitive negative.
- `FindEquivalentsAsync` returned an empty list on timeout. Because MMS reads its `OWNER_ID` out of that
  equivalence set (DR-009: it has nowhere to cache one), an unanswered lookup read as *"no MMS owner
  related"*. This is the same bug one level down, and it survives the first fix on its own.
- `MmsSegmentsHandler` mapped both to `BodHandlingResult.Rejected`.

### Decision

`ResolutionResult` and `MmsContextResolution` carry a `Transient` flag, and `MmsSegmentsHandler` returns
`ProcessingStatus.Failed` rather than `Rejected` when a block is transient.

The status choice is the substance. Both leave the batch unwritten, but **`Rejected` is a verdict on the
message** — it asserts the sender must change something and resend — while `Failed` says the same batch is
worth running again unchanged. Recording an infrastructure outage as a verdict on the payload is what made
the loss permanent.

Transient outcomes are also not cached in the per-batch `ownerByTwin` map. That cache exists to spare the
registry a round trip per segment, not to fix a momentary outage in place for the remainder of a batch.

### The retry is a re-post, not more polling

`ExchangeAsync` re-posts the request once after 20s unanswered. Polling harder would not have helped: if the
provider was not consuming the channel when the request was posted, **the request was never delivered**, and
the wait can only end in a timeout no matter how many times the consumer session is read. Only a second post
can reach a provider that woke during the wait.

Both request ids stay in play afterwards, because the ISBM read is keyed by request id and a provider that
woke just before the re-post is precisely the case where the *first* request gets answered. Both posts carry
the same `BODID`, so whichever reply lands first satisfies the existing echo check and the other is discarded.

### Recovery needed a new primitive

A failed *outbound* message could always be requeued, but an inbound message that failed during processing
had no route back — recovering one meant asking the sender to publish again, which is not something an
operator can do and not something a sender will agree to for a message it considers delivered.
`POST /admin/{participantId}/messages/{id}/replay` re-runs the stored payload through its registered handler.
It writes a *new* archive row and leaves the original `Failed` row intact, because that row is the evidence
of the incident being recovered from.

### Caveats carried forward

- **Replay assumes handler idempotency, and does not enforce it.** It was safe for `Sync Segments` because
  `MmsSegmentsHandler` matches on `LIGHT_SYSTEM_NAME` before creating. Replaying a handler without that
  property would double-write.
- **Rows already recorded `Rejected` stay terminal.** Segments lost to this bug before the fix do not
  self-heal and still read as data problems in the message log.
- **Replay depends on the payload having been retained.** A host without blob storage records an
  `unstored:` sentinel instead of a `ContentRef`, and the endpoint refuses rather than guessing.
- **20s is a judgement, not a measurement.** It is long enough that a merely busy provider answers first and
  no duplicate is ever sent, and short enough to sit well inside the 120s `ResponseTimeout` — but no cold
  start was actually timed.
- **Only the MMS segments path was hardened.** Other CIR callers still flatten a timeout into whatever their
  own failure shape is; the `Transient` flag now exists for them but is unused.

## DR-013 — A participant's customer store is an adapter, not a database handed to a handler

**Status:** Partially superseded by DR-014
**Date:** 2026-08-20
**Superseded parts:** The in-process constraint below — "The split is in-process. The `DbContext` is still
passed to the writer" — no longer holds. Participants are separately deployed Function Apps. Everything else
in this decision stands: the operations-only boundary, the transformation/persistence split, transient
classification at the point of failure, and the returned-keys contract all carry forward into DR-014's HTTP
shape. The reasoning recorded here about why the *earlier* draft was rejected remains accurate as history and
is answered directly in DR-014.
**Context:** Participants are to become deployable Function Apps fronting real customer systems. A prior
generation of this integration (`Interoperability_old`) solved the same problem, and the parts it got right
and wrong are both instructive. Full design in [participant-abstraction-spec.md](participant-abstraction-spec.md).

### The current seam is in the wrong place

`IBodHandler.HandleAsync` takes a `ParticipantDbContext`. The handler is therefore given the customer's
database, and one method performs context resolution, semantic transformation and persistence together —
`CmsSegmentsHandler` and `MmsSegmentsHandler` both do. The customer store cannot be swapped for a REST API
without rewriting the handler, and transformation cannot be tested without SQL.

### What the prior generation got right

Its pipeline was decomposed correctly: `TopicSubscriber → XMLParser → MimosaXmlParser → DataHubCCOMWriter`,
with `ConvertCCOMSegmentToMaximoLocation` (semantic → native) a *separate component* from `SegmentImport`
(native → customer API). The converter never touched the wire; the importer never understood CCOM. Pipelines
were declared as JSON, so adding a route was config rather than a deployment. And
`SupportedInteropFunctionality` negotiated by capability (`FastAssetsSupported`) rather than version number.
All three are adopted.

### What it got wrong, and why the same shape is not repeated

- **`IDataHubDatabase` had eight connection/credential members and one operation** (`WriteToDataHub`). The
  interface was a config bag with a method attached; consumers saw `Password` and `DataSource`.
- **`WriteToDataHub(string json, string storedProcName)`** let the caller supply the stored procedure, and
  the BOD-to-proc mapping lived in orchestration config. A change to the customer's schema was a change to
  orchestration. That is the leak this decision exists to prevent.
- **`IComponent.Initialize()` was dead** — it threw `NotImplementedException` in the converter and was empty
  in the importer.
- **Runtime state was written into configuration** (`Stats`, `lastRun`, `exceptions`, `state`), making config
  non-reproducible and racy.
- **`OnMessageAsync` returned bare `Task`.** Failure was an exception, so the DR-012 distinction could not be
  expressed at all. The old engine had no way to say "transient — replay me".

### The newer generation reached DR-012 independently, and shows where the wrong fix leads

`Interoperability` — the current generation of that system — extracted `lib/Abstractions` and `lib/Engine`
as standalone packages with one folder per connector, which is the packaging shape this decision targets.
It also arrived at DR-012 on its own: `MessageProcessingException.IsTransient`, branched on by every producer
(`ProcessAssetProducer`, `RequestForWorkProducer`, `AnomalyEventProducer`), with `APMRestAPI` and
`Analytics4DRestAPI` additionally letting the customer system declare transience through a `Transient: true`
response header. That header idea is adopted — the system that failed is best placed to say whether a retry
will help.

**Its classification mechanism is the cautionary part.** `RestAPIBase` decides transience from an
`HttpStatusCode` table that marks `Unauthorized` and `NotFound` transient — retrying indefinitely against a
bad credential — and, when that yields nothing, by substring-matching the exception text against a list
including `"TRY AGAIN"`, `"DEADLOCKED"`, `"failed to lock"`, `"the object is currently locked by"` and
`"Response status code does not indicate success: 404"`. Nearly every entry cites a bug number. The list is
scar tissue, one string per production incident.

That is the inevitable outcome of classifying transience *at the top of the stack for an exception thrown at
the bottom*: by then the only surviving evidence is the message text, so you match on it. The corrected rule
is that **the layer that knows why something failed is the layer that must classify it**. An adapter calling
a customer API knows whether it received a 503 or a validation rejection, and must say so in a typed result
at that moment.

### Decision

The customer boundary is `ICustomerWriter<TIntent>` (with a separate, paging-only `ICustomerReader<TRow>`),
exposing **operations only** — no connection strings, no credentials, no stored-procedure names, no
lifecycle method. Transformation moves to `IBodTransformer<TIntent>`, which performs no I/O and is therefore
testable without a database. Orchestration keeps the archive, identity correspondence, provenance and
verdicts.

**The split is in-process. The `DbContext` is still passed to the writer.** An earlier draft made each
participant a separately deployed Function App; review rejected that, because it would split one transaction
across two stores and allow customer rows and provenance to diverge — a failure mode that is currently
impossible. The interface exists to mark the boundary and make the write substitutable in tests, not to hide
the store.

`PersistOutcome` carries `PersistFailure.Transient`, set by the writer at the point of failure. This extends
DR-012 to the customer boundary — a customer system that did not answer is not a customer system that
refused — while deliberately not repeating the exception-text inference above.

### Generated keys forced the contract's shape

The naive `Transform → Persist` split does not survive the pilot participant. `CmsSegmentsHandler` calls
`SaveChangesAsync` *inside* its loop because `AssetID` is an IDENTITY column, and the identity correspondence
cannot be computed until the insert has assigned it. MMS is the opposite: `LIGHT_SYSTEM_ID` is computed as
`MAX(id) + 1` before the insert. Two participants, two key models.

So `PersistOutcome` returns `IReadOnlyList<PersistedItem>` carrying the key the writer assigned to each row,
and identity registration and provenance become an orchestration step consuming those keys. A contract
returning only counts could not express CMS at all.

Similarly, `CmsSegmentsHandler.ResolveSiteAsync` reads the database once per segment, so lookups are hoisted
into a `TransformContext` populated before transformation begins. That hoisting is what makes the
transformer pure, and it is a real behaviour change — one batched read replacing per-segment reads — not a
pure refactor.

### Wiring stays in code

Both prior generations wired stages by reflection — `componentType.GetMethod("Connect")` + `Invoke` — so a
mistyped pipeline failed in production, on a real message. The draft's answer was declarative YAML plus a
startup validator to catch those mismatches. The simpler answer is taken instead: registration in code, where
the compiler performs those checks and no validator needs writing. Declarative wiring earns its cost when
routes change without a deployment, by people who do not build the code; neither is true here.

### The contract is specified for inbound participants only

DR-013 draws its evidence from MaximoSaaS and DataHub, the two simplest connectors in the prior system.
The harder ones qualify the scope.

**InspectTech corroborates the split.** It organises by role rather than by BOD —
`Transformers/`, `Consumers/`, `Producers/`, `Dependencies/` — and `BusinessObjectDocumentToAsset` is
`IInput<BusinessObjectDocument>, IOutput<MimosaAssetInspectTech>` with **no repository injected at all**.
That is `IBodTransformer<TIntent>` arrived at independently, and it is the best evidence that the purity
constraint is achievable rather than merely tidy. The caution is that `AssetImport.cs` is 49KB: the split
bounds coupling, not size.

**APM shows what this decision does not cover.** `AssetChangeRequestProducer` is
`IOutput<AssetChangeRequest>, IComponent, ITriggerable` — it polls for unsent work, publishes, then marks
sent. Three consequences:

- The publish side is trigger-driven, not message-driven, so it has no message row to record a verdict on.
  Its transient failures are *rethrown* to let auto-reset restart the pipeline, the opposite of the inbound
  rule. The asymmetry is defensible but must be deliberate.
- Publishing before marking sent is at-least-once delivery with an idempotent consumer assumed. That needs
  reconciling with DR-011's outbox receipt before the publish side is specified.
- `ConvertMIMOSAHelper.cs` is 66KB shared across ten-plus BOD types, confirming that transformers must be
  per-BOD rather than per-participant, and that shared mapping helpers are unavoidable at that scale.

Accordingly this decision covers **inbound participants only**. The "participants are cheap to add" claim
is not fully demonstrated until a publishing participant exists.

### Caveats carried forward

- **This is indirection that a single-BOD participant does not need.** The justification is testability, not
  flexibility. There is no performance gain and no store becomes swappable.
- **There are no tests over `CmsSegmentsHandler` or `MmsSegmentsHandler`** — verified, not assumed. The
  refactor is only safe if characterisation tests are written *first* and left unchanged across it. An
  earlier draft claimed the split was "covered by tests"; that was untrue.
- **A generic writer interface can degrade into a lowest common denominator.** CMS and MMS already disagree
  on key allocation. Where a customer system needs genuinely bespoke operations, take a bespoke interface
  rather than widening the shared one.
- **The boundary is enforced only by review.** A writer taking a `DbContext` can be bypassed by a handler
  that also has one. Nothing in the type system prevents it.
- **Hoisting lookups into `TransformContext` changes read patterns**, and that record will accrete a field
  per lookup. If it grows past a handful, transformation genuinely needs data access and the design should
  be revisited rather than padded.
- **Nothing is implemented.** The spec is a proposal; `CmsSegmentsHandler` still takes a `DbContext`.




---

## DR-014 — A participant is authored by the owner of the customer data, so the boundary is a network hop

**Status:** Superseded by DR-015, before implementation
**Date:** 2026-08-21
**Superseded because:** the requirement it identified was right and the mechanism it chose was wrong.
Third-party authorship does demand a real boundary, but that boundary already existed as a published
standard — ws-ISBM — and this decision invented a proprietary one instead. Retained in full because
the analysis of at-least-once delivery and mandatory idempotency below survives the change intact and
is carried into DR-015; only the custom HTTP contract is discarded.
**Supersedes:** DR-013's in-process constraint. Full contract in
[participant-abstraction-spec.md](participant-abstraction-spec.md) §3.

### The requirement that changed the answer

Participants are to be authored by whoever owns the customer system. If CMS belongs to Meridium, Meridium's
developers build the CMS participant, and their only obligation is a prescribed interface invoked when a
message arrives on the channel.

DR-013 rejected separately deployed participants on the grounds that a split transaction lets customer rows
and provenance diverge. That reasoning was correct and the conclusion is still reversed, because it answered
a narrower question. It weighed a refactor for testability, where an in-process interface is sufficient and a
network hop buys nothing. It did not weigh third-party authorship, which an in-process interface cannot
support at all: Meridium cannot compile into our host, and would not accept the coupling if they could.

### Why an in-process interface could not have been the boundary

An interface a third party implements without reading our source is a real boundary. An in-process interface
is a promise enforced by review — DR-013 admitted as much in its own caveats: *"The boundary is enforced only
by review. A writer taking a `DbContext` can be bypassed by a handler that also has one."*

The distributed form removes that possibility structurally. A participant project cannot reference
`Oiie.Sandbox.Core` or `Oiie.Ccom`; the only permitted dependency is a DTO-only contract package. Leakage
becomes a compile error rather than a review finding. The acceptance test is correspondingly honest: author a
participant using only the contract and the spec, opening no file in `Oiie.Sandbox.Core`.

### What this costs, stated plainly

**The transaction splits and cannot be rejoined.** The participant commits to its own database, then returns
a verdict the orchestrator records in ours. A crash in between leaves the work done but unrecorded, and
redelivery repeats it. Two-phase commit across a customer boundary is unavailable and would be refused by a
customer if offered.

DR-013 named this as disqualifying. It is now accepted, because the alternative is not a safer architecture
but a different requirement. The mitigation is contractual rather than technical: delivery is **at-least-once**,
and **every participant must be idempotent** on `BodId`. CMS satisfies this by construction — `AssetTag` is
`UNIQUE` and the store upserts, so redelivery updates the rows the first delivery created.

A participant that appends unconditionally will duplicate customer data under redelivery. The orchestrator
cannot detect this, and the duplicates are indistinguishable from legitimate rows. This is the sharpest edge
in the design and the reason for the conformance suite below.

**DR-012 becomes a third party's judgement.** The `Failed`/`Rejected` distinction — a registry that does not
answer is not a registry that says no — now depends on a participant author classifying correctly. Getting it
wrong reproduces LTP-4 invisibly: a `Rejected` verdict is terminal and never retried, so a transient
condition reported as `Rejected` destroys data.

The contract answers this in three ways. The rule is stated testably: *would an identical delivery in ten
minutes behave differently?* `Transient` is a required field when the outcome is `Failed`, so it cannot be
omitted by inattention. And §7.1 publishes an executable conformance suite, because without one "adhere to
the prescribed interface" is an aspiration rather than a checkable claim.

### Decision

Each participant stack is an independently deployed Function App exposing `POST /api/bod`. It receives the
**raw BOD XML** and owns all CCOM parsing.

Pre-parsing nouns for the participant was considered and rejected: it would require the orchestrator to
understand every noun any participant might ever accept, which is exactly the coupling this decision removes.
The orchestrator forwards verbatim and interprets nothing.

The response carries the verdict in the **body**, which is authoritative. HTTP status is transport-level and
may originate from infrastructure the participant never touched — a cold start, a gateway timeout, a platform
429 — so status is read only when no well-formed body arrived. This is the same instinct as the
`Transient: true` response header adopted from `Interoperability` in DR-013, made structural rather than
advisory: the system that failed is best placed to say whether a retry will help.

Identity correspondence and provenance stay with the orchestrator, driven off a uniform `PersistedEntity`
carrying the key the customer system assigned. `EntityKey` is a string precisely because key models differ —
CMS allocates an IDENTITY `int`, MMS pre-allocates a `long` — and the orchestrator never interprets it. This
is DR-013's returned-keys insight carried across the wire, and it is what allows a new participant to require
no orchestrator code.

Endpoint discovery is configuration: participant id to base URL, with the function key resolved from Key
Vault. Adding a participant is therefore a data change, not a code change. Self-registration was rejected
because any endpoint able to reach the orchestrator could claim a participant id. ws-CIR was rejected because
it registers business-object identity, not service endpoints; conflating the two would overload a registry
whose meaning is already precise.

`RemoteBodHandler` implements the existing `IBodHandler`, so `InboxPump` is unchanged and the remote hop
hides behind a seam that already exists.

### Caveats carried forward

- **A participant author can silently break idempotency.** Nothing in the contract enforces it and the
  orchestrator cannot observe it. The conformance suite's redelivery case is the only defence, and it is
  opt-in.
- **Latency and cold starts are now in the ingest path.** A cold participant may take seconds to answer.
  Treated as transient, but it changes the failure profile of a demo.
- **Verdict fidelity is delegated.** A participant that reports every failure as `Rejected` will look
  healthy while losing data.
- **Function keys are shared secrets.** Adequate for a sandbox; managed identity is the right answer before
  a real customer system is on the other end.
- **The claim is not yet demonstrated.** It holds when someone outside this repository authors a participant
  from the contract alone. Until then it remains an assertion, and the in-repo reference implementation is
  weak evidence for it.
- **Existing in-process handlers are untouched.** MMS, RegLocation and the rest still take a `DbContext`.
  The two models coexist until there is reason to migrate them.


---

## DR-015 — The prescribed interface is ws-ISBM, and there is no orchestrator

**Status:** Accepted
**Date:** 2026-08-21
**Supersedes:** DR-014 in full, and DR-013's in-process participant split. Specification in
[participant-abstraction-spec.md](participant-abstraction-spec.md).

### The observation that reversed the decision

*"The central orchestrator seems to defeat the disconnected nature of the ISBM intent."*

It does. DR-014 kept a component that received every BOD and decided who should see it. That is a
hub, and OIIE exists to describe an ecosystem without one. The disconnected quality of ISBM is not
an incidental property of the messaging layer; it is the thing being demonstrated.

### Why the error was not visible from inside DR-014

DR-014's own text contains the evidence and draws the wrong conclusion from it. §6 of the
superseded spec specified config-driven endpoint discovery: participant id to base URL, function key
from Key Vault. DR-014 then explicitly rejected ws-CIR for that role, on the grounds that it
"registers business-object identity, not service endpoints."

That reasoning was sound and the conclusion should have been that a *service registry was the wrong
thing to be building*. Instead it became a justification for building a bespoke one. The tell was
there: a design that has to invent discovery, addressing, retry policy and a verdict envelope is
reconstructing a message bus. There was already a message bus, deployed, with 26 operations and a
conformance suite.

The underlying mistake is worth naming because it is repeatable. The requirement was stated as *a
participant must adhere to a prescribed interface*. That was read as *we must design an interface*.
It should have been read as *we must identify the interface*, and the answer was in the spec the
project is named after.

### What is decided

Each participant is an independently deployed Function App that integrates **only** through the
ws-ISBM REST API. No orchestrator, no dispatcher, no participant registry, no custom contract, and
no participant aware that any other participant exists.

Routing is channel topology. A participant subscribes to the channels it consumes and publishes to
the channels it produces; the handover chain is emergent. Adding a subscriber to a channel requires
no change to the publisher, which is the property that makes the "add a participant cheaply" claim
demonstrable rather than asserted.

Verdicts are BODs. On request-response, the response BOD carries the outcome, and
`AcknowledgeRegistry` already exists for exactly this. On publish-subscribe there is no verdict at
all, and that is correct: a publisher that needs to know who consumed its message wanted
request-response.

The consequence for third-party authorship is the real prize. A vendor implementing against ISBM has
built something that works with any conformant provider. Under DR-014 they would have implemented
our proprietary contract and gained nothing transferable — and would reasonably have asked why.

### What this does not fix, contrary to first impressions

**Read-then-remove is at-least-once, not exactly-once.** A participant commits to its customer
database and then calls `RemovePublication`: two systems, no shared transaction, and a crash between
them means the work is done and unacknowledged. The split transaction DR-013 objected to and DR-014
accepted is still present. ISBM changes *who* redelivers, not *whether* duplicate application is
possible. An early draft of the ISBM instructions claimed exactly-once; that claim is withdrawn, and
the mandatory-idempotency obligation from DR-014 carries over unchanged.

**The idempotency key is `BODID`, not the ISBM `MessageId`.** `MessageId` is assigned per channel and
de-duplicates transport redelivery on that channel only. It does not survive a hop: when REG reads a
publication and republishes it onward, the downstream message carries a new `MessageId`, so a
redelivered-and-reprocessed BOD reaches MMS looking entirely new. De-duplicating on `MessageId` writes
the maintenance record twice.

The rule adopted is therefore: **a participant preserves the inbound `BODID` when republishing the
same business fact, and mints a new one only when originating a fact or answering a request.** The
ws-CIR provider's existing behaviour — a fresh BODID per response BOD — is already correct under this
rule. Forwarding participants are where it must be applied deliberately.

**Notification delivery is best-effort and currently unretried.**
`HttpNotificationDispatcher.NotifyAsync` catches all exceptions from the listener PUT, logs, and
returns; the dispatching Service Bus trigger then completes successfully, so nothing retries. The
message is not lost — it stays queued until removed — but nothing rings the doorbell again, and a
participant that was cold when the notification fired stops receiving it indefinitely. Participants
therefore MUST poll as a backstop, and the existing ws-CIR timer drain is kept rather than replaced.

### Caveats carried forward

- **The publication and consumer-request ISBM routes are unverified.** `IIsbmClient` marks them as
  inferred from convention; ws-CIR exercises only the provider-request and subscription halves. The
  new topology is publication-centric, so every hop except CIR's own runs on unproven routes. The
  handover-chain test exercises them before any participant depends on them.
- **A participant author can still silently break idempotency**, and nothing in the ecosystem can
  detect it. Removing the orchestrator removes even the possibility of central detection.
- **Provenance and identity correspondence lose their central home.** The orchestrator wrote both
  after a successful apply. Each participant must now record its own, most naturally via the CIR
  request channel. Not yet designed.
- **The single-pane UI loses its vantage point.** The orchestrator saw every step, which is what made
  one screen showing the whole flow straightforward. The UI must now reconstruct the chain from ISBM
  channel state plus CIR entries. Arguably a more honest demonstration of a distributed system, but
  it is unbuilt work that the demo depends on.
- **Whether a dead session is distinguishable from an empty queue is unknown.** Both may present as
  404. If so, a participant can hold a session that will never deliver and appear merely idle.
- **The architecture is still unproven by the test that matters**: someone outside this repository
  authoring a participant from the ISBM spec alone. It is now a far more plausible claim, because the
  interface is a published standard rather than ours — but plausibility is not evidence.
- **No existing code has been changed.** DR-013's in-process handlers, `InboxPump` and the ws-CIR
  direct endpoint all still run. Standalone participants are built and proven first; migration
  follows. `Oiie.Participants.Contract` is superseded but still on disk.

---

## DR-016 — The ISBM channel is derived from the iTwin federation id, not configured

**Status:** Decided
**Date:** 2026-08-20
**Context:** `EngEngine` was built with a hardcoded `ChannelUri` of
`/OIIE-SANDBOX/Enterprise/Site/Eng` and a topic of `Sync.Segments`, both of which predate
[isbm-channel-naming-convention.md](ISBM%20Channels/isbm-channel-naming-convention.md).

### Decision

The engine holds `Enterprise` and `Domain` and composes
`/{enterprise}/{itwin-federation-id}/{domain}/publication`, resolving the iTwin from ENG at
drain time. Topic is `oiie:sc01/ccom:SyncSegments`. A `ChannelUriOverride` exists for brokers
provisioned before the convention, and is empty by default.

### Why

- **A configured URI is a copy of the convention that can drift from it**, and a settings file
  is exactly where that drift goes unnoticed. Deriving it states the convention once.
- **Configuring the iTwin next to the iModel creates a pair that can disagree.** The failure
  mode is a handover delivered to the wrong twin's subscribers, which from the engine's side
  is indistinguishable from success. Resolving it from ENG removes the possibility.
- **The federation id outlives the name.** A renamed corridor or re-scoped project breaks every
  subscription keyed to a readable name. The readable name goes in the channel description.
- **The scenario belongs in the topic** because the same BOD means different things at different
  points in a journey: `SyncSegments` from ENG is a design proposal, from REG-LOCATION it is an
  approved location, and a subscriber wanting only one could not otherwise tell them apart.

### What this leaves open

- **Only the engineering leg was converted.** `sc01-design-release.yaml`,
  `sc01-greenfield-allocation.yaml` and the `eng` and `reg-location` personality packs moved.
  The `/OIIE-SANDBOX/Enterprise/Site/OandM` leg did not, because it is shared with SC02, SC11
  and the MMS/CMS packs. Moving it in isolation would have left MMS subscribed to a URI nothing
  publishes to — and SC01's central assertion is that *nothing reaches MMS*, which would then
  pass for the wrong reason.
- **The participant channel list is what actually binds.** `ScenarioChannels.RequirePublisher`
  and `InboxPump` read the PersonalityPack; `setup.channels` only provisions and asserts. A
  scenario renamed without its pack produces a run where nothing arrives and nothing says why.
- **The sandbox iTwin federation id is a literal repeated in four files.** It should become a
  single shared constant when the remaining scenarios convert.

---

## DR-017 — ENG adopts a supplied FederationGuid and mints only as a fallback

**Status:** Decided
**Date:** 2026-08-20
**Context:** `EngSegmentsBuilder` derived a UUID via `CcomUuid.FromKey(iModelId, code)` when an
element had no `FederationGuid`, and the sandbox ENG UI minted one server-side with no way for an
operator to supply the identity the entity already had elsewhere.

### Decision

An element without a `FederationGuid` is not published; it is filtered out of the drain with a
warning, and the marker still goes. `Segment.UUID` is the `FederationGuid` and `IDInInfoSource`
carries ENG's `ECInstanceId` registered against it. The sandbox ENG UI accepts an operator-supplied
FederationGuid with a Suggest button, and refuses a value already held by another segment with a 409.

### Why

- **The derived-UUID fallback was wrong in an expensive way.** It is stable, so it looks correct
  for as long as nobody federates the element. The day someone does, the same pump arrives
  downstream under a second identity with nothing to indicate it was ever one thing. Publishing
  nothing is recoverable; publishing a fabricated identity is not.
- **Adoption beats minting when the entity is already identified.** A tag register or handover
  sheet already holds an id; minting a fresh one produces two identities for one pump.
- **A pasted id that is already in use must be refused.** Copy-paste is the workflow, and pasting
  the wrong row is how it fails — silently, because two segments sharing an identity look fine in
  ENG and only surface downstream, where a receiver upserting on the identity has the second
  overwrite the first and reports no error either.
- **Suggest mints server-side** so a suggested id uses the same version-7 scheme as one ENG
  assigns itself. A client-side v4 would look identical to a reviewer but not be time-ordered.

### What this leaves open

- **Rule 1 of the FederationGuid guideline says the iModel assigns the FederationGuid and no
  other system creates or overrides it.** The UI change is in tension with that. Either the
  guideline admits brownfield adoption (example 5 gestures at it) or the UI is reconciled with
  the rule. Tracked in [open-items.md](open-items.md).
- **The two ENG implementations still diverge.** `EngService` (sandbox) mints via
  `ITagIdentityService`; `EngProvider` (`ElementUpsert`) accepts but never mints. Only one of
  them is governed by anything the guideline says.
- **The composite ENG key was thought to be missing from the wire.** Superseded by DR-018:
  three of the four parts were already travelling, and the fourth is now fixed.

---

## DR-018 — The ENG composite key travels in CCOM's identity fields, not in invented elements

**Status:** Decided
**Date:** 2026-08-20
**Context:** DR-017 recorded that rule 2 of the
[FederationGuid guideline](FederationId/federation-guid-guideline.md) was unmet — that only
`ECInstanceId` reached the wire. Reviewing the actual BOD against
[SyncSegmentsWithoutAttributes.xml](Sample%20BODs/SyncSegmentsWithoutAttributes.xml) showed
that was wrong.

### Decision

ENG's composite key maps onto CCOM's existing identity fields:

| ENG value | CCOM field |
|---|---|
| FederationGuid | `Segment/UUID` |
| iModelId | `Segment/InfoSource/UUID` |
| ECInstanceId | `Segment/IDInInfoSource` |
| CodeValue | `Segment/ShortName` |
| UserLabel | `Segment/FullName` |

Three of these were already correct. `InfoSource/UUID` was not: it carried
`CcomUuid.ForInfoSource("ENG")`. It now carries the iModelId. `Segment/Type` was given its
own `InfoSource` rather than sharing the segment's.

### Why

- **The guideline's XML sketch was illustrative and became load-bearing.** It showed
  `<FederationGuid>`, `<IModelId>`, `<ECInstanceId>`, `<CodeValue>` and `<Action>` as
  elements. CCOM defines none of them; a BOD in that shape fails `CCOM.xsd` validation. The
  sketch caused a review to conclude the implementation was broken when the implementation was
  closer to right than the document describing it.
- **A hash of the string "ENG" names the kind of system, not the instance.** It is a
  well-formed, stable UUID that looks correct on inspection and is useless: it cannot say
  which iModel element 44732 lives in, which is exactly what rule 8 promises a CIR holder can
  determine. This is the same failure class as DR-017's derived UUID — a fabricated identifier
  that passes every check except being true.
- **Sharing the InfoSource with `Segment/Type` would corrupt the key.** Once `InfoSource/UUID`
  means "the iModel", reusing it for the EC class asserts that the class name and the
  `ECInstanceId` are two identifiers within one source; a receiver composing the composite key
  from that pair builds one that resolves to nothing.
- **Action codes are collection-level in OAGIS.** `Add`/`Remove` per segment is not
  expressible, so an add and a removal are two BODs.

### What this leaves open

- **The BOD had no test coverage before this change.** 113 tests passed while
  `EngSegmentsBuilder` was entirely unexercised, so the suite was not evidence for a wire
  change. `EngSegmentsBuilderTests` now pins the mapping and parses through `BodEnvelope`, the
  path a receiver takes. Other builders may be in the same position and have not been checked.
- **Nothing validates a published BOD against `CCOM.xsd`.** The schemas are in `schemas/ccom/`
  and the mapping is now asserted field by field, but no test asserts the document is
  schema-valid. That is what would have caught the guideline's shape immediately.
- **`InfoSource/IDInInfoSource` carries the readable iModel code in the documented example but
  the engine does not populate it.** Only the UUID travels. Harmless, but a human reading the
  message sees a bare GUID.

---

## DR-019 — Rule 1 admits brownfield adoption; rule 4 has ENG and ALIM both register

**Status:** Decided
**Date:** 2026-08-20
**Context:** Verifying the implementation against the
[FederationGuid guideline](FederationId/federation-guid-guideline.md) surfaced two places
where the code and the guideline disagreed, each of which could have been resolved in either
direction.

### Decision

**Rule 1** now admits brownfield adoption. Where an entity already carries an identity in a
tag register, handover sheet or predecessor system, ENG adopts that value rather than minting
a new one. The sandbox ENG authoring UI is the sanctioned path. Adoption happens at creation
and once only; rule 7 immutability applies thereafter.

**Rule 4** now has ENG and ALIM both register against the same CIRID, rather than ALIM
registering solely on ENG's behalf. ALIM continues to register the ENG composite key as
ENG-IMODEL because it holds the full key from the BOD.

### Why

- **Minting over an existing identity creates the duplication the guideline exists to
  prevent.** A greenfield-only rule 1 would have forced the UI to be removed, and the
  brownfield case — the common one — would then acquire a second identity for a thing that
  already had one. Example 5 already gestured at this; the rule now says it.
- **A single registrar is a single point of silent failure.** Rule 5 makes registration order
  irrelevant and §3.1.2 makes a second assertion of a held CIRID a no-op, so both registering
  is convergent, not conflicting. With one registrar, a failure at that participant leaves the
  identity absent from the registry with nothing positioned to notice.
- **The alternative for rule 4 was to keep ENG publish-only for simplicity.** Rejected: the
  simplicity is real but buys little, since ENG already holds the key it would register and
  the registry tolerates the duplicate assertion by design.

### What this leaves open

- **Neither registration is implemented on the ECSchema path.** No ENG-IMODEL entry is
  written by either participant. `SyncEngAsync` does register, but from the sandbox `Tag`
  table — the sandbox ENG personality, not `EngProvider`. ENG proper is ECSchema-based and
  has elements with a `CodeValue`, not tags with a `TagNumber`; a tag is REG-LOCATION's
  ALIM-side counterpart (`Element.CodeValue` = `Tag.Code`). The sandbox path is therefore
  not the registration rule 4 means, and rule 8's "direct iModel navigation" claim does not
  hold until the ECSchema path registers. Tracked in [open-items.md](open-items.md).
- **Two ENG implementations with different vocabularies coexist.** `EngService`/`SyncEngAsync`
  on `Tag`, and `EngProvider` on `EngElement` per the ECSchema. Only the latter is the design
  going forward. Whether the sandbox path is retired or explicitly marked a simulator is
  undecided, and the ambiguity has already caused one misreading.

---

## DR-020 — The stewardship gate lives in REG-LOCATION; the engine reacts to it

**Status:** Decided
**Date:** 2026-09-01
**Context:** SC01 requires that segments reach operations only once a steward has accepted the
proposed tags. The question was where that gate sits, and an early reading put it in the
integration layer — the engine would hold approved work and release it.

**Decision.** Approval is a state transition on `dbo.tags.state` inside `RegLocationProvider`,
performed by `POST tags/{tagId}/approve`. `RegLocationEngine` has no gate of its own: it learns
that a decision was taken and carries the result onward.

### Why

- **An engine-side gate would mean approvals only counted while the integration was running.**
  A steward's decision is a fact about the registry and has to survive the engine being stopped,
  redeployed or removed entirely. Holding it in engine state would make the customer's own
  governance a property of somebody else's middleware.
- **Approval is not an edit.** It has its own route rather than a `state` field on
  `PUT tags/{tagId}`, because releasing a tag to operations and correcting its spelling carry
  different authority. One route doing both would mean anything permitted to rename a tag was
  also permitted to release it.
- **The transition is conditional in SQL.** `ApproveTagAsync` updates only where
  `state = 'Proposed'` and distinguishes a missing row (404) from an already-decided one (409),
  so two stewards racing cannot both be told they decided.

### The notification is thin, and delivery is best-effort

`IApprovalNotifier` posts identity and provenance only — tag id, code, revision, GUID, scope,
who decided and when. The engine reads the tag back before publishing.

- **A fat notification would become a second, worse copy of the registry.** It is a claim about
  a past moment; the registry is the only thing that knows the present one. A tag whose approval
  was reversed between send and receipt must not still reach the channel.
- **Delivery never fails the steward's request.** The approval is committed before the notify
  call, and `HttpApprovalNotifier` swallows transport failures. Making the registry's
  availability depend on an integration it does not own would be the wrong trade, and it is
  exactly the coupling the provider/engine split exists to prevent.
- **Because delivery can be lost, `SweepAsync` exists.** A missed event is missed forever; a
  sweep that is behind merely catches up. The two paths are not redundant — the notification
  makes the engine prompt, the sweep makes it correct.

### Consequences

- REG-LOCATION acquires exactly one outward-facing seam, configured by
  `RegLocation__ApprovalNotificationUrl` and defaulting to `NullApprovalNotifier`. It still
  knows no topic, no BOD and no channel.
- Publication is idempotent by construction: the BOD is a `Replace`, and engine state keys the
  published set by federation GUID *plus revision*, so a repeat is a no-op while a genuine
  revision still travels.
- The engine publishes under `domain = operations` rather than `engineering`. The same
  `SyncSegments` on the same iTwin means a different thing once a steward has accepted it, and
  a subscriber that wants only accepted locations could not otherwise tell them apart.
- `RegLocationEngine` holds no `ProjectReference` to `RegLocationProvider`, mirroring
  `EngEngine`/`EngProvider`. The shared record shapes are duplicated deliberately, so a breaking
  change to the provider's API surfaces here as a decision rather than a silent recompile.

