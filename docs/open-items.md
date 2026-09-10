# Open items

Pending work carried between sessions. Decisions belong in
[decision-register.md](decision-register.md); this file is only for things not
yet done.

## Our TaxonomySet BODs diverge from the official MIMOSA schemas

**Status:** not started. Raised 2026-09-10 after the official `*Taxonom*.xsd`
files were added to `schemas/ccom/BOD/Messages/Configuration/`, which can now be
compared against the reconstructions in `schemas/ccom/BOD/Messages/New/`.

The reconstructions were built when no published XSD could be found, and were
deliberately emitted into the sandbox extensions namespace
(`http://www.openoandm.org/sandbox/extensions/1.0`) rather than CCOM's, precisely
so they could not be mistaken on the wire for future official BODs of the same
name. That call now pays off: the official BODs have landed and nothing we
publish collides with them. Nothing is broken today, so this is debt to schedule
rather than a defect to fix.

MIMOSA published nine files — `Taxonomies`, `TaxonomySets` and
`TaxonomyConnections`, each with `Get`, `Show` and `Sync`. Our reconstruction
covers one cell of that grid. The divergences:

- **Plural nouns.** Official is `GetTaxonomySets` / `ShowTaxonomySets`; ours is
  singular, so the topic `OIIE:S35:V1.0/CCOM:GetTaxonomySet:R1.0` is wrong
  against the real BOD name.
- **The request is a filter language, not a selector.** This is the substantive
  gap. Official `TaxonomySetsCriteria` is built from `CCOMQuery.xsd` filter types
  (`UUIDFilter`, `TextFilter`, `UTCDateTimeFilter`) with AND-within /
  OR-across-criteria semantics, plus `InfoSourceUUID`, `InfoSourceShortName`,
  `InfoSourceTypeUUID` and a `countOnly` mode. Ours is plain-typed fields. This
  should be replaced rather than patched.
- **Base type.** Official extends `oa:BusinessObjectDocumentType` and includes
  `CCOMQuery.xsd` (request) or `CCOMElements.xsd` (response); we hand-rolled the
  BOD attributes and included `CCOM.xsd`.
- **Response shape is close.** Both flatten `Type` + `Taxonomy` + `TaxonomySet`
  side by side, so that instinct was right. Official adds `Count`
  (`cct:NumericType`) for `countOnly`, and types the first element as `BaseType`
  where we used `Type`.
- **Our `Version` / `AsOf` have no official home.** We invented
  `TaxonomySetResponseType` extending `TaxonomySet`; the CCOM type is just
  `Entity` + `Nameable` + `Taxonomy`. `RdlTaxonomyValidator` has no other
  freshness signal, so migrating needs somewhere for these to go.
- **`Sync` is unmodelled.** All three families have a push verb; our RDL flow is
  request/response only.

The open decision is whether to migrate to the official BODs — renaming to
plural, adopting `TaxonomySetsCriteria`, moving into the CCOM namespace, dropping
`Version`/`AsOf` — or to keep the sandbox BODs and carry this as known
divergence. Migrating moves `TaxonomySetBods.cs`, `TaxonomySetResponder`,
`TaxonomySetRequestListener`, `RdlTaxonomyValidator`, the topic strings and the
tests together, so it wants its own session rather than being folded into other
work.

Also stale once this is settled: `schemas/ccom/BOD/Messages/New/*.xsd` and
`schemas/sandbox/*.xsd` declare `https://www.mimosa.org/ccom4` where CCOM uses
`http://`. Harmless today because nothing validates against those
reconstructions, and `Namespaces.Ccom` in code is correct.

## `RegLocationEngine` is pinned to a single `iTwinFederationId`

**Status:** not started. Raised 2026-09-08 after a day zero produced no CIR or
REG-LOCATION entries with no error.

`RegLocationEngine` reads `iTwinFederationId` from app settings and derives its
inbound channel from it, so `GET engine/status` reported

    inboundChannelUri: /acme/523099d2-.../engineering/publication

while ENG had published the promoted element to the newly created twin
`d543ebf6-...`. The engine was subscribed to the channel of a twin that day zero
had destroyed, so it read zero messages indefinitely.

