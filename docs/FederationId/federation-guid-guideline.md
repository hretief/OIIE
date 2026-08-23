# FederationGUID as the Common Identity
## Guideline for the Bentley Systems OIIE Implementation

**Author:** Solution Engineering
**Date:** August 2026
**Status:** Guideline for adoption

---

## Principle

Every engineering segment created in an iModel is assigned a **FederationGuid** — a UUID
that is immutable, globally unique, and owned by that physical thing for its entire lifecycle.
This FederationGuid becomes the **CIRID** in the Common Interoperability Registry, creating a
single identity that links every system's native key for the same physical asset.

The FederationGuid is not a system ID. It is the **identity of the thing itself**. System IDs
(iModelId::ECInstanceId::CodeValue, LOC-000001, 234441, CP-001) are how individual systems
refer to the thing. The FederationGuid is how every system agrees they're talking about the
same thing.

---

## The Problem It Solves

In a typical owner-operator environment, the same physical pump exists in four systems
under four different identification schemes:

| System | Role | Native key | Format | Knows about the others? |
|---|---|---|---|---|
| iModel (ENG) | Engineering design | im-9340-mech::44732::TIC-106 | Composite: iModelId::ECInstanceId::CodeValue | No |
| ALIM (REG-LOCATION) | Asset governance | LOC-000001 | Single functional location ID | No |
| MMS (Maintenance) | Work management | 234441 | Maintenance asset number | No |
| CMS (Condition) | Monitoring | CP-001 | Monitoring point ID | No |

Without a common identity, reconciling these requires manual mapping tables, brittle
name-matching heuristics, or proprietary middleware. When a pump is replaced, renamed,
or relocated, the mappings break and nobody knows until a work order targets the wrong asset.

### The ENG identification challenge

ENG's native key is a **composite** — no single field uniquely identifies an element:

| Part | What it is | Why it's needed | Example |
|---|---|---|---|
| `iModelId` | Which iModel the element lives in | An iTwin may have multiple iModels (Mechanical, Structural, Civil) — the same CodeValue could exist in two of them | `im-9340-mech` |
| `ECInstanceId` | Element ID within that iModel | Unique within one iModel but meaningless outside it | `44732` |
| `CodeValue` | Human-readable engineering tag | What engineers call it — but not unique across iModels | `TIC-106` |

All three parts together resolve uniquely to one element. The FederationGuid is the
iModel platform's answer to this: a single UUID that resolves all three parts, assigned at
element creation and never changed.

**With FederationGuid as the CIRID:**

Every system registers its native key — including ENG's composite key — against the same
UUID. Any system can query the CIR with its own ID and discover every other system's ID for
the same physical asset, including the exact iModel, element, and tag to navigate to.

---

## How It Works

### Step 1: The FederationGuid Is Born

When a design element is created in the iModel, the iModel platform assigns a FederationGuid.
This happens once, at creation. The GUID never changes, even if:

- The element is modified, moved, or renamed
- The iModel is branched, merged, or versioned
- The element is exported to other systems
- The physical asset is replaced in the field (the replacement gets a new FederationGuid)

```
iModel "im-9340-mech" creates element:
  ECInstanceId: 44732
  CodeValue: TIC-106
  → FederationGuid: 550e8400-e29b-41d4-a716-446655440000
  → This GUID will be the CIRID everywhere, forever
```

### Step 2: The FederationGuid and ENG Key Travel With the Data

When the ENG Engine compiles a SyncSegments BOD from a Named Version, each segment
carries its FederationGuid AND the composite ENG key:

```xml
<SyncSegments>
  <Segment>
    <FederationGuid>550e8400-e29b-41d4-a716-446655440000</FederationGuid>
    <IModelId>im-9340-mech</IModelId>
    <ECInstanceId>44732</ECInstanceId>
    <CodeValue>TIC-106</CodeValue>
    <Name>Temperature Indicator Controller</Name>
    <AssetType>Instrument</AssetType>
    <Action>Add</Action>
  </Segment>
</SyncSegments>
```

The FederationGuid, iModelId, ECInstanceId, and CodeValue are all required fields.
They are never omitted and never generated downstream.

### Step 3: Every Participant Registers Its Native Key

When ALIM receives the segment, it registers **both ENG's composite key and its own
functional location** against the FederationGuid as the CIRID:

```
ALIM → CIR: ProcessRegistry (CIRID: 550e8400-...)
  Entry: im-9340-mech::44732::TIC-106  (Category: ENG-IMODEL)
  Entry: LOC-000001                     (Category: FUNCTIONAL-LOC)
```

