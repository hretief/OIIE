# Open items

Pending work carried between sessions. Decisions belong in
[decision-register.md](decision-register.md); this file is only for things not
yet done.

## Bridge free-text iTwin `Type` to the normalised `iTwinType` key

**Status:** deferred by decision, not blocked. Raised 2026-09 while giving the
site type a stored identity.

`dbo.iTwinType` now holds the boundary a twin is drawn around — `iTwinTypeId`
(the value published as `Site.Type.UUID`) and `Number` (its name) — and
`dbo.iTwin.iTwinTypeId` references it. Storing the identity rather than deriving
it from the name is what lets a boundary be renamed without reclassifying every
site already published under it.

The unresolved half is that `Type` is **free text in the source**. Nothing
maps an *unrecognised* incoming string onto an `iTwinTypeId`. A string that
matches `iTwinType.Number` now resolves (see below); one that does not leaves
the reference null, and a twin with a null boundary is skipped by the publisher.
This is not
biting today because the iTwin Platform exposes no UI for `Type`, so the value
never varies: `District` is bootstrapped in `schema.sql` and every twin in the
demo carries it. The bridge is unnecessary for as long as that holds.

It stops holding the moment a twin can arrive with a boundary nobody seeded. The
decision to make then is what an unrecognised string should do:

- **Auto-create** an `iTwinType` row on first sight — permissive, matches the
  old mint-on-first-sight behaviour, and means a typo silently becomes a second
  boundary with its own identity.
- **Reject, or leave the reference null** — the vocabulary stays curated, an
  unrecognised boundary is visible rather than absorbed, and the twin is skipped
  by the publisher until someone classifies it.

The second is the stronger position given that the whole point of storing the id
was identity stability, but it needs somewhere for a human to add a boundary,
which the demo does not have. Worth settling before ENG ingests twins from
anywhere other than the sandbox UI.

Note `UQ_iTwinType_Number` already prevents two rows sharing a name, so whichever
way this goes, the split-identity failure is caught at the database rather than
discovered downstream in REG-LOCATION.

### `dbo.iTwin.[Type]` duplicates `dbo.iTwinType.Number`

Raised 2026-09 while conforming the SyncSites payload to the sample. **Payload
half resolved; the column duplication remains.**

The boundary's *name* is stored twice:

- `dbo.iTwin.[Type]` — `NVARCHAR(100)`, the free-text value as it arrived from
  the platform, kept from before the type table existed.
- `dbo.iTwinType.Number` — the same name, on the row the twin now references
  through `iTwinTypeId`.

Nothing keeps the two in step. `UQ_iTwinType_Number` guards the normalised copy
only, so the denormalised one on the twin can drift from the row it is supposed
to be naming, and a rename applied to `iTwinType.Number` leaves every
`iTwin.[Type]` stale.

What has been done: the provider now resolves `iTwinTypeId` by matching
`[Type]` against `iTwinType.Number` on upsert, and projects that row's `Number`
onto the twin record as `ITwinTypeNumber`. `EngSitesBuilder` publishes
`Site.Type.ShortName` from the projection, so all three published `Type`
elements now come from the same row and cannot disagree. `[Type]` remains as the
raw as-received string but no longer reaches the wire.

What remains: `[Type]` is still a second home for the name, and it is still the
input the resolution matches on, so a value that drifts from `iTwinType.Number`
silently stops resolving and leaves `iTwinTypeId` null. The column should
probably go once the question above is settled — if unrecognised boundaries are
rejected, `[Type]` has no remaining job; if they are auto-created, it is the
value the row gets minted from and has to survive until that row exists.

## Rename the sandbox's ENG "tag" vocabulary to "element"

**Status:** not started. Raised 2026-09 while routing element authoring through
the ENG provider.

ENG has no tags. `EngFunctions` says so explicitly — *"There is no /tags route
either. An element is what an engineer calls a tag, so a second route for it
would be a second name for one thing"* — and the schema holds `dbo.Element`. The
UI is also already correct: it says "segment" throughout, and `api.ts` notes the
wire types are the odd ones out.

The wrong name is confined to the layer between them:

- `/admin/eng/tags` (GET and POST) in `SandboxAdminEndpoints`
- `listTags` / `createTag` / `Tag` / `NewTag` / `CreatedTag` / `TagList` in
  `WorkflowOrchestration/src/api.ts`
- `EngService.AddTagAsync` / `ListTagsAsync` and the `Tag` entity in
  `Oiie.Sandbox.Core`, plus the sandbox's `eng.Tag` table

Mostly mechanical, but not purely cosmetic: three names for one concept is why
the save-to-one-store, read-from-another defect took so long to see. Renaming
the route is a breaking change to any saved scenario that posts to it, so sweep
`Scenarios/` at the same time.

Note the sandbox participant's own `eng.Tag` table may keep the name if the
Sandbox is modelling a system that genuinely calls them tags — the decision to
make is whether the Sandbox is rehearsing ENG (rename) or standing in for a
process-industry tag register (keep, and rename only the API surface).

## Decide whether vocabulary translation belongs in the participant engines

**Status:** open question, not yet a plan. Raised 2026-09.

Related to the item above, and the more interesting half of it. The UI currently
talks to the Sandbox in one vocabulary and the Sandbox translates to each
participant's own — but that translation is spread across the admin endpoints
and the provider adapters rather than living anywhere nameable.

If the UI is meant to be talking to a semantic-layer abstraction that resolves
into local participant vocabulary, then this logic belongs in the participant
engines, not in the UI and not in the Sandbox API. That would also make the
`rdl:*` versus `ENG.*` split above a translation concern rather than a
two-stores concern, and might dissolve it.

Worth settling before the ENG schema work, since it changes where that work
lands.

## Remove the unconfigured-provider fallback

**Status:** not started. Raised 2026-09.

`ProviderEndpointOptions.IsConfigured` lets the Sandbox serve the ENG and
REG-LOCATION panels from its own participants when no provider URL and key are
set, so a clone with no Azure access still runs every panel.