This is silent by construction: an empty channel and a wrong channel are the same
observation. It cost most of a session to find, because the ENG side of the
publish looked correct in isolation and only a side-by-side comparison of the two
channel URIs showed the mismatch.

The engine needs to follow the twins that actually exist rather than one baked in
at deploy time -- either by subscribing per site as sites are ingested (it already
derives per-site URIs in `SiteIngestionService`), or by being reconfigured as part
of day zero. Until then, every day zero requires the setting to be updated by hand
and the engine restarted.

Worth considering alongside it: `engine/status` should say when its configured
twin is one no participant is publishing to, so this reports itself instead of
presenting as silence.

## Sandbox deploys are additive, leaving deleted files alive in dev

**Status:** not started. Raised 2026-09-08. To be fixed when MMS is done and
again when CMS is picked up.

`cms/personality.yaml` was deleted from `Oiie.Sandbox.Core/PersonalityPacks/` but
still exists in `site/wwwroot/PersonalityPacks/` on `acme-api-sandbox-dev`,
confirmed over Kudu: the deployed app has `cms`, `eng`, `mms` and `reg-location`
while source has only the latter three. Deployment overlays files without removing
ones that have gone, so the pack has survived every redeploy since.

The consequence is a live `ParticipantRegistry` containing a participant that
exists nowhere in the repository. Its declared channels are
`/OIIE-SANDBOX/Enterprise/Site/OandM` and `.../OandM-Events`, which is why day
zero recreates `OandM-Events` and stamps it "OIIE Sandbox day zero" despite no
source file declaring it. Anything iterating `registry.All` -- channel purge,
rebuild, provisioning -- silently acts on the phantom.

`git status` cannot show this: the divergence lives entirely in deployed state.

Two things to settle when CMS is picked up:

- Whether `cms` should have a pack in source as `mms` does. It is deployed and
  running (`acme-api-cms-dev`, `acme-engn-cms-dev`) and `sc02` still declares it
  as a subscriber, so its absence from the packs may be the accident rather than
  its presence in `wwwroot`.
- Making the deploy clean rather than additive, which is a separate defect and
  true regardless of how the first question is answered.

## `CmsEngine` opens its sites subscription without a `subscriberId`

**Status:** not started. Raised 2026-09-07 while fixing the same fault in
`MmsEngine` (DR-027).

`CmsEngine/Application/SiteIngestionService.cs` calls
`OpenSubscriptionSessionAsync(_options.SitesChannelUri, _options.SitesTopics, ct)`
with no `subscriberId`. `_sessionId` is a field, so it is lost on every restart
and redeploy, and without a stable id the broker mints a fresh subscription each
time — stranding whatever the previous one had not yet read. This is exactly the
fault that stopped `MmsEngine` ingesting a new site, where it presented as
`messagesRead: 0` with no error.

The fix is the one-line pattern `RegLocationEngine` and now `MmsEngine` use:
`subscriberId: $"{_options.SourceId}:sites"`. Left out of the DR-027 change to
keep it scoped.

While there, check `CmsEngine`'s site mapper against `Site.FullName`. ENG only
began publishing `FullName` in DR-027; if the CMS mapper reads it, it was
receiving nothing, and if it reads `ShortName`, confirm that is what CMS
actually wants to name an owner by.

## `acme-engn-reglocation-dev` has no Application Insights telemetry

**Status:** not started. Raised 2026-09-06 while debugging DR-025.

Querying `traces`, `requests`, and even an unfiltered `union requests, traces`
over the last two hours against the app's own Application Insights component
returned zero rows, despite the timer functions genuinely running (confirmed
independently via `GET engine/status` and by triggering `POST
engine/ingest` on demand). Either the connection string is missing or
misconfigured, or telemetry is being sampled away entirely.

This made the `IngestEnabled=false` bug (DR-025) far harder to diagnose than it
should have been — the timer's own `logger.LogError` for a failed drain would
never have been seen either, since it goes through the same pipe. Worth
auditing `APPLICATIONINSIGHTS_CONNECTION_STRING` across all four engine apps
(`acme-engn-eng-dev`, `acme-engn-reglocation-dev`, `acme-engn-cms-dev`,
`acme-engn-mms-dev`) and confirming each actually emits telemetry, not just that
the setting is present.

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