ALIM registers the ENG key on behalf of ENG because it has the full composite key from
the SyncSegments BOD. This ensures the ENG identification is in the CIR even though the
ENG Engine itself doesn't interact with the CIR directly.

When ALIM publishes the approved segment to the asset-config channel, the SyncSegments
BOD still carries the FederationGuid and the ENG composite key. MMS receives it, creates
its maintenance asset (234441), and registers:

```
MMS → CIR: ProcessRegistry (CIRID: 550e8400-...)
  Entry: 234441 (Category: MMS-ASSET)
```

CMS does the same:

```
CMS → CIR: ProcessRegistry (CIRID: 550e8400-...)
  Entry: CP-001 (Category: CMS-POINT)
```

### Step 4: The CIR Holds the Complete Picture

After all participants register:

```
CIR Entry for CIRID 550e8400-e29b-41d4-a716-446655440000:
  ┌──────────────────────────────────┬──────────────────┬─────────────────┐
  │ Native Key                       │ Category         │ Registered by   │
  ├──────────────────────────────────┼──────────────────┼─────────────────┤
  │ im-9340-mech::44732::TIC-106     │ ENG-IMODEL       │ ALIM            │
  │ LOC-000001                       │ FUNCTIONAL-LOC   │ ALIM            │
  │ 234441                           │ MMS-ASSET        │ MMS             │
  │ CP-001                           │ CMS-POINT        │ CMS             │
  └──────────────────────────────────┴──────────────────┴─────────────────┘
```

Any system can now query:

```
MMS asks: GetEquivalentEntries for 234441
CIR returns:
  CIRID: 550e8400-...
  im-9340-mech::44732::TIC-106 (ENG-IMODEL)
  LOC-000001 (FUNCTIONAL-LOC)
  234441 (MMS-ASSET) ← the one queried
  CP-001 (CMS-POINT)

MMS now knows:
  → iModel: im-9340-mech
  → Element: ECInstanceId 44732
  → Engineering tag: TIC-106
  → Can open the exact iModel, navigate to element 44732,
    see the 3D geometry, specifications, and full history
```

---

## Why the FederationGuid, Not a CIR-Generated CIRID

### The ordering problem disappears

With a CIR-generated CIRID, the first system to register creates the CIRID. Every downstream
system depends on that registration happening first and the CIRID being included in the data.
If ALIM fails to register before publishing, MMS has no CIRID to register against.

With the FederationGuid, every system already has the CIRID because it was in the data
from the moment the segment was created in the iModel. Registration can happen in any order:

```
Scenario: MMS registers before ALIM finishes CIR registration

  ALIM publishes SyncSegments with FederationGuid: 550e8400-...
  MMS receives it, creates 234441
  MMS registers 234441 → CIRID: 550e8400-... in CIR     ← first to register
  ALIM registers ENG key + LOC-000001 → CIRID: 550e8400-... in CIR   ← second

  Result: identical to the reverse order. The CIR doesn't care who's first.
```

### The round-trip disappears

With a CIR-generated CIRID:

```
ALIM → CIR: Register (no CIRID supplied)
CIR → ALIM: Here's your CIRID: CIR-00042        ← round-trip
ALIM: Now I can include CIR-00042 in the published SyncSegments
```

With the FederationGuid:

```
ALIM: FederationGuid is 550e8400-... (already in the data)
ALIM → CIR: Register with CIRID = 550e8400-...   ← no round-trip needed for the ID
ALIM: Publish SyncSegments (FederationGuid was already there)
```

ALIM still registers in the CIR (for the identity mapping), but it doesn't need to wait
for the CIR to tell it the CIRID before publishing. The registration and the publication
can happen in parallel.

### The replacement scenario is clean

When TIC-106 is physically replaced by TIC-107 in the field:

```
New iModel element created for TIC-107 in im-9340-mech:
  ECInstanceId: 44891
  CodeValue: TIC-107
  → New FederationGuid: 661f9511-f30c-52e5-b827-557766551111
  → This is a DIFFERENT physical thing, so it gets a DIFFERENT GUID

CIR after replacement:
  CIRID 550e8400-... (original pump — decommissioned):
    im-9340-mech::44732::TIC-106 (Decommissioned)
    LOC-000001 (historical)
    234441 (Retired)
    CP-001 (Archived)

  CIRID 661f9511-... (replacement pump — active):
    im-9340-mech::44891::TIC-107 (Active)
    LOC-000001 (same location, new equipment)
    234442 (Active)
    CP-002 (Active)
```

