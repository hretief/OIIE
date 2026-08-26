#!/usr/bin/env python3
"""Generate an example CCOM 4.1 SyncSegments BOD carrying DOT construction PayItems
as MaterialItemOnSegment occurrences.  Every element used is declared in CCOM.xsd /
Meta.xsd (OAGIS Platform 1.2.1).  UUIDs for reference data are taken from the MIMOSA
'CCOM Reference Data' set; all project-scoped UUIDs are deterministic (uuid5)."""

import uuid
from xml.sax.saxutils import escape

CCOM = "http://www.mimosa.org/ccom4"
NS = uuid.UUID("6ba7b811-9dad-11d1-80b4-00c04fd430c8")  # RFC4122 URL namespace


def U(path: str) -> str:
    return str(uuid.uuid5(NS, "https://www.dot.state.mn.us/oiie/" + path))


# ---------------------------------------------------------------- reference data
RD = {
    "status.active":      "db4bf287-2374-4e9c-bd4b-fadaada24b99",  # EffectiveStatusType Active
    "lrt.material":       "67782185-ac49-44f7-8b8c-cafba7fa9608",  # LogisticResourceType Material
    "lrt.labour":         "bed9a6de-f54e-47bd-8ae5-01fa3c68a84e",  # LogisticResourceType Labour
    "segtype.bridge":     "6c42560b-6057-4cab-91e8-27cde5434eb8",  # SegmentType Bridge
    "uom.usd":            "39c197f5-759f-41c7-805e-2a87b6f9e29d",  # UnitOfMeasure US Dollar
    "uom.cubicyards":     "e4108785-fa78-4838-900a-b602c38db4e7",  # UnitOfMeasure Cubic Yards
    "uom.days":           "0904787c-69f1-416f-b3be-5cc4585ca5e2",  # UnitOfMeasure Days
    "uom.each":           "9a1d5f8c-8711-42c9-8b5d-fca99d87dfdc",  # UnitOfMeasure Each
    "uomq.currency":      "dbc268de-6847-4729-926f-6f46a18af09d",  # UOMQuantity Currency
}
# Project-scoped reference data (MnDOT must publish these via SyncTypes / SyncOrderedLists)
RD["uom.lumpsum"] = U("uom/LumpSum")
RD["pstype.payitem"] = U("propertysettype/DOTPayItem")

IS_IMODEL = ("InfoSource", U("infosource/iModel/BR27043"), "MnDOT iTwin iModel - SP 2758-92")
IS_SPECBOOK = ("InfoSource", U("infosource/MnDOTSpecBook2020"),
               "MnDOT Standard Specifications for Construction, 2020 Edition")



# ================================================================= PROVENANCE
# Source taxonomy used in the emitted XML comments:
#
#   ASPECT   column of the DOT_PayItem1ElementAspect ECSQL projection (alias given)
#   ELEMENT  bis.Element / GeometricElement3d - requires a JOIN not present in the query
#   IMODEL   iModel- or iTwin-level metadata (FederationGuid, changeset, contract props)
#   ECSCHEMA ECSchema metadata - db.getSchemaProps("DOT"); KindOfQuantity persistence unit
#   REFDATA  MIMOSA 'CCOM Reference Data' UUID - fixed, ships with the standard
#   LOOKUP   requires an MnDOT-published crosswalk table (unit code -> UOM UUID, etc.)
#   MINTED   deterministic uuid5 computed by the extractor from stable inputs
#   CONFIG   deployment / participant configuration
#   RUNTIME  generated at send time
#   GAP      NOT AVAILABLE from the query or any identified source - must be resolved

SRC = {}


def S(path, kind, detail, note=""):
    SRC[path] = (kind, detail, note)


def ann(path, level=None, inline=True):
    """Render a provenance comment for a mapped path."""
    if path not in SRC:
        return "  <!-- [???] unmapped -->" if inline else ""
    kind, detail, note = SRC[path]
    body = (f"[{kind}] {detail}" + (f" | {note}" if note else "")).replace("--", "-")
    if inline:
        return f"  <!-- {body} -->"
    return f"{ind(level)}<!-- {body} -->\n"


def c(kind, detail, note=""):
    """Ad-hoc inline provenance comment."""
    body = (f"[{kind}] {detail}" + (f" | {note}" if note else "")).replace("--", "-")
    return f"  <!-- {body} -->"


# ---- reference / master-item level (denormalised copy of the spec book row) ---
S("SpecBook Reference Item.SpecBook",   "ASPECT", "PayItem_SpecBook",
  "reference-level value denormalised onto every aspect row")
S("SpecBook Reference Item.Class",      "ASPECT", "PayItem_Class")
S("SpecBook Reference Item.Type",       "ASPECT", "PayItem_Type")
S("SpecBook Reference Item.Item",       "ASPECT", "PayItem_Item", "de-duplication key for the master")
S("SpecBook Reference Item.REFITEM_NM", "ASPECT", "PayItem_RefitemNm")
S("SpecBook Reference Item.Unit",       "ASPECT", "PayItem_Unit", "also drives the UnitOfMeasure LOOKUP")
S("SpecBook Reference Item.UnitPlan",   "ASPECT", "PayItem_UnitPlan")
S("SpecBook Reference Item.IsPlanQty",  "ASPECT", "PayItem_IsPlanQty")
S("Help.Tip",  "ASPECT", "Help_Tip",  "reference-level; denormalised in source")
S("Help.Link", "ASPECT", "Help_Link", "reference-level; denormalised in source")

# ---- occurrence: Quantity ----------------------------------------------------
S("Quantity.Method",               "ASPECT", "Quantity_Method")
S("Quantity.Description",          "ASPECT", "Quantity_Description")
S("Quantity.FactorItem",           "ASPECT", "Quantity_FactorItem")
S("Quantity.FactorUser",           "ASPECT", "Quantity_FactorUser")
S("Quantity.Factor",               "ASPECT", "Quantity_Factor",
  "ALIAS REQUIRED, collides with Cost_Factor")