- **No common vocabulary travels in `SegmentType` — this now blocks MMS.** See
  DR-030. ENG publishes its own EC class name, so every consumer needs a private
  map back from ENG's vocabulary. REG-LOCATION absorbed that with
  `InboundClassMap`; MMS makes the cost plain, because its table names *are* its
  classes and an unmapped type has no generic table to fall back to. The fix is
  for `SegmentType` to carry a governed RDL key that each participant maps to and
  from at its own edge. Two things are needed and only the first is blocking:
  agreement on the key vocabulary, and eventually the RDL participant itself
  (Technical Specification §237/§468) for definition propagation.
  - **Largely resolved by DR-030's 2026-09-09 addendum.** ENG now publishes
    `rdl:Streetlight` for its one mapped class, and as of
    2026-09-10 `RdlTaxonomyValidator` checks the configured keys against the live
    library over ISBM. What remains is breadth: one class is agreed, and MMS still
    has no segments leg consuming `InboundRdlTableMap`.
  - **Planned resolution: map out-of-band, pre-load CIR, drop the code match.**
    The per-participant class maps are transitional. The intended end state is a
    complete ENG→RDL mapping performed out-of-band as part of establishing the
    ENG-RDL relationship, pre-loaded into CIR as
    `(IDInSource=ENG.Streetlight, SourceID=ENG, CIRID=<RDL class GUID>)` entries.
    Participants then resolve by registry lookup and match on the class UUID, so
    the string code stops being load-bearing and a rename in RDL no longer breaks
    the binding. **Half done as of 2026-09-11:** `CirClassResolver` performs the
    lookup and ENG publishes the resolved CIRID as `Type.UUID`. The seeding half
    now exists too: with `RegisterClassIdentityInCir` on, a miss makes ENG ask RDL
    for the class and register the mapping with RDL's own class GUID as the CIRID,
    so the registry fills itself from the first drain that meets an unmapped class.
    Both switches are off by default. What remains is breadth — one class is
    mapped — and Obstacle 3: REG-LOCATION still keys on the string code, so the
    coordinated switch to matching on UUID is still ahead.
  - **Which RDL class is a MnDOT light unit?** A MIMOSA/OIIE modelling decision,
    not a coding one, and the MMS slice waits on it.
    `rdl:FunctionalLocation` (1001) is probably wrong; `rdl:Equipment` (1701) is
    nearer, since a light unit is physical plant rather than a location.
    Answered provisionally for the light-unit slice as `rdl:Streetlight` (1703,
    a child of 1701); the formal governance process is still to come.
- **The ENG-class-to-`class_id` map is guesswork.** `InboundClassMap` seeds
  `Functional:FunctionalComponentElement` to `1001` (`rdl:FunctionalLocation`),
  but nobody has confirmed that is the intended correspondence. Note the original
  justification — that the two sides share no joinable value — is now stale:
  `class_objects` carries `code`, `name`, `description` and `parent_class_id`, and
  its seeded codes are already `rdl:`-prefixed. Once the wire carries RDL keys
  this becomes an ordinary lookup against `class_objects.code` and the map retires.
- **An unmapped class silently lands on the fallback.** Logged at warning and
  filed under `InboundFallbackClassId` rather than rejected, so a wrong mapping
  table produces plausible-looking tags of the wrong class rather than an error.
  A steward is the only thing that would catch it. Live runs are hitting this
  constantly — every segment so far has logged the fallback warning, so the map
  is not merely unconfirmed, it is unused.