The FederationGuid distinguishes the two physical pumps. The functional location (LOC-000001)
stays the same because the location didn't change — only the equipment at that location did.
The CIR preserves the full history: you can always trace back to what was at LOC-000001 before
the replacement, including the exact iModel element (ECInstanceId 44732) that represented it.

---

## Two-Level Identity Scheme

The FederationGuid for segments mirrors the FederationId convention we use for iTwins in
channel naming. The same principle applies at two levels:

| Level | Identifies | UUID assigned by | Used as | Lifetime |
|---|---|---|---|---|
| **iTwin FederationId** | A facility or project | iTwin platform | Channel URI segment | Life of the facility or project |
| **Segment FederationGuid** | A physical asset | iModel (ENG) | CIRID in the CIR | Life of the physical asset |

Both are UUIDs assigned at creation, never changed, and carry identity through every system
boundary. The channel convention gives each iTwin a stable namespace; the FederationGuid gives
each asset within that iTwin a stable identity.

```
/mndot/550e8400-e29b-41d4-a716-446655440000/asset-config/publication
       ▲ iTwin FederationId
       │ Identifies the facility

  SyncSegments BOD on this channel:
    <Segment>
      <FederationGuid>661f9511-f30c-52e5-b827-557766551111</FederationGuid>
      <IModelId>im-9340-mech</IModelId>
      <ECInstanceId>44891</ECInstanceId>
      <CodeValue>TIC-107</CodeValue>
                      ▲ Segment FederationGuid + ENG composite key
                      │ Identifies the physical asset and its exact location in the iModel
    </Segment>
```

---

## Participant Native Key Reference

Every participant has its own identification scheme. All must be registered in the CIR
against the FederationGuid for the interoperability chain to be complete:

| Participant | Native key format | Example | Category | Who registers |
|---|---|---|---|---|
| ENG (iModel) | iModelId::ECInstanceId::CodeValue (composite) | im-9340-mech::44732::TIC-106 | ENG-IMODEL | ALIM (on behalf of ENG) |
| ALIM (REG-LOCATION) | Functional location ID | LOC-000001 | FUNCTIONAL-LOC | ALIM |
| MMS (Maintenance) | Maintenance asset number | 234441 | MMS-ASSET | MMS |
| CMS (Condition) | Monitoring point ID | CP-001 | CMS-POINT | CMS |

**Why ALIM registers the ENG key:** The ENG Engine publishes SyncSegments but doesn't
interact with the CIR directly. ALIM receives the full segment data including the composite
ENG key and registers it alongside its own functional location. This keeps the ENG Engine
simple (publish-only) while ensuring the CIR has the engineering identification.

---

## Workflow Examples

### Example 1: New Asset Installation

A new temperature controller is designed and installed at MnDOT's TH-61 corridor facility.

```
1. Designer creates TIC-106 in MicroStation
   → iModel Connector syncs to iModel "im-9340-mech"
   → ECInstanceId: 44732, CodeValue: TIC-106
   → FederationGuid assigned: 550e8400-...

2. Named Version created in iModel
   → ENG Engine publishes SyncSegments to /mndot/{fed-id}/engineering/publication
   → Segment carries: FederationGuid, iModelId, ECInstanceId, CodeValue

3. ALIM Engine receives SyncSegments via ISBM webhook
   → Persists segment with all identification in REG-LOCATION
   → User approves TIC-106, maps to LOC-000001

4. ALIM Engine registers in CIR (CIRID = FederationGuid 550e8400-...):
   → im-9340-mech::44732::TIC-106 (ENG-IMODEL)
   → LOC-000001 (FUNCTIONAL-LOC)

5. ALIM Engine publishes approved SyncSegments to /mndot/{fed-id}/asset-config/publication
   → Segment carries: FederationGuid, iModelId, ECInstanceId, CodeValue, LOC-000001

6. MMS receives SyncSegments via ISBM webhook
   → Creates maintenance asset 234441
   → Registers 234441 (MMS-ASSET) → CIRID: 550e8400-... in CIR

7. CMS receives SyncSegments via ISBM webhook
   → Creates monitoring point CP-001
   → Registers CP-001 (CMS-POINT) → CIRID: 550e8400-... in CIR

Result:
  CIRID 550e8400-...
    im-9340-mech::44732::TIC-106 (ENG)
    LOC-000001 (ALIM)
    234441 (MMS)
    CP-001 (CMS)
  Four systems, one CIRID, full traceability from 3D model to work order.
```

