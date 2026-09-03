# SyncSites Delete Workflow — Cascaded Site Removal

> **Status: the REG-LOCATION leg is implemented and verified end to end. MMS and CMS are not.**
>
> Steps 1-6, 9 and 10 are built, deployed and exercised against dev: ENG Provider
> removes the twin, ENG Engine announces it, and REG-LOCATION Engine cancels the
> site's CIR entries, cascades the scope delete, and tears down the per-iTwin
> channels. Steps 7 and 8 (MMS, CMS) have no handlers.
>
> A verified run reports `sitesRemoved:1, cirEntriesRemoved:1, channelsRemoved:2,
> failed:0`, after which the CIR query returns empty, the scope is gone, both
> channels 404, and REG-LOCATION health shows `orphanedRows:0`. The scope-id
> reuse case is verified explicitly: after deleting the highest scope, the next
> site claims the same id and registers in CIR cleanly.
>
> Steps below are marked **[current]** where an implementation exists and
> **[target]** where none does. The identifiers and channel URIs are written to
> match the code that registers them, because a delete that does not mirror its
> registration exactly will match nothing — see *Identifier Formats*.
>
> Entry points: `DELETE /admin/eng/itwins/{iTwinId}` on the sandbox API drives
> the whole flow. `DELETE /scopes/{scopeId}/cascade` on the REG-LOCATION
> Provider removes registry rows alone, without touching CIR or ISBM.

## Context

When an iTwin/Site is deleted in ENG, the deletion must propagate through the OIIE
ecosystem: REG-LOCATION must remove the site and all its contents, the CIR must be
cleaned of all identity registrations for that site, and downstream subscribers (MMS, CMS)
must remove their records and deregister from the CIR.

The SyncSites (Delete) flow mirrors the SyncSites (Add) flow in reverse. The same
channels, the same participants, the same engines — but with a `Delete` verb that
triggers cascaded removal instead of provisioning.

---

## Provider vs. Engine Distinction

This distinction is critical throughout:

| Concept | What it is | What it can do |
|---|---|---|
| **Provider** | The customer product (e.g., the REG-LOCATION database, the MMS application) | Manipulate its own content only. We cannot add or assume functionality within it. |
| **Engine** | The integration adapter between ISBM and the Provider | Listens for ISBM events, reads/writes the Provider's data through its existing interfaces, publishes to ISBM, interacts with the CIR. |

When this document says "REG-LOCATION Engine sends CancelEntries to the CIR," it means:
- The **Engine** (our code) calls the CIR via ISBM
- The **Engine** then calls the REG-LOCATION **Provider** through its existing stored procedures or APIs to delete the scope, items, and serials
- The **Provider** itself has no knowledge of ISBM or CIR

---

## Identifier Formats

A `CancelEntries` matches on the full five-part key. Get any part wrong and it
matches nothing, so these must mirror `SiteIngestionService.RegisterInCirAsync`
exactly. The trap is the GUID:

| Field | Value | Notes |
|---|---|---|
| `RegistryID` | `acme` | `Options.Enterprise`. Not a per-site value. |
| `CategoryID` | `ITWIN-SITE` / `SITE-TYPE` | Only these two are registered today. |
| `CategorySourceID` | `REG-LOCATION` | `Options.SourceId`. Required — part of the key. |
| `EntryIDInSource` | **the integer `scopeId` / `itemId`** | *Not* the GUID. |
| `EntrySourceID` | `REG-LOCATION` | `Options.SourceId`. |

**The GUID is not the key.** The site's `iTwinId` is written to the entry's
`Cirid` field; `EntryIDInSource` carries the REG-LOCATION integer id. A delete
built around the GUID will raise `EntryNotFoundFault` for every entry while the
rows sit untouched. To find the entries you must first read the scope and its
item from the Provider and use their ids.

**Scope ids are reused.** `NextIdAsync` allocates `MAX(id) + 1`, so a deleted
scope's id is handed to the next site created. Because `ITWIN-SITE` keys on that
id, an entry missed during deletion is not merely litter: the next site to claim
the id collides with it, and its own registration fails with a conflict. This is
why CIR deregistration is not optional cleanup — leaving it undone breaks a
later, unrelated create.

