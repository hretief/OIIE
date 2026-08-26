# Representing DOT Construction PayItems in a CCOM 4.1 `SyncSegments` BOD

**Analysis date:** 2026-08-25
**Schema basis:** MIMOSA CCOM `4.1.0-draft` (`targetNamespace="http://www.mimosa.org/ccom4"`), CCOM BOD message set, OAGIS Platform 1.2.1 (`Meta.xsd`, `Fields.xsd`), UN/CEFACT `CoreComponentType_2p0.xsd`
**Deliverable:** `SyncSegments_MnDOT_PayItems_Example.xml` (validates; see §5)

---

## 0. Provenance note — read this first

`CCOM.xsd` and `SyncSegments.xsd` were **not** present in the project knowledge. The project contains the
ws-CIR 1.0 package (`CommonInteroperabilityRegistry.xsd`, the eleven CIR BOD schemas) plus the OAGIS
subset ws-CIR imports (`Meta.xsd`, `Fields.xsd`, `CodeLists.xsd`) — none of which declare a single
CCOM type, a `Segment`, or an `Item`.

To answer the questions with schema evidence rather than recollection, the CCOM schema package was
obtained from the MIMOSA organisation repository **`github.com/mimosa-org/ccom-dotnet`**, path `XSD/`:

| File | Role |
|---|---|
| `XSD/CCOM.xsd` | CCOM 4.1.0-draft, © MIMOSA 2019, 4361 lines |
| `XSD/BOD/Messages/CCOMElements.xsd` | global element declarations, `include`s CCOM.xsd |
| `XSD/BOD/Messages/Configuration/SyncSegments.xsd` | the BOD under analysis |
| `XSD/BOD/OAGIS/{Meta,Fields,CodeLists,…}.xsd` | OAGIS Platform 1.2.1 envelope |
| `data/CCOM Reference Data.xml` | MIMOSA reference data (UOM, LogisticResourceType, SegmentType…) |

The `BOD_Catalog.xlsx` in the project independently corroborates the BOD: **`SyncSegments` = verb `Sync`,
noun `Segments`, category Registry**, Tech-XML equivalents `CreateSegment` / `UpdateSegment`. The catalogue
also confirms a negative that matters below: the OIIE BOD register contains **no** commercial, cost,
estimate, or bill-of-quantities noun of any kind.

**Action for you:** diff this `CCOM.xsd` against your controlled copy before treating the XML as
production-ready. The `4.1.0-draft` version string in the schema header is not a released tag.

---

## 1. Executive recommendation

**Model a PayItem as a three-node chain, anchored on the CCOM association entity `MaterialItemOnSegment`:**

```
Segment                                  the design artefact (functional location)
 └── MaterialItemOnSegment               ← THE PAYITEM OCCURRENCE (repeats 0..*)
      │   └── PropertySetForEntity/PropertySet
      │        └── Group[Quantity|Cost|Time|Funding|ConstructionPhase|
      │                  PlanPrep|Tabulation|Asset|QAQC|Remarks|SourceAuthoring]
      └── MaterialItem                   the contract item as used on this project
           └── MaterialMasterItem        ← THE SPECBOOK REFERENCE ITEM
                └── PropertySetForEntity/PropertySet   (SpecBook-invariant attributes)
```

Direct answers to the four questions posed:

**Should a PayItem be represented by `MaterialMasterItem`?**
*Partly — as the reference/definition half only.* `MaterialMasterItem` is a **catalog definition**. Its
entire declared content is `Entity` + `Nameable` + `Type` + `ParentComponent`/`ChildComponent` +
`AssetType?` + `ModelForMaterialMasterItem*`. It has **no quantity, no unit of measure, no price, no cost,
no funding, no schedule**. Putting the Segment's 412.5 CY and $685.00/CY on a global SpecBook item 2461.504
would corrupt the catalog. Use it for the SpecBook definition and nothing else.

**Should it be represented by OAGIS `Item`?**
*No — it is not legal here.* Two independent blockers, both verified:
1. The OAGIS material shipped with and imported by the CCOM BOD set is the **Platform 1.2.1 envelope only**
   (`Meta.xsd`, `Fields.xsd`, `CodeLists.xsd`, datatypes). No OAGIS Nouns or Components schema is present;
   `Item`, `ItemMaster`, `ItemQuantity`, `ItemInstance`, `POLine`, `RequisitionLine`, `BOMComponent` and
   every other OAGIS line-level construct is **undeclared** in the schema set.
2. Even if it were declared, `SyncSegments/DataArea/Segments` permits exactly two children —
   `RegistrationSite` (type `Site`) and `Segment` (type `Segment`) — and `Segment` admits no foreign-namespace
   content anywhere. There is no `UserArea` and no `xs:any` in the noun.

**Should it be represented by `AttributeSet`?**
*Not as the PayItem itself, and not under that name.* Two findings:
- **`AttributeSet` is deprecated in CCOM 4.1.** The schema says so explicitly: `AttributeSet` is now a
  do-nothing extension of `PropertySet`, annotated *"Deprecated. Still defined for backwards/forwards
  compatibility. Will be removed in next major release."* The same applies to `AttributeSetDefinition`,
  `AttributeSetForEntity`, `AttributeType`, `AttributeGroup`, `Attribute`. **The correct 4.1 construct is
  `PropertySet`.**
- A `PropertySet` is a property sheet with no commercial identity, no reference-item link, and no
  association semantics to the Segment beyond "these properties describe that entity". Using one
  `PropertySet` per PayItem *is* schema-valid — `Entity` allows `PropertySetForEntity` 0..* — but it throws
  away the fact that a PayItem **is** a resource requirement against a functional location, which CCOM models
  as a first-class entity. `PropertySet` is the right vehicle for the **payload inside** the occurrence, not
  for the occurrence itself.

