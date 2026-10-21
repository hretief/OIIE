# Channel topology

Who publishes what, to which channel, and who is listening. One file per
scenario in `Oiie.Sandbox.Core/Topology/`, read at runtime and served to the
engines over HTTP.

## Why it is central

A channel has two ends. Before this, each end declared the URI separately — the
publisher in its settings, the subscriber in its own, and a scenario file that
asserted on a third copy. Three strings that must match exactly, with nothing
checking that they do.

The failure mode is what makes it worth fixing. A subscriber left on a stale URI
does not error. It opens a session against a channel nobody writes to and waits,
reporting healthy, while the publisher reports every publication as successful.
Nothing is wrong anywhere until someone counts rows at the far end.

## The golden rule

**Adding a participant may require configuration and provisioning. It must never
require recompiling the solution.**

Everything below follows from that. Topology is YAML deployed as content, not an
embedded resource. The composition root names no scenario. The engines discover
their channels rather than declaring them.

## Schema

```yaml
scenarioId: sc01              # matches the scenario segment of the topics below
name: Design release to the location registry
scenario: 1                   # ordinal within the use case; ordering only, not a gate
useCase: UC01

completion:                   # how to tell the scenario finished
  participant: reg-location   # where to look
  contains: stewardship-items # what proves arrival
  description: ENG segments have arrived and await a stewardship decision.

channels:
  - uri: /{enterprise}/{iTwinId}/engineering/publication
    type: Publication         # or Request
    publisher: eng            # one, and it must be a registered participant
    subscribers: [reg-location]
    topics: [oiie:sc01/ccom:SyncSegments]
    description: Engineering design proposals from ENG to the location registry.
```

`{enterprise}` and `{iTwinId}` are substituted at resolve time, so one
declaration serves every twin and adding a twin needs no edit here. The
federation id is formatted `D` — matching what the engines interpolate. Any
other format yields a URI that differs from the one being published to, which
the broker accepts without complaint.

## Completion is observed, not enforced

Nothing stops an operator running SC02 before SC01. The UI does not gate, and
there is no run record to advance. A scenario is done when its data is visible
at the receiving participant, which is what `completion` names.

This is deliberate. A run record can disagree with the participants — reporting
success for data that was later reset — and the participants are the ones
telling the truth. SC02 run first finds an empty queue and has nothing to
approve, which is a clearer signal than a refusal would be.

## Precedence

Topology wins where an engine consults it; local settings are the fallback.
REG-LOCATION's approval publisher currently derives its two destinations from
`RegLocationEngineOptions`, rather than looking them up over HTTP. Its defaults
are pinned against the SC02 YAML by a regression test. Changing these routes
requires keeping the publisher settings and topology declaration aligned.
MMS's first segment leg likewise discovers matching per-site operations channels
from ISBM and uses its configured SC02 topics, rather than the topology client's
first subscription match (which would select the legacy sites channel).
Regression tests pin its defaults to the approval publisher's operations route.

| Situation | Channel used |
|---|---|
| Topology declares the channel | The declared URI and topics |
| Sandbox unreachable, previously fetched | Last known topology |
| Sandbox unreachable, never fetched | The engine's derived/configured URI |
| `SandboxBaseUrl` unset | The engine's derived/configured URI |

An engine that cannot reach the sandbox keeps publishing where it was
configured to, rather than stopping. Topology is the better answer, not the only
one. Engines cache for five minutes: per-publish would put the sandbox in the
path of every message, and fetch-once would make a topology edit require an
engine restart.

Channel and topics always travel together. A centrally resolved channel combined
with locally configured topics would put a subscriber on the right channel
filtering for a topic nobody posts under — the same silent non-delivery by a
narrower route.

## Adding a participant

No rebuild at any step.

1. Add `Oiie.Sandbox.Core/PersonalityPacks/{id}/personality.yaml` — schema,
   source id, ISBM and CIR settings.
2. Name the participant as a `publisher` or in `subscribers` on the relevant
   topology channels, adding a scenario file if it introduces one.
3. Restart or redeploy the sandbox so the files are re-read, then run
   `POST /admin/isbm/channels/ensure`. Startup does this too; provisioning is
   idempotent.
4. Point the participant's engine at the sandbox with `SandboxBaseUrl`, and set
   `ParticipantId` and `ScenarioId` to match the topology.

A publisher naming a participant that is not registered is reported as a failed
channel result rather than throwing, so one bad declaration does not stop the
rest from being provisioned.

## Endpoint

```
GET /admin/topology
GET /admin/topology?iTwinId={guid}
```

Returns the enterprise, and per scenario its completion rule and channels with
both the resolved `uri` and the `template` it came from. Without a twin the
`{iTwinId}` placeholder is left in place, so the shape of a per-iTwin channel is
visible without naming one.

## Current topology

**SC01 — design release.** ENG publishes segments to
`/{enterprise}/{iTwinId}/engineering/publication`; REG-LOCATION subscribes and
files each as a proposal. Stops at REG-LOCATION deliberately: design data may
not describe what gets built, so publication is not what admits it to
operations — approval is.

**SC02 — stewardship approval.** REG-LOCATION republishes approved sites to
`/OIIE-SANDBOX/Enterprise/Site/OandM` and approved segments to **both**
`/{enterprise}/{iTwinId}/engineering/publication` (ENG) and
`/{enterprise}/{iTwinId}/operations/publication` (MMS). Both segment routes use
`oiie:sc02/ccom:SyncSegments`, not SC01's proposal topic.