### Category source vs. entry source

These two fields look interchangeable and are not. Conflating them is what makes
ownership ambiguous:

| Field | Answers |
|---|---|
| `CategorySourceID` | Who **owns and maintains this category** — and therefore who may delete from it |
| `EntrySourceID` | Whose **namespace the identifier belongs to** |
| `SourceOwnerId` | Who **wrote this particular row** |

An entry may carry an identifier minted elsewhere while still belonging to the
engine that filed it. `im-9340-mech::44732::TIC-106` is an ENG identifier, but
the row asserting *"this ENG tag corresponds to this CIRID"* is REG-LOCATION's
assertion about ENG's data, not ENG's own registration. So `EntrySourceID: ENG`
and `CategorySourceID: REG-LOCATION`.

`SITE-TYPE` already works this way: the site type originates in ENG, but the
category is REG-LOCATION's index of it.

---

## Trigger

The iTwin/Site is deleted on the Bentley platform. This emits an event that ENG Engine
receives (via webhook or bootstrap). ENG Engine publishes a SyncSites BOD carrying the
`Delete` action code to the enterprise sites channel.

In the sandbox there is no platform webhook, so `DELETE /admin/eng/itwins/{iTwinId}`
stands in for it. That route removes the twin from the ENG Provider first — the
provider is the system of record — and announces the removal only once the
provider has confirmed it. Announcing first would ask the ecosystem to tear down
a site ENG might then refuse to delete, and ENG does refuse: a twin still holding
iModels returns 409 rather than cascading the design away with the project.

---

## Step-by-Step Flow

### Step 1: ENG Engine Publishes SyncSites (Delete) — [current]

ENG Engine receives the iTwin deletion event, compiles a SyncSites BOD carrying the
`Delete` action code, and publishes it:

```
Channel:  /{enterprise}/enterprise/sites/publication
          e.g. /acme/enterprise/sites/publication
Topic:    oiie/ccom:SyncSites
Verb:     Sync, with actionCode="Delete" on the ActionExpression
```

The literal `enterprise` sits where a federation id sits on every other channel,
because this message is what creates or destroys an iTwin context and so cannot
be addressed to one.

The BOD carries only the `Site.UUID` (= iTwinId / FederationId). No other detail is
needed — the UUID is sufficient to identify everything that must be removed, and
anything else could only agree with what the receiver already holds or be wrong.

```xml
<SyncSites>
  <oa:ApplicationArea>
    <oa:BODID>delete-site-a1b2c3d4-...</oa:BODID>
  </oa:ApplicationArea>
  <DataArea>
    <oa:Sync>
      <oa:ActionCriteria>
        <oa:ActionExpression actionCode="Delete"
                             expressionLanguage="Xpath">/SyncSites/DataArea/Sites</oa:ActionExpression>
      </oa:ActionCriteria>
    </oa:Sync>
    <Sites>
      <Site>
        <UUID>ce40a55a-6954-4de3-85c7-f796c3e423d9</UUID>
      </Site>
    </Sites>
  </DataArea>
</SyncSites>
```

**Why the Sync verb and not a `<oa:Delete/>` verb element.** An earlier draft of
this document showed a root of `SyncSites` containing an `oa:Delete` verb. That
shape cannot be read. OAGIS derives the noun by stripping the verb name from the
root element name, so `SyncSites` + verb `Delete` yields the noun `SyncSites`
rather than `Sites`, matching no handler — and because an unrecognised BOD is
skipped rather than failed, the receiver would report success having done
nothing. Keeping the `Sync`/`Sites` pair intact and varying the action code is
both what the action code exists for and the only form that survives parsing.
`ActionCodes.Delete` already existed for exactly this purpose.

### Step 2: REG-LOCATION Engine Receives the Delete — [current]

REG-LOCATION Engine subscribes to `/{enterprise}/enterprise/sites/publication` with
topic `oiie/ccom:SyncSites`. It receives the webhook notification, reads the BOD,
and detects the `Delete` verb.

### Step 3: REG-LOCATION Engine Queries the Provider for All Segments in This Site — [current, partial]