### Example 2: Asset Replacement

TIC-106 fails and is replaced by TIC-107. The replacement is a different physical device
with different specifications, created as a new element in the same iModel.

```
1. Designer creates TIC-107 in MicroStation (marks TIC-106 for removal)
   → iModel Connector syncs to iModel "im-9340-mech"
   → TIC-107: ECInstanceId: 44891, FederationGuid: 661f9511-... (NEW)
   → TIC-106: ECInstanceId: 44732, FederationGuid: 550e8400-... (marked decommissioned)

2. ENG Engine publishes SyncSegments:
   → Segment: TIC-107 (Add, FederationGuid: 661f9511-..., iModelId: im-9340-mech, ECInstanceId: 44891)
   → Segment: TIC-106 (Remove, FederationGuid: 550e8400-..., iModelId: im-9340-mech, ECInstanceId: 44732)

3. ALIM approves both:
   → TIC-107 → LOC-000001 (same location, new equipment)
   → TIC-106 → decommissioned

4. ALIM Engine registers in CIR:
   → im-9340-mech::44891::TIC-107, LOC-000001 → CIRID: 661f9511-... (NEW entry)
   → Updates CIRID 550e8400-... entries to Decommissioned status

5. ALIM Engine publishes SyncSegments (both segments, full ENG keys included)

6. MMS receives:
   → Creates 234442 for TIC-107, registers → CIRID: 661f9511-...
   → Retires 234441 for TIC-106

7. CMS receives:
   → Creates CP-002 for TIC-107, registers → CIRID: 661f9511-...
   → Archives CP-001 for TIC-106

CIR after replacement:
  CIRID 550e8400-... (decommissioned):
    im-9340-mech::44732::TIC-106, LOC-000001, 234441, CP-001

  CIRID 661f9511-... (active):
    im-9340-mech::44891::TIC-107, LOC-000001, 234442, CP-002

Full history preserved. Audit trail from old pump to new pump intact.
MMS can trace: 234442 replaced 234441 at LOC-000001, and navigate
to both iModel elements (44732 and 44891) for full specifications.
```

### Example 3: Cross-System Lookup

MMS needs to find the engineering specification for maintenance asset 234441.

```
1. MMS → CIR: GetEquivalentEntries for 234441

2. CIR returns:
   CIRID: 550e8400-e29b-41d4-a716-446655440000
   Entries:
     im-9340-mech::44732::TIC-106  (Category: ENG-IMODEL)
     LOC-000001                     (Category: FUNCTIONAL-LOC)
     234441                         (Category: MMS-ASSET) ← queried
     CP-001                         (Category: CMS-POINT)

3. MMS now knows:
   → iModel: im-9340-mech (can open in Bentley tools)
   → Element: ECInstanceId 44732 (can navigate directly to it)
   → Engineering tag: TIC-106 (human-readable reference)
   → Functional location: LOC-000001 (ALIM reference)
   → Monitoring point: CP-001 (CMS reference)
   → FederationGuid: 550e8400-... (universal resolver)
```

### Example 4: Multi-Discipline Lookup

An iTwin has multiple iModels. The same functional location has elements in both
the Mechanical and Structural models.

```
Mechanical iModel (im-9340-mech):
  ECInstanceId: 44732, CodeValue: TIC-106
  FederationGuid: 550e8400-...

Structural iModel (im-9340-struct):
  ECInstanceId: 8891, CodeValue: SUPPORT-TIC-106
  FederationGuid: 773b1733-...

These are DIFFERENT physical things (the instrument vs. its mounting bracket),
so they get DIFFERENT FederationGuids and DIFFERENT CIR entries:

  CIRID 550e8400-... (the instrument):
    im-9340-mech::44732::TIC-106 (ENG-IMODEL)
    LOC-000001 (FUNCTIONAL-LOC)
    234441 (MMS-ASSET)

  CIRID 773b1733-... (the mounting bracket):
    im-9340-struct::8891::SUPPORT-TIC-106 (ENG-IMODEL)
    LOC-000001-BRACKET (FUNCTIONAL-LOC)
    234444 (MMS-ASSET)

The composite ENG key (iModelId::ECInstanceId::CodeValue) disambiguates
elements that have similar names across iModels. Without the iModelId,
"TIC-106" and "SUPPORT-TIC-106" could be confused. With the full composite
key, every element resolves unambiguously to one iModel, one element, one thing.
```

