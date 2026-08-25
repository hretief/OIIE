# ENG Change Detection and Event Publication Pattern

## Overview

The **ENG system** (Bentley iModels) is treated as a closed System of Record (SoR):

- No direct database access
- No database triggers
- No schema modifications
- No custom tables
- No changes to ENG application logic
- Only supported ENG REST APIs may be used

The triggering event for all downstream flow is a **committed state change in ENG**, never a
UI action. A button press, an API call, a bulk import and a background process are all
equivalent: if the change commits, the flow runs; if it does not commit, nothing happens.
The engine is a consumer of committed facts, not a participant in the interaction that
produced them.

---

## Target state versus emulation

Real Bentley iModels **pushes** events to a registered webhook. It is not polled. A genuine
integration therefore receives events; it does not detect them.

Our sandbox ENG is a closed emulated system that has no webhook to register against, so we
recover the same events by polling ENG's REST API and comparing against a local cache. This
is an **externalized outbox** — outbox semantics reconstructed from outside, because the
participant will not emit for us.

Two points follow, and they govern the whole design:

1. **Polling the API is not polling the database.** No credentials into the participant's
   estate, no schema changes, no CDC to get approved, and load is governed by the
   participant's own published API limits.
2. **The detector's output must be compatible with the real webhook payload.** The change
   detection service is a stand-in for a transport we cannot yet receive. If the envelope it
   emits matches what Bentley sends, then adopting the real webhook later is a transport
   swap and nothing downstream changes.

The change detection service is therefore a **seam**, explicitly marked as the component a
real deployment replaces.

```text
  TARGET (real Bentley)                    EMULATION (sandbox)

  +----------------+                       +----------------+
  |  ENG / iModels |                       |  ENG Provider  |
  +--------+-------+                       +--------+-------+
           | webhook push                           ^ poll REST API
           v                                        |
  +----------------+                       +--------+-------------+
  | Engine         |                       | Change Detection Svc |
  | (webhook sink) |                       | (emits same envelope)|
  +----------------+                       +--------+-------------+
                                                    |
                                                    v
                                            +----------------+
                                            | Engine         |
                                            +----------------+
```

---

## The event contract

The contract is defined by what Bentley emits. We conform to it rather than inventing our
own shape.

```json
{
   "content": {
        "changesetId": "254c63645055a96d4a920425f5dfbc3adec5b602",
        "changesetIndex": "1",
        "imodelId": "72640bf2-2173-4276-b896-c157bee0df76",
        "userId": "e5c7ae4f-2d72-4319-b96a-d46492e4f860",
        "versionId": "51c0ce59-a60d-47fc-a2e4-974712c707ce"
    },
    "eventType": "iModels.namedVersionCreated.v1",
    "enqueuedDateTime": "11/3/2023 8:07:01 PM",
    "iTwinId": "ce40a55a-6954-4de3-85c7-f796c3e423d9",
    "messageId": "32369e4b-c9ff-47e4-b422-83b97247ce5b",
    "webhookId": "7da4ba7e-3a35-4d97-a1b0-6d453aa97493"
}
```

Note what the payload does **not** contain: no elements, no segments, no design data. It
carries identity and intent only. This is deliberate and is preserved as a principle below.

### Why `namedVersionCreated` is the event

In Bentley's model a named version is an immutable snapshot pinned to a changeset. Creating
one **is** the deliberate handover act — there is no draft-then-release cycle to wait for.

Our emulation works the same way. A named version is a marker: it is pinned to a changeset
position at the moment it is created, and membership is derived from that position rather
than recorded against it. There is no gate and no validation step in ENG — validation belongs
to REG-LOCATION, and a defect is remedied by fixing ENG and creating a new named version.
Creating the marker is therefore the moment the detector publishes. Authoring elements
publishes nothing.

### Identifier mapping

The real contract is GUID-based and changeset-oriented. Our store is not. The emulation must
carry the following so a faithful envelope can be produced:

| Envelope field    | Source in emulation                                        |
|-------------------|------------------------------------------------------------|
| `iTwinId`         | `EngITwin.ITwinId` — already a GUID                        |
| `content.imodelId`| `EngIModel.IModelId` — already a GUID                      |
| `content.versionId`| **Requires a stable GUID per named version.** `EngNamedVersion.NamedVersionId` is a `long`; a GUID must be minted at creation and stored alongside it. |
| `content.changesetId` | **No native concept.** Synthesised and pinned when the marker is created. ENG-internal — see below. |
| `content.changesetIndex` | Monotonic counter per iModel. Also ENG-internal. |
| `content.userId`  | Creating principal, where known.                            |
| `messageId`       | Minted per publication attempt.                             |
| `webhookId`       | Fixed per subscription; synthetic in emulation.             |

Without the stored `versionId` GUID and `changesetId`, the envelope cannot be produced
faithfully and the later swap to a real webhook breaks. This is a schema change to the ENG
emulation, not to any customer system.

### `changesetId` is ENG-internal

`changesetId` and `changesetIndex` are meaningful **only inside ENG**. They describe a
position in one iModel's own history and mean nothing to any other participant.

They appear on the notification envelope because that envelope is Bentley's shape and we
conform to it rather than inventing our own. Their only legitimate use downstream is
correlation: telling the engine which marker to fetch, and letting it recognise that it is
working from a superseded event. That is the whole of it.

They must never cross into business traffic. Nothing the engine publishes to REG-LOCATION
over ISBM may carry a changeset identifier, and no participant may key, store or reconcile
against one. A consumer that persists a `changesetId` has coupled itself to ENG's internal
history, and will break the moment the emulation is swapped for a real iModel — at which
point the synthesised values it has been storing turn out to have meant nothing at all.

What travels between participants is the design data itself and the identifiers participants
share: codes, federation identifiers, and the named version's own GUID.

### Event types

Modelled on ENG's actual domain — iTwins, iModels, named versions, elements. There is no
asset or work order vocabulary in ENG; that belongs to other participants.

```text
iModels.namedVersionCreated.v1     Primary. A named version was created — the handover act.
iModels.changesetCreated.v1        Design content changed within an iModel.
iModels.iModelCreated.v1           A new model appeared under an iTwin.
```

---

## Thin event, then fetch back

The engine receives the event, then **queries the ENG API** for the full dataset — the
elements and segments belonging to the named version.

The change detection service holds cached ENG data in order to detect changes, so there is a
standing temptation to enrich the event from that cache. Do not. Keep the event thin:

- Fat events go stale between publication and consumption.
- Design data does not belong on a message backbone.
- Authorization stays on the fetch, where it can be enforced per consumer.

The event carries the `changesetId` / `versionId` the engine is expected to read. If the
fetch returns a different current version, the engine can detect that it is working from a
superseded event rather than silently processing stale data. This is correlation only: the
engine reads these values and discards them, and neither reaches REG-LOCATION.

The engine never touches the participant's database. Authenticated HTTP to the participant's
API is the only access path.

---

# Architecture

```text
                     +------------------+
                     |       ENG        |
                     |  REST CRUD APIs  |
                     +--------+---------+
                              ^
                              | Poll changes (emulation only;
                              | real ENG pushes a webhook)
                              |
+---------------------------------------------------+
| Change Detection Service            [SEAM]        |
|---------------------------------------------------|
| Azure Function (Timer Trigger)                    |
|                                                   |
| - Read changed records from ENG                   |
| - Compare with previously known state             |
| - Detect business events                          |
| - Publish Bentley-shaped envelope to Event Grid   |
| - Update local cache and checkpoint               |
+----------------------+----------------------------+
                       |
                       v
             +----------------------+
             | Integration Database |
             |----------------------|
             | NamedVersionCache    |
             | ElementCache         |
             | ChangeCheckpoint     |
             | BaselineState        |
             | PublishedEvent       |
             +----------+-----------+
                        |
                        v
                +---------------+
                | Event Grid    |   participant -> engine notification
                +-------+-------+
                        |
                        v
                +---------------+
                |  ENG Engine   |   fetches full data from ENG API,
                +-------+-------+   then translates
                        |
                        v
                +---------------+
                |     ISBM      |   participant -> participant business messages
                +-------+-------+
                        |
                        v
                 REG-LOCATION, CIR, CMS/MMS
```