Before anything can be deleted, the Engine must know what exists under this site. The
Engine queries the REG-LOCATION Provider for all segments (functional locations) registered
under the Scope identified by `Site.UUID`.

This query also yields the two integer ids the CIR keys are built from — the
`scopeId` and the site type's `itemId` — which Step 4 cannot proceed without:

```
Engine → Provider: "Give me the scope where guid = ce40a55a-..., and its contents"

Provider returns:
  Scope:  scopeId = 7, itemId = 12 (site type)
  LOC-000001 (FederationGuid: 550e8400-..., EngTag: TIC-106)
  LOC-000002 (FederationGuid: 661f9511-..., EngTag: FV-201)
  LOC-000003 (FederationGuid: 772a0622-..., EngTag: PT-305)
```

The Engine now has the integer ids the CIR entries are keyed on, plus the list of
FederationGuids for the segments.

### Step 4: REG-LOCATION Engine Deregisters from the CIR — [current, partial]

The REG-LOCATION Engine sends a `CancelEntries` BOD to the CIR via the ISBM request
channel, deleting **only the categories it owns** — those whose
`CategorySourceID` is its own `REG-LOCATION`.

Today that is two categories: `ITWIN-SITE` and `SITE-TYPE`. The `ENG-IMODEL` and
`FUNCTIONAL-LOC` entries below are **[target]** — the segment registration that
would create them does not exist yet, so there is currently nothing to cancel:

```
REG-LOCATION Engine → CIR (via /acme/enterprise/cir/request):

CancelEntries:
  DeleteEntries:
    EntryIdentifier:                      # [current] the site itself
      RegistryID: acme
      CategoryID: ITWIN-SITE
      CategorySourceID: REG-LOCATION
      EntryIDInSource: 7                  # the scopeId -- NOT the GUID
      EntrySourceID: REG-LOCATION

    EntryIdentifier:                      # [target] per segment
      RegistryID: acme
      CategoryID: FUNCTIONAL-LOC
      CategorySourceID: REG-LOCATION
      EntryIDInSource: LOC-000001
      EntrySourceID: REG-LOCATION

    EntryIdentifier:                      # [target] ENG composite key
      RegistryID: acme
      CategoryID: ENG-IMODEL
      CategorySourceID: REG-LOCATION      # REG-LOCATION maintains this category
      EntryIDInSource: im-9340-mech::44732::TIC-106
      EntrySourceID: ENG                  # the identifier is ENG's
```

The `ENG-IMODEL` block is the one worth pausing on. The identifier is ENG's, but
the category is REG-LOCATION's cross-reference index, so `CategorySourceID` is
`REG-LOCATION` and REG-LOCATION deletes it under the ownership rule rather than
as an exception to it. See *Category source vs. entry source*.

The site type is deregistered **only if this was the last site using it**:

```
CancelEntries:
  DeleteEntries:
    EntryIdentifier:
      RegistryID: acme
      CategoryID: SITE-TYPE
      CategorySourceID: REG-LOCATION
      EntryIDInSource: 12                 # the itemId -- NOT Site.Type.UUID
      EntrySourceID: REG-LOCATION
```

The guard matters and is easy to miss. A site type is shared: `ITWIN-SITE` is an
instance, `SITE-TYPE` is a classification, and Step 5 deletes the item only when
no other site references it. Cancelling the `SITE-TYPE` entry unconditionally
would strip the CIR identity from a type that is still in use by other sites,
leaving a live item no consumer can resolve. Apply the same last-reference test
to both, or skip the `SITE-TYPE` cancel entirely.

### Step 5: REG-LOCATION Engine Deletes from the Provider — [current]

This step exists today as `DELETE /scopes/{scopeId}/cascade`, which performs the
whole sequence in one transaction:

```
Engine → Provider: DELETE /scopes/{scopeId}/cascade

  1. Delete child scopes first, depth-first (scopes.parent_id is a real FK)
  2. Clear the scope's context pointer (object_id / object_type → NULL)
  3. Delete tags, then serials, then items
  4. Delete the scope
```

