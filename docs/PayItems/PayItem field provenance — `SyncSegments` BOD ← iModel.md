# PayItem field provenance — `SyncSegments` BOD ← iModel

Companion to `SyncSegments_MnDOT_PayItems_Annotated.xml`, which carries the same information
as inline XML comments. This document is the flat index.

**Coverage:** all 77 columns of the ECSQL projection are consumed. Six BOD values have **no
identified source** and are marked `[GAP]` in both artefacts.

---

## 1. Source taxonomy

| Tag | Meaning |
|---|---|
| `ASPECT` | A column of the `DOT_PayItem1ElementAspect` projection. Alias given. |
| `ELEMENT` | `bis.Element` / `GeometricElement3d`. **Requires a JOIN the query does not have.** |
| `IMODEL` | iModel / iTwin metadata — FederationGuid of the iModel, changeset, contract properties. |
| `ECSCHEMA` | ECSchema metadata via `db.getSchemaProps("DOT")` — KindOfQuantity persistence units. |
| `ECSTRUCT` | Structural echo of a source EC struct (one `PropertyGroup` per struct). |
| `REFDATA` | A MIMOSA *CCOM Reference Data* UUID. Fixed, ships with the standard. |
| `LOOKUP` | Needs an MnDOT-published crosswalk table. |
| `MINTED` | Deterministic `uuid5` computed by the extractor from stable inputs. |
| `CONFIG` | Deployment / participant configuration. |
| `RUNTIME` | Generated at send time. |
| `GAP` | **Not available from the query or any identified source. Must be resolved.** |

`MIOS` = `MaterialItemOnSegment`, `PS` = `PropertySet`, `G[…]` = `PropertyGroup`, `P[…]` = `SetProperty`.

---

## 2. What the BOD needs that the aspect does not supply

These are the values that make the difference between a property sheet and a publishable BOD.

### 2.1 Requires a join to `bis.Element`

The query selects `Element.Id` but never joins through it. Four BOD values live on the element,
including the most important one in the whole message.

| BOD path | Source | Why it matters |
|---|---|---|
| `Segment/UUID` | `bis.Element.FederationGuid` | **This is the CIRID.** Every participant registers its native identifier against it. Without it there is no correspondence and no handover. |
| `Segment/ShortName` | `bis.Element.CodeValue` | Human-facing location identifier |
| `Segment/FullName` | `bis.Element.UserLabel` | |
| `Segment/IDInInfoSource` | composed with `ElementId` | Round-trip traceability |

```sql
FROM DgnCustomItemTypes_DOT_DigitalAssetMetadata.DOT_PayItem1ElementAspect a
  JOIN bis.Element e ON e.ECInstanceId = a.Element.Id
-- add: e.FederationGuid, e.CodeValue, e.UserLabel, ec_classname(e.ECClassId,'s:c')
```

### 2.2 The six `[GAP]` values

| BOD path | What it is | Where to look |
|---|---|---|
| `G[Asset]/P[Project_ID]` | **Contract / SP number.** Keys `MaterialItem` and its `IDInInfoSource`. | iModel/iTwin properties, or a `bis.Subject` / `RepositoryLink` property. This is the highest-priority gap — the contract grain is load-bearing in the model. |
| `G[Asset]/P[Project]` | Contract name | as above |
| `G[Asset]/P[Status]` | Lifecycle status | Almost certainly the **named version**, not the element. This is what gates Issued for Construction. |
| `G[Asset]/P[PlanID]` | Plan sheet identifier | possibly derivable from `Tabulation_SheetNum` |
| `G[PlanPrep]/P[LabelPlanP]` | — | **Invented in the first draft.** The query exposes only `Label1`, `Label2`, `LabelNote`. Delete it unless a real column exists. |
| `MaterialItem/IDInInfoSource` | contract number ‖ item | blocked on the first row |

### 2.3 Two `[LOOKUP]` crosswalks MnDOT must publish