## Channel separation

Two channels, with distinct responsibilities. They are not alternatives.

- **Event Grid** carries *participant to engine* notification. It signals "something
  committed in ENG, come and look". It is not a business channel and downstream participants
  do not subscribe to it.
- **ISBM** remains the *participant to participant* business channel, per the existing
  architecture and `ISBMProvider/Infrastructure/ServiceBusMessageBroker.cs`. All BOD traffic
  and all inter-participant flow rides ISBM.

Events must not fan out from Event Grid directly to consuming systems. The engine is the
only subscriber; ISBM is the onward path. Bypassing the engine would put raw
participant-shaped events onto consumers and defeat the translation boundary.

### The ISBM channel the engine publishes onto

Per [isbm-channel-naming-convention.md](../ISBM%20Channels/isbm-channel-naming-convention.md),
the channel is `/{enterprise}/{itwin-federation-id}/{domain}/{type}` — for ENG:

```
/acme/{itwin-federation-id}/engineering/publication
```

with topic `oiie:sc01/ccom:SyncSegments`.

The engine does not hold this URI in configuration. It holds the enterprise and
the domain, resolves the owning iTwin from ENG via `GET imodels/{iModelId}`, and
composes the rest. Configuring the iTwin alongside the iModel would create a pair
that can disagree, and that particular disagreement delivers a handover to the
wrong twin's subscribers while looking, from the engine's side, like a successful
publication.

### How ENG's identity travels in the BOD

ENG's native key is a composite — `iModelId`, `ECInstanceId`, `CodeValue` — and no
single part identifies an element on its own: `ECInstanceId` is unique only within one
iModel, and `CodeValue` can repeat across the iModels of one iTwin.

CCOM has no elements by those names, so the parts map onto CCOM's existing identity
fields. The authoritative shape is
[SyncSegmentsWithoutAttributes.xml](../Sample%20BODs/SyncSegmentsWithoutAttributes.xml),
which validates against `CCOM.xsd`:

| ENG value | CCOM field |
|---|---|
| FederationGuid | `Segment/UUID` |
| iModelId | `Segment/InfoSource/UUID` |
| ECInstanceId | `Segment/IDInInfoSource` |
| CodeValue | `Segment/ShortName` |
| UserLabel | `Segment/FullName` |

`Segment/UUID` is the FederationGuid unaltered, because that value becomes the CIRID
and every participant registers its own key against it. An element with no
FederationGuid is not published at all — see DR-017.

`InfoSource/UUID` carries the iModelId rather than an identifier for "ENG" as a kind
of system. The distinction is easy to get wrong and expensive to find: a well-formed
UUID derived from the string `ENG` looks correct on inspection, but leaves a receiver
holding element `44732` with no way to say which iModel to open it in, which is
precisely what rule 8 of the FederationGuid guideline promises it can do.

`Segment/Type` carries its own `InfoSource`, not the segment's. Once `InfoSource/UUID`
means "the iModel", sharing it would assert that the EC class name and the
`ECInstanceId` are two identifiers within one source, and a receiver composing the
composite key from that pair would build one that resolves to nothing.

Action codes apply to the whole `Segments` collection through the OAGIS
`ActionExpression`, not to individual segments. The engine publishes `Replace`, since
receivers upsert on the sender's identifier and `Replace` makes republication after a
failed drain idempotent rather than duplicating rows.

