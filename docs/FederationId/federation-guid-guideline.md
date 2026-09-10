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
| `CodeValue` | The element's human-readable code | What engineers write on a drawing — but not unique across iModels | `TIC-106` |

All three parts together resolve uniquely to one element. The FederationGuid is the
iModel platform's answer to this: a single UUID that resolves all three parts, assigned at
element creation and never changed.

**A note on vocabulary.** ENG is an ECSchema-based schema and knows nothing about tags. Its
unit of identity is an element, but *not* every element — the equivalence is with the
functional branch of the hierarchy specifically:

```
BisCore.Element                          (abstract — everything, including geometry)
  └── BisCore.RoleElement                (abstract)
      └── Functional.FunctionalElement   (abstract — the functional role)
          └── Functional.FunctionalComponentElement
              └── ENG.Controller, ENG.LightingColumn, ENG.PtzCamera, …  (sealed)
```

It is `Functional.FunctionalElement` and its descendants that correspond to a
REG-LOCATION tag. `BisCore.Element` also covers physical and geometric elements, which have
no tag counterpart at all — a lantern's geometry is not a thing REG-LOCATION registers.

| ENG (ECSchema) | REG-LOCATION (ALIM) |
|---|---|
| `Functional.FunctionalElement` (and descendants) | `Tag` |
| `Element.CodeValue` | `Tag.Code` |
| `Element.FederationGuid` | the CIRID both sides register against |

A tag is the ALIM-side counterpart of a *functional* element in ENG. Two errors follow from
getting this wrong: reading ENG in tag vocabulary (an ENG element has a `CodeValue`, never a
tag number), and assuming every ENG element has a tag counterpart. Only the functional ones
do, which is also why `IsPublishable` matters — what ENG publishes is the functional view.

**With FederationGuid as the CIRID:**

Every system registers its native key — including ENG's composite key — against the same
UUID. Any system can query the CIR with its own ID and discover every other system's ID for
the same physical asset, including the exact iModel and element to navigate to.

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
carries its FederationGuid AND the composite ENG key.

CCOM has no `FederationGuid`, `IModelId`, `ECInstanceId` or `CodeValue` element, and
inventing them produces a document that fails validation against `CCOM.xsd`. The four
values map onto CCOM's existing identity fields instead — see
[SyncSegmentsWithoutAttributes.xml](../Sample%20BODs/SyncSegmentsWithoutAttributes.xml)
for the authoritative shape:

| ENG value | CCOM field | Why |
|---|---|---|
| FederationGuid | `Segment/UUID` | The federated identity of the thing, which is the CIRID |
| iModelId | `Segment/InfoSource/UUID` | Identifies *which* collection ENG's record lives in |
| ECInstanceId | `Segment/IDInInfoSource` | ENG's own record id within that InfoSource |
| CodeValue | `Segment/ShortName` | The element's code as an engineer writes it |
| UserLabel | `Segment/FullName` | The human-readable label |

```xml
<SyncSegments xmlns="http://www.mimosa.org/ccom4" releaseID="1.0">
  <ApplicationArea xmlns="http://www.openapplications.org/oagis/9">
    <Sender>
      <LogicalID>ENG</LogicalID>
      <ComponentID>EngEngine</ComponentID>
      <ReferenceID>Design Release 3</ReferenceID>
    </Sender>
    <CreationDateTime>2026-08-20T13:21:00Z</CreationDateTime>
    <BODID>62a1a6dd-a0f8-4609-970c-a2cadd75c740</BODID>
  </ApplicationArea>
  <DataArea>
    <Sync xmlns="http://www.openapplications.org/oagis/9">
      <ActionCriteria>
        <ActionExpression actionCode="Replace"
                          expressionLanguage="Xpath">/SyncSegments/DataArea/Segments</ActionExpression>
      </ActionCriteria>
    </Sync>
    <Segments>
      <Segment>
        <!-- FederationGuid: the CIRID, unaltered -->
        <UUID>550e8400-e29b-41d4-a716-446655440000</UUID>
        <!-- ECInstanceId: ENG's record id -->
        <IDInInfoSource>44732</IDInInfoSource>
        <InfoSource>
          <!-- iModelId: which iModel that record lives in -->
          <UUID>9f3d2a10-4b7c-4e88-9a11-0c5e7d3f4a22</UUID>
          <IDInInfoSource>im-9340-mech</IDInInfoSource>
          <ShortName>ENG</ShortName>
        </InfoSource>
        <!-- CodeValue -->
        <ShortName>TIC-106</ShortName>
        <!-- UserLabel -->
        <FullName>Temperature Indicator Controller</FullName>
      </Segment>
    </Segments>
  </DataArea>
</SyncSegments>
```