| Target | Keyed on | Notes |
|---|---|---|
| `UnitOfMeasure` UUID | `PayItem_Unit` | Maps `CY`, `LS`, `EA`, `LF`… to MIMOSA UOM UUIDs. **`Lump Sum` does not exist in MIMOSA reference data** and must be minted and registered in the CIR. Used at 22 sites in the example. |
| `LogisticResourceType` UUID | `PayItem_Class` or spec division | Material / Labour / Tool / Utility / Document. No source column carries this; a 2461 concrete item and a 2563 traffic-control item are indistinguishable in the projection. |

Also needed: `SegmentType` (Bridge, Culvert, Pavement…), derived from the element's ECClass or
SpatialCategory — a third crosswalk, on the element rather than the aspect.

### 2.4 Two `ECSCHEMA` unit traps

`KindOfQuantity`-typed properties are stored in **persistence units**, which are frequently not
the units a human would expect. Read them from `db.getSchemaProps("DOT")`, cache per schema
version, and convert in the extractor — never assume.

| Column | Suspicion |
|---|---|
| `Time_ItemTime` | Days or seconds? Check whether the property is a bare `Double` or KoQ-typed. |
| `QAQC_MajorTimeValue` | KoQ-typed; persistence unit is **likely seconds**, and the example labels it *Days*. |

Emitting a MIMOSA `UnitOfMeasure` UUID is an assertion about the unit. Get this wrong and the
BOD is confidently, silently incorrect — schema validation will not catch it.

### 2.5 Configuration and runtime

`RegistrationSite`, both participant `LogicalID`s, the `InfoSource` UUIDs, the spec book
identifier used in the `MaterialMasterItem` key, `CreationDateTime` and `BODID`. None of these
come from the iModel; they come from the Service Directory and deployment config.

---

## 3. Property-level mapping