**Is a two-level master-item plus Segment-specific occurrence model required?**
*Yes — and CCOM actually forces three nodes, not two.* `Segment/MaterialItemOnSegment` is the occurrence;
its `MaterialItem` child is mandatory-ish in practice (the association is meaningless without it) and
`MaterialItem` in turn carries a **required `xs:choice`** of exactly one of `AssetType | Model | Asset |
MaterialMasterItem`. So you cannot express "this Segment consumes catalog item X" without materialising
the intermediate `MaterialItem`. That is not redundancy: it is the schema's separation of
*catalog definition* (`MaterialMasterItem`) from *the item as instantiated in a project's bill*
(`MaterialItem`) from *its assignment to one functional location* (`MaterialItemOnSegment`).

**Grain matters more than construct choice.** The commonest implementation error is assuming one aspect
instance maps onto one CCOM entity. It does not: an aspect splits across three nodes at three different
grains, and only `MaterialItemOnSegment` is 1:1 with it. §3.1 sets out the source shape, the target
shape, the de-duplication keys and the two traps (aspect IDs are not stable identity; the aspect carries
a stale copy of the spec book row).

**On material vs. non-material PayItems** — the working hypothesis (that `MaterialMasterItem` is too narrow
for services) is **rejected on the evidence**. `MaterialItem` and `MaterialMasterItem` both extend
`LogisticResource`, whose `Type` is `LogisticResourceType`, documented as *"Different kinds of logistic
resources (e.g. materials, equipment, tools, documents, utilities)."* The MIMOSA reference data ships 32
`LogisticResourceType` instances including **`Labour`**, `Tool`, `Software`, `Utility`, `Document`, `Material`,
`Material, Part`, and `Undetermined`. Mobilization, traffic control, flagging, inspection and erosion control
are therefore representable through the same construct, discriminated by `Type` — no second modelling
pattern is needed, and the mixed DOT catalog stays homogeneous. (See §5 for the one reference-data gap.)

---

## 2. Schema evidence

### 2.1 The BOD envelope

`SyncSegments.xsd` (namespace `http://www.mimosa.org/ccom4`), verbatim structure:

```xml
<xs:element name="SyncSegments">
  <xs:complexType><xs:complexContent>
    <xs:extension base="oa:BusinessObjectDocumentType">   <!-- contributes oa:ApplicationArea + releaseID -->
      <xs:sequence>
        <xs:element name="DataArea">
          <xs:complexType><xs:sequence>
            <xs:element ref="oa:Sync"/>
            <xs:element name="Segments" minOccurs="0" maxOccurs="unbounded">
              <xs:complexType><xs:sequence>
                <xs:element name="RegistrationSite" type="Site"    minOccurs="0" maxOccurs="unbounded"/>
                <xs:element name="Segment"          type="Segment" minOccurs="1" maxOccurs="unbounded"/>
              </xs:sequence></xs:complexType>
            </xs:element>
          </xs:sequence></xs:complexType>
        </xs:element>
      </xs:sequence>
    </xs:extension>
  </xs:complexContent></xs:complexType>
</xs:element>
```

`releaseID` is **required** (`oa:BusinessObjectDocumentType`). `oa:ApplicationArea` requires
`oa:CreationDateTime`; `Sender`, `Receiver`, `Signature`, `BODID`, `UserArea` are optional.
`oa:Sync` is `SyncType` → `ActionVerbType` → `VerbType` (abstract, empty) — its only content is
`oa:ActionCriteria*`, so **`oa:Sync` carries no `UserArea` either**.

### 2.2 Selected entities

| Entity | Declaring XSD | Complex type | Base chain | Key children used |
|---|---|---|---|---|
| `Segment` | `CCOM.xsd` L1108 | `Segment` | `Entity` | `ShortName`, `FullName`, `Description`, `Type`(`SegmentType`), `RegistrationSite`, **`MaterialItemOnSegment` 0..∞** |
| `MaterialItemOnSegment` | `CCOM.xsd` L1488 | `MaterialItemOnSegment` | `Entity` | `MaterialItem?`, `Segment?` (+ everything on `Entity`) |
| `MaterialItem` | `CCOM.xsd` L1457 | `MaterialItem` | `LogisticResource` → `Entity` | `Nameable`, `Type`(`LogisticResourceType`), **required choice** `AssetType|Model|Asset|MaterialMasterItem`, `MaterialItemOnSegment*`, `TestComponentTypeRegion*` |
| `MaterialMasterItem` | `CCOM.xsd` L1477 | `MaterialMasterItem` | `LogisticResource` → `Entity` | `Nameable`, `Type`, `AssetType?`, `ModelForMaterialMasterItem*` |
| `LogisticResourceType` | `CCOM.xsd` L1448 | `LogisticResourceType` | `BaseType` → `Entity` | reference data; open list |
| `PropertySetForEntity` | `CCOM.xsd` L801 | `PropertySetForEntity` | `Entity` | choice `AttributeSet|PropertySet`, `Entity?` |
| `PropertySet` | `CCOM.xsd` L656 | `PropertySet` | `Entity` | `Nameable`, `Type?`, `Definition?`, choice `SetAttribute*|SetProperty*`, `Group*` |
| `PropertyGroup` | `CCOM.xsd` L582 | `PropertyGroup` | `Entity` | `Nameable`, `Definition?`, `Order?`, `Group*`, choice `SetAttribute*|SetProperty*` |
| `Property` | `CCOM.xsd` L238 | `Property` | `Entity` | `Nameable`, choice `Type?|Definition?`, `ValueContent?`, `Order?`, `ValueIsValidInDefinition?` |
| `ValueContent` | `CCOM.xsd` L185 | `ValueContent` | — | choice of `BinaryData BinaryObject Boolean Coordinate EnumerationItem Measure MultiParameter Number Percentage Probability Text URI UTCDateTime UUID XML` |
| `Measure` | `CCOM.xsd` L111 | `Measure` | — | `Value` (`cct:NumericType`), `UnitOfMeasure?` |
| `UnitOfMeasure` | `CCOM.xsd` L521 | `UnitOfMeasure` | `BaseType` → `Entity` | `ConversionScale?`, `ConversionOffset?`, `UOMQuantity?` |