The iModelId is a UUID, so it goes in `InfoSource/UUID`; the readable code
(`im-9340-mech`) travels alongside it as `InfoSource/IDInInfoSource` for a human
reading the message. All four identity values are required. They are never omitted
and never generated downstream.

Note that `Add` and `Remove` are not per-segment elements either. The action applies
to the whole `Segments` collection through the OAGIS `ActionExpression`, so a
publication that adds and one that removes are two BODs, not one BOD with mixed rows.

### Step 3: Every Participant Registers Its Native Key

When ALIM receives the segment, it registers **both ENG's composite key and its own
functional location** against the FederationGuid as the CIRID:

```
ALIM → CIR: ProcessRegistry (CIRID: 550e8400-...)
  Entry: im-9340-mech::44732::TIC-106  (Category: ENG-IMODEL)
  Entry: LOC-000001                     (Category: FUNCTIONAL-LOC)
```

ALIM registers the ENG key because it has the full composite key from the SyncSegments
BOD. ENG registers what it originates as well; both target the same CIRID, so the two
registrations converge rather than competing (see rule 4).

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
  → CodeValue: TIC-106
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
      <UUID>661f9511-f30c-52e5-b827-557766551111</UUID>   ... Segment FederationGuid
      <IDInInfoSource>44891</IDInInfoSource>              ... ECInstanceId
      <InfoSource>
        <UUID>9f3d2a10-4b7c-4e88-9a11-0c5e7d3f4a22</UUID> ... iModelId
      </InfoSource>
      <ShortName>TIC-107</ShortName>                      ... CodeValue
                      ▲ Identifies the physical asset and its exact location in the iModel
    </Segment>
```

---

## Participant Native Key Reference

Every participant has its own identification scheme. All must be registered in the CIR
against the FederationGuid for the interoperability chain to be complete:

| Participant | Native key format | Example | Category | Who registers |
|---|---|---|---|---|
| ENG (iModel) | iModelId::ECInstanceId::CodeValue (composite) | im-9340-mech::44732::TIC-106 | ENG-IMODEL | ENG and ALIM (both, same CIRID) |
| ALIM (REG-LOCATION) | Functional location ID | LOC-000001 | FUNCTIONAL-LOC | ALIM |
| MMS (Maintenance) | Maintenance asset number | 234441 | MMS-ASSET | MMS |
| CMS (Condition) | Monitoring point ID | CP-001 | CMS-POINT | CMS |

**Why ALIM also registers the ENG key:** ALIM receives the full segment data and holds
the complete composite key, so it can register ENG-IMODEL alongside its own functional
location. ENG registers what it originates too — both register against the same CIRID,
and registration order does not matter, so whichever arrives first establishes the entry
and the other converges on it.

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
   → Segment: TIC-107 (FederationGuid: 661f9511-..., iModelId: im-9340-mech, ECInstanceId: 44891)
   → Segment: TIC-106 (FederationGuid: 550e8400-..., iModelId: im-9340-mech, ECInstanceId: 44732)
   Note: the action code applies to the whole Segments collection, not to individual
   segments, so an add and a removal are two BODs rather than two rows in one.

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
   → CodeValue: TIC-106 (human-readable reference)
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

## Instance Data vs Reference Data — Two Minting Regimes

Everything above concerns **instance data**: the individual physical things a
project creates. **Reference data** — the vocabulary those things are classified
against — follows a different and equally deliberate rule.

| | Instance data | Reference data |
|---|---|---|
| Examples | elements, segments, tags, sites | classes, property definitions, relationship definitions, enumeration values |
| Minted by | the originating participant | **always RDL** |
| Today | ENG mints, via the iModel or the authoring UI's *Suggest* button | RDL mints, stored in `class_objects` / `dbo.objects.guid` |
| Alternate workflow | RDL mints and issues to ENG as a checklist | — no alternate; RDL is the sole authority |
| Others' role | hold their own key for the same thing, and register it | adopt RDL's UUID as-is |
| What CIR records | an **equivalence** between independently-minted keys | a **cross-reference** from a local schema term to the governed one |

The distinction matters because it changes what CIR is doing.

For instance data, three systems each mint their own key for one physical valve
and CIR asserts those denote the same thing. No participant's key is more
correct than another's; the CIRID is what links them.

For reference data there is no competing identity to reconcile. RDL mints once
and everyone else adopts. A class's UUID is not a local id awaiting federation —
it **is** the federation identity from the moment RDL creates it.

### Why a mapping step exists today

ENG, MMS and CMS each model the world with their own schema, and those schemas
do not match RDL's. ENG has `ENG.Streetlight`; RDL has `rdl:Streetlight`. Both
are legitimate descriptions of the same kind of thing, held by systems with
different purposes. That difference is permanent; **the config file expressing it
is not**.

Today each participant carries a local map — `OutboundRdlClassMap` in ENG,
`InboundClassMap` in REG-LOCATION, `InboundRdlTableMap` in MMS. These are
private, asserted, and duplicated per participant, so the same correspondence is
restated in several places with no single point of truth. `RdlTaxonomyValidator`
makes them *checkable* against the live library, which is an improvement on
asserting them blindly, but it does not remove the duplication.

### Target state: map out-of-band, pre-load CIR, retire code matching

The intended end state is a **complete ENG→RDL mapping performed out-of-band**
as part of establishing the relationship, then **pre-loaded into CIR** as
registry entries. Once those entries exist, the correspondence is resolved by
lookup against a shared registry rather than by string comparison inside each
participant.

```
Now:     ENG.Streetlight ──local config map──▶ rdl:Streetlight ──match on code──▶ class row
                                                                  ▲ rename breaks this

