# Participant Architecture — ISBM-Native Design Specification

**Status:** Approved for implementation
**Supersedes:** the central-orchestrator design and the custom participant HTTP contract
(`Oiie.Participants.Contract` / `RemoteBodHandler`).
**Rationale and history:** DR-015 in `decision-register.md`. This document states *what to build*.

> **Implementation status:** no existing code has been changed to match this document. Standalone
> participants are built and proven against `testing/test-handover-chain.ps1` first; only then is the
> existing code system migrated. §9 records what each existing component becomes, as a plan, not as
> a description of the code today.

---

## 1. Objective

A participant is an **independently deployed Function App, authored by the owner of the customer
data**, that integrates by talking to the **ws-ISBM Service Provider** and nothing else.

The prescribed interface is therefore **the ISBM REST API**, which is a published standard, not a
contract we invent. This is the load-bearing decision and everything else follows from it. A
participant author implements against ISBM, so their work is portable to any conformant provider
and their conformance is worth something outside this sandbox. A proprietary contract would have
made it worth nothing.

There is **no orchestrator**. No component routes BODs between participants, and no participant
knows that any other participant exists. Each knows only the channels it consumes and the channels
it produces. The handover chain is an emergent property of the channel topology.

**Definition of done for the whole effort:** a new participant can join the ecosystem by deploying
a Function App that subscribes to its input channels and publishes to its output channels. No edit
to any existing participant, to `Oiie.Sandbox.Core`, or to the ISBM provider. Adding a participant
is a **configuration and topology change, not a code change.**

### Vocabulary — read this before writing any mapping code

The two vocabularies must not be mixed. Which one a name belongs to tells you which side of a
participant's mapping it may appear on.

| Term | Layer | Meaning |
| --- | --- | --- |
| `Segment` | CCOM / wire | The semantic noun for a **functional location**. This is what arrives in the BOD, and the only vocabulary a mapping's *input* uses. |
| `CmsAsset` / `AssetTag` | CMS native | CMS's own asset table and its UNIQUE tag column. CMS-side vocabulary only. |
| `LightSystem` | MMS native | MMS's own vocabulary for the same inbound Segment. |

**CMS does not store functional locations.** It turns each inbound `Segment` into an *asset
placeholder*, because a condition-monitoring system monitors assets, not design artefacts. So
`Segment` to `CmsAsset` is a deliberate change of kind, not a rename — and it is precisely the
semantic step the mapping exists to make explicit.

`AssetTag` is therefore **never** a CCOM term. It appears only on the CMS side of the boundary, and
the same Segment becomes a `LightSystem` in MMS. A mapping that mentions `AssetTag` in MMS, or
`LightSystemName` in CMS, is wrong by construction.

### Constraints

- **C1.** Each participant is its own Function App, deployed independently, integrating **only**
  through the ISBM REST API. There is no custom participant-to-participant contract and no
  participant registry.

  *(C1 has now been stated three ways. It first required in-process participants; it was then
  changed to remote participants behind a bespoke HTTP contract; both are superseded by DR-015. The
  register keeps all three because the reasoning is the useful part.)*
- **C2.** A participant MUST NOT call another participant directly. If participant A needs
  something from participant B, that is a channel, not an HTTP call.
- **C3.** A participant MAY reference `Oiie.Ccom` for CCOM/BOD schema types and `Oiie.Isbm.Client`
  for transport. It MUST NOT reference `Oiie.Sandbox.Core` for orchestration. Sharing *schemas* is
  correct — the BOD is the contract. Sharing a *dispatcher* is what we removed.
- **C4.** Mapping code inside a participant should perform no I/O, so it is testable without a
  database. Recommended, not contractual.
- **C5.** Delivery is **at-least-once**. Every participant MUST be idempotent per §4.3. This is not
  softened by ISBM; see §4.2.
- **C6.** A participant MUST NOT depend on ordering between BODs, and MUST NOT assume an entity
  referenced by one BOD was created by an earlier one.
- **C7.** Notifications are an optimisation, not the delivery mechanism. A participant MUST also
  poll, per §4.5.

---

## 2. Solution layout