- **Unresolved: tags are landing on `class_id = 1001` when 1703 was expected.**
  Observed 2026-09-10 on two elements added through ENG. Both mapping tables are
  correct — `OutboundRdlClassMap` has `ENG.Streetlight → rdl:Streetlight`,
  `InboundClassMap` has `rdl:Streetlight → 1703` — and both sides agree on the
  wire contract (`EngSegmentsBuilder` writes `Type.IDInInfoSource`,
  `IncomingSegmentMapper` reads it). 1001 is `InboundFallbackClassId`, so the
  fallback fired, which means the segment arrived with **no `SegmentType` at
  all**: `EngSegmentsBuilder` omits the element entirely when
  `ResolveRdlClassKey` returns null.
  - **Most likely cause**, per DR-030's addendum: the shipped test element is a
    `Bis:PhysicalElement`, which is deliberately unmapped so it exercises the
    degradation path. If the two new elements are anything other than
    `ENG.Streetlight`, the fallback is doing its documented job and the fix is a
    map entry, not a wire-format change.
  - **To confirm**, against the ENG database:
    `SELECT ECInstanceId, CodeValue, FullyQualifiedECClassName FROM dbo.vElement ORDER BY CreatedUtc DESC;`
    The view builds the lookup key as `CONCAT(SchemaName, '.', ClassName)`, so
    that column is exactly what `OutboundRdlClassMap` is keyed on. Either
    engine's warning log settles it too.
  - **Note this is orthogonal to the UUID-vs-code question** in DR-030. If no
    `Type` element is published, neither matching strategy would help.
- **`CreateTagRequest.State` still defaults to `Approved` in the provider.** The
  inbound leg passes `Proposed` explicitly, so it is correct today, but the safe
  behaviour depends on every future caller remembering. The default should
  probably be inverted, with the bootstrap seed stating `Approved` out loud.
- **Inbound identity matching is GUID-only, and a correction opens a new
  revision.** Resolved by DR-028: a segment is compared against the highest
  revision on file for its GUID. Identical content is ignored, a still-`Proposed`
  row is corrected in place, and a change to an already-decided row is filed as a
  new row at `revision = max + 1`. Outbound publishes only the highest revision
  per GUID. What remains open is that matching is still GUID-only, so a location
  republished under a *new* GUID is a new location as far as the registry is
  concerned.
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

## Register the deployed origin in the IMS client's allowed CORS origins

Raised 2026-09 while testing sign-in on the deployed sandbox. **Not fixable from
this repository.**

After `<origin>/signin-oidc` was registered as a redirect URI, sign-in reached
IMS and came back, but the browser-side token exchange in
[auth.ts](../WorkflowOrchestration/src/auth.ts) then failed on
`POST https://ims.bentley.com/connect/token` with no `Access-Control-Allow-Origin`
header. The signature is `net::ERR_FAILED` reported alongside status `200`: IMS
answered, the browser discarded the response.

Redirect URIs and allowed CORS origins are **separate lists** on the IMS client
registration. Registering the first does not populate the second, which is why
fixing the `invalid redirect_uri` error surfaced this one rather than resolving
it. `https://acme-api-sandbox-dev.azurewebsites.net` needs adding to the allowed
CORS origins by someone with access to the Bentley IMS client registration.

Nothing in this solution can set that header — it is emitted by IMS, not by the
sandbox — so there is no code change that would work around it. Note also that
renaming the Azure site changes the origin and breaks both lists at once.

## Redeploy the sandbox UI to ship the runtime admin-key bundle

Raised 2026-09 alongside the admin-key rotation. **Done** — recorded because the
Vite inlining trap is easy to reintroduce.

The admin key was previously set as `VITE_SANDBOX_ADMIN_KEY` in
`WorkflowOrchestration/.env.local`, and Vite inlines every `VITE_` variable into
the bundle it builds. Because that bundle is served from the API's own
`wwwroot`, the key was readable by anyone who loaded the site.

The app now reads the key from `sessionStorage` and prompts for it at runtime,
`.env.local` no longer sets it, and [deploy.ps1](../deploy/sandbox/deploy.ps1)
clears the variable for the build and refuses to publish if the key still
appears in the output. The cleaned bundle has been deployed and the leaked key
was rotated in `mndot` and in the app settings, so the old value is dead.

Anyone adding a secret to a `VITE_` variable will ship it to the browser again.
The build guard catches only the admin key, by value.

## Key Vault recovery does not restore role assignments

Raised 2026-09 after `mndot` was soft-deleted during cleanup and recovered.
**Resolved for the sandbox; recorded because the trap is not specific to it.**

