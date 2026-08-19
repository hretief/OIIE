# Open items

Pending work carried between sessions. Decisions belong in
[decision-register.md](decision-register.md); this file is only for things not
yet done.

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

The obstacle is that there is no harness for the dispatcher at all. `SimHost.Tests`
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

**Status:** design idea, raised 2026-08-19. Nothing started.

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

The 88 tests in `tests/SimHost.Tests` are structural: they assert schema
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