Judged unnecessary overhead: the sandbox is not expected to run without secrets.
The cost is that every ENG path has two implementations to keep in step, which is
what allowed authoring to write one store while the list read another. Removing
it means deleting the `SandboxEngSource` / `SandboxRegLocationSource` branch in
`Program.cs` and failing loudly at startup when configuration is absent.

Check first whether any test or scenario depends on the fallback binding.

## Extend the ENG schema so it can own classification

**Status:** not started. Deferred deliberately 2026-09; rationale in
[2026-09-eng-classes-two-stores.md](decision-records/2026-09-eng-classes-two-stores.md).

Classification currently lives in two stores. The ENG panel's `CLASS KEY`
dropdown reads the Sandbox participant DB (`rdl:*`, from `classes.yaml`), while
ENG elements are classified against `acme-db-eng-dev.dbo.ECClass` (`ENG.*`). The
intent is for ENG to become the system of record, with the Sandbox seeded from
it at day zero.

It cannot be done yet: `dbo.ECClass` holds a name, a schema and an inheritance
edge, and `schema.sql` notes that *"ECProperty is not modelled"*. Three things
the Sandbox model provides have nowhere to live in ENG — the `Aspect` vs
`Taxonomy` distinction (`ClassModifier` is not the same concept), `ClassProperty`
requirement levels with min/max bounds, and the §6.5.4 narrowing rules.

**Prerequisites, in order:**

- Add property and aspect tables to `EngProvider/Infrastructure/Sql/schema.sql`,
  with the element-to-property path going through the element's assigned
  `ECClassId`.
- Extend `ENG_BOOTSTRAP.SQL` and `SqlEngDesignStore` to carry them.
- Rename `rdl:*` to `ENG.*` across `PersonalityPacks`, `ClassificationResolverTests`,
  `CcomAttributeMapper` and REG-LOCATION — cross-cutting, and the main cost.
- Reverse the flow: have `ClassFixtureLoader` (or a replacement) read from the
  ENG provider rather than from YAML.

**Do not** simply repoint the dropdown at `/api/classes` as an interim step. The
per-participant asymmetry is load-bearing — REG-LOCATION holds fewer classes than
ENG on purpose, and giving every participant ENG's full vocabulary removes the
degraded-binding and unbound-proposal behaviour the sandbox exists to show.

## Verify CIR interaction against the FederationGuid guideline

**Status:** partly done 2026-08-20. Rules 1, 2, 3, 5, 6, 7, 9 resolved; rules 4
and 8 outstanding.

[federation-guid-guideline.md](FederationId/federation-guid-guideline.md) states
how FederationGuid is meant to become the CIRID. The review is done; what follows
is what it found and what remains.

**Resolved:**

- **Rule 3 holds.** `CirRegistrationService` uses `ProcessRegistry` with
  `entry.CIRID` set to the FederationGuid, and `SqlCirStore.MergeCiridAsync`
  stores it as-is. §3.1.2 prevents a supplied CIRID overwriting a held one, so
  rules 5, 6 and 7 are enforced by the registry rather than by convention. ENG
  and REG-LOCATION assert the same UUID and converge without a steward.
- **Rule 2 holds, after a fix.** The guideline's XML sketch was illustrative and
  wrong — CCOM has no `FederationGuid`, `IModelId`, `ECInstanceId` or `CodeValue`
  element. Three of the four parts were already travelling correctly; only
  `InfoSource/UUID` was wrong, carrying a hash of the string `ENG` rather than the
  iModelId. Fixed and covered by `EngSegmentsBuilderTests`. See DR-018.
- **Rule 1 amended.** The guideline now admits brownfield adoption of an existing
  FederationGuid, with the sandbox ENG UI as the sanctioned path.

**Outstanding:**

- **Publication does not filter to the functional branch.** A tag corresponds to
  `Functional.FunctionalElement` and its descendants, not to `BisCore.Element` generally.
  But `EngPublicationService` filters a marker's contents only by
  `IsPublishable` (`FederationGuid is not null`), and `GetNamedVersionElementsAsync` selects
  every element at the marker position regardless of class. Today every concrete ENG class
  in `ENG_BOOTSTRAP.SQL` derives from `FunctionalComponentElement`, so the two sets coincide
  and nothing misbehaves — but that is a property of the current seed data, not a constraint
  the code enforces. Adding one physical or geometric class would start publishing segments
  that have no tag counterpart, and REG-LOCATION would raise stewardship proposals for
  geometry. Decide whether the filter belongs in the SQL (`vNamedVersionElement` joined to
  the class hierarchy) or in `IsPublishable` via `FullyQualifiedECClassName`. `EngElement`
  already carries the class name, so the engine-side check is available without a schema
  change.

- **Rule 4 — no ENG-IMODEL entry is registered from the ECSchema path.** Decided that ENG
  and ALIM both register against the same CIRID. Neither does so from the real design yet.
  `SyncEngAsync` registers from the sandbox `Tag` table, which is the *sandbox* ENG
  personality, not `EngProvider`/ENG ECSchema — ENG proper has elements, not tags. That
  sandbox path is superseded by the ECSchema decision, so it is not the registration this
  rule means. REG-LOCATION already retains the FederationGuid, ECInstanceId and CodeValue
  from the inbound BOD, and with DR-018 can also read the iModelId from `InfoSource/UUID`,
  so the composite key is reconstructable on the ALIM side — but nothing registers it.
  Rule 8's "direct iModel navigation" claim does not hold until something does.
- **Confirm the sandbox `Tag`-based ENG personality is being retired.** `EngService` and
  `SyncEngAsync` operate on `Tag`/`TagNumber`; `EngProvider` operates on `EngElement`/
  `CodeValue` per the ECSchema. Two implementations, different vocabularies, and only the
  ECSchema one is the design going forward. Decide whether the sandbox path is retired or
  kept as a simulator, and mark it so — it currently reads as if it were ENG.
- **No BOD is validated against `CCOM.xsd`.** The schemas are in `schemas/ccom/`.
  A schema-validity assertion would have caught the guideline's invented shape
  immediately, and would guard every future BOD change.