S("Quantity.Override",             "ASPECT", "Quantity_Override",
  "ALIAS REQUIRED, collides with Cost_Override")
S("Quantity.CompQuantity",         "ASPECT", "Quantity_CompQuantity",
  "raw geometric take-off, pre-rounding")
S("Quantity.RoundingConservative", "ASPECT", "Quantity_RoundingConservative")
S("Quantity.RoundingIncrement",    "ASPECT", "Quantity_RoundingIncrement")
S("Quantity.RoundingPrecision",    "ASPECT", "Quantity_RoundingPrecision")
S("Quantity.ItemQuantity",         "ASPECT", "Quantity_ItemQuantity",
  "THE take-off quantity - the one value ENG is authoritative for")

# ---- occurrence: Cost --------------------------------------------------------
S("Cost.EstimateType", "ASPECT", "Cost_EstimateType")
S("Cost.EngEstPrice",  "ASPECT", "Cost_EngEstPrice",
  "AASHTOWare template price AS OF CLONE TIME - preserves template lineage")
S("Cost.Override",     "ASPECT", "Cost_Override",   "user modification to the template")
S("Cost.Factor",       "ASPECT", "Cost_Factor",     "user modification to the template")
S("Cost.UnitPrice",    "ASPECT", "Cost_UnitPrice",  "resolved price after override/factor")
S("Cost.ItemCost",     "ASPECT", "Cost_ItemCost",   "derived: UnitPrice x ItemQuantity")

# ---- occurrence: Time --------------------------------------------------------
S("Time.EngEstUnitPerDay", "ASPECT", "Time_EngEstUnitPerDay", "template production rate at clone time")
S("Time.UnitOverride",     "ASPECT", "Time_UnitOverride")
S("Time.UnitFactor",       "ASPECT", "Time_UnitFactor")
S("Time.WorkforceFactor",  "ASPECT", "Time_WorkforceFactor")
S("Time.UnitPerDay",       "ASPECT", "Time_UnitPerDay")
S("Time.ItemTime",         "ASPECT", "Time_ItemTime",
  "ECSCHEMA CHECK: bare Double or KindOfQuantity? persistence unit may be seconds, not days")

# ---- occurrence: Funding -----------------------------------------------------
S("Funding.Code",                 "ASPECT", "Funding_Code")
S("Funding.Name",                 "ASPECT", "Funding_Name")
S("Funding.Payer",                "ASPECT", "Funding_Payer")
S("Funding.Percentage",           "ASPECT", "Funding_Percentage")
S("Funding.FederalParticipation", "ASPECT", "Funding_FederalParticipation",
  "String, domain unknown - normalise to Boolean if it is a Y/N flag")

# ---- occurrence: ConstructionPhase ------------------------------------------
S("ConstructionPhase.YR",           "ASPECT", "ConstructionPhase_YR")
S("ConstructionPhase.Stage",        "ASPECT", "ConstructionPhase_Stage")
S("ConstructionPhase.Class",        "ASPECT", "ConstructionPhase_Class",
  "ALIAS REQUIRED, collides with PayItem_Class and Tabulation_Class")
S("ConstructionPhase.Group",        "ASPECT", "ConstructionPhase_Group",
  "ALIAS + BRACKETS REQUIRED - [Group] is reserved")
S("ConstructionPhase.Sequence",     "ASPECT", "ConstructionPhase_Sequence")
S("ConstructionPhase.ActivityName", "ASPECT", "ConstructionPhase_ActivityName")

# ---- occurrence: PlanPrep ----------------------------------------------------
S("PlanPrep.LabelPlanP", "GAP", "no source column",
  "was invented in the first draft; the query exposes only Label1/Label2/LabelNote")
S("PlanPrep.LabelNote",  "ASPECT", "PlanPrep_LabelNote")
S("PlanPrep.Label1",     "ASPECT", "PlanPrep_Label1")
S("PlanPrep.Label2",     "ASPECT", "PlanPrep_Label2")

# ---- occurrence: Tabulation --------------------------------------------------
S("Tabulation.Class",    "ASPECT", "Tabulation_Class", "ALIAS REQUIRED - name collision")
S("Tabulation.Category", "ASPECT", "Tabulation_Category")
S("Tabulation.Group",    "ASPECT", "Tabulation_Group", "ALIAS + BRACKETS REQUIRED")
S("Tabulation.Code",     "ASPECT", "Tabulation_Code")
S("Tabulation.Title",    "ASPECT", "Tabulation_Title")
S("Tabulation.SheetNum", "ASPECT", "Tabulation_SheetNum")
S("Tabulation.DOT_DigitalAssetMetadata_ID_", "ASPECT",
  "Tabulation_DOT_DigitalAssetMetadata_ID_")

# ---- occurrence: Asset -------------------------------------------------------
S("Asset.Project_ID",  "GAP", "no source column",
  "CRITICAL: the contract/SP number keys MaterialItem and IDInInfoSource - find its real home")
S("Asset.Project",     "GAP", "no source column", "contract name - not exposed by this aspect")
S("Asset.Index",       "ASPECT", "Asset_Index", "BRACKETS REQUIRED - [Index] is reserved")
S("Asset.Status",      "GAP", "no source column",
  "lifecycle status drives the IFC release gate - likely ELEMENT or named-version metadata")
S("Asset.Instance",    "ASPECT", "Asset_Instance")
S("Asset.PlanID",      "GAP", "no source column")
S("Asset.PlanDecoder", "ASPECT", "Asset_PlanDecoder")
S("Asset.IsTracked",   "ASPECT", "Asset_IsTracked",
  "candidate discriminator for whether this occurrence warrants a functional location")