The order is forced by the schema, not chosen. The context pointer must be nulled
before the serial it references can go, or `FK_scopes_context_object` refuses the
delete. Items are deleted **only when no surviving scope references them** — the
same guard Step 4 applies to the `SITE-TYPE` entry.

The route returns the counts it deleted (`tagsDeleted`, `serialsDeleted`,
`itemsDeleted`, `scopesDeleted`), which is the only evidence afterwards of how
much the call actually destroyed.

The plain `DELETE /scopes/{scopeId}` is **not** a cascade: it refuses with 409 if
the scope still has contents. That refusal is deliberate — cascading is a
separate route so that destroying a site's contents cannot happen by omitting a
parameter.

### Step 6: REG-LOCATION Engine Publishes SyncSites (Delete) for Downstream Subscribers — [target]

REG-LOCATION Engine publishes a SyncSites (Delete) BOD to its own outbound channel so
MMS and CMS know to clean up their records:

```
Channel:  /{enterprise}/{iTwinFederationId}/{Domain}/publication
          e.g. /acme/ce40a55a-6954-4de3-85c7-f796c3e423d9/operations/publication
Topic:    oiie/ccom:SyncSites
Verb:     Delete
```

This is `Options.ChannelUriFor(siteGuid)` — derive it, do not spell it out. The
domain segment is `Options.Domain`, which defaults to `operations`. It is the
same channel the Add flow publishes approved segments on, and it is one of the
two channels bootstrapped when the site was created.

This BOD carries the Site.UUID and the list of FederationGuids (segments) that were
deleted, so downstream subscribers know exactly which of their records to remove:

```xml
<SyncSites>
  <DataArea>
    <oa:Delete/>
    <Sites>
      <Site>
        <UUID>ce40a55a-6954-4de3-85c7-f796c3e423d9</UUID>
        <Segments>
          <FederationGuid>550e8400-...</FederationGuid>
          <FederationGuid>661f9511-...</FederationGuid>
          <FederationGuid>772a0622-...</FederationGuid>
        </Segments>
      </Site>
    </Sites>
  </DataArea>
</SyncSites>
```

### Step 7: MMS Engine Receives and Processes the Delete — [target]

MMS Engine receives the delete notification:

1. **MMS Engine queries the MMS Provider** for assets matching the FederationGuids
   (the MMS Provider stores the FederationGuid/CIRID alongside its native asset IDs)
2. **MMS Engine deregisters from the CIR** — sends `CancelEntries` for its own entries:
   ```
   CancelEntries:
     EntryIdentifier: 234441 (Category: MMS-ASSET) → CIRID 550e8400-...
     EntryIdentifier: 234442 (Category: MMS-ASSET) → CIRID 661f9511-...
     EntryIdentifier: 234443 (Category: MMS-ASSET) → CIRID 772a0622-...
   ```
3. **MMS Engine deletes from the MMS Provider** — removes maintenance assets 234441,
   234442, 234443 through the Provider's existing interfaces

### Step 8: CMS Engine Receives and Processes the Delete — [target]

CMS Engine follows the same pattern:

1. Query CMS Provider for monitoring points matching the FederationGuids
2. Deregister from CIR: `CancelEntries` for CP-001, CP-002, CP-003
3. Delete from CMS Provider: remove monitoring points, baselines, sensor links

### Step 9: REG-LOCATION Engine Removes Per-iTwin ISBM Channels — [current]

After all downstream subscribers have processed the delete (or after a reasonable timeout),
the REG-LOCATION Engine removes the per-iTwin channels from ISBM.

Delete must mirror `BootstrapChannelsAsync`, which provisions exactly **two**
channels per site — no more:

```
DELETE /{enterprise}/{iTwinFederationId}/{InboundDomain}/publication
DELETE /{enterprise}/{iTwinFederationId}/{Domain}/publication

e.g. DELETE /acme/ce40a55a-.../engineering/publication
     DELETE /acme/ce40a55a-.../operations/publication
```

Derive both from `Options.InboundChannelUriFor(guid)` and
`Options.ChannelUriFor(guid)` rather than hardcoding the domain names. The
convention is expressed once in options precisely so a copy cannot drift from it,
and a delete list that names channels the bootstrap never created will leave the
real ones behind.