The critical fragments:

```xml
<!-- CCOM.xsd, complexType Segment -->
<xs:element name="MaterialItemOnSegment" type="MaterialItemOnSegment" minOccurs="0" maxOccurs="unbounded"/>
```

```xml
<!-- CCOM.xsd, complexType MaterialItem -->
<xs:extension base="LogisticResource"><xs:sequence>
  <xs:choice>
    <xs:element name="AssetType"          type="AssetType"          minOccurs="1" maxOccurs="1"/>
    <xs:element name="Model"              type="Model"              minOccurs="1" maxOccurs="1"/>
    <xs:element name="Asset"              type="Asset"              minOccurs="1" maxOccurs="1"/>
    <xs:element name="MaterialMasterItem" type="MaterialMasterItem" minOccurs="1" maxOccurs="1"/>
  </xs:choice>
  <xs:element name="MaterialItemOnSegment" type="MaterialItemOnSegment" minOccurs="0" maxOccurs="unbounded"/>
  …
</xs:sequence></xs:extension>
```

```xml
<!-- CCOM.xsd, complexType Entity (abstract) — every CCOM entity inherits these -->
<xs:element name="UUID" type="UUID" minOccurs="1" maxOccurs="1"/>
<xs:element name="IDInInfoSource" type="cct:IDType" minOccurs="0"/>
<xs:element name="InfoSource" type="InfoSource" minOccurs="0"/>
…
<xs:choice>
  <xs:element name="AttributeSetForEntity" type="PropertySetForEntity" minOccurs="0" maxOccurs="unbounded"/>
  <xs:element name="PropertySetForEntity"  type="PropertySetForEntity" minOccurs="0" maxOccurs="unbounded"/>
</xs:choice>
```

```xml
<!-- CCOM.xsd — the deprecation that settles Question 1 -->
<xs:complexType name="AttributeSet">
  <xs:complexContent><xs:extension base="PropertySet">
    <xs:annotation><xs:documentation>Deprecated. Still defined for backwards/forwards
    compatibility. Will be removed in next major release.</xs:documentation></xs:annotation>
  </xs:extension></xs:complexContent>
</xs:complexType>
```

### 2.3 Exact legal location within `SyncSegments`

```
SyncSegments
└─ DataArea
   └─ Segments
      ├─ RegistrationSite            (Site — optional context)
      └─ Segment
         ├─ UUID / IDInInfoSource / InfoSource / EffectiveStatusType     [Entity]
         ├─ ShortName / FullName / Description / Type / RegistrationSite [Segment]
         └─ MaterialItemOnSegment*                                       ← PayItem occurrence
            ├─ UUID / IDInInfoSource / InfoSource / EffectiveStatusType  [Entity]
            ├─ PropertySetForEntity → PropertySet → Group* → SetProperty*[Entity]
            └─ MaterialItem
               ├─ UUID / IDInInfoSource / InfoSource                     [Entity]
               ├─ ShortName / FullName / Type                            [LogisticResource]
               └─ MaterialMasterItem                                     ← SpecBook definition
                  ├─ UUID / IDInInfoSource / InfoSource                  [Entity]
                  ├─ PropertySetForEntity → PropertySet                  [Entity]
                  └─ ShortName / FullName / Description / Type           [LogisticResource]
```

**Element order is not negotiable.** XSD `complexContent/extension` concatenates sequences, so every entity
emits `Entity`'s children first (`UUID` … `PropertySetForEntity`), then the base type's own, then the
derived type's own. In particular `PropertySetForEntity` precedes `MaterialItem` inside
`MaterialItemOnSegment`, and `ShortName`/`Type` follow it inside `MaterialItem`.

### 2.4 Negative proof (what is *not* legal)

Validated deliberately-broken variants against the same schema:

| Attempted | Validator response |
|---|---|
| `<PayItem>` under `Segment` | `Element '{…ccom4}PayItem': This element is not expected. Expected is one of ( ShortName, FullName, Description, Type, RegistrationSite, IsTemplate, Template, IsGroup, ParentComponent, ChildComponent )` |
| `<MaterialMasterItem>` directly under `Segment` | `Element '{…ccom4}MaterialMasterItem': This element is not expected. Expected is one of ( IsTemplate, Template, IsGroup, ParentComponent, ChildComponent, Criticality, LifecycleStatus, LifecycleStatusType, SegmentModelEvent, AssetSegmentEvent )` |

That second result is the direct answer to Question 5: **a master item cannot hang off a Segment.** It
reaches the Segment only through `MaterialItemOnSegment → MaterialItem → MaterialMasterItem`.

---

## 3. Semantic mapping

**Legend for target paths**

- `MMI:` = `…/MaterialItemOnSegment/MaterialItem/MaterialMasterItem` — **reference item** (SpecBook definition)
- `MI:` = `…/MaterialItemOnSegment/MaterialItem` — **contract item** (item as used on this project)
- `OCC:` = `…/Segment/MaterialItemOnSegment` — **Segment-specific occurrence**
- `PS/G[x]/P[y]` = `PropertySetForEntity/PropertySet/Group[ShortName='x']/SetProperty[ShortName='y']/ValueContent/…`

### 3.1 Source shape vs. target shape

The single most common modelling error here is assuming the iModel aspect maps one-to-one onto a
single CCOM entity. It does not. **One aspect instance splits across three CCOM nodes**, and each of
those nodes has a different grain.

#### The source shape

| Source construct | Grain | Notes |
|---|---|---|
| `bis.Element` | one design artefact | the Segment. Has a `FederationGuid` |
| `DOT_PayItem`*n*`ElementAspect` (class) | one **component slot** of the element | slot identity encoded in the class *name* — see §3.1.4 |
| aspect *instance* | one pay item dataset | `ElementMultiAspect`, so a slot may in principle hold several |
| struct property (`Cost`, `Quantity`, …) | a field group | no independent identity; maps to `PropertyGroup` |