The federation id is used rather than a readable site name because names change.
A corridor renamed or a project re-scoped would break every subscription pointing
at it; the UUID is assigned once and never moves. The readable name belongs in the
channel's description field.

---

# High-Level Flow

## Initial load (baseline)

On first execution:

1. Query all relevant iTwins, iModels, named versions and elements from ENG.
2. Populate the local cache.
3. Record an explicit baseline completion row.
4. Do **not** publish events during baseline.

### Re-baselining is an explicit operation

An empty cache must never implicitly mean "suppress everything". If the cache is lost or a
redeploy starts empty, an implicit re-baseline would silently swallow every change that
occurred while the service was down — a total, undetectable data loss.

The rule:

```text
cache empty AND no prior baseline recorded   -> baseline, publish nothing
cache empty AND prior baseline recorded      -> REFUSE TO START
cache populated                              -> normal incremental processing
```

Recovery from the refusal state is an operator decision: either restore the cache from
backup, or deliberately re-baseline by setting an explicit override flag, accepting that the
gap will not be published. The refusal is the point — it forces the loss to be a decision
rather than an accident.

```sql
CREATE TABLE BaselineState
(
    IntegrationName      VARCHAR(100)  NOT NULL,
    BaselineStartedUtc   DATETIME2     NOT NULL,
    BaselineCompletedUtc DATETIME2     NULL,
    OverrideReason       NVARCHAR(500) NULL
);
```

## Incremental processing

On each polling cycle:

1. Query ENG for records changed since the checkpoint, **less an overlap margin**.
2. Compare returned values with cached values.
3. Detect newly created named versions.
4. Publish corresponding events to Event Grid.
5. Update the cache and advance the checkpoint.

---

# Change Detection

Timestamp windowing and hash comparison are **one mechanism, not two options**. The overlap
is what makes the window safe; the hash is what makes the overlap harmless.

## Overlapping watermark

A naive `modifiedSince = LastProcessedUtc` silently loses records:

- Rows committed during the query window but stamped just before it.
- Clock skew between ENG and the detection service.
- Ties exactly on the boundary.

Instead, query with a safety margin and treat the checkpoint as a *safe watermark* rather
than "last seen":

```http
GET /elements?modifiedSince={LastProcessedUtc - OverlapMinutes}
```

`OverlapMinutes` of 2 is a reasonable starting point and should exceed expected clock skew
plus maximum transaction duration.

This deliberately re-reads records already seen. That is safe only because of the hash
comparison below, which suppresses the resulting duplicates.

## Hash comparison

For each returned record, hash the attributes that matter:

```text
NamedVersionId, VersionGuid, Name, IModelId, ChangesetIndex
ECInstanceId, CodeValue, UserLabel, DisplayName, ParentECInstanceId, ChangesetIndex
```

```text
SHA256(current values) != cached HashValue   -> genuine change, publish
SHA256(current values) == cached HashValue   -> re-read from overlap, suppress
```

For named versions the hash is a formality: a marker is immutable, so a version already in
the cache can only be a re-read from the overlap. What the detector is really watching for is
a `NamedVersionId` it has never seen. There is no state transition to compare, because there
is no state — the marker's appearance *is* the event.

## Cache schema

