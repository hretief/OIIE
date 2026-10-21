# Workflow Orchestrator Demo Walkthrough (SC01 and SC02)

This walkthrough covers ENG authoring through REG-LOCATION stewardship and
the first deployed MMS streetlight ingestion leg, verified on Azure dev
2026-10-21.

Each step separates what the system **does** from what an observer might
reasonably assume it does. The gaps are called out inline rather than collected
at the end, because the moment to know a pane is seeded rather than delivered is
while looking at it, not afterwards.

**Hosts this walkthrough assumes are running:** Sandbox API (7241), ISBM
provider, ENG provider (7071), ENG engine (7254), REG-LOCATION provider (7073),
REG-LOCATION engine. To see sites reach CMS and MMS, their providers (7075 and
7074) and their engines must be running too \u2014 the providers alone will stay empty,
since nothing seeds them any more.

---

## 1. Open Workflow Orchestrator

1. Demo User opens the **Workflow Orchestrator**.

---

## 2. Select "Day Zero" Option

1. Demo User selects **"Day Zero"** from the User Profile Menu.

This calls `POST /admin/reset/day-zero`, which empties:

1. **ISBM channels** — every channel is deleted. Registry-known channels are then
   recreated; channels the registry does not recognise are deleted and left
   deleted, and are reported under `channelsRemoved`.
2. **Open ISBM sessions** — closed before their channels are removed.
3. **Sandbox participant schemas** — dropped and re-initialised, so the iTwin
   carousel shows only **"+"** and no content panes are active.
4. **ENG provider tables** and the **ENG engine watermark** — via `eng/reset` and
   `engine/reset`. Both are needed: the engine remembers published versions by
   VersionGuid, so clearing ENG's rows without clearing that memory yields an
   engine that silently declines to republish.
5. **REG-LOCATION provider tables** and the **REG-LOCATION engine published
   set** — via `reglocation/reset` and the engine's own `engine/reset`. Schema and
   `bootstrap.sql` are re-applied afterwards, because REG-LOCATION's bootstrap is
   structural (classification scaffolding), not demo data. As with ENG, both are
   needed: the engine remembers what it has published by federation GUID and
   revision, so clearing the registry alone leaves an engine that declines to
   republish and a `publishedTags` count that survives day zero.
6. **MMS provider tables** — via `mms/reset`, if configured; the MMS engine's
   `engine/reset` also clears its local site/segment subscription handles.
7. **CMS provider tables** — via `cms/reset`, if configured.

The response reports `providersReset` and an `actionRequired` list.

> **Two things to know before claiming "everything is empty":**
>
> - A provider whose base URL is not configured in `Sandbox:*ProviderBaseUrl` is
>   **skipped, not wiped**. This is deliberate — running only part of the stack is
>   normal — but it means day zero's completion does not by itself prove that MMS
>   and CMS are empty. Check `actionRequired`.
> - The **CMS and MMS engines hold no durable state**, so there are no "CMS Engine
>   tables" or "MMS Engine tables" to clear. Unlike ENG, neither keeps a
>   watermark: their only state is an in-memory ISBM subscription session that
>   goes when the host restarts. MMS's engine reset explicitly clears those
>   handles after channel reset; it does not itself delete durable backlog,
>   wipe provider data or replay approvals.
>
> The **CIR registry's own data is not cleared** by day zero. Entries registered
> earlier keep their CIRIDs. Use `POST /admin/cir/registry/delete` for a true
> greenfield, then re-register.

---

## 3. Site Added

1. User chooses **"+"** from the iTwin Carousel.
2. User picks an iTwin from a list presented by a call to the iTwin Platform.
3. Selection of an iTwin activates the UI content panels.

`POST /admin/eng/itwins/add` then, in order:

1. **Creates the iTwin's ISBM publication channel** using the prescribed naming
   convention (`IsbmChannelProvisioner.EnsureForITwinAsync`). This happens
   *before* the announcement, because publishing to a channel that does not exist
   fails. The result is reported as `ChannelUri`, or `ChannelError` on failure.
2. **Records the iTwin in the ENG provider** (`POST /itwins`). Recording precedes
   announcing deliberately: the engine publishes what the provider holds rather
   than trusting the event's copy, so announcing first would publish a twin the
   provider cannot describe.
3. **Announces `iTwins.iTwinCreated.v1`** to the ENG engine, which composes and
   publishes a **SyncSites** BOD.

Channel creation and announcement are reported **separately**, so a twin that was
registered but not announced is visible as exactly that rather than as a generic
failure.

### What actually consumes SyncSites

- **REG-LOCATION** genuinely subscribes and ingests.
  `RegLocationEngine.SiteIngestionService` drains the sites channel, maps the
  `Site` noun, writes it to the REG-LOCATION provider, **provisions the per-iTwin
  channels the site implies**, and **registers the site in CIR**. Site ingestion
  writes directly with no steward gate — a site is the context in which later
  assertions are made, not an assertion that could be rejected.
