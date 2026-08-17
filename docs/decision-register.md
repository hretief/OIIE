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

- **Untested.** There is no harness for the dispatcher: `SimHost.Tests` builds `ParticipantDbContext`
  against a dummy connection string for model inspection only, and there are no fakes for
  `IIsbmClientAccessor`, `IBodBuilder` or `IIsbmSessionStoreAccessor`. The guard is unexercised on the happy
  path by construction, since it only fires on a retry after a lost confirmation.
- **The index needs a day-zero reset to exist.** `ParticipantSchemaInitializer` short-circuits on the
  `Message` sentinel table, so the composite index is absent from any database created before this change.
  The guard still works — it is a query, not a schema dependency — but is not index-backed until then.