```
Oiie.Isbm.Client/                   THE TRANSPORT (shared, already exists)
    IIsbmClient.cs                  ISA-95.00.06 messaging operations
    IsbmRestClient.cs               HTTP implementation

  Referenced by every participant. This is a client for a standard API, not a
  contract we own: replacing it with any conformant ISBM client must be possible.

Oiie.Ccom/                          THE SHARED SCHEMAS
    CCOM types, BOD types           the BOD is the contract

{Participant}Provider/              ONE PER PARTICIPANT
    Functions/NotificationHandler   webhook receiver (PUT /notifications/...)
    Functions/PollTimer             backstop poll (§4.5) — not optional
    Application/BodProcessor        domain logic: parse, apply, build output BOD
    Infrastructure/                 the customer system, or a client for it
```

A participant author copies an existing participant. They never open `Oiie.Sandbox.Core`.

CMS departs from this template: the participant and the customer system are separate Function Apps,
and the customer system is not a participant at all. See §5.2.

---

## 3. The prescribed interface — ISBM

### 3.1 Channel topology

The topology *is* the routing. Each channel carries one class of BOD between a producing role and a
consuming role.

```
Channel                          Type        Producer → Consumer     BOD types
──────────────────────────────────────────────────────────────────────────────────────
/oiie/engineering-updates        Publication ENG → REG               ProcessRegistry
/oiie/cir/request                Request     Any → CIR               GetRegistry, ProcessRegistry,
                                                                     GetEquivalentEntries
/oiie/asset-config               Publication REG → MMS, CMS          ProcessRegistry
/oiie/maintenance-events         Publication MMS → O&M, CONTROL      AssetSegmentEvent
```

Two channel types, and the difference determines whether a verdict exists at all:

- **Publication** — publish-subscribe. Fire-and-forget. The publisher does not know who subscribes,
  does not know whether anyone did, and **gets no verdict**. This is by design; wanting a verdict
  here means the interaction was request-response.
- **Request** — request-response. The consumer posts a request and reads a response BOD. The
  response BOD *is* the verdict.

Adding a consumer to a publication channel requires no change to the publisher. That is the
property the whole architecture exists to produce.

### 3.2 The interaction loop

Every subscribing participant runs the same five steps.

```
1. Subscribe   OpenSubscriptionSession(channelUri, topics, listenerUrl)   once, at startup
2. Wake        webhook PUT /notifications/{sessionId}/{messageId}         or poll (§4.5)
3. Read        ReadPublication(sessionId)                                 null = queue empty
4. Process     apply to the customer system                               MUST be idempotent
5. Remove      RemovePublication(sessionId)                               acknowledges
```

**Do not remove before processing.** `RemovePublication` is the acknowledgement, and a message
removed before it is applied is lost with no trace. Removing after processing risks doing the work
twice, which idempotency makes harmless. That asymmetry is why the order is not negotiable.

**A message that cannot be processed should be left queued** so it is retried — but a message that
can *never* be processed, such as unparseable content, must be removed, or it blocks every message
behind it. Report what was discarded and why; the existing `IsbmDrainReport.Discarded` is the
pattern to follow.

### 3.3 Session lifecycle

Open once, reuse, reconnect on failure. **Do not re-open sessions on a timer** — each open is a new
session, and the old one keeps accumulating messages nobody reads.

```
startup → look up stored session id for this participant
        → if found, probe it; if the probe says the session is gone, discard it
        → if absent or dead, open a new session and persist the id
```

Persist session ids outside the Function App's memory, since instances recycle freely.

> **Unverified:** whether a dead session is distinguishable from an empty queue on read. The client
> maps 404 to "empty", so if a dead session also returns 404 the probe cannot tell them apart, and a
> participant could sit forever holding a session that will never deliver. Establish the real
> behaviour before relying on any probe. If they are indistinguishable, prefer re-opening on a
> failure signal rather than probing.

### 3.4 Which ISBM routes are proven

`IIsbmClient` marks its own provenance, and the marking matters for build order: the halves this
topology leans on hardest are the ones never exercised.

| Operation group | Status |
| --- | --- |
| Provider-request (read request, post response) | Verified against a live ISBM 2.1 provider by ws-CIR |
| Subscription (open, read, remove) | Verified |
| **Publication (open session, post publication)** | **UNVERIFIED — inferred from convention** |
| **Consumer-request (open session, post request, read response)** | **UNVERIFIED — inferred** |
| Channel management (create, get, delete) | **UNVERIFIED** — ws-CIR consumes channels it did not create |

