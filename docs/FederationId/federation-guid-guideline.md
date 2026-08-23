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
(TIC-106, LOC-000001, 234441, CP-001) are how individual systems refer to the thing. The
FederationGuid is how every system agrees they're talking about the same thing.

---

## The Problem It Solves

In a typical owner-operator environment, the same physical pump exists in four systems
under four different identifiers:

| System | Role | Calls it | Knows about the others? |
|---|---|---|---|
| iModel (ENG) | Engineering design | TIC-106 | No |
| ALIM (REG-LOCATION) | Asset governance | LOC-000001 | No |
| MMS (Maintenance) | Work management | 234441 | No |
| CMS (Condition) | Monitoring | CP-001 | No |

Without a common identity, reconciling these requires manual mapping tables, brittle
name-matching heuristics, or proprietary middleware. When a pump is replaced, renamed,
or relocated, the mappings break and nobody knows until a work order targets the wrong asset.

**With FederationGuid as the CIRID:**

Every system registers its native key against the same UUID. Any system can query the CIR
with its own ID and discover every other system's ID for the same physical asset.

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
iModel creates element "TIC-106"
  → FederationGuid: 550e8400-e29b-41d4-a716-446655440000
  → This GUID will be the CIRID everywhere, forever
```

### Step 2: The FederationGuid Travels With the Data

When the ENG Engine compiles a SyncSegments BOD from a Named Version, each segment
carries its FederationGuid:

```xml
<SyncSegments>
  <Segment>
    <FederationGuid>550e8400-e29b-41d4-a716-446655440000</FederationGuid>
    <TagID>TIC-106</TagID>
    <Name>Temperature Indicator Controller</Name>
    <AssetType>Instrument</AssetType>
    <Action>Add</Action>
  </Segment>
</SyncSegments>
```

The FederationGuid is a first-class field in every SyncSegments BOD. It is never omitted,
never generated downstream, and never replaced.

### Step 3: Every Participant Registers Against the FederationGuid

When ALIM receives the segment and creates its functional location (LOC-000001),
it registers the mapping in the CIR with the FederationGuid as the CIRID:

```
ALIM → CIR: ProcessRegistry
  Entry: TIC-106    (Category: ENG-TAG)         → CIRID: 550e8400-...
  Entry: LOC-000001 (Category: FUNCTIONAL-LOC)  → CIRID: 550e8400-...
```

The ws-CIR spec supports caller-supplied CIRIDs. ALIM passes the FederationGuid;
the CIR stores it directly rather than generating its own.

When ALIM publishes the approved segment to the asset-config channel, the SyncSegments
BOD still carries the FederationGuid. MMS receives it, creates its maintenance asset
(234441), and registers:

```
MMS → CIR: ProcessRegistry
  Entry: 234441 (Category: MMS-ASSET) → CIRID: 550e8400-...
```

CMS does the same:

```
CMS → CIR: ProcessRegistry
  Entry: CP-001 (Category: CMS-POINT) → CIRID: 550e8400-...
```

### Step 4: The CIR Holds the Complete Picture

After all four participants register:

```
CIR Entry for CIRID 550e8400-e29b-41d4-a716-446655440000:
  ├── TIC-106      (Category: ENG-TAG)          — registered by ALIM
  ├── LOC-000001   (Category: FUNCTIONAL-LOC)   — registered by ALIM
  ├── 234441       (Category: MMS-ASSET)        — registered by MMS
  └── CP-001       (Category: CMS-POINT)        — registered by CMS
```

Any system can now query:

```
MMS asks: GetEquivalentEntries for 234441
CIR returns: TIC-106, LOC-000001, CP-001 — all under CIRID 550e8400-...

CMS asks: GetEquivalentEntries for CP-001
CIR returns: TIC-106, LOC-000001, 234441 — same CIRID
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
  ALIM registers TIC-106, LOC-000001 → CIRID: 550e8400-... in CIR   ← second

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
New iModel element created for TIC-107
  → New FederationGuid: 661f9511-f30c-52e5-b827-557766551111
  → This is a DIFFERENT physical thing, so it gets a DIFFERENT GUID