Channel deletion also removes the underlying Service Bus topics and queues (the ISBM
Provider handles this). After this step, the iTwin has no footprint in the ISBM infrastructure.

### Step 10: REG-LOCATION Engine Removes the SyncSites Message from ISBM — [target]

REG-LOCATION Engine calls RemovePublication on the enterprise sites channel to acknowledge
receipt. The delete workflow is complete.

---

## CIR Cleanup Responsibility

**Each engine deregisters exactly the categories it owns.** Ownership is not a
convention to be remembered — it is written on every row:

> An engine may cancel an entry if and only if the entry's
> `CategorySourceID` equals that engine's own `Options.SourceId`.

That test is mechanical, so it can be asserted in code rather than reviewed by
eye. No engine needs a list of what it is allowed to touch, and no engine needs
an exception carved out for a category whose identifiers came from elsewhere:

| Category | `CategorySourceID` (owner) | `EntrySourceID` (id namespace) | Key (`EntryIDInSource`) | Deleted by |
|---|---|---|---|---|
| `ITWIN-SITE` | REG-LOCATION | REG-LOCATION | the integer `scopeId` | REG-LOCATION Engine [current] |
| `SITE-TYPE` | REG-LOCATION | REG-LOCATION | the integer `itemId` | REG-LOCATION Engine, if last reference [current] |
| `ENG-IMODEL` | REG-LOCATION | ENG | `im-9340-mech::44732::TIC-106` | REG-LOCATION Engine [target] |
| `FUNCTIONAL-LOC` | REG-LOCATION | REG-LOCATION | `LOC-000001` | REG-LOCATION Engine [target] |
| `MMS-ASSET` | MMS | MMS | `234441` | MMS Engine [target] |
| `CMS-POINT` | CMS | CMS | `CP-001` | CMS Engine [target] |

`ENG-IMODEL` is the only row where the two source fields differ, and it is the
reason the rule keys on `CategorySourceID` rather than `EntrySourceID`. Had it
been stamped `CategorySourceID: ENG`, REG-LOCATION would be cancelling an
ENG-owned entry — a genuine violation — while ENG, which has no CIR client and
no knowledge this correlation exists, would be the only participant permitted to
clean it up. Nothing would ever delete it.


After all engines have deregistered, the CIRIDs (550e8400-..., 661f9511-..., etc.)
have no remaining entries. The CIR retains the empty CIRIDs as tombstones, or the
implementation may choose to garbage-collect them.

---

## ws-CIR Operations Used

| Operation | BOD | When | Sent by |
|---|---|---|---|
| **DeleteEntries** | `CancelEntries` | Removing specific entries by their primary key | Each Engine for its own entries |
| **DeleteCategory** | `CancelCategory` | Removing an entire category (alternative, if categories are per-site) | Optional |
| **DeleteRegistry** | `CancelRegistry` | Nuclear option — removes everything in a registry | Not used in this flow |

`CancelEntries` is the right granularity: it deletes specific entries identified by
RegistryID + CategoryID + CategorySourceID + EntryIDInSource + EntrySourceID. Each
deletion is atomic per the spec — if any identifier is not found, the CIR throws a fault
and no partial deletion occurs.

**That atomicity is why the engine sends one entry per request.** The behaviour
was confirmed against the dev provider: a batch of two identifiers, the first
absent and the second present, returned 404 and rolled back — the present entry
survived. The same two keys sent individually returned 404 then 204, and the
entry was removed.

This matters precisely in the case the flow must survive. A delete that fails
partway is redelivered, and the second attempt necessarily finds some entries
already removed. Batched, that request can never succeed, and the entries still
present are stranded permanently — which, because `ITWIN-SITE` keys on a reused
scope id, breaks the next site to claim that id. One request each costs N round
trips and makes every one independently retryable.

---

## Ordering and Timing

The delete cascade has a natural ordering:

```
1. REG-LOCATION Engine deregisters from CIR        (clean CIR before deleting Provider data)
2. REG-LOCATION Engine deletes from Provider        (remove the scope and contents)
3. REG-LOCATION Engine publishes delete downstream  (notify MMS, CMS)
4. MMS Engine deregisters from CIR                  (parallel with CMS)
5. MMS Engine deletes from Provider                 (parallel with CMS)
6. CMS Engine deregisters from CIR                  (parallel with MMS)
7. CMS Engine deletes from Provider                 (parallel with MMS)
8. REG-LOCATION Engine removes ISBM channels        (after downstream processing)
9. REG-LOCATION Engine removes ISBM message         (acknowledge receipt)
```

**CIR deregistration before Provider deletion** ensures that if the Provider deletion
fails partway through, the CIR entries are already gone and won't point to stale data.
The Provider deletion is idempotent, so a retry after a partial failure is safe.

**Steps 4-7 are parallel.** MMS Engine and CMS Engine process independently — neither
waits for the other.

**Step 8 (channel removal) should wait** until downstream subscribers have had time to
process. Options:
- Fixed delay (simplest)
- MMS and CMS publish acknowledgment BODs on a separate channel
- REG-LOCATION Engine checks that the `{Domain}` subscription is empty before deleting

---

## Idempotency

The delete flow must be safe to execute twice:

| Step | On duplicate execution |
|---|---|
| CancelEntries to CIR | CIR throws `EntryNotFoundFault` — Engine catches and continues |
| Delete from Provider | Cascade route returns 404 — Engine treats as success |
| Publish delete downstream | Duplicate SyncSites (Delete) — subscribers de-duplicate on BODID |
| Remove ISBM channels | ISBM returns 404 — Engine treats as success |

Note that a 409 from the Provider is **not** a duplicate-execution case and must
not be swallowed: it means the scope still has contents that the cascade did not
remove, which is a real failure.

De-duplicate at the message level on `BODID`: if the same delete BOD arrives twice
(ISBM redelivery), skip the entire processing on the second delivery.

---

## Comparison: Add vs. Delete Flow

| Step | SyncSites (Add) | SyncSites (Delete) |
|---|---|---|
| ENG Engine publishes | `oa:Sync` verb | `oa:Delete` verb |
| REG-LOCATION Engine receives | Creates Scope, Item, Serial | Reads ids, deregisters CIR, cascades delete |
| REG-LOCATION Engine → CIR | `ProcessRegistry` (register entries) | `CancelEntries` (deregister entries) |
| REG-LOCATION Engine publishes | SyncSites to `{Domain}` channel (approved segments) | SyncSites (Delete) to `{Domain}` channel |
| REG-LOCATION Engine → ISBM | Bootstrap the 2 per-iTwin channels | Remove the same 2 channels |
| MMS/CMS Engine receives | Create assets/points, register in CIR | Delete assets/points, deregister from CIR |

The symmetry is intentional, and it is also the correctness test: whatever the Add
flow wrote is exactly what the Delete flow must remove, keyed the same way. Any
asymmetry between the two — a channel bootstrapped but not deleted, an entry
registered under one key and cancelled under another — is a leak.

---

## Verification Checklist

After the SyncSites (Delete) workflow completes, verify:

1. **CIR:** No `ITWIN-SITE` entry remains keyed on the deleted `scopeId`
2. **CIR:** No `SITE-TYPE` entry remains for the `itemId`, *unless* another site still uses it
3. **CIR:** No entries remain for the site's segment CIRIDs (query by each FederationGuid) [target]
4. **REG-LOCATION Provider:** Scope with `guid = iTwinId` no longer exists (404)
5. **REG-LOCATION Provider:** `health` reports `orphanedRows: 0` and `untrustedConstraints: 0`
6. **MMS Provider:** No maintenance assets reference the deleted FederationGuids [target]
7. **CMS Provider:** No monitoring points reference the deleted FederationGuids [target]
8. **ISBM:** Both per-iTwin channels (`{InboundDomain}`, `{Domain}`) no longer exist
9. **ISBM:** SyncSites message removed from the enterprise sites subscription
10. **Re-create:** Add the same site again. It must register cleanly — this is the
    check that actually catches a missed CIR entry, because the reused `scopeId`
    will collide with anything left behind.