S("Asset.AssetID",     "ASPECT", "Asset_AssetID",
  "NOT an identity - see 3.4; ElementId hex, may be renumbered by a connector resync")

# ---- occurrence: QAQC --------------------------------------------------------
S("QAQC.MajorCostValue",     "ASPECT", "QAQC_MajorCostValue")
S("QAQC.MajorTimeValue",     "ASPECT", "QAQC_MajorTimeValue",
  "ECSCHEMA CHECK: KindOfQuantity-typed; persistence unit likely SECONDS not days")
S("QAQC.IsMajorItemLookup",  "ASPECT", "QAQC_IsMajorItemLookup")
S("QAQC.IsMajorCost",        "ASPECT", "QAQC_IsMajorCost")
S("QAQC.IsMajorTime",        "ASPECT", "QAQC_IsMajorTime")
S("QAQC.IsCheckQty",         "ASPECT", "QAQC_IsCheckQty")
S("QAQC.IsCheckCost",        "ASPECT", "QAQC_IsCheckCost")
S("QAQC.IsSiblingRequired",  "ASPECT", "QAQC_IsSiblingRequired",
  "relevant to the component-slot question - siblings are the other numbered aspect classes")
S("QAQC.PastContractCount",  "ASPECT", "QAQC_PastContractCount")
S("QAQC.IsCommonItem",       "ASPECT", "QAQC_IsCommonItem")
S("QAQC.ElementDescription", "ASPECT", "QAQC_ElementDescription")

# ---- occurrence: Remarks / SourceAuthoring ----------------------------------
S("Remarks.Remark",            "ASPECT", "Remarks_Remarks")
S("SourceAuthoring.SPECBOOK_FILTER", "ASPECT", "SpecbookFilter",
  "top-level aspect column, not inside a struct")
S("SourceAuthoring.REFITEM_SEARCH",  "ASPECT", "RefitemSearch",
  "top-level aspect column, not inside a struct")


IND = "  "


def ind(n):
    return IND * n


# ---------------------------------------------------------------- emitters
def ref(tag, uid, short=None, level=0, extra=""):
    """A by-UUID reference to an Entity (UUID first, then optional ShortName)."""
    s = f"{ind(level)}<{tag}>\n{ind(level+1)}<UUID>{uid}</UUID>\n"
    if short:
        s += f"{ind(level+1)}<ShortName>{escape(short)}</ShortName>\n"
    s += extra
    s += f"{ind(level)}</{tag}>\n"
    return s


def measure(value, uom_uuid, uom_name, level, uomq=None):
    s = f"{ind(level)}<Measure>\n"
    s += f"{ind(level+1)}<Value>{value}</Value>\n"
    s += f"{ind(level+1)}<UnitOfMeasure>{c('LOOKUP', 'unit code -> MIMOSA UnitOfMeasure UUID', 'crosswalk keyed on PayItem_Unit; MnDOT must publish and register in the CIR')}\n"
    s += f"{ind(level+2)}<UUID>{uom_uuid}</UUID>\n"
    s += f"{ind(level+2)}<ShortName>{escape(uom_name)}</ShortName>\n"
    if uomq:
        s += f"{ind(level+2)}<UOMQuantity>\n{ind(level+3)}<UUID>{uomq[0]}</UUID>\n"
        s += f"{ind(level+3)}<ShortName>{escape(uomq[1])}</ShortName>\n{ind(level+2)}</UOMQuantity>\n"
    s += f"{ind(level+1)}</UnitOfMeasure>\n"
    s += f"{ind(level)}</Measure>\n"
    return s


def prop(occ_key, defn_path, short, kind, value, level, order=None, uom=None):
    """One CCOM Property.  Order of children is fixed by the schema:
       Entity(UUID..) , ShortName , (Type|Definition) , ValueContent , Order"""
    s = f"{ind(level)}<SetProperty>{ann(defn_path)}\n"
    s += (f"{ind(level+1)}<UUID>{U('property/' + occ_key + '/' + defn_path)}</UUID>"
          f"{c('MINTED', 'uuid5(occurrence-key + property path)')}\n")
    s += f"{ind(level+1)}<ShortName>{escape(short)}</ShortName>\n"
    s += (f"{ind(level+1)}<Definition>"
          f"{c('MINTED', 'PropertyDefinition UUID', 'MnDOT must publish via SyncAttributeSetDefinitions BEFORE this BOD')}\n"
          f"{ind(level+2)}<UUID>{U('propertydefinition/' + defn_path)}</UUID>\n")
    s += f"{ind(level+2)}<ShortName>{escape(defn_path)}</ShortName>\n{ind(level+1)}</Definition>\n"
    s += f"{ind(level+1)}<ValueContent>\n"
    if kind == "Measure":
        s += measure(value, uom[0], uom[1], level + 2, uom[2] if len(uom) > 2 else None)
    elif kind == "Text":
        s += f"{ind(level+2)}<Text>{escape(str(value))}</Text>\n"
    elif kind == "Number":
        s += f"{ind(level+2)}<Number>{value}</Number>\n"
    elif kind == "Boolean":
        s += f"{ind(level+2)}<Boolean>{'true' if value else 'false'}</Boolean>\n"
    elif kind == "Percentage":
        s += f"{ind(level+2)}<Percentage>{value}</Percentage>\n"
    elif kind == "URI":
        s += f"{ind(level+2)}<URI>{escape(str(value))}</URI>\n"
    else:
        raise ValueError(kind)
    s += f"{ind(level+1)}</ValueContent>\n"
    if order is not None:
        s += f"{ind(level+1)}<Order>{order}</Order>\n"
    s += f"{ind(level)}</SetProperty>\n"
    return s