`az keyvault recover` brings back the vault and its secrets but **not** the role
assignments on it. Every managed identity that read from the vault silently
loses access, and the vault looks healthy from the portal.

For the sandbox this presented as `ContainerStartupFailure`, exit code 134,
because `AddSandboxKeyVault` in
[SandboxCoreRegistration.cs](../Oiie.Sandbox.Core/Application/SandboxCoreRegistration.cs)
runs against `builder.Configuration` before the host exists — so the failure
aborts the process rather than surfacing as a failed request. Re-granting
`Key Vault Secrets User` to the web app's principal fixed it.

Two things worth carrying forward:

- Restoring **your own** access is not enough and actively masks the problem: the
  CLI starts working while the app is still locked out.
- While a restart is crash-looping, Azure keeps serving from the last good
  instance, so a corrected app setting appears not to have applied. Confirm the
  instance is actually running before concluding a config change was wrong.

The other identities that read `mndot` (the `isbm-*` consumers) have **not** been
audited. If anything else authenticated to that vault by managed identity, it
has the same missing assignment and will fail the next time it restarts.

## An ad-hoc role assignment blocks the next full infrastructure deploy

Raised 2026-09 while recovering from the Key Vault deletion. **Blocking, but
only for `-not $SkipInfrastructure` runs.**

`infra/sandbox/main.bicep` creates the API's `Key Vault Secrets User` grant under
a deterministic name derived from `guid(keyVault.id, apiApp.id, <roleId>)`. While
getting the app running again, the same role was granted to the same principal
from the CLI, which minted assignment `92ae537b-e84b-4479-8949-c129aeebb97a`
under a random name instead.

Azure treats that as a duplicate, so the Bicep deployment now fails with
`RoleAssignmentExists` naming that id. The sandbox deploys today only because
`-SkipInfrastructure` avoids the template.

The fix is to delete the ad-hoc assignment and let Bicep recreate it — the role
itself is correct, so nothing is broken until someone runs a full deploy. This
needs `Microsoft.Authorization/roleAssignments/delete`, which the account used in
this session does not have:

```
az role assignment delete --ids "<vaultId>/providers/Microsoft.Authorization/roleAssignments/92ae537b-e84b-4479-8949-c129aeebb97a"
```

The general lesson is that granting a role by hand at a scope Bicep also manages
converts an idempotent template into a failing one, because the two disagree
about the assignment's *name* while agreeing about its content.

## Every route the UI calls needs an entry in `AdminKeyMiddleware`

Raised 2026-09 after three separate route failures in one testing session.

`AdminKeyMiddleware` fails closed by design — an unlisted `/admin` route needs
the key. The Workflow Orchestration app is unauthenticated, so every route it
calls must appear in `ReadOnlyRoutes` or `UnauthenticatedWriteRoutes`, and
nothing enforces that. Adding a route to `api.ts` and forgetting the middleware
is a silent break found only by clicking the button.

Three were missing and have been added: `GET /admin/eng/imodels`,
`POST /admin/eng/imodels/sync`, and `GET /admin/eng/federation-id/suggest`. An
audit of `api.ts` found a fourth, `POST /admin/eng/itwins/add`, before it was hit.

Two things make this worse than it sounds:

- **The failure does not look like an auth failure.** The iModels lookup in
  `App.tsx` caught the 401 and set an empty list, which renders identically to a
  twin with no iModels. That catch now logs before clearing, but the same shape
  exists anywhere a fetch failure degrades to empty.
- **The exemptions are a temporary hole.** `UnauthenticatedWriteRoutes` exists to
  be deleted when real sign-in arrives, so every addition makes that removal
  larger. `itwins/add` is the one to look at first: it is additive like the rest,
  but it also provisions ISBM channels.

Worth a test that walks the routes referenced in `api.ts` and asserts each is
either exempt or deliberately guarded, so the list cannot drift again. The
guarded ones are legitimate — `/admin/reset/day-zero` is destructive and should
prompt — so the test has to encode intent rather than just presence.

## `-SkipInfrastructure` silently disabled the bundle leak guard

Raised 2026-09. **Fixed; recorded because the shape recurs.**