Every hop in §5 except CIR's own runs on an unverified route. `test-handover-chain.ps1` exercises
these routes directly, before any participant depends on them, for exactly this reason.

---

## 4. Verdicts, delivery and idempotency

### 4.1 Verdicts are BODs

There is no verdict DTO and no verdict status code.

| Interaction | How the outcome is reported |
| --- | --- |
| Request-response | The response BOD. `AcknowledgeRegistry` carries an acknowledge code; `ShowRegistry` carries the answer. |
| Publish-subscribe | **There is none.** The publisher does not learn who processed the BOD or whether it succeeded. |

HTTP status codes on ISBM calls describe *the ISBM call*, not the business outcome. A 200 from
`PostPublication` means the provider accepted the message for delivery. It says nothing about
whether a subscriber ever applied it.

The DR-012 distinction still governs, because it is about classification, not transport: a
dependency that did not answer is not a dependency that said no. A participant that cannot process
a BOD now must leave it queued for retry. A participant that will *never* be able to process it
must remove it and say so. Conflating those two destroys data in one direction and blocks the queue
in the other.

### 4.2 At-least-once — what ISBM does and does not fix

ISBM's read-then-remove **does not give exactly-once processing**, and any claim that it does should
be treated as a defect in the document making it.

The split remains: a participant commits to its customer database, then calls `RemovePublication`.
Those are two operations against two systems with no shared transaction. A crash in between leaves
the work done and unacknowledged, and the BOD is delivered again.

What ISBM genuinely improves is that redelivery is now the *provider's* well-defined behaviour
rather than an orchestrator's retry policy, and that an unacknowledged message is never silently
dropped. Both are real gains. Neither removes the participant's obligation to be idempotent.

### 4.3 Idempotency, and the key to use

**`BODID` is the idempotency key.** Not the ISBM `MessageId`.

`MessageId` is assigned per channel by the provider. It de-duplicates *transport* redelivery of one
message on one channel, and it is the right key for that. But it does not survive a hop:

```
ENG publishes            BODID=B1   MessageId=M1   on /oiie/engineering-updates
REG reads M1, applies, republishes             →   on /oiie/asset-config
MMS receives                        MessageId=M2

REG crashes before RemovePublication, is redelivered M1, republishes again
MMS receives                        MessageId=M3   ← different id, same business fact
```

De-duplicating on `MessageId`, MMS sees M2 and M3 as unrelated and writes the maintenance record
twice. De-duplicating on `BODID`, it recognises the repeat — **but only if REG preserved B1.**

**The propagation rule:**

> A participant **preserves the inbound `BODID`** when republishing the same business fact onward.
> It **mints a new `BODID`** only when originating a fact of its own, or when answering a request.

Under this rule the ws-CIR provider's current behaviour is already correct: it mints a new BODID for
each response BOD, and a response is a new fact. It is the *forwarding* participants — REG above
all — that must be written deliberately.

Two acceptable implementation strategies:

- **Natural idempotency.** Match on a stable business key and upsert. CMS does this on `AssetTag`,
  which is `UNIQUE`, so a redelivery updates the row the first delivery created. Preferred: no extra
  state.
- **Explicit de-duplication.** Record processed `BODID` values and short-circuit on a repeat.
  Necessary when the customer system has no stable key to match on.

A participant that appends unconditionally will duplicate customer data under redelivery. Nothing
in the ecosystem can detect this, and the duplicate rows are indistinguishable from legitimate ones.
**This is the single most important obligation in this document.**

### 4.4 What a participant may assume

- The BOD is well-formed XML.
- `BODID` is stable across redeliveries of the same message on the same channel.
- The same BOD may arrive more than once (C5).
- Nothing else. In particular, no ordering between BODs (C6).

### 4.5 Notifications are a doorbell, not the delivery mechanism

A participant MUST run a low-frequency backstop poll in addition to its webhook.

This is not defensive over-engineering; it is required by the provider's current behaviour.
`HttpNotificationDispatcher.NotifyAsync` catches every exception from the listener PUT, logs a
warning, and returns normally. The dispatching Service Bus trigger therefore **completes
successfully**, and the notification is never retried.