Note the two independent multiplicity mechanisms: numbered classes give N slots, the multi-aspect base
gives N rows within a slot. Establish empirically which is actually in use before building the extractor
(query in §3.1.5).

#### The target shape

| CCOM node | Grain | Carries |
|---|---|---|
| `Segment` | one per design element | element identity; `FederationGuid` = UUID = CIRID |
| `MaterialItemOnSegment` | **one per aspect instance** | all Segment-specific values: quantity, cost, funding, time, phase, plan prep, tabulation, source asset, QA/QC |
| `MaterialItem` | one per (item × contract) | the item as it appears in this project's bill. Holds identity and `LogisticResourceType`; no values |
| `MaterialMasterItem` | one per (item × spec book) — **global** | the SpecBook definition: item number, name, description, unit, `IsPlanQty`, help |

#### 3.1.1 The aspect is the occurrence

Only `MaterialItemOnSegment` is 1:1 with an aspect instance. Take a project in which item `2461.504` is
used on twelve segments:

| Node | Instances |
|---|---|
| aspect instances | **12** |
| `MaterialItemOnSegment` | **12** |
| `MaterialItem` | **1** |
| `MaterialMasterItem` | **1** — and the same one in every other project, forever |

In the example BOD, `MaterialItem` is spelled out inline inside each `MaterialItemOnSegment`, which makes
it *look* owned by the occurrence. That is serialisation, not semantics: CCOM entities are identified by
UUID and containment is only where you chose to write one out in full. With one occurrence per item the
distinction never surfaces; at twelve it does.

#### 3.1.2 De-duplication keys

Deriving `MaterialItem/UUID` per aspect mints twelve distinct contract items for one pay item, and any
downstream rollup of "total 2461.504 on SP 2758-92" silently returns twelve unrelated things. Only the
occurrence key may contain anything segment- or aspect-specific:

```
MaterialMasterItem/UUID    = uuid5(ns, specBookId  + "::" + PayItem.Item)
MaterialItem/UUID          = uuid5(ns, projectId   + "::" + PayItem.Item)
MaterialItemOnSegment/UUID = uuid5(ns, segmentFederationGuid + "::" + PayItem.Item + "::" + componentCode)
```

The extractor therefore groups all aspects for the project by item number and emits one master and one
contract item per group, not one per aspect.

**Open question to settle before ALIM registers identities:** should `componentCode` also appear in the
`MaterialItem` key? If a light pole's slot 1 and slot 3 both carry `2461.504` (concrete in the foundation,
concrete in the base) they are the same contract item used twice, so no. If MnDOT ever treats the slot as
contractually distinct, yes. Cheap now, expensive later.

#### 3.1.3 The aspect holds a denormalised snapshot

`SpecBook`, `Class`, `Type`, `Description`, `Unit` and `UnitPlan` are **copied onto every aspect instance**
at authoring time. An iModel authored in 2025 against the 2020 spec book holds values that may no longer
match the current master.

Treat `PayItem.Item` as the only authoritative field in that struct — a pointer — and source
`MaterialMasterItem`'s description and unit from the spec book system itself. Otherwise the master's
content depends on which aspect the extractor happened to read first, which surfaces months later as
"why does 2461.504 say something different in this BOD."

#### 3.1.4 Component slots (unresolved)

The numbered classes are not occurrence ordinals — they designate **components of an assembly** that the
CAD model does not distinguish geometrically. A light pole system is one element with one geometry;
`DOT_PayItem1ElementAspect` may be the pole shaft and `DOT_PayItem2ElementAspect` the luminaire.

This is a source-side design defect: class names are carrying instance identity, so adding a component
requires a schema change and slot ordinals mean nothing across element types.

Two consequences for the target shape, one settled and one not:

- **Settled:** one element still produces **one BOD**. `Sync` is state-replacing against a UUID-keyed
  noun, so two `SyncSegments` for the same `Segment/UUID` means the second clobbers the first or the
  receiver invents merge semantics the BOD does not specify. It also breaks IFC handover atomicity.
- **Open:** whether component slots become **child Segments** (`Segment/ChildComponent/Child`, verified
  as valid — a nested child Segment with its own `MaterialItemOnSegment` validates) or stay flat as
  sibling occurrences on one Segment. Flat is faithful to the iModel but erases the maintainability
  boundary MMS needs: a luminaire replaced on an eight-year cycle needs a functional location to be
  booked against. Promotion is not algorithmic — it needs a mapping table (aspect class → component role
  → does this warrant a functional location), because mobilization and traffic control never do.

#### 3.1.5 Discovery queries to run before building the extractor

```sql
-- Do the numbered classes share a base? Determines whether one polymorphic
-- query reads every slot, or the extractor enumerates classes forever.
SELECT c.Name, bc.Name AS BaseClass
FROM meta.ECClassDef c
  JOIN meta.ClassHasBaseClasses r ON r.SourceECInstanceId = c.ECInstanceId
  JOIN meta.ECClassDef bc ON bc.ECInstanceId = r.TargetECInstanceId
WHERE c.Name LIKE 'DOT_PayItem%'

-- Does a single slot ever hold more than one row? If always empty, the
-- multi-aspect base is vestigial and slot = pay item.
SELECT Element.Id, COUNT(*) AS n
FROM ONLY DOT.DOT_PayItem1ElementAspect
GROUP BY Element.Id HAVING COUNT(*) > 1
```

### 3.2 Identity and classification → reference item