- **CMS and MMS also subscribe**, through `CmsEngine` and `MmsEngine`. Each opens
  its **own** session on the same enterprise sites channel, so all three
  subscribers receive every site rather than competing for it. Each maps the
  `Site` noun, calls its provider's `POST /sites`, and registers the result in
  CIR under its own `SourceId`.

  Neither engine touches its provider's database. `CmsProvider` and `MmsProvider`
  emulate customer systems and are reachable only over their REST APIs, which is
  why the engines are separate hosts with no project reference to them.

  The mapping, in both cases:

  | Provider field | CCOM source |
  | --- | --- |
  | `SiteId` | `Site.UUID` |
  | `SiteCode` | `Site.ShortName` |
  | `SiteName` | `Site.FullName` (falls back to the code) |
  | `Description` | `Site.Description` |
  | `SiteType` | `Site.Type.ShortName` |

  A site with no `UUID` or no `ShortName` is **rejected rather than invented**:
  the first would have no federated identity, the second no code an operator
  could refer to.

Nothing about CMS or MMS sites is seeded any more. A greenfield starts with both
systems genuinely empty, so sites appearing there is evidence the flow ran.

So the claim "every downstream system registers their Site against the CirId" now
holds for **REG-LOCATION, CMS and MMS**.

---

## 4. Segments Added

1. User goes to **SC01** and creates a new element/segment.

    1. The UI requires a GUID, whether generated or pasted.
    2. When the element is created, it persists in the ENG Provider.

---

## 5. Segment Released from ENG Provider

1. User creates a named version.
    1. One or more elements may be affected.
2. Named version is a trigger for the ENG Engine to compose and publish **SyncSegments**.
3. SyncSegments includes **RegistrationSite.UUID**, which is the same UUID as the iTwinId.
4. SyncSegments carries the UUID that was generated or pasted in ENG.

> Publication succeeds only if the per-iTwin channel exists and the engine's
> watermark has not already recorded that version. Both are handled by steps 2 and
> 3 above; a "queued" message in the UI reflects the outbox, **not** confirmed
> delivery to any consumer.

---

## 6. Segments Received by REG-LOCATION

1. `RegLocationEngine.SegmentIngestionService` subscribes to **SyncSegments** on
   the relevant per-iTwin channel.
2. The engine unpacks the BOD (`IncomingSegmentMapper`).
3. The segment is persisted into the REG-LOCATION Provider via
   `RegLocationRestClient`.

Unlike sites, segments are **proposed for stewardship** rather than written
straight through — a segment is an assertion about the plant that a steward may
reject. Expect segments to require disposition before they appear as accepted
registry content.

---

## 7. Approve and verify in MMS

1. In **SC02 → REG-LOCATION**, approve the proposed streetlight.
2. REG-LOCATION resolves its governed class UUID and registration-site UUID,
   then publishes the enriched `SyncSegments` on both the engineering return
   route and the operations route. Completion requires both routes to succeed.
3. MMS consumes from its durable per-site operations subscription. It resolves
   the class through CIR to `LIGHT_UNIT_INVENTORY` and the site to `OWNER_ID`.
   Missing mappings defer processing and retain the publication.
4. The provider upserts by `EXT_ASSET_ID = Segment.UUID`. MMS writes the native
   `LIGHT_UNIT_ID` into CIR's `ASSET/MMS`, retaining the original UUID, before
   acknowledging the publication. Native light-system grouping stays MMS-owned.
5. Select the same iTwin and **TAMS (MMS)** persona. The live inventory shows
   MMS ID, name, federation UUID, owner, grouping and update time. There is no
   manual Store Assets action.

Dev ingestion checks every **30 seconds**; the open MMS screen refreshes every
**10 seconds** and has a Refresh button. These are separate intervals, not an
end-to-end deadline. Site ingestion remains every two minutes. As a reference,
**9200 - District 2** held XX01, XX02 and XX03 during live verification.

The panel reads `GET /admin/mms/lightunits?twin={uuid}` through the Sandbox API.
Missing owner mapping, empty inventory and provider errors have distinct states;
there is no unscoped fallback. Bootstrap subscriptions before publishing, and
see [rollout and replay](../scenarios/channel-topology.md#mms-rollout-and-replay)
before backfilling approvals that predate them. Do not reset or replay merely
because an asynchronous approval has not appeared immediately.

---

## Summary: implemented vs. assumed

| Claim | Status |
| --- | --- |
| Day zero clears ISBM channels, sessions, sandbox schemas | Implemented |
| Day zero clears ENG provider + engine watermark | Implemented |
| Day zero clears REG-LOCATION, MMS, CMS providers | Implemented (skipped if base URL unconfigured) |
| Day zero clears CIR registry | **Not** included; separate endpoint |
| "MMS/CMS Engine tables" cleared | Not applicable — those engines hold no durable state |
| Add iTwin creates per-iTwin ISBM channel | Implemented |
| Add iTwin records in ENG and publishes SyncSites | Implemented |
| REG-LOCATION consumes SyncSites, provisions channels, registers CIR | Implemented |
| MMS/CMS consume SyncSites | Implemented — via `CmsEngine`/`MmsEngine` into each provider's REST API |
| MMS/CMS site data seeded | **Removed** — a greenfield starts empty |
| ENG publishes SyncSegments on named version | Implemented |
| REG-LOCATION consumes SyncSegments into the provider | Implemented (via steward disposition) |
| REG-LOCATION publishes approved SyncSegments to ENG and MMS | Deployed; governed class and registration-site UUIDs included |
| MMS persists approved streetlights and federates native IDs | Deployed; `LIGHT_UNIT_INVENTORY` and CIR `ASSET/MMS` |
| MMS SC01/SC02 inventory screen | Live, site-scoped provider data; deployed with Sandbox API/UI |
| MMS dev segment polling | Every 30 seconds; screen refresh every 10 seconds |