The consequence is bounded but bad: the BOD is *not* lost, because it stays queued until removed —
but nothing will ever ring the doorbell again. A participant that relies solely on the webhook, and
happens to be cold or redeploying when the notification fires, stops receiving that message
indefinitely. In a demo this presents as a chain that silently halts mid-flow.

A timer that calls `ReadPublication` regardless of notifications closes this completely and costs
one function. The webhook then does what it is good at — reducing latency — without being load-
bearing. The existing ws-CIR timer drain is exactly this pattern and should be **kept**, not removed
when its webhook is added.

---

## 5. The handover chain

The three-identifier demo, TIC-106 → LOC-000001 → 234441:

```
Step  Actor  What happens                                  Channel
──────────────────────────────────────────────────────────────────────────────────────
 1    ENG    publishes tag update for TIC-106              → /oiie/engineering-updates
 2    REG    wakes, reads the ProcessRegistry BOD          ← /oiie/engineering-updates
 3    REG    maps TIC-106 → LOC-000001 internally
 4    REG    requests identity registration                → /oiie/cir/request
 5    CIR    wakes, reads the request, registers           ← /oiie/cir/request
 6    CIR    responds AcknowledgeRegistry                  → /oiie/cir/request
 7    REG    reads the acknowledgement, removes the input
 8    REG    publishes the registered asset (BODID preserved from step 1)
                                                           → /oiie/asset-config
 9    MMS    wakes, reads, maps LOC-000001 → 234441        ← /oiie/asset-config
10    MMS    updates maintenance records, removes
11    MMS    optionally publishes completion               → /oiie/maintenance-events
```

No component coordinates this. Each step is one Function App reacting to its own channel. Step 8's
parenthesis is the §4.3 rule in action, and it is the step most likely to be got wrong.

### 5.1 Participant roles

| Participant | Subscribes | Publishes | Request-response |
| --- | --- | --- | --- |
| ENG | — | `/oiie/engineering-updates` | — |
| REG-LOCATION | `/oiie/engineering-updates` | `/oiie/asset-config` | consumer on `/oiie/cir/request` |
| CIR | — | — | **provider** on `/oiie/cir/request` |
| MMS | `/oiie/asset-config` | `/oiie/maintenance-events` | — |
| CmsEngine | `/oiie/asset-config` | — | — |

`CmsProvider` is absent from this table on purpose: it is a customer system, not a participant. It
holds no session, subscribes to nothing, and is unaware ISBM exists. See §5.2.

CMS and MMS both subscribe to `/oiie/asset-config` and neither knows about the other. Adding CMS
requires no change to REG. That is the claim this architecture makes, and §8 is how it gets tested.

### 5.2 Reference packaging — CMS is split, MMS is not

CMS is deployed as **two** Function Apps. MMS is deployed as **one**. The asymmetry is deliberate and
is itself part of the demonstration.

```
CmsEngine/     THE PARTICIPANT — what a vendor writes
    holds the subscription session on /oiie/asset-config
    receives PUT /notifications/{sessionId}/{messageId}
    reads the BOD, parses CCOM, maps Segment → AssetTag
    calls CmsProvider over its published API
    removes the message only after that call succeeds
    references Oiie.Ccom + Oiie.Isbm.Client
    NO database access of any kind

CmsProvider/   THE CUSTOMER SYSTEM — our emulator of Meridium's product
    sites, assets, assets/{tag}, health
    owns the SQL schema
    knows nothing of ISBM, OIIE, CCOM, BODs or channels
```

**Only `CmsEngine` would survive into production.** In a real deployment `CmsProvider` is deleted and
`CmsEngine` is pointed at Meridium's actual endpoint. That is what makes `CmsEngine` the honest
reference for a vendor-authored participant: it is the sole artefact a vendor would write.

#### The rule that makes the split meaningful

> **`CmsEngine` may use only access that a third party could be granted. If applying a BOD needs a
> privilege the customer would not hand an integrator, the boundary is fake.**

Stated this way rather than as "no connection string" because the interface a customer system
publishes is not always HTTP. If Meridium's real integration surface is ODBC, the participant talks
to a database and a connection-string prohibition would be wrong. The distinction that survives both
cases is **published versus private**: a shared `DbContext`, a private schema, or an internal service
class is private access; a documented API or a documented ODBC schema with its own credentials is
published access.