| Source EC property | Target XML path | CCOM type | Level |
|---|---|---|---|
| `PayItem.Item` | `MMI:/ShortName` **and** `MMI:/IDInInfoSource` | `cct:TextType` / `cct:IDType` | reference |
| `PayItem.REFITEM_NM` | `MMI:/FullName` (+ `PS/P[REFITEM_NM]/Text` for fidelity) | `cct:TextType` | reference |
| `PayItem.Description` | `MMI:/Description` | `cct:TextType` | reference |
| `PayItem.SpecBook` | `MMI:/InfoSource` (UUID+ShortName) **and** `PS/P[SpecBook]/Text` | `InfoSource` | reference |
| `PayItem.Class` | `MMI:PS/P[Class]/Text` | `cct:TextType` | reference |
| `PayItem.Type` | `MMI:PS/P[Type]/Text` | `cct:TextType` | reference |
| `PayItem.Unit` | `MMI:PS/P[Unit]/Text` + realised structurally as `UnitOfMeasure` on every `Measure` | `cct:TextType` / `UnitOfMeasure` | reference |
| `PayItem.UnitPlan` | `MMI:PS/P[UnitPlan]/Text` | `cct:TextType` | reference |
| `PayItem.IsPlanQty` | `MMI:PS/P[IsPlanQty]/Boolean` | `xs:boolean` | reference |
| *(material vs service)* | `MMI:/Type` and `MI:/Type` → `LogisticResourceType` | reference-data UUID | both |
| `Help.Tip` | `MMI:PS/G[Help]/P[Tip]/Text` | `cct:TextType` | reference |
| `Help.Link` | `MMI:PS/G[Help]/P[Link]/URI` | `URI` | reference |

### 3.3 Occurrence data → `MaterialItemOnSegment`

All rows below live under `OCC:PS/G[…]/P[…]/ValueContent/…`.

| Source EC property | Group | Property | ValueContent | Unit / currency |
|---|---|---|---|---|
| `Quantity.Method` | Quantity | Method | `Text` | — |
| `Quantity.Description` | Quantity | Description | `Text` | — |
| `Quantity.FactorItem` | Quantity | FactorItem | `Number` | dimensionless |
| `Quantity.FactorUser` | Quantity | FactorUser | `Number` | dimensionless |
| `Quantity.Factor` | Quantity | Factor | `Number` | dimensionless |
| `Quantity.Override` | Quantity | Override | `Number` | dimensionless |
| `Quantity.CompQuantity` | Quantity | CompQuantity | `Measure` | item UOM |
| `Quantity.RoundingConservative` | Quantity | RoundingConservative | `Boolean` | — |
| `Quantity.RoundingIncrement` | Quantity | RoundingIncrement | `Number` | — |
| `Quantity.RoundingPrecision` | Quantity | RoundingPrecision | `Number` | — |
| `Quantity.ItemQuantity` | Quantity | ItemQuantity | `Measure` | item UOM |
| `Cost.EstimateType` | Cost | EstimateType | `Text` | — |
| `Cost.EngEstPrice` | Cost | EngEstPrice | `Measure` | **US Dollar** (UOMQuantity `Currency`) |
| `Cost.Override` | Cost | Override | `Measure` | US Dollar |
| `Cost.Factor` | Cost | Factor | `Number` | dimensionless |
| `Cost.UnitPrice` | Cost | UnitPrice | `Measure` | US Dollar |
| `Cost.ItemCost` | Cost | ItemCost | `Measure` | US Dollar |
| `Time.EngEstUnitPerDay` | Time | EngEstUnitPerDay | `Number` | items/day¹ |
| `Time.UnitOverride` | Time | UnitOverride | `Number` | — |
| `Time.UnitFactor` | Time | UnitFactor | `Number` | — |
| `Time.WorkforceFactor` | Time | WorkforceFactor | `Number` | — |
| `Time.UnitPerDay` | Time | UnitPerDay | `Number` | items/day¹ |
| `Time.ItemTime` | Time | ItemTime | `Measure` | **Days** |
| `Funding.Code` | Funding | Code | `Text` | — |
| `Funding.Name` | Funding | Name | `Text` | — |
| `Funding.Payer` | Funding | Payer | `Text` | — |
| `Funding.Percentage` | Funding | Percentage | **`Percentage`** | % of item cost |
| `Funding.FederalParticipation` | Funding | FederalParticipation | `Text` | see §5 note |
| `ConstructionPhase.YR` | ConstructionPhase | YR | `Text` | program year label² |
| `ConstructionPhase.Stage/Class/Group/Sequence/ActivityName` | ConstructionPhase | (same names) | `Text` | — |
| `PlanPrep.LabelPlanP/LabelNote/Label1/Label2` | PlanPrep | (same names) | `Text` | — |
| `Tabulation.Class/Category/Group/Code/Title/SheetNum` | Tabulation | (same names) | `Text` | — |
| `Tabulation.DOT_DigitalAssetMetadata_ID_` | Tabulation | DOT_DigitalAssetMetadata_ID_ | `Text` | — |
| `Asset.Project_ID/Project/Index/Status/Instance/PlanID/PlanDecoder/AssetID` | Asset | (same names) | `Text` | — |
| `Asset.IsTracked` | Asset | IsTracked | `Boolean` | normalised from string³ |
| `QAQC.MajorCostValue` | QAQC | MajorCostValue | `Measure` | US Dollar |
| `QAQC.MajorTimeValue` | QAQC | MajorTimeValue | `Measure` | Days |
| `QAQC.IsMajorItemLookup/IsMajorCost/IsMajorTime/IsCheckQty/IsCheckCost/IsSiblingRequired/IsCommonItem` | QAQC | (same names) | `Boolean` | normalised from string³ |
| `QAQC.PastContractCount` | QAQC | PastContractCount | `Number` | integer |
| `QAQC.ElementDescription` | QAQC | ElementDescription | `Text` | — |
| `Remarks.*` | Remarks | Remark | `Text` | provisional⁴ |
| `SPECBOOK_FILTER` | SourceAuthoring | SPECBOOK_FILTER | `Text` | authoring state⁵ |
| `REFITEM_SEARCH` | SourceAuthoring | REFITEM_SEARCH | `Text` | authoring state⁵ |