The admin key was resolved inside `deploy.ps1`'s `if (-not $SkipInfrastructure)`
block, but the guard that greps the built bundle for that key sits further down
and is written as `if ($adminKey)`. With `-SkipInfrastructure` the variable was
empty, so the check quietly did nothing — the switch people reach for when infra
is already in place also turned off the protection added hours earlier.

The lookup now runs unconditionally, before the infrastructure block.

The general defect is a safety check written as `if ($x)` where `$x` is populated
elsewhere: it degrades to a no-op rather than an error when the value is missing.
Other guards in these scripts are worth reading with that in mind.

## `/health` returns 404 on the deployed sandbox

Raised 2026-09 while probing the deployed API. **Low confidence, unverified.**

`GET https://acme-api-sandbox-dev.azurewebsites.net/health` returns 404, while
the deployment's own verification step passes and reports `participants: 4`,
`isbmConfigured: True`, `storage: True` — so the app is healthy and something
answers for it.

Most likely the probe used the wrong path and the real endpoint is elsewhere, in
which case the only thing to fix is the documentation that says `/health`. Worth
confirming which, since a health path that 404s is the kind of thing a monitor
gets pointed at and silently misreports.

## REG-LOCATION reads no segments from the engineering channel

Raised 2026-09-11 while chasing the day-zero symptom "neither classes nor
elements registered". **Resolved 2026-09-11.** Root cause and fix are recorded
at the end of this item; the narrative below is kept because it is the record of
how three wrong diagnoses were arrived at.

ENG publishes correctly — reset then drain gives `markersSeen 1,
markersPublished 1, segmentsPublished 2` — but every REG-LOCATION ingest returns
`messagesRead: 0`, including immediately after a reset with the subscription
opened before ENG published. `CirClassRegistrar` runs per ingested segment, so it
has never executed, which is why CIR holds ENG's `RDL-CLASS` row and not
REG-LOCATION's mirror, and why REG-LOCATION emits no traces at all.

The two reported halves are one defect. The `dbo.tags` rows (`LL-001`, `LL-002`,
both `class_id 1703`) are residue from an earlier successful run, so elements
looked registered while classes did not, and the fault appeared to sit in class
registration when it sits in message delivery.

Both sides agree on `/acme/{itwin}/engineering/publication` and on the topic, and
the channel exists on the broker. Two untested suspects:

- ENG's status reports its topic list duplicated
  (`["oiie:sc01/ccom:SyncSegments","oiie:sc01/ccom:SyncSegments"]`), which hints
  the publish-side topic construction is not what the configuration implies.
- The durable subscriber id may name a subscription orphaned when day zero
  recreated the channel. The recovery path for exactly that case exists in
  `SegmentIngestionService.DrainSiteAsync` but never fired — `sessionReopened`
  stayed `false` even on the poll after a REG-LOCATION reset.

Two prior diagnoses (a stale negative cache, then a reset/drain race) were wrong.
Both changes were kept on their own merits; neither addressed this. See the
decision register addenda of the same date, including how broken live readings
produced false evidence.

### Root cause and fix

The second suspect was right about the subscription and wrong about the cause.
Nothing orphaned the subscription: `TableSessionRegistry` never persisted
`SubscriberId`. It was set on `SessionMetadata` at open time and used to derive
the durable subscription name, but `RegisterAsync` did not write the column and
`ToSessionMetadata` did not read it, so every session rehydrated from Table
Storage came back with `SubscriberId` null. The broker then fell back to the
session id and read from `pub-<hash>|<sessionId>`, a subscription that has never
existed, while the real one accumulated the backlog unread.

Two changes, both in `ISBMProvider`:

- `TableSessionRegistry` now persists and restores `SubscriberId`, so a
  rehydrated session resolves to the same durable subscription it opened.
- `ServiceBusMessageBroker.PeekNextAsync` re-ensures the subscription before
  recreating the receiver on `MessagingEntityNotFound`. Previously it only
  evicted the cached receiver, so the retry hit the same missing entity and the
  fault surfaced as a 404 — indistinguishable from an empty queue to a caller.
  That is what made a permanent misconfiguration look like a channel that was
  merely quiet, and it is why the symptom read as `messagesRead: 0` for days
  rather than as an error.