- **CIR writes are invisible when the step before them fails.** `acme-db-cir-dev`
  was empty for some time and read as a wiring or wrong-database problem; the
  database and connection string were correct all along. Site registration in CIR
  is step 11 of `SiteIngestionService`, and the run was aborting at step 7, so
  nothing ever reached CIR. The ingest report said only `failed: 1` — the actual
  SQL error was in the provider's telemetry, one hop away. Worth making the
  report carry the failure reason rather than a count, since the natural reading
  of an empty registry is that the registry is misconfigured.

## ENG spine tables leak into every participant schema

**Status:** diagnosed, not fixed. Agreed 2026-08-19 to document only.

`cms` and `mms` each physically contain `Tag`, `TagRelationship` and
`NamedVersion` tables. They are always empty, nothing reads or writes them, and
no participant but ENG has any use for them.

They are an EF modelling artefact rather than a design decision, which makes the
comment on `ConfigurePersonality` currently false:

> conditioning on the schema here gives each participant exactly its own tables
> — reg_asset never learns that eng.Tag exists, which matches the database grants
> rather than merely coexisting with them.

### Cause

`ParticipantDbContext.OnModelCreating` applies the twin query filters
unconditionally, before `ConfigurePersonality` runs:

```csharp
modelBuilder.Entity<Tag>().HasQueryFilter(...);              // ~line 90
modelBuilder.Entity<NamedVersion>().HasQueryFilter(...);     // ~line 93
modelBuilder.Entity<TagRelationship>().HasQueryFilter(...);  // ~line 96
```

`modelBuilder.Entity<T>()` *creates* the entity type as a side effect, so all
three enter every schema's model. `ParticipantSchemaInitializer` builds tables
straight from that model via `IRelationalDatabaseCreator.CreateTablesAsync`, so
every schema gets them — with default conventions rather than the `ToTable` names
and indexes `ConfigureEng` would have applied, which is the tell.

### Fix

Move the three `HasQueryFilter` calls inside `ConfigureEng`. `ConfigureEng` is
`static` and the filters read the instance property `ITwinId`, so it must also
drop `static`.

Cheap and low risk: no call site changes anywhere. Every ENG read already goes
through `db.Set<Tag>()` rather than a `DbSet` property, and there are no
`DbSet<Tag>`, `DbSet<TagRelationship>` or `DbSet<NamedVersion>` members on the
context to update. The stray tables are empty by construction, so nothing is
lost by no longer creating them.

Worth adding a `ContextOwnershipTests` case asserting the `cms` and `mms` models
contain no `Tag` entity type, since the failure is silent and would otherwise
regress unnoticed.

## Scenario 11 tables are sandbox-only support tables

**Status:** decided to retain, 2026-08-19. Recorded so the reasoning is not
relitigated.

Removing these was considered and rejected. `EquipmentRecord`, `WorkOrder`,
`AssetInstallationEvent`, `MonitoredLocationRecord` and `MonitoredAssetRecord`
have no customer counterpart, which reads as a fidelity problem against the rule
in `Oiie.Sandbox.Core/PersonalityPacks/README.md` that mapped participants map
customer tables column-for-column.