¹ Production rate has no matching MIMOSA reference UOM (it is *item-UOM per day*, so the unit is
item-dependent). Carried as `Number`; add a project UOM if a receiving system needs the dimension.
² `YR` is a program-year *label*, not an instant. `UTCDateTime` would over-specify it. If your consumers
need a date, add a second property with `ValueContent/UTCDateTime`.
³ Declared `String` in the EC schema but boolean-valued in practice. Serialised as `Boolean` per constraint 5.
Record the Y/N ⇄ true/false normalisation in the `PropertyDefinition`.
⁴ `Remarks` members were not supplied. Mapped as a repeatable `Text` property inside a `Remarks` group. If the
EC struct turns out to be richer (author, timestamp, category), add sibling properties, or use
`ValueContent/XML` (declared, `format` attribute required) to carry the fragment verbatim.
⁵ Filter/search fields are UI state on the aspect, not PayItem semantics. Included for round-trip fidelity;
they are the first candidates to drop from the production profile.

### 3.4 Cross-cutting identity

Derivation keys are in §3.1.2; this table covers the traceability side.

| Concern | Mechanism |
|---|---|
| Segment ↔ iModel element | `Segment/IDInInfoSource` = `iModelId::ECInstanceId::CodeValue`; `Segment/UUID` = the FederationGuid / CIRID |
| Occurrence ↔ ElementAspect | `MaterialItemOnSegment/IDInInfoSource` = `iModelId::AspectECInstanceId::DOT_PayItem1ElementAspect` — **traceability only, never identity** (see below) |
| Aspect IDs are not stable | ElementAspects have no `FederationGuid`, and a connector that deletes and re-inserts aspects on resync will renumber them. An occurrence UUID derived from `AspectECInstanceId` therefore churns on every republish and ALIM sees creates instead of updates. Confirm with the connector team whether aspect IDs survive a resync; until then, derive from §3.1.2 |
| SpecBook item stability | `MaterialMasterItem/UUID` is deterministic (uuid5 over the item number) so 2461.504 is the same UUID in every BOD, every project |
| Property semantics | every `SetProperty` carries `Definition/UUID` pointing at a `PropertyDefinition`; consumers key on the definition UUID, never on `ShortName` |
| Set structure | `PropertySet/Definition` → `PropertySetDefinition` UUID, published separately via `SyncPropertySetDefinitions` |
| Reference data | `LogisticResourceType`, `SegmentType`, `UnitOfMeasure`, `UOMQuantity`, `EffectiveStatusType` all referenced by their MIMOSA reference-data UUIDs |

### 3.5 Fields that cannot be represented without compromise

| Field | Issue | Handling |
|---|---|---|
| `Cost.*` currency | CCOM `Measure/Value` is `cct:NumericType`; CCOM never references `cct:AmountType`, so there is no `currencyID` attribute anywhere in a CCOM payload | Currency carried as `UnitOfMeasure` = *US Dollar* (`39c197f5-…`) whose `UOMQuantity` = *Currency* (`dbc268de-…`). Semantically complete, but a consumer must resolve the UOM to learn the currency |
| `Unit` = `LS` | No *Lump Sum* `UnitOfMeasure` in MIMOSA reference data | Project-scoped `UnitOfMeasure` minted with a deterministic UUID; must be published as reference data |
| Service PayItems | No `LogisticResourceType` for *Service* / *Construction Activity*; nearest shipped values are `Labour` and `Undetermined` | Example uses `Labour`. Recommend MnDOT publish `Service, Construction Activity` — `LogisticResourceType` extends `BaseType`, so extending the code list needs **no schema change** |
| `Funding.Percentage` basis | `Percentage` is a bare numeric with no "percentage of what" slot | Basis conveyed by group membership (`Funding`) and pinned in the `PropertyDefinition` |
| Multiple funding splits | The source struct is single-valued (one code, one percentage) | `PropertyGroup` is repeatable (`Group` 0..∞), so multiple `Funding` groups can be emitted when the source model grows |

---

## 4. Example `SyncSegments` BOD

File: **`SyncSegments_MnDOT_PayItems_Example.xml`** — 2,457 lines. Contents:

- OAGIS `ApplicationArea` with `Sender` (ENG Engine), `Receiver` (ALIM / REG-LOCATION), `CreationDateTime`, `BODID`
- `oa:Sync` verb, `Segments` noun, one `RegistrationSite`, one `Segment` (`BR27043-DECK`, `SegmentType` = *Bridge*)
- **Two PayItem occurrences**, both as `MaterialItemOnSegment`:
  - **material** — MnDOT 2461.504 *Structural Concrete (3Y33)*, 412.5 CY @ $685.00/CY = $282,562.50, NHPP 80% federal, Stage 2, 6.0 days, flagged a major cost and major time item
  - **non-material** — MnDOT 2563.601 *Traffic Control*, 1 LS @ $145,000.00, STBG 50%, `LogisticResourceType` = *Labour*, price overridden after district review
- Every UUID is deterministic (uuid5 over a stable path) so re-generation is idempotent; MIMOSA reference-data UUIDs are used verbatim

Representative excerpt — the occurrence, its cost group, and the reference item:

```xml
<MaterialItemOnSegment>
  <UUID>1a30cb99-…</UUID>
  <IDInInfoSource>bc7a1f2e-…::0x30000000a17::DOT_PayItem1ElementAspect</IDInInfoSource>
  <InfoSource>
    <UUID>…</UUID><ShortName>MnDOT iTwin iModel - SP 2758-92</ShortName>
  </InfoSource>
  <EffectiveStatusType><UUID>db4bf287-2374-4e9c-bd4b-fadaada24b99</UUID><ShortName>Active</ShortName></EffectiveStatusType>
  <PropertySetForEntity>
    <UUID>…</UUID>
    <PropertySet>
      <UUID>…</UUID>
      <ShortName>DOT Pay Item Assignment</ShortName>
      <Type><UUID>…</UUID><ShortName>DOT Pay Item</ShortName></Type>
      <Definition><UUID>…</UUID><ShortName>DOT_PayItem1.Assignment</ShortName></Definition>
      <Group>
        <UUID>…</UUID><ShortName>Cost</ShortName>
        <Definition><UUID>…</UUID><ShortName>Cost</ShortName></Definition>
        <Order>2</Order>
        <SetProperty>
          <UUID>…</UUID>
          <ShortName>UnitPrice</ShortName>
          <Definition><UUID>…</UUID><ShortName>Cost.UnitPrice</ShortName></Definition>
          <ValueContent>
            <Measure>
              <Value>685.00</Value>
              <UnitOfMeasure>
                <UUID>39c197f5-759f-41c7-805e-2a87b6f9e29d</UUID>
                <ShortName>US Dollar</ShortName>
                <UOMQuantity>
                  <UUID>dbc268de-6847-4729-926f-6f46a18af09d</UUID>
                  <ShortName>Currency</ShortName>
                </UOMQuantity>
              </UnitOfMeasure>
            </Measure>
          </ValueContent>
          <Order>5</Order>
        </SetProperty>
        …
      </Group>
      …
    </PropertySet>
  </PropertySetForEntity>
  <MaterialItem>
    <UUID>…</UUID>
    <IDInInfoSource>SP2758-92|2461.504</IDInInfoSource>
    <ShortName>2461.504 (SP 2758-92)</ShortName>
    <Type><UUID>67782185-ac49-44f7-8b8c-cafba7fa9608</UUID><ShortName>Material</ShortName></Type>
    <MaterialMasterItem>
      <UUID>…</UUID>
      <IDInInfoSource>2461.504</IDInInfoSource>
      <InfoSource><UUID>…</UUID><ShortName>MnDOT Standard Specifications … 2020 Edition</ShortName></InfoSource>
      …
      <ShortName>2461.504</ShortName>
      <FullName>STRUCTURAL CONCRETE (3Y33)</FullName>
      <Type><UUID>67782185-…</UUID><ShortName>Material</ShortName></Type>
    </MaterialMasterItem>
  </MaterialItem>
</MaterialItemOnSegment>
```

---

## 5. Validation

**Method — libxml2 (`xmllint`), full schema closure resolved from local imports:**

```bash
xmllint --noout \
  --schema XSD/BOD/Messages/Configuration/SyncSegments.xsd \
  SyncSegments_MnDOT_PayItems_Example.xml
```

**Result:** `SyncSegments_MnDOT_PayItems_Example.xml validates`

**Independent cross-check — Python `lxml`:**

```python
schema = etree.XMLSchema(etree.parse("XSD/BOD/Messages/Configuration/SyncSegments.xsd"))
schema.validate(etree.parse("SyncSegments_MnDOT_PayItems_Example.xml"))   # → True, empty error_log
```

Schema closure exercised: `SyncSegments.xsd` → `CCOMElements.xsd` → `CCOM.xsd` → `CoreComponentType_2p0.xsd`;
plus `Meta.xsd` → `Fields.xsd` → `CodeLists.xsd` → the four ISO/UNECE/IANA code-list schemas →
`UnqualifiedDataTypes.xsd` / `QualifiedDataTypes.xsd`.

**Control tests** — the shipped MIMOSA sample `example_bod_sync_segments.xml` validates against the same
schema (toolchain sanity), and both deliberately-invalid variants in §2.4 are correctly rejected. The
validator is enforcing, not silently accepting.

**Warnings:** none.

### Schema limitations found

1. **No currency data type in CCOM.** `cct:AmountType` (with `currencyID`) exists in
   `CoreComponentType_2p0.xsd` but CCOM references only `cct:NumericType`, `cct:TextType`, `cct:IDType`,
   `cct:DateTimeType`. Currency must ride on `UnitOfMeasure`.
2. **`MaterialItemOnSegment` has no native quantity.** Unlike `SegmentComponent` (which has `Order`), the
   association declares only `MaterialItem?` and `Segment?`. Every occurrence value — quantity included —
   must go into a `PropertySet`. This is the single largest semantic compromise in the recommendation, and
   it is unavoidable: no CCOM 4.1 entity carries a bill-of-quantities line quantity natively.
3. **No extension point in the noun.** `UserArea` exists only in `oa:ApplicationArea`; the `Segments`
   noun and `Segment` type admit nothing outside the CCOM namespace. The only declared escape hatch inside
   the payload is `ValueContent/XML` (`xs:anyType` + required `format`), scoped to a single property value.
4. **XSD cannot enforce PropertySet content.** Which groups and properties must appear is governed by
   `PropertySetDefinition` (documented as serving *"to assess structural conformance"*), not by XML Schema.
   The PayItem contract is therefore only as strong as the definitions you publish and the conformance
   check you implement.
5. **Reference-data gaps:** no *Lump Sum* UOM, no *Service / Construction Activity* `LogisticResourceType`,
   no production-rate UOM. All three are extensible without touching the schema.
6. **Round-trip caveat:** `FederalParticipation` is `String` in the source with unknown domain — carried as
   `Text`. If it is in fact a Y/N flag, normalise it to `Boolean` alongside the other `Is*` fields.
7. **`MaterialItem` cannot be stubbed by UUID alone.** Where an item repeats across segments you would
   naturally spell out `MaterialItem` once and reference it by UUID elsewhere. `MaterialItem`'s
   `xs:choice` of `AssetType | Model | Asset | MaterialMasterItem` is mandatory, so the minimum legal
   stub is two nested UUIDs, not one — verified:

   ```xml
   <!-- FAILS: Missing child element(s) -->
   <MaterialItem><UUID>…</UUID></MaterialItem>

   <!-- VALIDATES -->
   <MaterialItem><UUID>…</UUID>
     <MaterialMasterItem><UUID>…</UUID></MaterialMasterItem>
   </MaterialItem>
   ```

   Conversely, `MaterialItemOnSegment` with **no** `MaterialItem` at all validates — the schema will not
   stop you emitting a meaningless occurrence, so this belongs in your own conformance check.