def group(occ_key, group_name, order, props, level):
    """PropertyGroup: Entity(UUID), ShortName, Definition, Order, Group*, SetProperty*"""
    s = f"{ind(level)}<Group>{c('ECSTRUCT', 'DOT_PayItem1ElementAspect.' + group_name, 'one PropertyGroup per source EC struct')}\n"
    s += f"{ind(level+1)}<UUID>{U('propertygroup/' + occ_key + '/' + group_name)}</UUID>\n"
    s += f"{ind(level+1)}<ShortName>{escape(group_name)}</ShortName>\n"
    s += f"{ind(level+1)}<Definition>\n{ind(level+2)}<UUID>{U('propertygroupdefinition/' + group_name)}</UUID>\n"
    s += f"{ind(level+2)}<ShortName>{escape(group_name)}</ShortName>\n{ind(level+1)}</Definition>\n"
    s += f"{ind(level+1)}<Order>{order}</Order>\n"
    for p in props:
        s += prop(occ_key, group_name + "." + p[0], p[0], p[1], p[2], level + 1,
                  order=p[3], uom=(p[4] if len(p) > 4 else None))
    s += f"{ind(level)}</Group>\n"
    return s


def property_set(occ_key, set_name, set_type_uuid, set_type_name, defn_uuid, defn_name,
                 flat_props, groups, level):
    """PropertySetForEntity -> PropertySet.
       PropertySet order: Entity(UUID..), ShortName, Type, Definition, SetProperty*, Group*"""
    s = f"{ind(level)}<PropertySetForEntity>\n"
    s += f"{ind(level+1)}<UUID>{U('propertysetforentity/' + occ_key + '/' + set_name)}</UUID>\n"
    s += f"{ind(level+1)}<PropertySet>\n"
    L = level + 2
    s += f"{ind(L)}<UUID>{U('propertyset/' + occ_key + '/' + set_name)}</UUID>\n"
    s += f"{ind(L)}<ShortName>{escape(set_name)}</ShortName>\n"
    s += ref("Type", set_type_uuid, set_type_name, L)
    s += ref("Definition", defn_uuid, defn_name, L)
    for p in flat_props:
        s += prop(occ_key, set_name + "." + p[0], p[0], p[1], p[2], L,
                  order=p[3], uom=(p[4] if len(p) > 4 else None))
    for g in groups:
        s += group(occ_key, g[0], g[1], g[2], L)
    s += f"{ind(level+1)}</PropertySet>\n"
    s += f"{ind(level)}</PropertySetForEntity>\n"
    return s


def info_source(uid, name, level):
    return ref("InfoSource", uid, name, level)


# ---------------------------------------------------------------- pay item data
USD = (RD["uom.usd"], "US Dollar", (RD["uomq.currency"], "Currency"))
CY = (RD["uom.cubicyards"], "Cubic Yards")
LS = (RD["uom.lumpsum"], "Lump Sum")
DAY = (RD["uom.days"], "Days")