```sql
CREATE TABLE NamedVersionCache
(
    NamedVersionId    BIGINT           NOT NULL,
    VersionGuid       UNIQUEIDENTIFIER NOT NULL,
    IModelId          UNIQUEIDENTIFIER NOT NULL,
    Name              NVARCHAR(200)    NULL,
    ChangesetId       VARCHAR(64)      NOT NULL,
    ChangesetIndex    INT              NOT NULL,
    HashValue         VARCHAR(64)      NULL,
    LastModifiedUtc   DATETIME2        NULL,
    LastSeenUtc       DATETIME2        NOT NULL
);

CREATE TABLE ElementCache
(
    ECInstanceId      BIGINT           NOT NULL,
    IModelId          UNIQUEIDENTIFIER NOT NULL,
    ChangesetIndex    INT              NOT NULL,
    CodeValue         NVARCHAR(200)    NULL,
    HashValue         VARCHAR(64)      NULL,
    LastModifiedUtc   DATETIME2        NULL,
    LastSeenUtc       DATETIME2        NOT NULL
);

CREATE TABLE ChangeCheckpoint
(
    IntegrationName   VARCHAR(100)     NOT NULL,
    LastProcessedUtc  DATETIME2        NOT NULL,
    OverlapMinutes    INT              NOT NULL DEFAULT 2
);
```

The integration database is a cache and processing store. It is **never** a system of record,
and nothing may read business truth from it.

---

# Reliability

## At-least-once delivery

Delivery is at-least-once. Consumers must be idempotent.

The envelope `messageId` is **not** a usable idempotency key — it is minted per publication
attempt and changes on republish. Consumers must key on the business transition:

```text
idempotency key = source + iTwinId + imodelId + versionId + eventType
```

Processing the same key twice must be a no-op.

## Ordering

Events may arrive out of order or be redelivered. The engine cannot assume that a named
version event arrives after the iModel event it depends on.

- Order within an iModel is established by `changesetIndex`, not by arrival time or
  `enqueuedDateTime`.
- If a prerequisite has not arrived, the engine defers the event and retries rather than
  failing permanently or fabricating the missing parent.
- Because the engine fetches current state from the API, a late event whose data has since
  been superseded is detectable by comparing the fetched version against the event's
  `versionId`.

## Failure recovery

If Event Grid is unavailable:

1. Persist the event as `Pending` in `PublishedEvent`.
2. Retry according to policy.
3. Mark `Published` on success.

The checkpoint must not advance past events that are still `Pending`, otherwise a crash
between publication and checkpoint advance loses the change permanently.

```sql
CREATE TABLE PublishedEvent
(
    MessageId         UNIQUEIDENTIFIER NOT NULL,
    IdempotencyKey    VARCHAR(300)     NOT NULL,
    EventType         VARCHAR(100)     NOT NULL,
    Payload           NVARCHAR(MAX)    NOT NULL,
    State             VARCHAR(20)      NOT NULL,
    Attempts          INT              NOT NULL DEFAULT 0,
    CreatedUtc        DATETIME2        NOT NULL,
    PublishedUtc      DATETIME2        NULL
);
```

---

# Polling cadence

Determined by business latency requirements, ENG API limits and expected volumes.

```text
Every 30 seconds    demo and interactive scenarios
Every 1 minute      default
Every 5 minutes     high volume or constrained API quotas
```

Cadence is an emulation concern only. Under the real webhook it disappears entirely.

---

# Key Architectural Principles

1. **ENG remains the authoritative system of record.**
2. **No modifications are made to ENG databases, schemas or application code.** The
   emulation's own schema additions for `versionId` and `changesetId` are additions to our
   simulated ENG, not to any customer system.
3. **All integration occurs through supported ENG REST APIs.** Never the database.
4. **The triggering event is committed state, never a UI action.**
5. **Events carry identity and intent only; the engine fetches the full dataset from the
   participant API.**
6. **Events represent business actions, in ENG's own domain vocabulary.**
7. **`changesetId` and `changesetIndex` never leave the notification path.** They are ENG's
   internal history, carried on the envelope for correlation only, and must not appear in
   business traffic or be stored by any consumer.
8. **The change detection service is a seam** standing in for a webhook, and emits the exact
   envelope the real system emits.
9. **Event Grid notifies the engine; ISBM carries business traffic between participants.**
10. **The integration database is a cache and processing store, never a system of record.**
10. **Baseline suppression is explicit and auditable; an empty cache never silently
    suppresses an outage's worth of change.**