`sessionReopened` stayed `false` throughout because the recovery path keys off a
session fault, and there was no session fault to detect: the read succeeded and
honestly reported an empty subscription. It was the wrong subscription.

Verified live after deploying the broker: reset and drain ENG
(`segmentsPublished 2`), then ingest returns
`messagesRead 1, segmentsSeen 2, alreadyKnown 2`, with a matching
`PostPublication 201` / `ReadPublication 200` / `RemovePublication 204` triple on
the same session id in broker telemetry. Subsequent polls return 0, which is now
a true empty rather than a masked fault.

The first suspect — ENG reporting its topic list duplicated — is cosmetic and did
not affect delivery; the subscription rule matches on a single topic either way.
It is left as a separate tidy-up.

## Class identity caches outlive the CIR registry they describe

Raised 2026-09-11, after the delivery fix above. **Resolved 2026-09-11.**

With delivery working, day zero still produced a CIR holding no `RDL-CLASS`
entries. The engines reported success throughout: ENG published, REG-LOCATION
ingested, tags were filed, and nothing logged an error.

Both engines cache what they have already established about a class.
`CirClassResolver` caches the resolved identity for an ENG EC class;
`CirClassRegistrar` caches the fact that an inbound class has been verified.
Both caches are in-memory and live for the lifetime of the Functions host. Day
zero drops the CIR registry, but the hosts are not restarted, so the caches
survive the database they describe. Every subsequent lookup answered from cache,
concluded the mapping already existed, and skipped the write that would have
recreated it. The registry stayed empty and nothing reported a fault, because
from each engine's point of view there was no work to do.

This was masked for some time by re-drains: any run that happened to restart a
host repopulated the rows, so the registry intermittently looked correct. The
defect was only isolated by querying `cir.Entry` directly rather than through the
provider API, after deliberately deleting the two `RDL-CLASS` rows.

The fix follows the rule that a cache must be invalidated by whatever invalidates
its source. Both classes gained `ClearCacheAsync`, wired into the reset endpoints
the engines already exposed (`EngEngineReset`, `RegLocationEngineReset`). Day
zero calls `ResetCirAsync` before `ResetAsync`, so CIR is dropped first and the
caches are cleared immediately afterwards — identity is never left describing
rows that no longer exist.

Verified by deleting both `RDL-CLASS` rows from `cir.Entry`, then reset and
drain: both rebuilt. Confirmed end to end over two independent day-zero runs,
each producing 3 `SITE`, 2 `RDL-CLASS` and 2 `FunctionalLocation` entries, the
latter after REG-LOCATION approval. A warm repeat reuses the two `RDL-CLASS`
rows rather than adding more, which is the cross-reference behaving correctly; a
third row for the same class would be the defect.

## Day zero orphaned the CIR provider's publication channel

Raised 2026-09-11. **Resolved 2026-09-11.**

Every day zero closed with:

> 2 channel(s) not known to the registry were deleted and NOT recreated. Any
> system still holding a session on one will keep polling an id the broker has
> forgotten; restart it or have it re-open.

Day zero builds its delete list from everything the broker reports, so that
clutter left by earlier demos is removed, but its recreate list only from the
participant registry. A channel that is discovered and not expected is therefore
deleted and deliberately left gone.

The CIR provider is configured with two channels — `Isbm__RequestChannelUri`
(`/OIIE/CIR/Request`) and `Isbm__PublicationChannelUri`
(`/OIIE/CIR/Publication`). Only the request channel was declared in the
personality packs. The publication channel was discovered as an orphan, deleted,
and then re-created by the provider itself, so the warning returned on every run
and the provider's publication session was destroyed each time.

The packs already carried the rule for the request channel — *reset ensures it
exists but never deletes it* — because deleting a channel owned by another system
destroys that system's long-lived session. The publication channel was simply
never added when that rule was written.

`CirSettings` gained `PublicationChannelUri`, declared in all four packs, and
`SandboxAdminEndpoints` now includes it wherever the request channel is treated
as foreign: day zero's `expected` set and the lighter reset's `foreignChannels`
list, both with `Ours = false` so it is ensured rather than owned.

