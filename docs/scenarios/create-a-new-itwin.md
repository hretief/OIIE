# SyncSites Workflow Specification

## Purpose

When a new iTwin (Site) is created on the Bentley iTwin platform, the site context must
propagate through the OIIE ecosystem before any engineering Segments can flow. This
document specifies the complete SyncSites workflow — the events, data transformations,
identity registrations, and channel provisioning that establish a site in REG-LOCATION.

**This is a prerequisite for SyncSegments.** Without a Site established in REG-LOCATION,
there is no Scope to hold functional locations, and the per-iTwin ISBM channels do not exist.

---

## Trigger: iTwin Created

A project manager creates a new iTwin on the Bentley platform. iTwins are either:
- **Thing / Asset** — a facility (as-built, permanent, operational)
- **Endeavor / Project** — a project (as-designed, temporary, design-phase)

The platform emits an `iTwins.iTwinCreated.v1` event:

```json
{
   "content": {
        "eventCreatedBy": "6411cfcf-...",
        "parentId": "8f6bd249-...",
        "iTwinClass": "Endeavor",
        "iTwinSubClass": "Project"
    },
    "eventType": "iTwins.iTwinCreated.v1",
    "iTwinId": "ce40a55a-6954-4de3-85c7-f796c3e423d9"
}
```

The event carries the `iTwinId` (the Federation ID) and the class/subclass, but not
the name, type, or description. Those require a callback.

---

## Step-by-Step Flow

### Step 1: ENG Engine Receives the Event

The ENG Engine receives the iTwin.Created event via webhook (or bootstrap endpoint
during development). The event tells ENG that a new site exists but not what it is.

### Step 2: ENG Engine Calls Back for Details

ENG calls the iTwin Platform API to get the full site information:

```
GET https://api.bentley.com/iTwins/{iTwinId}
```

Response:

```json
{
  "id": "ce40a55a-6954-4de3-85c7-f796c3e423d9",
  "class": "Endeavor",
  "subClass": "Project",
  "type": "Highway",
  "displayName": "US Route 202",
  "number": "HWYUSR202",
  "status": "active"
}
```

### Step 3: ENG Engine Compiles a SyncSites BOD

ENG transforms the iTwin details into a CCOM SyncSites BOD with the following field mapping:

| SyncSites field | Source | Example | Notes |
|---|---|---|---|
| `Site.UUID` | `iTwinId` | `ce40a55a-...` | The Federation ID. Immutable. Becomes the CIRID. |
| `Site.ShortName` | `displayName` | `US Route 202` | Human-readable name |
| `Site.FullName` | `displayName` + `number` | `US Route 202 — HWYUSR202` | Extended name with project number |
| `Site.Description` | Generated or from metadata | Free text | |
| `Site.Type.UUID` | `iTwin.iTwinTypeId` | `3f2b8c14-...` | See "Site Type Identity" below |
| `Site.Type.ShortName` | `type` | `District` | The twin's boundary |
| `Site.Type.FullName` | Label for the boundary | `A District of the State` | Not composed with `subClass` |

**What `type` means:** `type` names the *boundary* of the digital twin — what the
twin is drawn around. It is owner-defined free text, not a closed vocabulary: a DOT
scopes twins to a `District` because that is the area it manages, while an operator
might scope one to a `Plant` or a `Highway`. It is a separate axis from
`class`/`subClass`, which say where the twin sits on the lifecycle (`Thing`/`Endeavor`
× `Asset`/`Project`).

`Site.Type.FullName` labels the boundary alone. It is deliberately *not* `type +
subClass`: a District is the same District whether this twin is the asset or the
project delivering it, so folding the lifecycle axis into the label would give one
boundary two names.

**Site Type Identity:** the boundary is a row in `dbo.iTwinType` — `iTwinTypeId`
and `Number` — and `dbo.iTwin.iTwinTypeId` references it, many twins to one
boundary. `Site.Type.UUID` is that stored id, carried through unchanged, so the
second District classifies as the same `District` as the first; otherwise
REG-LOCATION holds two item rows nothing can tell apart.

The id is **stored rather than derived from the name**. Deriving it would need no
table, but it would make the identity a function of the spelling: correcting a
boundary's name would silently reclassify every site already published under the
old one. Storing it means a rename touches `Number` and leaves `iTwinTypeId`
alone.

`District` is bootstrapped in `schema.sql` with a fixed id, because the iTwin
Platform has no UI for `Type` and the value cannot be entered at source. The seed
is reapplied on every schema run — including against a database that has the table
but has lost the row — so a day-zero reset reproduces the same identity rather
than handing REG-LOCATION a second District.

The reference is nullable: a twin registered without a boundary is skipped by the
publisher rather than published with an empty classification.

> Mapping an arbitrary free-text `type` onto an `iTwinTypeId` is not yet
> implemented, and is deferred while `District` is the only boundary in play. See
> [open-items.md](../open-items.md).

### Step 4: ENG Engine Publishes to ISBM

ENG publishes the SyncSites BOD to the enterprise-level sites channel:

```
Channel:  /{enterprise}/enterprise/sites/publication
Topic:    oiie/ccom:SyncSites
```

This channel is at the enterprise level because it creates iTwin contexts — it cannot be
scoped to an iTwin that doesn't exist yet. This is the one flow where the enterprise-level
channel is structurally required.

### Step 5: ALIM Engine Receives Notification

ALIM Engine subscribes to the enterprise sites channel with topic `oiie/ccom:SyncSites`.
When ENG publishes, ALIM receives an ISBM webhook notification, reads the BOD, and
processes it.

### Step 6: ALIM Creates the Scope

The Scope is the organizational container in REG-LOCATION. It represents the iTwin as
a working context within which all subsequent segments, functional locations, and
structures will be registered.

| REG-LOCATION entity | Value | Source |
|---|---|---|
| `scope.guid` | `ce40a55a-...` | `Site.UUID` (= iTwinId = Federation ID) |
| `scope.name` | `US Route 202` | `Site.ShortName` |

If a Scope with this GUID already exists, update its name/description (idempotent).

### Step 7: ALIM Creates the Item (Site Type)

The Item represents the **type** of facility or project — not the instance. Multiple
sites can share the same type (e.g., all Highway projects reference the same Item).

| REG-LOCATION entity | Value | Source |
|---|---|---|
| `item.guid` | `f7e8d9c0-...` | `Site.Type.UUID` |
| `item.code` | `Highway` | `Site.Type.ShortName` |
| `item.description` | `Highway Project` | `Site.Type.FullName` |
| `item.type` | `U` (Unit/Site) | Fixed for sites |

If an Item with this GUID already exists, update its properties (idempotent).

### Step 8: ALIM Creates the Serial (Site Instance)

The Serial represents **this specific site** — the instance of the type. It is the
physical thing (or project) that the iTwin represents.

| REG-LOCATION entity | Value | Source |
|---|---|---|
| `serial.guid` | `ce40a55a-...` | `Site.UUID` (same GUID as the Scope) |
| `serial.item` | Item from Step 7 | The type this instance belongs to |
| `serial.name` | `US Route 202` | `Site.ShortName` |
| `serial.description` | `US Route 202 — HWYUSR202` | `Site.FullName` |

The Scope and the Serial share the same GUID because they represent the same thing from
two perspectives: the Scope is the organizational context, the Serial is the asset identity.

### Step 9: ALIM Links the Serial to the Scope

The Scope's default object is set to the Serial (site instance):

```
scope.object_id = serial.id
scope.object_type = 18 (serial object type)
```

This establishes the Scope as "the context for this site instance."

### Step 10: ALIM Creates the Systems Structure

A container document for the plant breakdown hierarchy. All segments and functional
locations within this site will be organized under this structure.

| REG-LOCATION entity | Value | Source |
|---|---|---|
| `structure.guid` | Deterministic, derived from `Site.UUID` | Same site → same structure GUID |
| `structure.name` | `US Route 202` | `Site.ShortName` |
| `structure.class` | `SYSTEM_STRUCTURE` | Fixed |

### Step 11: ALIM Registers the Site in the CIR

ALIM registers the site's identity in the CIR so other systems can discover it:

```
ALIM → CIR: ProcessRegistry (CIRID = Site.UUID = iTwinId)
  Entry: {iTwinId}          Category: ITWIN-SITE
  Entry: {Site.Type.UUID}   Category: SITE-TYPE
```

### Step 12: ALIM Bootstraps Per-iTwin ISBM Channels

Now that the site exists in REG-LOCATION, the per-iTwin channels can be created:

```
/{enterprise}/{iTwinId}/engineering/publication
/{enterprise}/{iTwinId}/asset-config/publication
/{enterprise}/{iTwinId}/maintenance/publication
/{enterprise}/{iTwinId}/maintenance/request
/{enterprise}/{iTwinId}/condition/publication
```

These channels use the iTwinId (Federation ID) as the stable URI segment, following
the channel naming convention. The SyncSegments flow will use these channels.

Channel creation is idempotent — if the channel already exists (e.g., from a
reprocessed SyncSites), the ISBM provider returns 422 and the bootstrapper skips it.

### Step 13: ALIM Removes the Message from ISBM

ALIM calls RemovePublication to acknowledge receipt. The SyncSites BOD is consumed.

---

## What This Enables

After the SyncSites workflow completes, the following are true:

| What exists | Where | Purpose |
|---|---|---|
| Scope (iTwinId) | REG-LOCATION | Organizational context for all segments |
| Item (Site Type) | REG-LOCATION | Classification of this site |
| Serial (Site Instance) | REG-LOCATION | Asset identity of this specific site |
| Systems Structure | REG-LOCATION | Container for the breakdown hierarchy |
| CIR entry (CIRID = iTwinId) | CIR | Cross-system identity for the site |
| Per-iTwin ISBM channels | ISBM Provider | Communication paths for SyncSegments and all subsequent flows |

