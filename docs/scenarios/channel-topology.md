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

Topology wins where it answers; local settings are the fallback.

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
`/OIIE-SANDBOX/Enterprise/Site/OandM` and approved segments back to the
engineering channel. The same BOD travels again from a different sender with a
different meaning, which is why the topic carries `sc02`.

MMS and CMS appear as SC02 subscribers but have no backing store yet. They are
declared so the intended topology is stated and so the "nothing reached MMS"
assertion means something: an absent subscriber and a subscriber that received
nothing are different findings, and only the second is a test result.

The `/OIIE-SANDBOX/Enterprise/Site/OandM` URI is literal rather than derived. It
predates the convention and is provisioned on the broker in that exact form.
Normalising it is a broker change and a migration, not a text edit.