Target:  out-of-band mapping exercise ──pre-load──▶ CIR entries
         ENG.Streetlight ──CIR lookup──▶ RDL class GUID ──match on UUID──▶ class row
                                                            ▲ survives a rename
```

This is what closes the open issue in DR-030. `SegmentType` currently matches on
the string code, so renaming a class in RDL breaks the binding the GUID exists to
keep stable. Resolving through a pre-loaded CIR entry to the UUID removes that
fragility, because the code stops being load-bearing.

So the CIR registration for reference data is **not optional decoration**. It is
the mechanism that makes the per-participant code map unnecessary. Registering
`(IDInSource=ENG.Streetlight, SourceID=ENG, CIRID=<RDL class GUID>)` turns a
private mapping table into a shared fact. Note what it still is and is not: it
asserts an **equivalence between vocabulary terms** and does not mint or federate
an identity, because RDL already gave the class exactly one.

Pre-loading matters because the mapping is an analytical exercise, not something
to infer at runtime. Deciding that `ENG.Streetlight` corresponds to
`rdl:Streetlight` is a modelling judgement, made deliberately and reviewed once,
rather than guessed from a string as a message passes through.

### Current state

Elements are done and working. Classes are in progress — RDL mints and stores
the UUID, `ShowTaxonomySet` publishes it, and ENG validates its map against the
library. The lookup half of the target now exists: `CirClassResolver` asks CIR
for the class identity and publishes it as `SegmentType.UUID`. It is **off by
default** and falls back to the derived value on a miss, because the CIR entries
described above are **not yet pre-loaded**; enabling it against a cold registry
would otherwise stop typing every segment. The local maps remain load-bearing
until the pre-load happens, which is what finally closes DR-030.

How load-bearing they still are was demonstrated by renaming `rdl:LightingUnit`
to `rdl:Streetlight`: the key is matched on by three separate config maps, and
changing the RDL seed alone would have silently unbound the class across the
whole ENG→REG-LOCATION→MMS chain with no build error. That is precisely the
fragility the pre-load removes.

Property definitions, relationship definitions and enumeration values are **not
yet modelled at all**: `IRdlStore` currently holds namespaces, class groups and
classes only.

One design question is worth settling before the second reference-data kind is
built. An enumeration value is reference data, so RDL mints its UUID — but
ENG/MMS/CMS typically hold such values as schema-local lookup rows with their
own keys and nowhere to store a GUID. That is the same problem
`SiteIngestionService` already solves for `SETUP_OWNER` by pushing identity into
CIR precisely because the table has no column for it. Settle the pattern once,
rather than inventing it per kind.

---

## Rules for Implementers

1. **The iModel assigns the FederationGuid.** It is born in ENG and travels outbound
   through every system. No system downstream of ENG creates or overrides it.

   This rule governs **instance data**. Reference data follows rule 10 instead:
   RDL mints those UUIDs and ENG adopts them.

   The one exception is brownfield adoption: where an entity already carries an
   identity in a tag register, handover sheet or predecessor system, ENG adopts that
   value instead of minting a new one. Minting would produce a second identity for a
   thing that already had one, which is the duplication this guideline exists to
   prevent. The sandbox ENG authoring UI is the sanctioned path — it accepts an
   operator-supplied FederationGuid and refuses one already held by another segment.
   Adoption happens at creation and once only; the id is immutable thereafter under
   rule 7.

2. **Every SyncSegments BOD must include the FederationGuid AND the full ENG composite key**
   (iModelId, ECInstanceId, CodeValue). These are required, not optional. They travel in
   CCOM's existing identity fields — `Segment/UUID`, `Segment/InfoSource/UUID`,
   `Segment/IDInInfoSource` and `Segment/ShortName` respectively — because CCOM defines no
   elements by those names and inventing them yields a BOD that fails `CCOM.xsd` validation.
   The ENG Engine populates them from the iModel; the ALIM Engine preserves them when
   republishing.

3. **Every participant registers its native key against the FederationGuid as the CIRID.**
   Use the ws-CIR `ProcessRegistry` operation with the CIRID field set to the FederationGuid.
   The CIR stores it as-is.

4. **ENG and ALIM both register, each against the same CIRID.** ENG registers the key
   it originates; ALIM registers its own functional location (LOC-000001 as
   FUNCTIONAL-LOC) against that same FederationGuid. ALIM additionally registers the
   ENG composite key as ENG-IMODEL, because it holds the full key from the
   SyncSegments BOD and a legacy consumer may only ever see codes.

   Both registering is deliberate rather than redundant. Registration order does not
   matter (rule 5) and the CIRID is what links the entries, so whichever arrives first
   establishes the entry and the second converges on it. Requiring exactly one
   registrar would mean a failure at that one participant leaves the identity absent
   from the registry with nothing to notice.

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

10. **RDL mints all reference-data UUIDs; no other participant invents one.** Classes,
    property definitions, relationship definitions and enumeration values are governed by
    RDL and carry the UUID it assigned. A participant classifying against the vocabulary
    adopts that UUID rather than deriving or minting its own — a derived value looks
    well-formed and silently fails to match the library.

    Participants map their own schema terms to RDL's at their own edge today, but that
    local map is transitional. The intended end state is a complete out-of-band mapping
    pre-loaded into CIR, after which the correspondence resolves by registry lookup to the
    UUID and the per-participant code map retires. Until then, validate the local map
    against the library rather than assuming it: a key that does not resolve puts a false
    statement on the bus that every consumer records as fact.

---

## Summary

| Question | Answer |
|---|---|
| What is the FederationGuid? | A UUID assigned to every engineering segment at creation in the iModel |
| Who creates it? | The iModel platform (ENG), or adopted from an existing identity in brownfield |
| Can it change? | No — immutable for the life of the physical asset |
| What is it used as? | The CIRID in the Common Interoperability Registry |
| What is the ENG native key? | iModelId::ECInstanceId::CodeValue (composite, all three required) |
| Who registers the ENG key in CIR? | ENG and ALIM both, against the same CIRID |
| Who registers their own native key? | Every participant — ALIM (LOC-*), MMS (asset number), CMS (point ID) |
| Does registration order matter? | No — any participant can register at any time |
| How does it relate to the iTwin FederationId? | Same principle, different level: iTwin FederationId identifies the facility, segment FederationGuid identifies the asset within it |
| What happens on asset replacement? | New physical device → new FederationGuid → new CIR entry. Old entry preserved with full history |
| Can you navigate from CIR back to the 3D model? | Yes — the ENG-IMODEL entry has the iModelId and ECInstanceId to open the exact element |
| Who mints reference-data UUIDs? | RDL, always — classes, property defs, relationship defs, enumeration values |
| Why do participants still map class names? | Their schemas genuinely differ from RDL's, but the local config map is transitional — a pre-loaded CIR cross-reference replaces it |
| What does CIR record for reference data? | A cross-reference from a local vocabulary term to the governed one — an equivalence, not a new identity |
| How does code matching get retired? | Map ENG→RDL completely out-of-band, pre-load CIR, then resolve to the UUID instead of the string code |