Verifiable by inspection. If `CmsEngine` acquires access that Meridium would not grant an outside
integrator, the demonstration has silently failed and the boundary is in-process wearing a network
costume.

#### Why MMS is not split

Two participants built two different ways, both joining `/oiie/asset-config`, neither requiring any
change to REG or to each other, is a stronger demonstration than two identical ones. It shows the
ecosystem does not care how a vendor packages their side.

Splitting is a **demonstration choice, not an architectural recommendation.** A real vendor would
likely co-locate engine and system. If asked "would you deploy it this way?", the answer is "not
necessarily — the split exists to prove the participant has no privileged access."

#### What the split costs

The engine's call to the customer system can time out with the write already committed. The engine
cannot distinguish that from a write that never happened, so it must retry — which is safe **only**
because the customer system upserts on a `UNIQUE` key. See §6.1: this places a new obligation on the
*customer system*, which the co-located design did not need.

---

## 6. Participant behaviour that must not change

### 6.1 CMS

Carried forward unchanged from the previous design; only the transport differs.

| Behaviour | Detail |
| --- | --- |
| Kind change | An inbound CCOM `Segment` (functional location) becomes a CMS **asset placeholder**, not a location. |
| Match key | `AssetTag`, set from `Segment.IDInInfoSource`. `UNIQUE`, and the only thing CMS can match on: the table has no column for a foreign identifier, FederationId or CIRID, and none may be added (DR-009). |
| Update scope | `AssetName`, `Description`, `SiteID`, `UpdatedAtUTC` only. Never overwrite `AssetClassID`, criticality, serial or manufacturer — those belong to whoever surveyed the asset. |
| Placeholder status | `"Planned"`. |
| `AssetClassID` | Always null on create; the sender's classification is not CMS's. |
| Sites | Never created. Unprovisioned site means skip that segment. |
| Idempotency | Natural, via `AssetTag`. Satisfies §4.3 by construction. |

#### 6.1.1 The customer system's upsert must never become an insert

A constraint on `CmsProvider`, created by the §5.2 split.

`POST /assets` MUST match on `AssetTag` and update when the row exists. It MUST NOT insert
unconditionally.

The engine and the customer system are now separate processes, so a timeout leaves the engine unable
to determine whether the write committed, and its only correct response is to retry. That retry is
harmless *because* the upsert matches on a `UNIQUE` key. If the upsert ever becomes an insert, every
retry produces a duplicate asset, and the duplicates are indistinguishable from legitimate rows.

This is the same hazard as §4.3, one layer lower: the participant's idempotency obligation is
**discharged by** the customer system's upsert semantics. Note where the responsibility now sits —
the obligation is on `CmsEngine`, the guarantee that satisfies it lives in `CmsProvider`, and nothing
in `CmsEngine` can detect if that guarantee is withdrawn.

### 6.2 Site resolution and the transient trap

CMS resolves the site named by `Segment.RegistrationSite` before mapping. Three outcomes, and they
must not be conflated:

| Registry says | Meaning | Action |
| --- | --- | --- |
| No such relation | The site is genuinely unprovisioned | Skip the segment |
| Unreachable / timeout | Nothing is known yet | Leave the BOD queued; retry |
| Answered with a site | Proceed | Map |

Catching `Exception`, logging a warning and returning null collapses row two into row one. That is
LTP-4 (DR-012), and it must not be reproduced in a participant.

**Never classify from an exception message string.** Message text is locale-dependent and changes
between provider versions. Classify from the exception type or a SQL error number.

---

## 7. Build order

Ordered so that each step de-risks the next, and so the unverified ISBM routes are proven before
anything depends on them.

1. **`test-handover-chain.ps1`** — the executable contract, written first and failing. It defines
   the channels, the BODs and the expected flow, and it exercises the unverified publication and
   consumer-request routes directly. Each participant then makes one more section pass.
2. **Channel bootstrap** — idempotent creation of the four channels. `CreateChannel` returns 422
   when the channel exists; treat 201 and 422 as equally successful.