CIR after replacement:
  CIRID 550e8400-... (original pump):
    ├── TIC-106      (Status: Decommissioned)
    ├── LOC-000001   (unchanged — location is the same)
    ├── 234441       (Status: Retired)
    └── CP-001       (Status: Archived)

  CIRID 661f9511-... (replacement pump):
    ├── TIC-107      (Category: ENG-TAG)
    ├── LOC-000001   (same location, new equipment)
    ├── 234442       (new maintenance asset)
    └── CP-002       (new monitoring point)
```

The FederationGuid distinguishes the two physical pumps. The functional location (LOC-000001)
stays the same because the location didn't change — only the equipment at that location did.
The CIR preserves the full history: you can always trace back to what was at LOC-000001 before
the replacement.

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
                      ▲ Segment FederationGuid
                      │ Identifies the physical asset
```

---

## Workflow Examples

### Example 1: New Asset Installation

A new temperature controller is designed and installed at MnDOT's TH-61 corridor facility.

```
1. Designer creates TIC-106 in MicroStation
   → iModel Connector syncs to iModel
   → FederationGuid assigned: 550e8400-...

2. Named Version created in iModel
   → ENG Engine publishes SyncSegments to /mndot/{fed-id}/engineering/publication
   → Segment carries FederationGuid: 550e8400-...

3. ALIM Engine receives SyncSegments
   → Persists TIC-106 with FederationGuid in REG-LOCATION
   → User approves TIC-106, maps to LOC-000001

4. ALIM Engine registers in CIR:
   → TIC-106 (ENG-TAG) → CIRID: 550e8400-...
   → LOC-000001 (FUNCTIONAL-LOC) → CIRID: 550e8400-...

5. ALIM Engine publishes approved SyncSegments to /mndot/{fed-id}/asset-config/publication
   → Segment carries FederationGuid: 550e8400-...

6. MMS receives SyncSegments
   → Creates maintenance asset 234441
   → Registers 234441 (MMS-ASSET) → CIRID: 550e8400-... in CIR

7. CMS receives SyncSegments
   → Creates monitoring point CP-001
   → Registers CP-001 (CMS-POINT) → CIRID: 550e8400-... in CIR

Result: four systems, one CIRID, full traceability.
```

### Example 2: Asset Replacement

TIC-106 fails and is replaced by TIC-107. The replacement is a different physical device
with different specifications.

```
1. Designer creates TIC-107 in MicroStation (marks TIC-106 for removal)
   → iModel Connector syncs to iModel
   → TIC-107 FederationGuid: 661f9511-... (NEW — different physical device)
   → TIC-106 marked as decommissioned (EXISTING FederationGuid: 550e8400-...)

2. ENG Engine publishes SyncSegments:
   → Segment: TIC-107 (Action: Add, FederationGuid: 661f9511-...)
   → Segment: TIC-106 (Action: Remove, FederationGuid: 550e8400-...)

3. ALIM approves both:
   → TIC-107 → LOC-000001 (same location, new equipment)
   → TIC-106 → LOC-000001 (decommissioned)

4. ALIM Engine registers in CIR:
   → TIC-107 (ENG-TAG) → CIRID: 661f9511-... (NEW entry)
   → LOC-000001 (FUNCTIONAL-LOC) → CIRID: 661f9511-... (linked to new device)
   → TIC-106 entry status updated to Decommissioned

5. ALIM Engine publishes SyncSegments (both segments)

6. MMS receives:
   → Creates 234442 for TIC-107, registers → CIRID: 661f9511-...
   → Retires 234441 for TIC-106

7. CMS receives:
   → Creates CP-002 for TIC-107, registers → CIRID: 661f9511-...
   → Archives CP-001 for TIC-106

CIR after replacement:
  CIRID 550e8400-... (decommissioned pump):
    TIC-106 (Decommissioned), LOC-000001, 234441 (Retired), CP-001 (Archived)

  CIRID 661f9511-... (replacement pump):
    TIC-107 (Active), LOC-000001, 234442 (Active), CP-002 (Active)

Full history preserved. Audit trail intact.
```