---

## 6. Final decision record

**Selected construct**

> A PayItem occurrence on a Segment is a **`MaterialItemOnSegment`**, whose `MaterialItem` resolves to a
> **`MaterialMasterItem`** representing the SpecBook reference item, and whose Segment-specific quantity,
> cost, funding, time, phase, plan-preparation, tabulation, source-asset and QA/QC data are carried in a
> **`PropertySet`** attached via `PropertySetForEntity`, with one **`PropertyGroup`** per source EC struct
> and one **`Property`** per source field, each bound to a stable `PropertyDefinition` UUID.

**Why it is preferable**

- It is the only path the schema actually allows from `Segment` to a resource item — proved by the negative
  test in §2.4.
- It separates definition from occurrence exactly where DOT practice needs the seam: the SpecBook item is
  global and stable; the quantity, price and funding are properties of *this use on this Segment*.
- Repeating PayItems become repeating first-class entities, each with its own UUID. Nothing is flattened
  into `PayItem1.UnitPrice` / `PayItem2.UnitPrice` — constraint 4 satisfied structurally, not by convention.
- One construct covers materials, labour, services and contractual activities, discriminated by
  `LogisticResourceType`, which is open reference data.
- The EC `struct` shape maps onto `PropertyGroup` without invention; `PropertyGroupDefinition` even carries
  `MinOccurs`/`MaxOccurs`, so the iModel aspect's structure is expressible as a governed template.

**Why the rejected alternatives are less suitable**

| Rejected | Reason |
|---|---|
| `MaterialMasterItem` alone | Catalog definition only — no quantity, UOM, price, funding or schedule slots. Segment-specific values would pollute the global item |
| OAGIS `Item` (or any OAGIS line construct) | Undeclared in the schema set CCOM BODs import, and structurally illegal beneath `Segments`/`Segment` |
| One `AttributeSet` per PayItem | `AttributeSet` is deprecated in CCOM 4.1 in favour of `PropertySet`; and a property sheet models "descriptive data about an entity", not "a resource requirement against a functional location". It would be schema-valid and semantically hollow |
| Flattened properties directly on `Segment` | Loses repetition and per-item identity; violates constraint 4 |
| Separate `SyncLogisticResources` BOD with cross-reference | Legal (the noun exists in the BOD register) but splits an atomic Issued-for-Construction handover across two messages with no transactional guarantee |

**Known interoperability trade-offs**

1. **Deprecated-spelling ambiguity.** `Entity` offers `AttributeSetForEntity` *or* `PropertySetForEntity`
   (same underlying type), and the MIMOSA-shipped sample uses the deprecated `AttributeSet*` spelling. The
   example here uses the 4.1 `PropertySet*` spelling. Confirm which one AssetWise ALIM's deserialiser
   accepts before going live — a consumer generated from an older `xsd.exe` run may only bind the
   `Attribute*` names.
2. **Cost semantics live in a property sheet, not a typed commercial entity.** A CCOM consumer that
   doesn't understand the `DOT_PayItem1.Assignment` `PropertySetDefinition` sees an opaque sheet of
   numbers. Interoperability therefore rests on publishing the definitions (`SyncPropertySetDefinitions`)
   before or alongside the segment data, and registering their UUIDs in the CIR.
3. **Currency is indirect.** `UnitOfMeasure` = *US Dollar* is unambiguous but requires reference-data
   resolution; an ISO-4217 code is never present as an attribute in the payload.
4. **Three nodes per PayItem is verbose.** Two PayItems produce ~2,450 lines. For segments with dozens of
   items, consider transporting `MaterialMasterItem` definitions once via a separate registry
   synchronisation and referencing them by stub inside `SyncSegments`, as the shipped MIMOSA sample does
   for its type references. Note the exception in §5 limitation 7: most CCOM entities stub down to a bare
   `UUID`, but `MaterialItem` does not — its choice of `AssetType | Model | Asset | MaterialMasterItem`
   is mandatory, so its minimum stub is two nested UUIDs.
5. **The source is denormalised and the target is not.** One aspect instance splits across three CCOM
   nodes at three different grains (§3.1). Extraction must de-duplicate by item number before emitting,
   and must not treat the aspect's copy of the spec book row as authoritative. An extractor that emits
   one `MaterialMasterItem` per aspect is schema-valid and semantically wrong, and nothing in the
   validation chain will catch it.
6. **`4.1.0-draft`.** The schema self-identifies as a draft. Any conformance claim should name the exact
   schema build used.

---

## Appendix — reference-data UUIDs used

| Concept | ShortName | UUID | Source |
|---|---|---|---|
| `EffectiveStatusType` | Active | `db4bf287-2374-4e9c-bd4b-fadaada24b99` | MIMOSA |
| `LogisticResourceType` | Material | `67782185-ac49-44f7-8b8c-cafba7fa9608` | MIMOSA |
| `LogisticResourceType` | Labour | `bed9a6de-f54e-47bd-8ae5-01fa3c68a84e` | MIMOSA |
| `SegmentType` | Bridge | `6c42560b-6057-4cab-91e8-27cde5434eb8` | MIMOSA |
| `UnitOfMeasure` | US Dollar | `39c197f5-759f-41c7-805e-2a87b6f9e29d` | MIMOSA |
| `UnitOfMeasure` | Cubic Yards | `e4108785-fa78-4838-900a-b602c38db4e7` | MIMOSA |
| `UnitOfMeasure` | Days | `0904787c-69f1-416f-b3be-5cc4585ca5e2` | MIMOSA |
| `UOMQuantity` | Currency | `dbc268de-6847-4729-926f-6f46a18af09d` | MIMOSA |
| `UnitOfMeasure` | Lump Sum | *(uuid5, project-minted)* | **MnDOT must publish** |
| `PropertySetType` | DOT Pay Item | *(uuid5, project-minted)* | **MnDOT must publish** |