3. **CIR webhook adapter** — additive only. Add the listener endpoint and a provider-request session
   alongside the existing timer drain and direct endpoint. Domain logic does not change; this is a
   transport change. **Keep the timer** (§4.5).
4. **REG-LOCATION** — the most complex participant, and the only one that both forwards and issues
   requests. The §4.3 BODID rule lands here.
5. **ENG** — trivial publisher; can be a script.
6. **MMS** — subscriber, engine and store in one app.
7. **CmsEngine** — the split participant (§5.2). Built last of the participants because it proves the
   packaging claim, which is only meaningful once the chain it joins already works. `CmsProvider`
   already exists and is not modified.
8. **End-to-end** — the full chain, including the redelivery case.

CIR is third rather than first because it is the one participant whose ISBM integration is already
proven, which makes it the right thing to validate the pattern against — but only after there is a
test to validate it *with*.

---

## 8. Testing

`testing/test-handover-chain.ps1` is the contract. It runs before participants exist and reports
each absent participant as a skip with a reason, so the same script is meaningful at every stage.

It must cover:

| Phase | Asserts |
| --- | --- |
| Channel bootstrap | All four channels exist; re-running creates no duplicates |
| Publication route | Open session, post, read back, remove — **proves an UNVERIFIED route** |
| Consumer-request route | Post request, read response — **proves an UNVERIFIED route** |
| Topic filtering | A subscriber on topic A does not receive topic B |
| Fan-out | Two subscribers on one channel both receive the same publication |
| Chain | ENG → REG → CIR → MMS, all three identifiers linked |
| Fan-out to a second vendor | `CmsEngine` receives the same publication as MMS, with no change to REG |
| **Redelivery** | The same BOD delivered twice results in **one** maintenance record |
| Cleanup | Sessions closed, channels removed |

The redelivery phase is the one that justifies the script. A chain test that publishes once passes
against participants with no idempotency whatsoever, so without it the sharpest hazard in the design
goes unexercised until a customer finds it.

Fan-out matters for a different reason: it is the executable form of the architectural claim that a
participant can be added without touching the publisher.

---

## 9. Migration plan for the existing code

**Nothing here has been done.** Standalone participants are proven first.

| Existing | Becomes | Notes |
| --- | --- | --- |
| `InboxPump` | Removed as a router | May survive as a local test harness |
| `IBodHandler` implementations | Domain logic inside each participant | Logic moves intact; transport changes |
| `BodDispatcher` | `PostPublication` / `PostResponse` | Same BOD, different delivery |
| Participant endpoint config | Channel URIs and topics | Data change |
| `Oiie.Participants.Contract` | Deleted | Superseded before it was used |
| `Oiie.Sandbox.Core` | Keeps CCOM parsing and BOD building | Referenced for schemas, not orchestration |
| ws-CIR `/api/bods` | Removed after the webhook is proven | Both run in parallel during transition |
| ws-CIR timer drain | **Kept** | The §4.5 backstop |

Two consequences that are easy to miss:

- **Provenance and identity correspondence lose their home.** The orchestrator wrote these centrally
  after a successful apply. With no orchestrator, each participant records its own, most naturally
  through the CIR request channel. This is real work and is not yet designed.
- **The single-pane UI loses its vantage point.** The orchestrator saw every step, which is what made
  one screen showing the whole distributed flow easy. The Blazor UI must now reconstruct the chain
  from ISBM channel state plus CIR entries. Arguably a more honest demonstration — it shows the flow
  as an observer would have to see it — but it is not free, and the demo depends on it.

---

## 10. Known risks

| Risk | Mitigation | Residual |
| --- | --- | --- |
| Participant not idempotent | §4.3; redelivery test | Undetectable in production if it slips through |
| BODID not preserved on republish | §4.3 rule; chain test | Silent duplicates one hop downstream |
| Notification lost, chain halts | §4.5 backstop poll | None once the poll exists |
| Unverified ISBM routes | Test phases 2 and 3, before participants | Provider may differ from inference |
| Dead session indistinguishable from empty queue | §3.3 | **Open** — behaviour not yet established |
| Provider downtime | Participants fail cleanly and resume; messages stay queued | Chain stalls, does not corrupt |
| Ordering assumptions | C6 | A participant that assumes ordering will pass tests and fail in production |