PAYITEM_1 = dict(
    key="2461.504@BR27043-DECK",
    master_key="2461.504",
    item_no="2461.504",
    name="STRUCTURAL CONCRETE (3Y33)",
    description="Structural concrete, mix designation 3Y33, placed in bridge deck and "
                "integral wearing course, measured by volume in place.",
    lrt=(RD["lrt.material"], "Material"),
    uom=CY,
    unit_code="CY",
    master=[
        ("SpecBook", "Text", "MnDOT 2020 Standard Specifications for Construction", 1),
        ("Class", "Text", "2461", 2),
        ("Type", "Text", "504", 3),
        ("Item", "Text", "2461.504", 4),
        ("REFITEM_NM", "Text", "STRUCTURAL CONCRETE (3Y33)", 5),
        ("Unit", "Text", "CY", 6),
        ("UnitPlan", "Text", "CU YD", 7),
        ("IsPlanQty", "Boolean", True, 8),
    ],
    master_groups=[
        ("Help", 1, [
            ("Tip", "Text", "Use 3Y33 for bridge decks with an integral wearing course; "
                            "see Spec 2461.3.C for mix approval.", 1),
            ("Link", "URI", "https://www.dot.state.mn.us/pre-letting/spec/2020/2461.pdf", 2),
        ]),
    ],
    quantity=[
        ("Method", "Text", "Neat line takeoff from deck solid", 1),
        ("Description", "Text", "Deck slab 42.0 ft x 236.5 ft x 9.5 in plus integral wearing course", 2),
        ("FactorItem", "Number", "1.000", 3),
        ("FactorUser", "Number", "1.020", 4),
        ("Factor", "Number", "1.020", 5),
        ("Override", "Number", "0.000", 6),
        ("CompQuantity", "Measure", "404.41", 7, CY),
        ("RoundingConservative", "Boolean", True, 8),
        ("RoundingIncrement", "Number", "0.5", 9),
        ("RoundingPrecision", "Number", "1", 10),
        ("ItemQuantity", "Measure", "412.5", 11, CY),
    ],
    cost=[
        ("EstimateType", "Text", "Engineer Estimate - Historical Bid Average", 1),
        ("EngEstPrice", "Measure", "662.50", 2, USD),
        ("Override", "Measure", "0.00", 3, USD),
        ("Factor", "Number", "1.034", 4),
        ("UnitPrice", "Measure", "685.00", 5, USD),
        ("ItemCost", "Measure", "282562.50", 6, USD),
    ],
    time=[
        ("EngEstUnitPerDay", "Number", "60.0", 1),
        ("UnitOverride", "Number", "0.0", 2),
        ("UnitFactor", "Number", "1.000", 3),
        ("WorkforceFactor", "Number", "1.150", 4),
        ("UnitPerDay", "Number", "69.0", 5),
        ("ItemTime", "Measure", "6.0", 6, DAY),
    ],
    funding=[
        ("Code", "Text", "NHPP", 1),
        ("Name", "Text", "National Highway Performance Program", 2),
        ("Payer", "Text", "FHWA / MnDOT State Aid", 3),
        ("Percentage", "Percentage", "80.0", 4),
        ("FederalParticipation", "Text", "Participating", 5),
    ],
    phase=[
        ("YR", "Text", "2027", 1),
        ("Stage", "Text", "Stage 2", 2),
        ("Class", "Text", "Structures", 3),
        ("Group", "Text", "Superstructure", 4),
        ("Sequence", "Text", "040", 5),
        ("ActivityName", "Text", "Place bridge deck concrete", 6),
    ],
    planprep=[
        ("LabelPlanP", "Text", "STRUCTURAL CONCRETE (3Y33)", 1),
        ("LabelNote", "Text", "Includes integral wearing course; see Sheet S-14", 2),
        ("Label1", "Text", "DECK", 3),
        ("Label2", "Text", "3Y33", 4),
    ],
    tabulation=[
        ("Class", "Text", "Bridge", 1),
        ("Category", "Text", "Concrete", 2),
        ("Group", "Text", "Superstructure Quantities", 3),
        ("Code", "Text", "TAB-BR-CONC", 4),
        ("Title", "Text", "Tabulation of Bridge Concrete Quantities", 5),
        ("SheetNum", "Text", "S-04", 6),
        ("DOT_DigitalAssetMetadata_ID_", "Text", "DAM-2758-92-000417", 7),
    ],
    asset=[
        ("Project_ID", "Text", "2758-92", 1),
        ("Project", "Text", "TH 61 over Silver Creek - Bridge 27043 Replacement", 2),
        ("Index", "Text", "0042", 3),
        ("Status", "Text", "Issued for Construction", 4),
        ("Instance", "Text", "1", 5),
        ("PlanID", "Text", "SP2758-92-S04", 6),
        ("PlanDecoder", "Text", "SP-SHEET-REV", 7),
        ("IsTracked", "Boolean", True, 8),
        ("AssetID", "Text", "0x20000000c41", 9),
    ],
    qaqc=[
        ("MajorCostValue", "Measure", "250000.00", 1, USD),
        ("MajorTimeValue", "Measure", "5.0", 2, DAY),
        ("IsMajorItemLookup", "Boolean", True, 3),
        ("IsMajorCost", "Boolean", True, 4),
        ("IsMajorTime", "Boolean", True, 5),
        ("IsCheckQty", "Boolean", True, 6),
        ("IsCheckCost", "Boolean", False, 7),
        ("IsSiblingRequired", "Boolean", True, 8),
        ("PastContractCount", "Number", "37", 9),
        ("IsCommonItem", "Boolean", True, 10),
        ("ElementDescription", "Text", "Bridge deck slab, integral wearing course", 11),
    ],
    remarks=[
        ("Remark", "Text", "Quantity confirmed against deck solid revision C dated 2026-07-14.", 1),
    ],
    authoring=[
        ("SPECBOOK_FILTER", "Text", "MnDOT2020", 1),
        ("REFITEM_SEARCH", "Text", "2461*", 2),
    ],
)

