# Open items

Pending work carried between sessions. Decisions belong in
[decision-register.md](decision-register.md); this file is only for things not
yet done.

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

## CMS schema is defined but not wired up

**Status:** DDL only. `docs/DDL/CMS.SQL` defines the `cms` schema — `Location`,
`AssetClass`, `Asset` and related tables — and `deploy/sandbox/provision.ps1` already
provisions a `cms` schema and contained user. The persona exists in the React UI
(`App.tsx`), which places CMS on the `/OandM-Events` channel rather than MMS's
`/OandM`.

What does not exist yet is the participant itself: no `ConfigureCms` mapping in
`ParticipantDbContext`, no personality service, no BOD builders. Until that is added,
the CMS steps shown in the workflow UI are presentation only.

Build it as a **mapped participant** rather than a sandbox-native one, per
`Oiie.Sandbox.Core/PersonalityPacks/README.md` — the DDL is a given external shape, so
the same rule that governs MMS applies: map the tables column-for-column and do not
extend them.

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