### Example 5: Brownfield Project Handover

A rehabilitation project creates new design elements in a project iModel.
These must link to the existing facility through the CIR.

```
Facility iModel: im-9340-mech (permanent)
Project iModel:  im-9340-rehab (temporary)

1. Project designer creates TIC-108 in the project iModel
   → im-9340-rehab, ECInstanceId: 102, CodeValue: TIC-108
   → FederationGuid: 882c2844-... (new, assigned by the project iModel)

2. ENG Engine publishes SyncSegments from the project iTwin
   → FederationGuid: 882c2844-..., iModelId: im-9340-rehab, ECInstanceId: 102

3. ALIM receives, approves, maps to LOC-000004
   → Registers in CIR (CIRID: 882c2844-...):
     im-9340-rehab::102::TIC-108 (ENG-IMODEL)
     LOC-000004 (FUNCTIONAL-LOC)

4. MMS, CMS receive and register against CIRID: 882c2844-...

CIR entry:
  CIRID 882c2844-...
    im-9340-rehab::102::TIC-108 (ENG-IMODEL)  ← project iModel
    LOC-000004 (FUNCTIONAL-LOC)
    234445 (MMS-ASSET)
    CP-005 (CMS-POINT)

The ENG-IMODEL entry points to the PROJECT iModel (im-9340-rehab),
not the facility iModel. When the project closes:
  → The project iModel may be archived
  → The CIR entry persists, pointing to the archived iModel
  → If the element is migrated to the facility iModel, a new
    ENG-IMODEL entry is added with the facility iModelId
```

---

## Rules for Implementers

1. **The iModel assigns the FederationGuid.** No other system creates or overrides it.
   It is born in ENG and travels outbound through every system.

2. **Every SyncSegments BOD must include the FederationGuid AND the full ENG composite key**
   (iModelId, ECInstanceId, CodeValue). These are required fields, not optional. The ENG Engine
   includes them from the iModel; the ALIM Engine preserves them when republishing.

3. **Every participant registers its native key against the FederationGuid as the CIRID.**
   Use the ws-CIR `ProcessRegistry` operation with the CIRID field set to the FederationGuid.
   The CIR stores it as-is.

4. **ALIM registers the ENG composite key on behalf of ENG.** The ENG Engine doesn't interact
   with the CIR directly. ALIM receives the full segment data and registers both the ENG key
   (im-9340-mech::44732::TIC-106 as ENG-IMODEL) and its own key (LOC-000001 as FUNCTIONAL-LOC).

5. **Registration order doesn't matter.** Any participant can register at any time. The CIR
   links entries by CIRID regardless of which system registered first.

6. **A new physical asset gets a new FederationGuid.** Replacing TIC-106 with TIC-107 means
   two different FederationGuids, even if they're in the same iModel. The functional location
   may stay the same, but the CIRID changes because the physical thing changed.

7. **The FederationGuid is immutable.** Never change it, even if the element is modified,
   renamed, moved, or exported.

8. **The ENG composite key enables direct iModel navigation.** Any system holding the
   composite key (from the CIR) can open the exact iModel and navigate to the exact element
   to see 3D geometry, specifications, and change history.

9. **Cross-iModel elements get separate FederationGuids.** An instrument (in the Mechanical
   iModel) and its mounting bracket (in the Structural iModel) are different physical things.
   They get different FederationGuids and different CIR entries, even if they share a location.

---

## Summary

| Question | Answer |
|---|---|
| What is the FederationGuid? | A UUID assigned to every engineering segment at creation in the iModel |
| Who creates it? | The iModel platform (ENG) |
| Can it change? | No — immutable for the life of the physical asset |
| What is it used as? | The CIRID in the Common Interoperability Registry |
| What is the ENG native key? | iModelId::ECInstanceId::CodeValue (composite, all three required) |
| Who registers the ENG key in CIR? | ALIM, on behalf of ENG |
| Who registers their own native key? | Every participant — ALIM (LOC-*), MMS (asset number), CMS (point ID) |
| Does registration order matter? | No — any participant can register at any time |
| How does it relate to the iTwin FederationId? | Same principle, different level: iTwin FederationId identifies the facility, segment FederationGuid identifies the asset within it |
| What happens on asset replacement? | New physical device → new FederationGuid → new CIR entry. Old entry preserved with full history |
| Can you navigate from CIR back to the 3D model? | Yes — the ENG-IMODEL entry has the iModelId and ECInstanceId to open the exact element |