The ensure loop hardcoded `IsbmChannelType.Request`, which would have created the
publication channel with the wrong type — a fault that surfaces as a provider
unable to post, not as an error. Channel type is now derived from the URI.

This did not cause the missing `RDL-CLASS` rows: class registration travels over
`/OIIE/CIR/Request`, which was always registry-known and correctly recreated. The
two defects were concurrent and unrelated, which is part of why either took as
long as it did to see.

Only one orphan is accounted for. The warning reported two, and the second was
not present on the broker when the first was identified. The `channelsRemoved`
array in the day-zero response names them, so the next run that still warns will
identify it.

## MMS has no segment ingest leg

Raised 2026-09-11. **Not started.** Scoped, not implemented — recorded so the
next session begins on implementation rather than rediscovery.

MMS consumes sites and writes them to `SETUP_OWNER`, which works. It has no
equivalent of REG-LOCATION's segment leg, so approved locations never reach
`LIGHT_UNIT_INVENTORY` and MMS registers no `RDL-CLASS` mirror in CIR.

Most of the groundwork is already laid, largely by DR-030:

- `MmsProvider` is complete. `POST /lightunits` reaches
  `SqlMmsAssetStore.UpsertLightUnitsAsync`, which upserts on `EXT_ASSET_ID` and
  follows the same partial-success and 503-on-all-transient convention as the
  owners path, so the ingest leg's retry behaviour needs no special casing.
- `MmsEngineOptions.InboundRdlTableMap` already maps `rdl:Streetlight` to
  `LIGHT_UNIT_INVENTORY`, and `ResolveTargetTable` deliberately has no fallback:
  MMS's tables are its classes, so an unmapped key must decline the segment
  rather than pick a table.

What is missing is the engine's second leg:

1. `MmsRestClient.UpsertLightUnitsAsync` — mechanical, mirrors
   `UpsertOwnersAsync`.
2. `MmsEngine/Application/IncomingSegmentMapper` — maps `SyncSegments` onto
   `LightUnitUpsert`, dispatching on the segment's RDL key through
   `ResolveTargetTable`.
3. `MmsEngine/Application/SegmentIngestionService` — parallels REG-LOCATION's.
4. `MmsEngine/Application/CirClassRegistrar` — near-copy of REG-LOCATION's,
   including `ClearCacheAsync` so it is cleared by the reset that drops CIR.
5. Wiring: `Program.cs`, an ingest and a reset endpoint on `MmsEngineFunctions`,
   segments channel and topics on `MmsEngineOptions`, and the approved-locations
   channel declared in `mms/personality.yaml` — declared there, per the CIR
   publication channel item above, so day zero ensures it rather than orphaning
   it.

Estimated at roughly 60-70% of the REG-LOCATION effort: lower because the
provider, the table map and the DR-030 decisions already exist.

### `LIGHT_SYSTEM_ID` is not the engine's to populate

`LIGHT_UNIT_INVENTORY.LIGHT_SYSTEM_ID` names a parent light system, and nothing
in the sites leg establishes one, so an inbound location appears to have no
parent to attach to. This was initially read as a modelling gap that had to be
closed before the leg could be built.

Decided 2026-09-11 that it is not. The column is nullable, and it describes how
MMS groups its own assets — a maintenance system's internal structure, not
anything engineering asserts. The first pass leaves it null.

What the engine writes is a **footprint row**: the engineering facts about an
asset, in the MMS table that represents its class, identified by `EXT_ASSET_ID`
so MMS can recognise it again. Grouping it into a light system is MMS's to do,
by whatever rule MMS uses, and the upsert-on-`EXT_ASSET_ID` behaviour means a
later engineering update will not overwrite that choice.

The general rule, worth applying to the next such column: a nullable column that
encodes the receiving system's own organisation is that system's to populate.
Filling it from engineering data invents a fact the publisher never asserted, and
the invention is durable — it survives as data long after the reasoning behind it
is forgotten. Leaving it null is the honest representation of "engineering has no
opinion about this".