PAYITEM_2 = dict(
    key="2563.601@BR27043-DECK",
    master_key="2563.601",
    item_no="2563.601",
    name="TRAFFIC CONTROL",
    description="Furnishing, installing, maintaining and removing all traffic control devices, "
                "flagging and temporary signing required to stage construction. Non-material "
                "contract activity paid as a lump sum.",
    lrt=(RD["lrt.labour"], "Labour"),
    uom=LS,
    unit_code="LS",
    master=[
        ("SpecBook", "Text", "MnDOT 2020 Standard Specifications for Construction", 1),
        ("Class", "Text", "2563", 2),
        ("Type", "Text", "601", 3),
        ("Item", "Text", "2563.601", 4),
        ("REFITEM_NM", "Text", "TRAFFIC CONTROL", 5),
        ("Unit", "Text", "LS", 6),
        ("UnitPlan", "Text", "LUMP SUM", 7),
        ("IsPlanQty", "Boolean", False, 8),
    ],
    master_groups=[
        ("Help", 1, [
            ("Tip", "Text", "Lump sum item; do not tabulate device counts as pay quantities.", 1),
            ("Link", "URI", "https://www.dot.state.mn.us/pre-letting/spec/2020/2563.pdf", 2),
        ]),
    ],
    quantity=[
        ("Method", "Text", "Lump sum - no takeoff", 1),
        ("Description", "Text", "Single lump sum covering all staging for the bridge work", 2),
        ("FactorItem", "Number", "1.000", 3),
        ("FactorUser", "Number", "1.000", 4),
        ("Factor", "Number", "1.000", 5),
        ("Override", "Number", "0.000", 6),
        ("CompQuantity", "Measure", "1", 7, LS),
        ("RoundingConservative", "Boolean", False, 8),
        ("RoundingIncrement", "Number", "1", 9),
        ("RoundingPrecision", "Number", "0", 10),
        ("ItemQuantity", "Measure", "1", 11, LS),
    ],
    cost=[
        ("EstimateType", "Text", "Engineer Estimate - Percentage of Contract", 1),
        ("EngEstPrice", "Measure", "138000.00", 2, USD),
        ("Override", "Measure", "145000.00", 3, USD),
        ("Factor", "Number", "1.000", 4),
        ("UnitPrice", "Measure", "145000.00", 5, USD),
        ("ItemCost", "Measure", "145000.00", 6, USD),
    ],
    time=[
        ("EngEstUnitPerDay", "Number", "0.0", 1),
        ("UnitOverride", "Number", "0.0", 2),
        ("UnitFactor", "Number", "1.000", 3),
        ("WorkforceFactor", "Number", "1.000", 4),
        ("UnitPerDay", "Number", "0.0", 5),
        ("ItemTime", "Measure", "0.0", 6, DAY),
    ],
    funding=[
        ("Code", "Text", "STBG", 1),
        ("Name", "Text", "Surface Transportation Block Grant", 2),
        ("Payer", "Text", "FHWA / MnDOT State Aid", 3),
        ("Percentage", "Percentage", "50.0", 4),
        ("FederalParticipation", "Text", "Participating", 5),
    ],
    phase=[
        ("YR", "Text", "2027", 1),
        ("Stage", "Text", "All Stages", 2),
        ("Class", "Text", "Traffic", 3),
        ("Group", "Text", "Temporary Works", 4),
        ("Sequence", "Text", "005", 5),
        ("ActivityName", "Text", "Maintain traffic control through staged construction", 6),
    ],
    planprep=[
        ("LabelPlanP", "Text", "TRAFFIC CONTROL", 1),
        ("LabelNote", "Text", "See Traffic Control Plan sheets T-01 through T-06", 2),
        ("Label1", "Text", "TC", 3),
        ("Label2", "Text", "LS", 4),
    ],
    tabulation=[
        ("Class", "Text", "Traffic", 1),
        ("Category", "Text", "Traffic Control", 2),
        ("Group", "Text", "Lump Sum Items", 3),
        ("Code", "Text", "TAB-TC-LS", 4),
        ("Title", "Text", "Tabulation of Lump Sum Traffic Items", 5),
        ("SheetNum", "Text", "T-01", 6),
        ("DOT_DigitalAssetMetadata_ID_", "Text", "DAM-2758-92-000521", 7),
    ],
    asset=[
        ("Project_ID", "Text", "2758-92", 1),
        ("Project", "Text", "TH 61 over Silver Creek - Bridge 27043 Replacement", 2),
        ("Index", "Text", "0107", 3),
        ("Status", "Text", "Issued for Construction", 4),
        ("Instance", "Text", "1", 5),
        ("PlanID", "Text", "SP2758-92-T01", 6),
        ("PlanDecoder", "Text", "SP-SHEET-REV", 7),
        ("IsTracked", "Boolean", True, 8),
        ("AssetID", "Text", "0x20000000c41", 9),
    ],
    qaqc=[
        ("MajorCostValue", "Measure", "250000.00", 1, USD),
        ("MajorTimeValue", "Measure", "5.0", 2, DAY),
        ("IsMajorItemLookup", "Boolean", True, 3),
        ("IsMajorCost", "Boolean", False, 4),
        ("IsMajorTime", "Boolean", False, 5),
        ("IsCheckQty", "Boolean", False, 6),
        ("IsCheckCost", "Boolean", True, 7),
        ("IsSiblingRequired", "Boolean", False, 8),
        ("PastContractCount", "Number", "212", 9),
        ("IsCommonItem", "Boolean", True, 10),
        ("ElementDescription", "Text", "Project-wide temporary traffic control", 11),
    ],
    remarks=[
        ("Remark", "Text", "Override price applied after district review of 2025 lettings.", 1),
    ],
    authoring=[
        ("SPECBOOK_FILTER", "Text", "MnDOT2020", 1),
        ("REFITEM_SEARCH", "Text", "2563*", 2),
    ],
)


def material_master_item(pi, level):
    """MaterialMasterItem: Entity(UUID, IDInInfoSource, InfoSource, EffectiveStatusType,
       PropertySetForEntity) , ShortName, FullName, Description, Type"""
    k = pi["master_key"]
    s = (f"{ind(level)}<MaterialMasterItem>"
         f"{c('SCOPE', 'AASHTOWare spec book item', 'IDENTITY STUB ONLY - AASHTOWare is authoritative; see decision doc 3.2.1')}\n")
    L = level + 1
    s += (f"{ind(L)}<UUID>{U('materialmasteritem/' + k)}</UUID>"
          f"{c('MINTED', 'uuid5(specBookId :: PayItem_Item)', 'specBookId is CONFIG; PayItem_SpecBook string is not a stable key')}\n")
    s += f"{ind(L)}<IDInInfoSource>{pi['item_no']}</IDInInfoSource>{c('ASPECT', 'PayItem_Item')}\n"
    s += info_source(IS_SPECBOOK[1], IS_SPECBOOK[2], L)
    s += ref("EffectiveStatusType", RD["status.active"], "Active", L)
    s += property_set(
        "master/" + k, "SpecBook Reference Item",
        RD["pstype.payitem"], "DOT Pay Item",
        U("propertysetdefinition/DOT_PayItem1.ReferenceItem"),
        "DOT_PayItem1.ReferenceItem",
        pi["master"], pi["master_groups"], L)
    s += f"{ind(L)}<ShortName>{escape(pi['item_no'])}</ShortName>{c('ASPECT', 'PayItem_Item')}\n"
    s += f"{ind(L)}<FullName>{escape(pi['name'])}</FullName>{c('ASPECT', 'PayItem_RefitemNm')}\n"
    s += (f"{ind(L)}<Description>{escape(pi['description'])}</Description>"
          f"{c('ASPECT', 'PayItem_Description')}\n")
    s += ref("Type", pi["lrt"][0], pi["lrt"][1], L)
    s += f"{ind(level)}</MaterialMasterItem>\n"
    return s