### Example 3: Cross-System Lookup

MMS needs to find the engineering specification for maintenance asset 234441.

```
1. MMS → CIR: GetEquivalentEntries for 234441

2. CIR returns:
   CIRID: 550e8400-...
   Entries:
     TIC-106 (Category: ENG-TAG, Source: ALIM)
     LOC-000001 (Category: FUNCTIONAL-LOC, Source: ALIM)
     234441 (Category: MMS-ASSET, Source: MMS)    ← the one we queried
     CP-001 (Category: CMS-POINT, Source: CMS)

3. MMS now knows:
   → Engineering tag: TIC-106
   → Functional location: LOC-000001
   → CMS monitoring point: CP-001
   → FederationGuid: 550e8400-... (can look up the iModel element directly)
```

The FederationGuid doubles as a direct pointer back to the iModel element. MMS doesn't
just get a label ("TIC-106") — it gets a UUID that can be resolved in the iTwin platform
to retrieve the full engineering model, geometry, specifications, and history.

### Example 4: Brownfield Project Handover

A rehabilitation project creates new design elements that must link to the existing
facility's assets.

```
Facility iTwin: aaa-111-...
Project iTwin:  bbb-222-...

1. Project designer creates TIC-108 in the project iModel
   → FederationGuid: 772a0622-... (new, assigned by the project iModel)

2. ENG Engine publishes to /mndot/{project-fed-id}/engineering/publication
   → SyncSegments with FederationGuid: 772a0622-...

3. ALIM receives, approves, maps to LOC-000004
   → Registers in CIR: TIC-108, LOC-000004 → CIRID: 772a0622-...

4. ALIM publishes to /mndot/{facility-fed-id}/asset-config/publication
   → Cross-iTwin handover: project data lands in the facility's channel

5. MMS, CMS receive and register against CIRID: 772a0622-...

The FederationGuid originated in the project iModel but is now linked
to the facility's asset records. When the project closes, the CIRID
persists in the CIR, connecting the project's design history to the
facility's operational records permanently.
```

---

## Rules for Implementers

1. **The iModel assigns the FederationGuid.** No other system creates or overrides it.
   It is born in ENG and travels outbound through every system.

2. **Every SyncSegments BOD must include the FederationGuid.** It is a required field,
   not optional. The ENG Engine includes it from the iModel; the ALIM Engine preserves
   it when republishing approved segments.

3. **Every participant registers its native key against the FederationGuid as the CIRID.**
   Use the ws-CIR `ProcessRegistry` operation with the CIRID field set to the FederationGuid.
   The CIR stores it as-is.

4. **Registration order doesn't matter.** Any participant can register at any time. The CIR
   links entries by CIRID regardless of which system registered first.

5. **A new physical asset gets a new FederationGuid.** Replacing TIC-106 with TIC-107 means
   two different FederationGuids. The functional location (LOC-000001) may stay the same,
   but the CIRID changes because the physical thing changed.

6. **The FederationGuid is immutable.** Never change it, even if the element is modified,
   renamed, moved, or exported. The GUID identifies the physical thing across its entire
   lifecycle.

7. **Use the FederationGuid for cross-system resolution.** Any system holding a FederationGuid
   can query the CIR to discover every other system's identifier for the same asset, or
   resolve it directly against the iTwin platform to access the engineering model.

---

## Summary

| Question | Answer |
|---|---|
| What is the FederationGuid? | A UUID assigned to every engineering segment at creation in the iModel |
| Who creates it? | The iModel platform (ENG) |
| Can it change? | No — immutable for the life of the physical asset |
| What is it used as? | The CIRID in the Common Interoperability Registry |
| Who registers against it? | Every participant that creates a native ID for the same asset |
| Does registration order matter? | No — any participant can register at any time |
| How does it relate to the iTwin FederationId? | Same principle, different level: iTwin FederationId identifies the facility, segment FederationGuid identifies the asset within it |
| What happens on asset replacement? | New physical device → new FederationGuid → new CIR entry. Old entry preserved with full history |