The REG-LOCATION publisher now includes `RegistrationSite.UUID` from the tag's
scope and `Type.UUID` resolved through CIR for its approved `ClassId`. Missing
or ambiguous class mappings leave the approval pending publication. Proposal
ingestion remains independent of this outbound requirement.

MMS and CMS have standalone providers and engines. MMS's first segment subscriber
is deployed and verified on Azure dev for streetlights. Its durable subscription must be
established before approvals are published.
Do not regard SC02's sender-side completion rule as proof of MMS persistence.

### REG-LOCATION rollout and replay

- `ReturnApprovalsToEngineering` defaults to `true`. Setting it to `false`
  disables the configured engineering return route only.
- Remove stale `RegLocationEngine__Topics__*` overrides containing SC01. With
  no overrides the default is the SC02 segment topic. The checked-in local
  settings intentionally omit the array override to avoid appending to defaults.
- `ClassRegistryId` defaults to the enterprise. Class resolution uses that
  registry, `RDL-CLASS`, and REG-LOCATION's source/local class ID. A steward's
  class correction must already have a governed CIR cross-reference.
- Site and class lookups are scoped to a single sweep/notification, so missing
  mappings and rebuilt scopes are reconsidered on the next pass.
- A revision is marked published only after both configured routes succeed.
  Routes are attempted independently so an operations failure does not withhold
  the engineering return. Retry can redeliver to the successful route; consumers
  must upsert.
- Existing published revisions are **not automatically replayed** by this
  change. Once the receiving subscriptions are ready, explicitly call the
  existing `POST /api/engine/reset` and then `POST /api/engine/sweep` to republish
  approvals with the enriched payload. Engine reset clears publication tracking
  and class-check caches; it does not reset provider data. For a larger backfill,
  set `MaxTagsPerSweep` high enough to cover the candidate set: the existing
  sweep caps candidates before filtering already-published revisions, so
  repeated sweeps alone do not page through a larger registry.

### MMS rollout and replay

1. Deploy CIR first. `GET /api/health` must advertise
   `destinationCategoryEquivalence: true`. MMS refuses cross-category commands
   against older CIR code. No schema migration is required; the new REST fields
   select a destination category inside the existing transaction.
2. Deploy MMS engine with both flat schedule settings:
   `MmsEngineSitesIngestSchedule=0 */2 * * * *` and
   `MmsEngineSegmentsIngestSchedule=*/30 * * * * *` for dev/local testing
   (every 30 seconds). Other deployment environments retain `30 */2 * * * *`
   (every two minutes, at second 30). Site ingestion remains every two minutes.
   Code defaults leave `SegmentsIngestEnabled=false`; checked-in local/deploy
   settings opt in with `MmsEngine__SegmentsIngestEnabled=true`. MMS/CIR URLs,
   keys and `RegisterInCir=true` are required for the segment leg.
   Leave `MmsEngine__SegmentsTopics__*` unset to use the single code default;
   array overrides append to the initialized defaults rather than replacing them.
3. After per-site channels exist, call MMS `POST /api/engine/bootstrap`.
   Confirm `subscriptionsReady == sitesDiscovered` and `failed == 0`.
   This opens stable `MMS:segments:{site-guid}` subscriptions to
   `/{enterprise}/{site-guid}/operations/publication` with
   `oiie:sc02/ccom:SyncSegments`, without consuming or replaying anything.
   It works while ingestion is disabled, and reopens through stable subscriber
   IDs instead of trusting cached session handles.
   Repeat after adding sites and after broker/channel resets, before approval.
   The timer also discovers and subscribes new sites, but publications sent
   before their first durable subscription are not recoverable from that queue.
4. Ensure MMS's site ingestion has registered the site's `OWNER_ID` in
   `ITWIN-SITE/MMS`, and REG-LOCATION's instance registration exists in
   `FunctionalLocation/REG-LOCATION`. Missing entries produce visible deferrals.
   Repair or republish the missing upstream identity; do not invent an MMS anchor.
5. For earlier approvals such as WW01, explicitly reset **REG-LOCATION engine
   tracking**, then sweep, as above. Do not reset either provider database.
   This replays all selected approved candidates, not just WW01, and can
   redeliver to ENG too. No replay happens automatically on MMS deployment.
6. Call MMS `POST /api/engine/ingest-segments` or wait for its timer. Inspect
   `messagesAcknowledged`, `unitsUpserted`, `assetsRegistered`, `deferred`,
   `failed` and `notes`. A row written before a CIR outage may count as upserted
   without the message being acknowledged; retry safely upserts the same UUID.
   Verify `GET /api/lightunits` on MMS Provider and CIR's `ASSET/MMS` mapping,
   or use the site-scoped live MMS panel (Sandbox API/UI deployed together on
   Azure dev 2026-10-21):
   select the matching iTwin in SC02's MMS/TAMS persona to inspect persisted
   light units through `GET /admin/mms/lightunits?twin={uuid}`.

`POST /api/engine/reset` on MMS clears only local segment/site session handles.
It does not delete durable backlog, reset provider data or replay approvals.
Sandbox day zero now calls it using `Sandbox:MmsEngineBaseUrl` and
`Sandbox:MmsEngineKey`; the sandbox deploy script configures these independently
of MMS Provider's URL/key. Class and site resolutions have no long-lived cache.
See [engine.http](../../MmsEngine/engine.http) for manual requests.

The `/OIIE-SANDBOX/Enterprise/Site/OandM` URI is literal rather than derived. It
predates the convention and is provisioned on the broker in that exact form.
Normalising it is a broker change and a migration, not a text edit.