def material_item(pi, level):
    """MaterialItem: Entity(...), ShortName/FullName/Description, Type, then the
       required choice -> MaterialMasterItem"""
    k = pi["key"]
    s = (f"{ind(level)}<MaterialItem>"
         f"{c('SCOPE', 'the spec book item as used on THIS contract', 'grain = item x contract')}\n")
    L = level + 1
    s += (f"{ind(L)}<UUID>{U('materialitem/' + k)}</UUID>"
          f"{c('MINTED', 'uuid5(contractId :: PayItem_Item)', 'contractId is a GAP - see Asset.Project_ID')}\n")
    s += (f"{ind(L)}<IDInInfoSource>{escape('SP2758-92|' + pi['item_no'])}</IDInInfoSource>"
          f"{c('GAP', 'contract number + PayItem_Item', 'contract number has NO source column in the query')}\n")
    s += info_source(IS_IMODEL[1], IS_IMODEL[2], L)
    s += ref("EffectiveStatusType", RD["status.active"], "Active", L)
    s += f"{ind(L)}<ShortName>{escape(pi['item_no'] + ' (SP 2758-92)')}</ShortName>\n"
    s += f"{ind(L)}<FullName>{escape(pi['name'] + ' - contract item, SP 2758-92')}</FullName>\n"
    s += ref("Type", pi["lrt"][0], pi["lrt"][1], L,
             extra=f"{ind(L+1)}<!-- [LOOKUP] LogisticResourceType : NO source column. Derive from "
                   f"PayItem_Class / spec division, or publish a crosswalk. Material vs Labour vs "
                   f"Tool vs Utility vs Document. -->\n")
    s += material_master_item(pi, L)
    s += f"{ind(level)}</MaterialItem>\n"
    return s


def material_item_on_segment(pi, level):
    """MaterialItemOnSegment: Entity(UUID, IDInInfoSource, InfoSource, EffectiveStatusType,
       PropertySetForEntity), MaterialItem.  Segment back-reference omitted - implied by
       containment inside Segment."""
    k = pi["key"]
    s = (f"{ind(level)}<MaterialItemOnSegment>"
         f"{c('SCOPE', '1:1 with ONE aspect instance', 'AspectId + AspectClass identify the source row')}\n")
    L = level + 1
    s += (f"{ind(L)}<UUID>{U('materialitemonsegment/' + k)}</UUID>"
          f"{c('MINTED', 'uuid5(FederationGuid :: PayItem_Item :: componentCode)', 'componentCode derived from AspectClass - UNRESOLVED, see 3.1.4')}\n")
    s += (f"{ind(L)}<IDInInfoSource>"
          f"{escape('bc7a1f2e-3d44-4a91-9f0e-11d2c6a4b8e3::0x30000000a17::DOT_PayItem1ElementAspect')}"
          f"</IDInInfoSource>"
          f"{c('IMODEL+ASPECT', 'iModel FederationGuid :: AspectId :: AspectClass', 'traceability only - aspects have no FederationGuid and may be renumbered')}\n")
    s += info_source(IS_IMODEL[1], IS_IMODEL[2], L)
    s += ref("EffectiveStatusType", RD["status.active"], "Active", L)
    s += property_set(
        "occurrence/" + k, "DOT Pay Item Assignment",
        RD["pstype.payitem"], "DOT Pay Item",
        U("propertysetdefinition/DOT_PayItem1.Assignment"),
        "DOT_PayItem1.Assignment",
        [],
        [("Quantity", 1, pi["quantity"]),
         ("Cost", 2, pi["cost"]),
         ("Time", 3, pi["time"]),
         ("Funding", 4, pi["funding"]),
         ("ConstructionPhase", 5, pi["phase"]),
         ("PlanPrep", 6, pi["planprep"]),
         ("Tabulation", 7, pi["tabulation"]),
         ("Asset", 8, pi["asset"]),
         ("QAQC", 9, pi["qaqc"]),
         ("Remarks", 10, pi["remarks"]),
         ("SourceAuthoring", 11, pi["authoring"])],
        L)
    s += material_item(pi, L)
    s += f"{ind(level)}</MaterialItemOnSegment>\n"
    return s