| # | Source | ECSQL alias | BOD path | Note |
|---|---|---|---|---|
| 1 | ASPECT | `PayItem_SpecBook` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[SpecBook]` | reference-level value denormalised onto every aspect row |
| 2 | ASPECT | `PayItem_Class` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[Class]` |  |
| 3 | ASPECT | `PayItem_Type` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[Type]` |  |
| 4 | ASPECT | `PayItem_Item` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[Item]` | de-duplication key for the master |
| 5 | ASPECT | `PayItem_RefitemNm` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[REFITEM_NM]` |  |
| 6 | ASPECT | `PayItem_Unit` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[Unit]` | also drives the UnitOfMeasure LOOKUP |
| 7 | ASPECT | `PayItem_UnitPlan` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[UnitPlan]` |  |
| 8 | ASPECT | `PayItem_IsPlanQty` | `MIOS/MaterialItem/MaterialMasterItem/PS/P[IsPlanQty]` |  |
| 9 | ASPECT | `Help_Tip` | `MIOS/MaterialItem/MaterialMasterItem/PS/G[Help]/P[Tip]` | reference-level; denormalised in source |
| 10 | ASPECT | `Help_Link` | `MIOS/MaterialItem/MaterialMasterItem/PS/G[Help]/P[Link]` | reference-level; denormalised in source |
| 11 | ASPECT | `Quantity_Method` | `MIOS/PS/G[Quantity]/P[Method]` |  |
| 12 | ASPECT | `Quantity_Description` | `MIOS/PS/G[Quantity]/P[Description]` |  |
| 13 | ASPECT | `Quantity_FactorItem` | `MIOS/PS/G[Quantity]/P[FactorItem]` |  |
| 14 | ASPECT | `Quantity_FactorUser` | `MIOS/PS/G[Quantity]/P[FactorUser]` |  |
| 15 | ASPECT | `Quantity_Factor` | `MIOS/PS/G[Quantity]/P[Factor]` | ALIAS REQUIRED, collides with Cost_Factor |
| 16 | ASPECT | `Quantity_Override` | `MIOS/PS/G[Quantity]/P[Override]` | ALIAS REQUIRED, collides with Cost_Override |
| 17 | ASPECT | `Quantity_CompQuantity` | `MIOS/PS/G[Quantity]/P[CompQuantity]` | raw geometric take-off, pre-rounding |
| 18 | ASPECT | `Quantity_RoundingConservative` | `MIOS/PS/G[Quantity]/P[RoundingConservative]` |  |
| 19 | ASPECT | `Quantity_RoundingIncrement` | `MIOS/PS/G[Quantity]/P[RoundingIncrement]` |  |
| 20 | ASPECT | `Quantity_RoundingPrecision` | `MIOS/PS/G[Quantity]/P[RoundingPrecision]` |  |
| 21 | ASPECT | `Quantity_ItemQuantity` | `MIOS/PS/G[Quantity]/P[ItemQuantity]` | THE take-off quantity - the one value ENG is authoritative for |
| 22 | ASPECT | `Cost_EstimateType` | `MIOS/PS/G[Cost]/P[EstimateType]` |  |
| 23 | ASPECT | `Cost_EngEstPrice` | `MIOS/PS/G[Cost]/P[EngEstPrice]` | AASHTOWare template price AS OF CLONE TIME - preserves template lineage |
| 24 | ASPECT | `Cost_Override` | `MIOS/PS/G[Cost]/P[Override]` | user modification to the template |
| 25 | ASPECT | `Cost_Factor` | `MIOS/PS/G[Cost]/P[Factor]` | user modification to the template |
| 26 | ASPECT | `Cost_UnitPrice` | `MIOS/PS/G[Cost]/P[UnitPrice]` | resolved price after override/factor |
| 27 | ASPECT | `Cost_ItemCost` | `MIOS/PS/G[Cost]/P[ItemCost]` | derived: UnitPrice x ItemQuantity |
| 28 | ASPECT | `Time_EngEstUnitPerDay` | `MIOS/PS/G[Time]/P[EngEstUnitPerDay]` | template production rate at clone time |
| 29 | ASPECT | `Time_UnitOverride` | `MIOS/PS/G[Time]/P[UnitOverride]` |  |
| 30 | ASPECT | `Time_UnitFactor` | `MIOS/PS/G[Time]/P[UnitFactor]` |  |
| 31 | ASPECT | `Time_WorkforceFactor` | `MIOS/PS/G[Time]/P[WorkforceFactor]` |  |
| 32 | ASPECT | `Time_UnitPerDay` | `MIOS/PS/G[Time]/P[UnitPerDay]` |  |
| 33 | ASPECT | `Time_ItemTime` | `MIOS/PS/G[Time]/P[ItemTime]` | ECSCHEMA CHECK: bare Double or KindOfQuantity? persistence unit may be seconds, not days |
| 34 | ASPECT | `Funding_Code` | `MIOS/PS/G[Funding]/P[Code]` |  |
| 35 | ASPECT | `Funding_Name` | `MIOS/PS/G[Funding]/P[Name]` |  |
| 36 | ASPECT | `Funding_Payer` | `MIOS/PS/G[Funding]/P[Payer]` |  |
| 37 | ASPECT | `Funding_Percentage` | `MIOS/PS/G[Funding]/P[Percentage]` |  |
| 38 | ASPECT | `Funding_FederalParticipation` | `MIOS/PS/G[Funding]/P[FederalParticipation]` | String, domain unknown - normalise to Boolean if it is a Y/N flag |
| 39 | ASPECT | `ConstructionPhase_YR` | `MIOS/PS/G[ConstructionPhase]/P[YR]` |  |
| 40 | ASPECT | `ConstructionPhase_Stage` | `MIOS/PS/G[ConstructionPhase]/P[Stage]` |  |
| 41 | ASPECT | `ConstructionPhase_Class` | `MIOS/PS/G[ConstructionPhase]/P[Class]` | ALIAS REQUIRED, collides with PayItem_Class and Tabulation_Class |
| 42 | ASPECT | `ConstructionPhase_Group` | `MIOS/PS/G[ConstructionPhase]/P[Group]` | ALIAS + BRACKETS REQUIRED - [Group] is reserved |
| 43 | ASPECT | `ConstructionPhase_Sequence` | `MIOS/PS/G[ConstructionPhase]/P[Sequence]` |  |
| 44 | ASPECT | `ConstructionPhase_ActivityName` | `MIOS/PS/G[ConstructionPhase]/P[ActivityName]` |  |
| 45 | **GAP** | `—` | `MIOS/PS/G[PlanPrep]/P[LabelPlanP]` | was invented in the first draft; the query exposes only Label1/Label2/LabelNote |
| 46 | ASPECT | `PlanPrep_LabelNote` | `MIOS/PS/G[PlanPrep]/P[LabelNote]` |  |
| 47 | ASPECT | `PlanPrep_Label1` | `MIOS/PS/G[PlanPrep]/P[Label1]` |  |
| 48 | ASPECT | `PlanPrep_Label2` | `MIOS/PS/G[PlanPrep]/P[Label2]` |  |
| 49 | ASPECT | `Tabulation_Class` | `MIOS/PS/G[Tabulation]/P[Class]` | ALIAS REQUIRED - name collision |
| 50 | ASPECT | `Tabulation_Category` | `MIOS/PS/G[Tabulation]/P[Category]` |  |
| 51 | ASPECT | `Tabulation_Group` | `MIOS/PS/G[Tabulation]/P[Group]` | ALIAS + BRACKETS REQUIRED |
| 52 | ASPECT | `Tabulation_Code` | `MIOS/PS/G[Tabulation]/P[Code]` |  |
| 53 | ASPECT | `Tabulation_Title` | `MIOS/PS/G[Tabulation]/P[Title]` |  |
| 54 | ASPECT | `Tabulation_SheetNum` | `MIOS/PS/G[Tabulation]/P[SheetNum]` |  |
| 55 | ASPECT | `Tabulation_DOT_DigitalAssetMetadata_ID_` | `MIOS/PS/G[Tabulation]/P[DOT_DigitalAssetMetadata_ID_]` |  |
| 56 | **GAP** | `—` | `MIOS/PS/G[Asset]/P[Project_ID]` | CRITICAL: the contract/SP number keys MaterialItem and IDInInfoSource - find its real home |
| 57 | **GAP** | `—` | `MIOS/PS/G[Asset]/P[Project]` | contract name - not exposed by this aspect |
| 58 | ASPECT | `Asset_Index` | `MIOS/PS/G[Asset]/P[Index]` | BRACKETS REQUIRED - [Index] is reserved |
| 59 | **GAP** | `—` | `MIOS/PS/G[Asset]/P[Status]` | lifecycle status drives the IFC release gate - likely ELEMENT or named-version metadata |
| 60 | ASPECT | `Asset_Instance` | `MIOS/PS/G[Asset]/P[Instance]` |  |
| 61 | **GAP** | `—` | `MIOS/PS/G[Asset]/P[PlanID]` |  |
| 62 | ASPECT | `Asset_PlanDecoder` | `MIOS/PS/G[Asset]/P[PlanDecoder]` |  |
| 63 | ASPECT | `Asset_IsTracked` | `MIOS/PS/G[Asset]/P[IsTracked]` | candidate discriminator for whether this occurrence warrants a functional location |
| 64 | ASPECT | `Asset_AssetID` | `MIOS/PS/G[Asset]/P[AssetID]` | NOT an identity - see 3.4; ElementId hex, may be renumbered by a connector resync |
| 65 | ASPECT | `QAQC_MajorCostValue` | `MIOS/PS/G[QAQC]/P[MajorCostValue]` |  |
| 66 | ASPECT | `QAQC_MajorTimeValue` | `MIOS/PS/G[QAQC]/P[MajorTimeValue]` | ECSCHEMA CHECK: KindOfQuantity-typed; persistence unit likely SECONDS not days |
| 67 | ASPECT | `QAQC_IsMajorItemLookup` | `MIOS/PS/G[QAQC]/P[IsMajorItemLookup]` |  |
| 68 | ASPECT | `QAQC_IsMajorCost` | `MIOS/PS/G[QAQC]/P[IsMajorCost]` |  |
| 69 | ASPECT | `QAQC_IsMajorTime` | `MIOS/PS/G[QAQC]/P[IsMajorTime]` |  |
| 70 | ASPECT | `QAQC_IsCheckQty` | `MIOS/PS/G[QAQC]/P[IsCheckQty]` |  |
| 71 | ASPECT | `QAQC_IsCheckCost` | `MIOS/PS/G[QAQC]/P[IsCheckCost]` |  |
| 72 | ASPECT | `QAQC_IsSiblingRequired` | `MIOS/PS/G[QAQC]/P[IsSiblingRequired]` | relevant to the component-slot question - siblings are the other numbered aspect classes |
| 73 | ASPECT | `QAQC_PastContractCount` | `MIOS/PS/G[QAQC]/P[PastContractCount]` |  |
| 74 | ASPECT | `QAQC_IsCommonItem` | `MIOS/PS/G[QAQC]/P[IsCommonItem]` |  |
| 75 | ASPECT | `QAQC_ElementDescription` | `MIOS/PS/G[QAQC]/P[ElementDescription]` |  |
| 76 | ASPECT | `Remarks_Remarks` | `MIOS/PS/G[Remarks]/P[Remark]` |  |
| 77 | ASPECT | `SpecbookFilter` | `MIOS/PS/G[SourceAuthoring]/P[SPECBOOK_FILTER]` | top-level aspect column, not inside a struct |
| 78 | ASPECT | `RefitemSearch` | `MIOS/PS/G[SourceAuthoring]/P[REFITEM_SEARCH]` | top-level aspect column, not inside a struct |

---

## 4. Observations on the query itself

**`WHERE ElementId = 0x700000000ef` filters on an alias.** ECSQL resolves the `WHERE` clause
against the source, not the projection. Use `WHERE Element.Id = 0x700000000ef`, or better,
parameterise it.

**Only `DOT_PayItem1ElementAspect` is selected.** The numbered classes are component slots on one
assembly (`3.1.4` of the decision document). A single element carrying a shaft *and* a luminaire
will silently publish only the first. Either run the discovery query for a shared base class and
go polymorphic over it, or `UNION ALL` the numbered classes explicitly — and carry
`AspectClass` through as the `componentCode`, which the occurrence UUID key already expects.

**The aliasing is right.** `Quantity.Factor` vs `Cost.Factor`, `Quantity.Override` vs
`Cost.Override`, and three separate `Class` columns all collide without it. `[Time]`, `[Group]`,
`[Type]`, `[Class]`, `[Index]`, `[Description]`, `[Override]` are correctly bracketed.

**Add `QueryRowFormat.UseECSqlPropertyNames`** so the row keys match these aliases rather than
being positional.

**Prefer `getAspects()` for retrieval.** Use ECSQL to *select* which elements and aspects to
publish; then read the aspect instances through the API, where nested structs arrive as objects
and map straight onto `PropertyGroup` without a 77-column projection to maintain.

---

## 5. Order of work

1. **Add the `bis.Element` join.** Nothing publishes without `FederationGuid`.
2. **Find the contract number.** It keys `MaterialItem`; the model has a hole until it is located.
3. **Resolve unit persistence** for the two time fields via schema metadata.
4. **Publish the crosswalks** — UOM and LogisticResourceType — and register their UUIDs in the CIR.
5. **Publish `SyncAttributeSetDefinitions`** for all 156 `PropertyDefinition`s before any segment
   data. Per the decision document this is non-optional: the take-off quantity is the one value
   ENG is authoritative for, and it is the value the schema types least well.
6. **Settle the component-slot question** before the aspect class is baked into occurrence UUIDs.