They are better understood as **support tables**: state a participant needs to do
its job that the customer's own schema does not carry. Under the participant
abstraction described in [A required abstraction over participants](#a-required-abstraction-over-participants)
below, each function app fronts the customer tables and may legitimately own
support tables of its own. That is the frame to settle before renaming or
deleting any of these.

Reasons not to delete them now:

- **DR-009 already ruled on it** and says they are retained, marked sandbox-only,
  and are the first thing to revisit when the real work-order tables are known.
  Reversing that needs a superseding DR, not a quiet deletion.
- **It would remove a working capability, not dead weight.** `sc11-asset-install`
  is 232 lines of executable scenario asserting real semantics: sign-off rather
  than completion triggers publication, event time is distinct from message time,
  the receiver's history is append-only, and identity arrives unresolved. Deleting
  the tables means deleting `MmsWorkOrderService`, `MmsAssetSegmentEventsBuilder`,
  `CmsAssetSegmentEventsHandler`, their DI registrations, the `ContextOwnershipTests`
  cases over the three CMS entities, and `/admin/cms/assets`, which reads
  `MonitoredAssetRecord`.
- **The spec counts Scenario 11 as phase-1 exit criteria** ("scenarios 1, 2 and 11
  pass in CI end to end").

### Correction to DR-009

DR-009 groups `LocationRelationshipRecord` with `EquipmentRecord` and `WorkOrder`
as Scenario 11 constructs. That grouping is wrong on this point:
`LocationRelationshipRecord` is written by `MmsSegmentConnectionsHandler` during
**SC02**, the verified handover path. Scenario 11 only asserts it as a
precondition. It is sandbox-only in the sense of having no customer table, but it
is not SC11-only and deleting it would break SC02.

### Marker coverage

DR-009 requires these be explicitly marked sandbox-only in their doc comments.
Currently only the MMS ones are: `EquipmentRecord`, `WorkOrder` and
`LocationRelationshipRecord` carry the note. The three CMS entities —
`AssetInstallationEvent`, `MonitoredLocationRecord`, `MonitoredAssetRecord` — do
not, and should.

`cms.ASSET_CLASS` is a related but distinct case: it *is* customer schema and is
mapped and foreign-keyed from `ASSET`, but is never populated, because
`CmsSegmentsHandler` deliberately leaves `AssetClassId` null on a placeholder.
Real, correct, and simply not yet exercised.

## Test coverage for the outbox idempotency guard

**Status:** not started. The guard itself is implemented and deployed (DR-011);
only its coverage is outstanding.

`OutboxDispatcher.PostAsync` now refuses to republish an item that already has an
outbound `MessageRecord`. Nothing exercises it. It cannot be reached from the happy
path by construction — it only fires on a retry after a lost confirmation — so the
absence of failures says nothing about whether it works.

The obstacle is that there is no harness for the dispatcher at all. `Oiie.Sandbox.Tests`
builds `ParticipantDbContext` against a dummy connection string for model inspection
only, and there are no fakes for `IIsbmClientAccessor`, `IBodBuilder` or
`IIsbmSessionStoreAccessor`. Writing the first real test for a background service is
most of this task; the assertion is the small part.

Cases worth covering once a harness exists:

- **Duplicate suppressed:** an item whose `CorrelationId`/`Verb`/`Noun` already has an
  outbound record is marked `Posted` against that record and never posted again.
- **Same correlation, different noun:** two items sharing one `CorrelationId` — the
  `Segments` / `SegmentMeshConnections` pair `RegLocationService` queues — must *both*
  publish. This is the regression the guard was specifically shaped to avoid and the
  one most likely to reappear if anyone "simplifies" the match to correlation id alone.
- **First attempt:** no prior record means a normal post, with the guard invisible.

To force the behaviour manually in the meantime: reset a posted outbox item to
`Pending` while leaving its `Message` row in place, and watch for the "already posted
… closing it against that record" warning instead of a second publication.

## A required abstraction over participants

**Status:** specified 2026-08-20 in [participant-abstraction-spec.md](participant-abstraction-spec.md)
and DR-013. Nothing implemented.

**Scope reduced after review.** The spec now covers only the in-process split of
handlers into a pure transformer plus a customer writer, preserving the single
transaction. The Function App extraction described below is **deferred**, along
with `/describe`, a generic query contract, and declarative YAML pipelines. Each
is recorded with its trigger condition in §11 of the spec.

The reason for deferring is specific: separate deployables would split one
transaction across two stores, so customer rows and provenance could diverge — a
failure mode that is currently impossible — and would multiply cold-start
surfaces, which is what caused the LTP-4 incident (DR-012). The text below
records the original ambition, not the current plan.

The goal is that any repository can participate as long as it implements an
agreed interface, with each participant eventually deployable as its own Function
App: an HTTP trigger answering the ISBM webhook, reading its own session,
ingesting, and exposing the list endpoints its UI panel needs.

The Function App is the participant's interface to the customer tables it fronts.
It may also own **support tables** — state it needs to do its job that the
customer's schema does not carry. Support tables are a legitimate part of a
participant, not a fidelity defect, provided they stay behind the interface and
are never mistaken for customer schema. The Scenario 11 tables above are the
current example.

### What already exists

Half of this is built. `IBodHandler` and `IBodBuilder`
(`Oiie.Sandbox.Core/Application/Bods/IBodBuilder.cs`) are exactly the ingest
contract: `(Verb, Noun)` plus an optional `ParticipantId`, resolved by
`InboxPump` with participant-specific precedence and a generic fallback. CMS,
MMS and REG-LOCATION implement it and know nothing about each other, so a new
repo implementing `SyncSegments` already participates without an orchestrator
change. `personality.yaml` is already the capability manifest.

CIR is already an external Function App with its own sessions and reset
endpoint, so the deployment pattern is proven in this system.

### What is missing

**The read surface has no contract at all.** `/admin/cms/customer-assets`,
`/admin/mms/locations`, `/admin/reg-location/stewardship` and `/admin/eng/tags`
are four hand-written endpoints with four response shapes, four twin-scoping
implementations and four filter conventions. The stewardship `?state=` gap
existed precisely because nothing said a participant must expose its lifecycle
states.

Do **not** answer this with one `IListThings`. CMS holds assets, MMS holds light
systems, ENG holds segments; a single typed list method either collapses to
untyped dictionaries and throws away the domain modelling, or becomes a union
type that lies about every participant. What is genuinely common is narrower:

- **Twin scoping** — every participant answers "what do you hold for this iTwin"
  and every one must resolve through ws-CIR rather than match a column (DR-008,
  DR-009). `CmsContextResolver` and `MmsContextResolver` exist; REG-LOCATION has
  no sibling and each was written separately.
- **The unresolved answer** — MMS returns `resolved:false` with a reason, CMS
  returns `unresolvedContext` with a `detail`, REG-LOCATION does not model it.
  One concept, three shapes.
- **Collection discovery** — a participant declaring which collections it offers,
  their states and whether twin-scoping applies, so the React app renders panels
  from the declaration. Today every panel needs an `App.tsx` change.
- **Health and identity** — participant id, schema, declared channels, session
  status.

### Sequencing

The Function App boundary is the last step, not the first: crossing a process
boundary converts every design flaw into a distributed-systems problem.

1. Define the read contract in-process and refactor the four endpoint families
   onto it.
2. Render UI panels from participant-declared collections.
3. Extract exactly one participant — CMS is the best candidate, being a pure
   consumer with no publish path — and leave the rest in-process.
4. Generalise only after that one survives.

### Known obstacles

- `InboxPump` is a central loop polling *on behalf of* participants. The whole
  point of the inversion is that it dissolves, and control moves to each
  participant reacting to its own webhook.
- `IBodHandler.HandleAsync` takes a `ParticipantDbContext` — an in-process
  dependency handed in by the orchestrator. Across a process boundary that must
  become the participant's own concern. **That signature change is the crux of
  the design.**
- The ISBM webhook is a notification, not a delivery: the trigger still has to
  open/peek/read/remove, and a missed webhook must stay recoverable by a timer
  trigger. Both are needed.
- Webhook-triggered functions scale out, so two instances can be triggered for
  one session concurrently, and ISBM read/remove is not transactional with the
  SQL write. Handlers must be idempotent on message id (`MessageRecord` already
  archives by id) or concurrency must be pinned to one.
- Sessions are long-lived and provider-stored; a cold-starting function must
  reattach rather than re-open, or it leaks sessions.
- Every cross-participant resolution becomes a network hop. Scoped MMS reads are
  already ~1.9s (DR-010); distributing this makes it worse before better.
- `ParticipantDbContextFactory` centralises connection strings and schema
  binding. Split out, each function owns its own connection and managed
  identity — arguably more correct, but a provisioning change too.

## Endpoint-level tests for the MMS read path

**Status:** not started. Agreed at the end of the 2026-08-14 session, deferred so
the demo could be walked through first.

The 88 tests in `tests/Oiie.Sandbox.Tests` are structural: they assert schema
fidelity and identity semantics. That is the right thing for them to do, and it
is also why they could not catch either bug found on 2026-08-14. Both were in
the read/response path rather than in the registry logic:

1. `CirClient.ResolveAsync` returned an empty equivalence list on a cache hit,
   so `MmsContextResolver` reported a successfully related twin as unrelated.
2. `/admin/mms/locations` resolved the owner correctly, used it to filter, and
   then omitted it from the response.

Neither is reachable from a structural test, so add coverage over
`/admin/mms/locations` for:

- **Scoped:** a twin related to an owner that has inventory returns only that
  owner's rows, with `ownerId` and `ownerName` populated. Metro Traffic
  (`OWNER_ID` 2) and District 6 (`OWNER_ID` 8) are the only seeded owners with
  inventory, at 9 and 4 rows of the 13.
- **Unscoped:** no `twin` parameter returns all 13 rows with a null `ownerId`.
  This is the case that distinguishes a filtered result from an unfiltered one.
- **Unresolved:** a twin with no relation returns zero rows plus a reason, and
  never falls back to returning everything. Filtering failure must not leak one
  district's inventory to another.
- **Cache hit:** resolution stays correct on a second call. This is the specific
  regression from bug 1 above and the one most likely to reappear, since the
  CIRID is cached but the equivalence set deliberately is not.

An owner with no inventory is worth a case too: District 1 (`OWNER_ID` 4) is
related but has zero rows, which must read as resolved-but-empty rather than
unresolved.

## Latency of scoped MMS reads

**Status:** diagnosed, not fixed. Deferred by the user on 2026-08-14 to be
picked up later. Full evidence and ruled-out causes in DR-010.

A scoped `/admin/mms/locations?twin=…` takes ~1.9s to return 9 rows; the same
endpoint unscoped returns 13 rows in ~450ms. The cause is not the MMS table or
its indexing but a single ISBM round trip per scoped read, and within that, the
hardcoded 2s Service Bus receive timeout in
`ISBMProvider/Infrastructure/ServiceBusMessageBroker.cs`:

```csharp
var recv = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2), ct);
```

### Where to start

`PeekNextAsync` is the only receive call site in the provider, so the change is
small in size but not in blast radius. The 2s wait is *correct* for genuine
asynchronous consumers — `InboxPump` and `IsbmBodListener` long-poll on purpose,
and shortening it globally turns their efficient blocking reads into a busy spin
against Service Bus, raising cost and throttling risk.

So the shape of the fix is a per-call wait, not a smaller constant:

1. Thread a receive timeout through `PeekNextAsync` (config-backed, defaulting
   to the current 2s so subscriber behaviour is unchanged).
2. Pass a short wait only on the consumer-request read path, which is
   synchronous request/response and has a caller waiting on it.
3. Redeploy `ISBMProvider`. This cannot be validated locally, which is the main
   reason it was not done in the same session as the diagnosis.

Re-measure with the timings above as the baseline: unscoped ~450ms is the floor,
since it does no ISBM round trip at all.

### Do not

Cache the equivalence set to dodge the round trip without reading DR-008 and
DR-010 first. It looks like the obvious shortcut and it reintroduces the bug
where a successfully related twin reads as unrelated — the CIRID is cacheable,
the equivalence set is not, because MMS reads `OWNER_ID` out of it and has
nowhere local to store one.

## Asserted CIRIDs are untested

**Status:** shipped without coverage, `2aa2771`. Needs tests before `CmsEngine`
depends on the behaviour.

`CirRegistrationService` now asserts a caller-supplied identity as the CIRID
rather than letting the registry mint one, so that every system receiving the
same BOD registers under a single identity instead of each acquiring its own.
All three registration paths guard the assertion:

```csharp
if (site.SiteUuid != Guid.Empty)       // ~line 164
if (tag.FederationId != Guid.Empty)    // ~line 593
if (location.FederationId != Guid.Empty) // ~line 733
```

`Oiie.Sandbox.Tests` passes 99/99 against this, but nothing in it reaches the
registration path — the run confirms the change broke no existing behaviour, not
that the new behaviour is correct. The closest case,
`ContextOwnershipTests.Cms_site_retains_the_publisher_uuid_without_naming_it_a_twin`,
asserts only that the model retains the UUID.

### What to cover

Two cases per path, the second being the one that matters:

1. **Identity supplied** — entry carries `CIRID` equal to the supplied UUID, so
   two participants registering the same subject converge rather than producing
   two identities with nothing relating them.
2. **Identity absent** — entry leaves `CIRID` unset so the registry mints. These
   identifiers are non-nullable `Guid`, so absence arrives as `Guid.Empty`; the
   guards exist because asserting it would file *every* identity-less subject
   under one shared CIRID. That failure is silent and would corrupt the registry
   rather than throw, which is why it needs a test rather than a comment.

Worth asserting the merge rule from §3.1.2 alongside these: an existing CIRID
wins, so an assertion cannot overwrite an identity the registry already holds.
That property is what makes the change safe, and it is currently only claimed in
a comment.

## CMS and CIR deploy scripts still require a manual SQL grant

**Status:** fixed for ENG 2026-08-23, not yet applied to CMS or CIR.

`CmsProvider/deploy/deploy-functionapp.ps1` and
`CirProvider/deploy/deploy-functionapp.ps1` finish by *printing* the grant the
app needs rather than performing it:

```
CREATE USER [acme-id-<sys>-<env>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [...];
ALTER ROLE db_datawriter ADD MEMBER [...];
ALTER ROLE db_ddladmin  ADD MEMBER [...];
```

Both print `Deployed.` in green while the app cannot actually reach SQL, so the
deployment looks successful and health then reports
`Login failed for user '<token-identified principal>'`. This is what happened on
the ENG dev deployment and cost a round of manual debugging.

### Why it was left manual

`FROM EXTERNAL PROVIDER` resolves the principal through the directory, which
requires the SQL *server* to hold the Directory Readers role — only a Global
Admin can grant that. Neither script can assume it.

### Fix

Use the approach already proven in `deploy/cir/deploy.ps1` and now in
`EngProvider/deploy/deploy-functionapp.ps1`: create the user from the identity's
client ID as a SID, which needs no directory permission at all.

```powershell
$sidBytes = ([guid]$identityClientId).ToByteArray()
$sidHex   = '0x' + (($sidBytes | ForEach-Object { $_.ToString('X2') }) -join '')
# EXEC(N'CREATE USER [$identityName] WITH SID = $sidHex, TYPE = E');
```

Run it with `Invoke-Sqlcmd -AccessToken` from
`az account get-access-token --resource https://database.windows.net/`. The
caller must be an Entra admin on the server, which is a far lower bar than
Directory Readers. See the ENG script for the full block, including the retry
loop for a serverless database still resuming.

Three further points carried over from the ENG fix, all worth taking at the same
time:

- **Restart and verify, don't just claim success.** The schema bootstrap runs
  once at startup, so on a first deployment it runs *before* the grant exists
  and fails without taking the host down. The app must be restarted after the
  grant and `/api/health` polled until it answers 200, otherwise the script
  reports success over a broken app.
- **`func azure functionapp publish` is unreliable.** It failed repeatedly on
  ENG with `Timed out waiting for SCM to update the Environment Settings` even
  with SCM basic auth enabled, and `az functionapp deployment source config-zip`
  answered 502. `dotnet publish` plus a POST to the Kudu
  `api/publish?type=zip&isAsync=true` endpoint works and drops the Core Tools
  dependency.
- **Check `Invoke-Sqlcmd` up front.** Failing on a missing `SqlServer` module
  after the infrastructure has been created is the wrong order.

`db_ddladmin` is only needed while `<Sys>__AutoCreateSchema` is true; it should
come back off once each schema is settled.

## Finish wiring RegLocationEngine into a running SC01

**Status:** updated 2026-09-02. Both legs are deployed and the inbound leg has
now been run end to end against live Azure, including site registration and CIR.

`RegLocationEngine` carries both directions of SC01. Inbound, it subscribes to
ENG's channel, unpacks `SyncSegments` and files each segment as a `Proposed` tag
in `RegLocationProvider`. Outbound, a steward approves a tag, the provider
notifies the engine, the engine reads the tag back, publishes `SyncSegments` to
ISBM and registers the entry in CIR. See DR-020 for why the gate sits in the
provider rather than the engine.

Since verified live: SyncSites publishes from ENGEngine, `SiteIngestionService`
creates the scope, item and serial, links the scope's context to the serial, and
registers the site in CIR under category `ITWIN-SITE`. Segments file against the
scope resolved from `RegistrationSite.UUID` rather than a static fallback.

What remains:

- **The ENG-class-to-`class_id` map is guesswork.** `InboundClassMap` seeds
  `Functional:FunctionalComponentElement` to `1001` (`rdl:FunctionalLocation`),
  but nobody has confirmed that is the intended correspondence. It has to be
  configuration because the two sides share no joinable value: ENG names its EC
  class in `SegmentType/IDInInfoSource`, while `class_objects` has no name column
  and its seeded rows have null GUIDs. Giving classes GUIDs on both sides would
  turn this into a lookup and is the better long-term fix.
- **An unmapped class silently lands on the fallback.** Logged at warning and
  filed under `InboundFallbackClassId` rather than rejected, so a wrong mapping
  table produces plausible-looking tags of the wrong class rather than an error.
  A steward is the only thing that would catch it. Live runs are hitting this
  constantly — every segment so far has logged the fallback warning, so the map
  is not merely unconfirmed, it is unused.
- **`CreateTagRequest.State` still defaults to `Approved` in the provider.** The
  inbound leg passes `Proposed` explicitly, so it is correct today, but the safe
  behaviour depends on every future caller remembering. The default should
  probably be inverted, with the bootstrap seed stating `Approved` out loud.
- **Inbound identity matching is GUID-only, first-proposal-wins.** A segment
  whose GUID the registry already holds is ignored entirely, whatever state that
  tag is in. That makes redelivery harmless and never reopens a steward's
  decision, but it also means a genuine revision from ENG will not update
  anything. Revision handling is unimplemented.
- **A message that fails to file blocks the queue.** The drain stops rather than
  skipping ahead, and the publication stays on the channel. Correct for ordering
  and for not losing data, but a permanently bad message needs manual removal.
- **`GetApprovedTagsAsync` filters client-side.** It reads `tags` and keeps the
  approved ones in memory. Fine at demo scale and wrong at registry scale; a
  by-state route on the provider would fix it, but "approved" is nearly every row
  so the route needs paging before it is worth adding.
- **The CIR category is invented.** The engine writes entries under registry
  `{enterprise}` / category `FunctionalLocation`. Whether that matches what ENG
  registers on the ECSchema path is unverified, and if the two disagree the
  CIRIDs will not converge — which is the one thing the FederationGuid guideline
  exists to guarantee. Related to rules 4 and 8 above.
- **The outbound leg is still unexercised.** Approve → publish → CIR has not been
  run live; only the inbound direction has.
- **No integration test crosses the boundary.** The unit tests pin the mapping
  and the publish gate, but nothing exercises publish → subscribe → propose →
  approve → publish. That test needs a live provider and broker, so it belongs
  with the E2E suite rather than in `Oiie.Sandbox.Tests`.
- **`SegmentIngestionService` itself is untested.** Session reuse, the
  remove-after-success ordering, and the stop-on-failure behaviour are exercised
  by nothing — the same gap `EngPublicationService.DrainAsync` has.
- **Engine option defaults are not validated against the provider's schema.**
  Two defaults in `RegLocationEngineOptions` were silently wrong until a live run
  hit them: `SiteItemType` was `"Site"` against a `CHAR(1)` column, and
  `SiteTrnId` was `1` when bootstrap seeds only `trn_id 0`. Both surfaced as a
  bare `failed: 1` in the ingest report with the real cause only in the
  provider's exception telemetry. See the CIR item below.

## Remove the sandbox database (CMS and MMS remain)

**Status:** planned, not started. See DR-022 for the decision and reasoning.

The sandbox database holds three unrelated things, and DR-022 removes all three: emulated
participant data, messaging machinery, and scenario run state. The messaging tables are **deleted
rather than migrated to blob/table storage**, because `EngEngine` and `RegLocationEngine` already
publish and ingest without an outbox.

Staged so the demo works at every checkpoint:

1. Build the interactive ENG → REG-LOCATION flow while the current system still runs.
2. ~~Drop `AddSandboxMessagePumps()` from the API host.~~ **Done.** The pumps no longer run; build
   and the 128 sandbox tests pass without them. `AddSandboxMessagePumps` is retained but uncalled,
   so restoring it is a one-line change if the engines turn out not to cover something.
   **Consequence now live:** `MmsWorkOrderService` still enqueues to its outbox and nothing drains
   it, so MMS outbound is dormant. Accepted — the ENG → REG-LOCATION handover never reaches MMS —
   and it lifts when MMS gets a provider and engine of its own.
3. Delete `OutboxDispatcher`, `InboxPump`, and the messaging tables.
4. Retire `ScenarioRunner`, the scenario YAML, `SandboxDbContext`, the `/admin/scenarios*` routes,
   the `AdminKeyMiddleware` entries and the `Program.cs` bootstrap.
5. Remove the emulated ENG and REG-LOCATION schemas.

## Panel isolation: one panel, one provider

**Status:** decided, not started.

The demo represents each participant as a customer system with its own API surface, so a panel
should reach its own provider and nothing else. `SandboxAdminEndpoints.cs` is currently a single
~2400-line admin surface shared by every panel, including cross-participant reads, which lets a
panel see what its customer system would have no way of knowing.

Splitting it is not cosmetic: the point being demonstrated is loose coupling, and a shared endpoint
file quietly contradicts it. Blocked behind nothing, but larger than it looks — the reads need
attributing to a participant before they can be separated.


**What remains after all of it:** CMS and MMS are still DB-backed, deferred by decision. So the
sandbox still has a database when this work is done — the footprint is reduced, not eliminated, and
"no sandbox database" needs the CMS and MMS provider adapters first.

Two things to carry into the work:

- **The interactive flow must mint a `FederationGuid`.** `EngPublicationService` publishes only
  elements that have one, and skips the rest with a warning; a marker containing none is recorded
  as published and never retried. An operator can otherwise author a segment, cut a version, see
  success, and get nothing at REG-LOCATION. `ElementUpsert` accepts the guid but ENG never invents
  it, and rule 1 above already names the sandbox ENG UI as the sanctioned path for supplying it.
- **Retiring the runner loses the negative assertions.** Nothing will automatically prove the
  stewardship gate has not leaked to MMS; that becomes a matter of inspection.

## Decide how a segment arriving before its site is handled

**Status:** open question, not yet a plan. Raised 2026-09 while wiring
`RegistrationSite` through SyncSegments.

Settled already: a location cannot be registered into a site the participant does
not hold. `SegmentIngestionService` looks a scope up by the sender's
`RegistrationSite.UUID` and never creates one — `SiteIngestionService` is what
establishes a scope, and creating one on both legs would mint two scopes for a
plant that has one. Filing into a fallback scope is worse still: it succeeds
visibly and collects several plants into one, which no later correction can
separate.

What is *not* settled is what happens next. Today the segment is counted as
`SiteUnknown`, logged, and its message is left on the channel so a later drain
files it once the site arrives. That is safe but open-ended:

- **Nothing guarantees the site ever arrives.** SyncSites is a separate
  publication on a separate leg. If it is never sent, the segments retry forever
  and the only symptom is a counter nobody is watching.
- **The channel is not a queue with a dead letter.** A message that can never
  succeed is indistinguishable from one that has not succeeded *yet*, and the
  engine has no notion of "tried long enough".
- **Ordering is not guaranteed and probably should not be.** Requiring SyncSites
  to precede SyncSegments would couple two independent publications; the
  alternative is for the receiver to tolerate either order indefinitely, which is
  what it does now by accident rather than by decision.

Candidates, none chosen: have REG-LOCATION expose pending-site segments so a
steward can see them; have the engine request the site from ENG on a miss (which
makes the inbound leg a client of the sender, a real change to the shape of the
thing); or accept the current retry and add an alarm on `SiteUnknown` so the
condition is at least visible.

Worth settling alongside the SyncSites leg, since whichever way it goes the two
legs stop being fully independent.

## Trigger SyncSites from ENG rather than by hand

**Status:** open question blocking a small implementation. Raised 2026-09 after
fixing the two option defaults that were stopping site ingest.

An element reaching REG-LOCATION is already event-driven end to end: creating a
named version in ENGProvider fires `INamedVersionNotifier`, which posts to
ENGEngine's `engine/events/named-version-created`, which publishes SyncSegments.
Choosing an iTwin should work the same way, and the receiving half already does
— `EngEngineFunctions` has `bootstrap/itwin-created` taking an `ITwinCreatedEvent`,
and it publishes SyncSites when called.

The missing piece is only the emit. Nothing in ENGProvider's iTwin write path
notifies anyone, so today SyncSites is published by hand or waits for the timer.
The work is to generalise `INamedVersionNotifier` into an event notifier with a
second method (or add a sibling), emit post-commit from the iTwin route exactly
where `CreateNamedVersion` emits, and add `ITwinNotificationUrl` /
`ITwinNotificationKey` beside the named-version pair. The timer stays as the
backstop for the same reason it does on the element leg.

**The question to settle first:** what counts as the trigger. An iTwin with no
site type is skipped by the publisher — `PROBE-7` is skipped today for exactly
this reason — so if the event fires the instant a twin is picked, and its type is
assigned afterwards in the UI, the event fires while the twin is still
unpublishable and the poller quietly does the real work. The two options:

- **On insert.** Simplest, and honest about what happened: a twin was added. But
  the normal case for an untyped twin then depends on the backstop, which is the
  thing the trigger exists to avoid relying on.
- **On becoming publishable** — insert *or* the assignment of a site type. More
  code, and the emit is no longer a single call site, but the event then means
  "this twin can be published", which is what the receiver actually needs.

Leaning toward the second. Note also that a naive emit on every upsert would
republish on each UI refresh, so whichever is chosen must fire on the transition,
not on every write.

## Service Bus entity names are opaque against the channel convention

Raised 2026-09 while diagnosing orphaned subscriptions.

The ISBM channel URI convention in
[isbm-channel-naming-convention.md](ISBM%20Channels/isbm-channel-naming-convention.md)
is followed correctly: engines derive `/{enterprise}/{federation-id}/{domain}/{type}`
from options rather than from pasted configuration. The gap is one layer below.

`EntityNaming` maps each channel URI onto a Service Bus entity by SHA-256, so
`/acme/enterprise/sites/publication` becomes the topic `pub-e1eee839f3560dac`,
and a durable subscriber id becomes `sub-{hash}`. The hash is necessary — entity
names cannot contain `/` and are length-limited, so the URI cannot be used
verbatim — and it is stable, which is what matters functionally.

What it is not is legible. The mapping is one-way, so nobody reading the Service
Bus namespace can tell which channel a topic serves without recomputing the hash
by hand. That is exactly what had to be done to diagnose the orphaned
subscriptions, and it turned a five-minute question into a much longer one. It
will be worse for anyone who was not present when the convention was written.

Two cheap improvements, neither yet done:

- **Stamp the channel URI into the entity's `UserMetadata`** when
  `EnsureTopicAsync` / `EnsureQueueAsync` create it. The broker already accepts
  arbitrary metadata, and `BootstrapChannelsAsync` sets the readable name on the
  ISBM channel description for the same reason — the namespace becomes
  self-describing at no runtime cost.
- **Document the mapping in the naming-convention document.** It currently stops
  at the ISBM layer, so the first person to open the Service Bus namespace hits
  the same confusion. A short section naming the three prefixes (`pub-`, `req-`,
  `resp-`, plus `sub-` for durable subscribers) and the hash rule would close it.

Neither changes behaviour, so this is operability rather than correctness.


## Day zero leaves the ingest engines with dead subscriptions

Raised 2026-09, seen three times while verifying day zero.

Day zero deletes and recreates the channels, which destroys the Service Bus
subscriptions behind them. `RegLocationEngine` caches its session id in a field,
so the next `ingest-sites` call reaches for a receiver whose subscription no
longer exists and the drain returns **500 with an empty body**. It recovers only
when the engine is restarted, and until then the whole SyncSites leg is dead.

The receiver-eviction fix in `ServiceBusMessageBroker.PeekNextAsync` handles the
case where a *single* entity vanishes: it catches
`MessagingEntityNotFound`, drops the cached receiver and retries once. It does
not help here, because the durable subscription was removed wholesale and
recreating the receiver finds nothing to attach to. The engine is still holding
a session id the broker has forgotten.

Day zero's response already warns about this for foreign systems — *"Any system
still holding a session on one will keep polling an id the broker has
forgotten; restart it or have it re-open"* — but the REG-LOCATION and ENG
engines are not foreign, they are part of the same estate, and nothing restarts
them.

Options:

- **Have day zero restart the engines it breaks.** Honest and immediate, but
  couples the sandbox to a list of Function Apps it otherwise does not manage.
- **Make the engines re-open on a session fault.** Catch the not-found on read,
  discard the cached session id, and open a new subscription session. This is the
  more general fix: a session can also be lost to broker maintenance or a
  channel deleted by something other than day zero, and the engine should
  survive that without human intervention.
- **Have day zero close the engines' sessions before deleting channels.** Only
  works for sessions it can enumerate, which is why it does not already.

The second is the strongest: it fixes the class rather than the instance, and
the engine already treats a redelivered message as safe, so re-opening costs
nothing but a repeated read.

Until it is done, a day zero must be followed by restarting
`acme-engn-reglocation-dev` and `acme-engn-eng-dev`, or the first ingest after
the reset will 500.

## Day zero wipes ENG's EC classes and nothing restores them

Raised 2026-09 during a manual test: `GET /classes` returned `[]` on the ENG
provider.

Day zero resets ENG by running `drop.sql` then `schema.sql`. `schema.sql` seeds
`dbo.iTwinType` but not `dbo.ECClass` — the EC schemas, classes and inheritance
live in `docs/DDL/ENG_BOOTSTRAP.SQL`, which nothing in the runtime applies. Only
`EngHostFixture` does, for the E2E tests.

The RUNBOOK described applying it as a one-off "only on a freshly provisioned
ENG database". That was true when the ENG database was provisioned once; day
zero now freshly provisions it on every run, so the manual step is required
every time and the documentation did not say so. It does now.

The failure is quiet in the way that matters. `GET /classes` returns `200` with
an empty array rather than an error, so the ENG panel's class dropdown is simply
blank and authoring an element fails later on `FK_Element_ECClass` — an error
naming a constraint rather than the absent seed.

Options, in preference order:

- **Embed the EC seed in `EngProvider`'s `schema.sql`.** EC classes are
  reference data the provider cannot function without, not demo content, so the
  argument for keeping them outside the schema is weak. `ResetAsync` already
  runs `schema.sql`, so this needs no new call site.
- **Embed `ENG_BOOTSTRAP.SQL` as a third resource** and have `ResetAsync` apply
  it after `schema.sql`. Keeps seed data separate from DDL, at the cost of a
  third embedded resource and an ordering rule.
- **Add an `/eng/bootstrap` route** day zero calls. Most explicit, but puts the
  sandbox in charge of a provider's reference data.

Note the script also seeds a demo iTwin (`1111…`), iModel (`2222…`) and a root
Element. Those are **not** wanted on every reset — twins should arrive through
the UI or SyncSites — so whichever option is taken must seed the EC half only.
The EC-only extract is lines 83-273 of the script.

Applied manually for now.