**The SyncSegments flow can now begin.** When ENG publishes engineering Segments
(triggered by a Named Version event in the iModel), the Segments reference the Scope
that SyncSites created. ALIM receives them, maps them to functional locations within
the site's breakdown structure, and publishes approved segments to subscribers.

---

## Identity Summary

Two GUID identity relationships are established by this workflow:

| GUID | Assigned by | Represents | Appears as |
|---|---|---|---|
| `iTwinId` (Site.UUID) | iTwin Platform | The site itself | Scope GUID, Serial GUID, CIRID, channel URI segment |
| `Site.Type.UUID` | ENG Engine (generated, reused per type) | The type of site | Item GUID, CIR entry |

The iTwinId serves quadruple duty — it is the Scope identity, the Serial identity, the
CIRID in the CIR, and the Federation ID in the channel URI. This is by design: one UUID
identifies the site everywhere, in every system, forever.

---

## ISBM Channels Involved

| Channel | Level | Type | Publisher | Subscriber |
|---|---|---|---|---|
| `/{enterprise}/enterprise/sites/publication` | Enterprise | Publication | ENG Engine | ALIM Engine |
| `/{enterprise}/enterprise/cir/request` | Enterprise | Request | ALIM Engine | CIR |
| `/{enterprise}/{iTwinId}/engineering/publication` | Per-iTwin | Publication | ENG Engine | ALIM Engine |
| `/{enterprise}/{iTwinId}/asset-config/publication` | Per-iTwin | Publication | ALIM Engine | MMS, CMS |

The first two exist before any iTwin is created (provisioned at deployment time).
The per-iTwin channels are created by Step 12, after the site exists in REG-LOCATION.

---

## Idempotency Rules

Every step must produce the same result if executed twice with the same input:

| Step | Idempotency key | On duplicate |
|---|---|---|
| Create Scope | `objects.guid = Site.UUID` | Update name |
| Create Item (type) | `objects.guid = Site.Type.UUID` | Update name |
| Create Serial | `objects.guid = Site.UUID` | Update name |
| Link Serial → Scope | `scope.object_id` | No-op |
| Create Structure | `objects.guid = derived GUID` | Update name |
| CIR registration | `CIRID = Site.UUID` | CIR upserts |
| Channel bootstrap | `channelUri` | ISBM returns 422, skip |

At the message level: de-duplicate on `BODID`. If the same SyncSites BOD arrives twice
(ISBM redelivery after a crash), skip the entire processing on the second delivery.

---

## Triggering from the UI

The workflow UI is the ordinary way to start this flow during a demo. The iTwin carousel
shows only the twins that ENG actually holds; a `+` button at the end of it opens a picker
listing platform Asset iTwins that are **not** yet in the sandbox.

Choosing one posts to the Sandbox API:

```
POST /admin/eng/itwins/add
{ "iTwinId": "...", "displayName": "...", "number": "...",
  "twinClass": "Thing", "subClass": "Asset", "twinType": "Asset" }
```

The Sandbox API is the browser's only backend. It registers the twin locally, then makes
the two ENG Functions calls on the caller's behalf — `POST /itwins` on the ENG provider,
followed by `POST /bootstrap/itwin-created` on the ENG Engine — so the function keys and
the ordering stay server-side.

Registration and publication are reported separately because they fail separately. If the
twin is stored but SyncSites could not be published, the UI says so plainly rather than
reporting the add as a failure; the twin is in the sandbox either way, and re-adding it
retries the publication.

---

## Bootstrap for Development

Until the real iTwin webhook is connected, simulate the event by posting the same JSON
payload to a bootstrap endpoint on the ENG Engine:

```powershell
$body = @{
    content = @{
        iTwinClass = "Endeavor"
        iTwinSubClass = "Project"
    }
    eventType = "iTwins.iTwinCreated.v1"
    iTwinId = "ce40a55a-6954-4de3-85c7-f796c3e423d9"
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method POST `
    -Uri "http://localhost:7254/api/bootstrap/itwin-created" `
    -Headers @{ "Content-Type" = "application/json" } `
    -Body $body
```

The bootstrap endpoint accepts the same format as the real webhook. When ready to go live,
register the webhook URL with the iTwin platform and the handler is identical — switching
from bootstrap to live is a URL change, not a code change.

---

## Verification Checklist

After running the SyncSites workflow (UI, bootstrap, or live), verify:

1. **ISBM:** SyncSites BOD was published to `/{enterprise}/enterprise/sites/publication`
2. **REG-LOCATION database:** Scope exists with `guid = iTwinId`
3. **REG-LOCATION database:** Item exists with `guid = Site.Type.UUID`
4. **REG-LOCATION database:** Serial exists with `guid = iTwinId`, linked to Item
5. **REG-LOCATION database:** Scope.object_id = Serial.id
6. **REG-LOCATION database:** Systems Structure document exists under the Scope
7. **CIR:** Entry exists with `CIRID = iTwinId`, entries for ITWIN-SITE and SITE-TYPE
8. **ISBM:** Per-iTwin channels created (`engineering`, `asset-config`, `maintenance`, `condition`)
9. **ISBM:** SyncSites message removed from the subscription (no orphaned messages)