def build():
    seg_uuid = U("segment/BR27043-DECK")
    site_uuid = U("site/MnDOT-District1")
    out = []
    out.append('<?xml version="1.0" encoding="UTF-8"?>\n')
    out.append(
        '<!--\n'
        '  Example: DOT construction PayItems carried on a Segment in a CCOM 4.1 SyncSegments BOD.\n'
        '  Source: Bentley iModel ElementAspect DOT_PayItem1ElementAspect.\n'
        '  Model:  Segment -> MaterialItemOnSegment (occurrence) -> MaterialItem -> MaterialMasterItem\n'
        '          (SpecBook reference item).  Occurrence-specific quantity, cost, funding, time,\n'
        '          phase, plan-prep, tabulation and QA/QC data are carried as a CCOM PropertySet on\n'
        '          the MaterialItemOnSegment association.\n'
        '  Every element below is declared in CCOM.xsd 4.1.0 or OAGIS Platform 1.2.1 Meta.xsd.\n'
        '\n'
        '  ============================ PROVENANCE ANNOTATIONS ============================\n'
        '  Every value below carries an inline comment naming where it comes from.\n'
        '\n'
        '    [ASPECT]   column of the DOT_PayItem1ElementAspect ECSQL projection (alias given)\n'
        '    [ELEMENT]  bis.Element / GeometricElement3d : requires a JOIN the query lacks\n'
        '    [IMODEL]   iModel / iTwin metadata (FederationGuid, changeset, contract props)\n'
        '    [ECSCHEMA] ECSchema metadata : db.getSchemaProps("DOT"); KindOfQuantity units\n'
        '    [ECSTRUCT] structural echo of a source EC struct\n'
        '    [REFDATA]  MIMOSA "CCOM Reference Data" UUID : fixed, ships with the standard\n'
        '    [LOOKUP]   needs an MnDOT-published crosswalk table\n'
        '    [MINTED]   deterministic uuid5 computed by the extractor\n'
        '    [CONFIG]   deployment / participant configuration\n'
        '    [RUNTIME]  generated at send time\n'
        '    [SCOPE]    note on what the containing node asserts\n'
        '    [GAP]      NOT AVAILABLE from the query or any identified source : MUST RESOLVE\n'
        '\n'
        '  Search this file for "[GAP]" to find every unsourced value. There are 6.\n'
        '  Search for "ECSCHEMA CHECK" for the unit-conversion traps.\n'
        '  ===============================================================================\n'
        '-->\n')
    out.append(
        '<SyncSegments\n'
        '  xmlns="http://www.mimosa.org/ccom4"\n'
        '  xmlns:oa="http://www.openapplications.org/oagis/9"\n'
        '  xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"\n'
        '  xsi:schemaLocation="http://www.mimosa.org/ccom4 '
        '../XSD/BOD/Messages/Configuration/SyncSegments.xsd"\n'
        '  releaseID="4.1.0" versionID="1.0" systemEnvironmentCode="Test" languageCode="en-US">\n')

    # ApplicationArea
    out.append(f'{ind(1)}<oa:ApplicationArea>\n')
    out.append(f'{ind(2)}<oa:Sender>\n')
    out.append(f'{ind(3)}<oa:LogicalID>{U("participant/ENGEngine")}</oa:LogicalID>'
               f'{c("CONFIG", "ENG participant ID", "from the OpenO&M Service Directory")}\n')
    out.append(f'{ind(3)}<oa:ComponentID>ENG Engine (iTwin/iModel)</oa:ComponentID>\n')
    out.append(f'{ind(3)}<oa:ConfirmationCode>Always</oa:ConfirmationCode>\n')
    out.append(f'{ind(2)}</oa:Sender>\n')
    out.append(f'{ind(2)}<oa:Receiver>\n')
    out.append(f'{ind(3)}<oa:LogicalID>{U("participant/ALIMEngine")}</oa:LogicalID>'
               f'{c("CONFIG", "REG-LOCATION participant ID", "from the OpenO&M Service Directory")}\n')
    out.append(f'{ind(3)}<oa:ID>REG-LOCATION</oa:ID>\n')
    out.append(f'{ind(2)}</oa:Receiver>\n')
    out.append(f'{ind(2)}<oa:CreationDateTime>2026-08-25T14:32:07Z</oa:CreationDateTime>'
               f'{c("RUNTIME", "UTC clock at publish")}\n')
    out.append(f'{ind(2)}<oa:BODID>{U("bodid/example-payitems-001")}</oa:BODID>'
               f'{c("RUNTIME", "new UUID per message")}\n')
    out.append(f'{ind(1)}</oa:ApplicationArea>\n')

    # DataArea
    out.append(f'{ind(1)}<DataArea>\n')
    out.append(f'{ind(2)}<oa:Sync/>\n')
    out.append(f'{ind(2)}<Segments>\n')

    # RegistrationSite
    out.append(f'{ind(3)}<RegistrationSite>'
               f'{c("CONFIG", "owning district / registration authority", "not derivable from the iModel")}\n')
    out.append(f'{ind(4)}<UUID>{site_uuid}</UUID>\n')
    out.append(f'{ind(4)}<ShortName>MnDOT-D1</ShortName>\n')
    out.append(f'{ind(4)}<FullName>Minnesota Department of Transportation, District 1</FullName>\n')
    out.append(f'{ind(3)}</RegistrationSite>\n')

    # Segment
    out.append(f'{ind(3)}<Segment>\n')
    L = 4
    out.append(f'{ind(L)}<UUID>{seg_uuid}</UUID>'
               f'{c("ELEMENT", "bis.Element.FederationGuid", "THE CIRID. Requires JOIN bis.Element ON Element.Id = ElementId -- NOT in the query")}\n')
    out.append(f'{ind(L)}<IDInInfoSource>'
               f'bc7a1f2e-3d44-4a91-9f0e-11d2c6a4b8e3::0x20000000c41::BR27043-DECK'
               f'</IDInInfoSource>'
               f'{c("IMODEL+ASPECT", "iModel FederationGuid :: ElementId :: CodeValue", "ElementId IS in the query; the other two are not")}\n')
    out.append(info_source(IS_IMODEL[1], IS_IMODEL[2], L))
    out.append(ref("EffectiveStatusType", RD["status.active"], "Active", L))
    out.append(f'{ind(L)}<ShortName>BR27043-DECK</ShortName>'
               f'{c("ELEMENT", "bis.Element.CodeValue", "requires the JOIN")}\n')
    out.append(f'{ind(L)}<FullName>Bridge 27043 - Superstructure Deck</FullName>'
               f'{c("ELEMENT", "bis.Element.UserLabel", "requires the JOIN")}\n')
    out.append(f'{ind(L)}<Description>As-designed deck of Bridge 27043 carrying TH 61 over '
               f'Silver Creek. Issued for Construction, SP 2758-92.</Description>\n')
    out.append(ref("Type", RD["segtype.bridge"], "Bridge", L,
                   extra=f'{ind(L+1)}<!-- [LOOKUP] SegmentType : NO source column. Derive from the '
                         f'element ECClass or SpatialCategory via an MnDOT crosswalk. -->\n'))
    out.append(ref("RegistrationSite", site_uuid, "MnDOT-D1", L))
    out.append(material_item_on_segment(PAYITEM_1, L))
    out.append(material_item_on_segment(PAYITEM_2, L))
    out.append(f'{ind(3)}</Segment>\n')

    out.append(f'{ind(2)}</Segments>\n')
    out.append(f'{ind(1)}</DataArea>\n')
    out.append('</SyncSegments>\n')
    return "".join(out)


if __name__ == "__main__":
    import sys
    open(sys.argv[1], "w", encoding="utf-8").write(build())
    print("written", sys.argv[1])