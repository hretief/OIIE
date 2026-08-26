# The O&M Registries: What They Are and Why Skipping Them Costs More

**REG-LOCATION · REG-ASSET · REG-PRODUCT · (and the REG-MATERIAL gap)**

Audience: product managers and developers who suspect these registries are architectural
overhead that engineering processes can absorb.
Basis: OIIE Use Case Package (MIMOSA, v1.4 catalogue, 2021), the OIIE Scenario set, MIMOSA
CCOM 4.1, ws-CIR 1.0.

---

## The argument in one paragraph

The engineering model answers *what was designed*. The registries answer *what exists now, what
is it called in each system, and what is installed in it today*. Those two answers are identical
for about a week — from Issued for Construction until the first field change — and then they
diverge permanently. Every hour of the next fifty years of operations runs on the second answer.
If you do not build somewhere to hold it, each downstream system builds its own private version,
they disagree within a year, and reconciling them becomes a permanent staffing line rather than a
one-time build.

This document states what each registry is for, what it can actually do, and what specifically
breaks when it is left out. It also says plainly which parts you can defer, because a document
that claims everything is essential deserves the skepticism it gets.

---

## First, what a "registry" is not

Three misconceptions do most of the damage in this conversation.

**It is not another CAD system.** A registry holds no geometry. REG-LOCATION holds the tag, its
classification, its place in the breakdown structure, its connections, and the property sets that
say what is *required* at that location. The iModel remains the design authority. The registry is
the operational authority.

**It is not the EAM/CMMS.** This is the objection worth taking most seriously, and it is answered
in full below. Short version: the maintenance system is *provisioned from* the registry, and it is
one of several consumers. The moment there are two consumers, the registry is the thing that
keeps them agreeing.

**It is not the CIR.** Registry and CIR do different jobs and neither works alone. The registry
holds *content* — the location and its properties. The CIR holds *identity equivalence* — the
fact that `BR27043-DECK` in engineering, `LOC-000001` in the registry, and `234441` in the
maintenance system are one physical thing. A registry without a CIR is an island. A CIR without a
registry is a table of identifiers pointing at nothing.

**It is a role, not a product.** OIIE is explicit that an SDAIR may act as REG-LOCATION, and
several scenarios note the same for the other registry components. Substituting an existing
product — AssetWise ALIM as REG-LOCATION, for instance — is conformant. The question is never
"do we buy a registry"; it is "which system plays this role, and does it expose the interfaces
the scenarios require."

---

## The four registries

OIIE's glossary defines **REG** as the O&M structure (breakdown structure and functional
locations), asset, model and index registry, with a standardized data dictionary and taxonomy.
Individual components are named `REG-<component>`.

| Registry | Answers the question | Timescale of the data |
|---|---|---|
| **REG-LOCATION** | *Where can work happen, and what belongs there?* | Decades — outlives every asset installed in it |
| **REG-ASSET** | *What physical thing is installed there right now?* | Years — changes with every replacement |
| **REG-PRODUCT** | *What kind of thing is it, and what can it do?* | Follows the manufacturer, not the project |
| **REG-MATERIAL** | *What did we agree to build, and at what cost?* | **Not defined in OIIE — see §4** |

---

### 1. REG-LOCATION — the functional location registry

#### Purpose

The authoritative operational record of the functional location topology. Use Case 1 states that
it is the logical first step in the entire framework, that all other use cases are predicated on
it, and that it has no preconditions itself. Nothing else in OIIE works until this exists.

#### What it holds

Per Scenario 1, considerably more than a tag list:

- Functional locations with identification properties (ID, tag, name) and categorical properties
  bound to reference data
- **Topology** — interconnections, flow, and intended physical sequence between segments
- **Breakdown structures** — hierarchical decomposition. Scenario 1 draws the distinction
  precisely: topology shows *interconnection* between functional segments; breakdown structure
  shows *composition* of a segment
- **Datasheet properties** — the functional *requirements* of whatever gets installed at that
  location, as `PropertySetDefinition` / `PropertyDefinition`
- **Diagram traceability** — the link back to the originating drawing, including position, scale,
  title block and canvas context

#### Capabilities

| Capability | Scenario | Direction |
|---|---|---|
| Receive and register network/segment/tag data | **S1** | ENG → REG-LOCATION |
| Provision downstream O&M systems | **S2** | REG-LOCATION → O&M |
| Receive plant/process change advisories | **S27** | ENG → REG-LOCATION |
| Publish change advisories onward | **S28** | REG-LOCATION → O&M |
| Serve queries (`GetSegments`, `GetBreakdownStructures`) | — | on request |
| Push RFIs for models matching known data | **S37** | REG → OEM PRODUCT |

It is a consumer, a publisher, and a requester — not a passive store.

#### What breaks without it

A light pole assembly is one element in the design model, with one geometry. Its luminaire is
replaced on an eight-year cycle; its foundation is not replaced at all. Design had no reason to
distinguish them — there is no geometric difference.

Without REG-LOCATION, the maintenance system has nowhere to book "replaced the luminaire." It can
only record "did something to the pole." Ten years later nobody can answer how many luminaires
failed early, which manufacturer's units failed most, or whether the district is over-spending on
a known-bad batch. The data was never wrong; it was never *separable*.

The registry exists precisely because the operational decomposition is not the design
decomposition — and it is the only place that difference can legitimately be recorded.

---

### 2. REG-ASSET — the serialized asset registry

#### Purpose

The record of the physical, serialized things installed in the functional locations, and the
history of those installations over time.

#### What it holds

Per Scenario 4: identification properties, categorical properties, datasheet properties
describing the equipment's *capabilities* (as opposed to REG-LOCATION's *requirements*), metadata,
the link to the asset's product model, and — critically — the **installation relationship**
between an asset and the segment it sits on. Scenario 4 notes that an asset can be installed and
removed from various segments, establishing lifecycle tracking for every subsequent use case.

Installations and removals are typed events with fixed reference UUIDs, so they are machine-
interpretable rather than free text:

- `Installation of Asset on Segment` — `ecc99353-412b-4995-bd71-1cbc6fc16c7c`
- `Removal of Asset on Segment` — `3a45e126-b234-42a0-b3b1-07c29522d02d`

#### Capabilities

| Capability | Scenario | Direction |
|---|---|---|
| Receive as-built asset and installation data | **S4** | CONSTRUCT → REG-ASSET |
| Provision O&M systems with asset data | **S5** | REG-ASSET → O&M |
| Serve asset removal/installation history | **S33** | REG-ASSET → O&M |
| Receive as-maintained updates from the field | **S6** | O&M → O&M |
| Receive package installation events | **S40** | CONSTRUCT → O&M |

#### What breaks without it

The distinction REG-ASSET protects is between *the position* and *the thing in the position*. Both
are real, both need identity, and they change at different rates.

Skip it and the serial number ends up in a free-text field on the maintenance work order —
sometimes. Then: warranty claims cannot be substantiated, because you cannot prove which unit was
in place on which date. A manufacturer recall cannot be scoped, because you cannot list the
affected serials or where they went. Reliability analysis is impossible, because failures cannot
be attributed to a unit's own service life as opposed to the position's age. And a unit swapped
between two locations during maintenance silently corrupts both histories.

None of these show up in year one. All of them are unrecoverable by year five, because the
evidence was never captured.

---

### 3. REG-PRODUCT — the product model registry

#### Purpose

The internal, owner-controlled copy of manufacturer product data. Scenario 7 is careful about
the framing: the OEM is the authoritative source, but the data is **replicated into an internal
system for management within the execution environment**, so that the owner can attach their own
observations and supplementary data to the OEM's original.

That last clause is the whole point. It is not a cache. It is where your operational experience of
a product accumulates alongside the manufacturer's specification.

#### What it holds

Product models, and for each model the datasheets — OEM-supplied and internally authored.
Scenario 7 recommends Industry Standard Datasheet Definitions (ISDDs): standardized property sets
for equipment classes based on industry reference datasheets, represented in CCOM. Standardizing
here is what makes automated requirement-to-model matching possible at all.

#### Capabilities

| Capability | Scenario | Direction |
|---|---|---|
| Pull model data from OEM PDM | **S7** | OEM PDM → REG-PRODUCT |
| Serve model data to O&M systems | **S8** | REG-PRODUCT → O&M |
| Receive OEM engineering change advisories | **S25** | OEM PDM → REG-PRODUCT |
| Distribute change advisories internally | **S26** | REG-PRODUCT → O&M |
| Push RFIs for models matching known asset data | **S37** | REG → OEM PRODUCT |

#### What breaks without it

Two failures, one slow and one sharp.

The slow one is like-kind replacement. Fifteen years after construction a component fails and the
exact model is discontinued. Finding a valid substitute requires knowing what the original
actually had to do — its functional requirements — and what the candidate can do. If neither was
ever recorded in a comparable form, procurement guesses, and the guess is only tested in service.

The sharp one is the change advisory. When an OEM issues a safety or engineering change notice,
somebody has to answer "do we have any of those, and where." With REG-PRODUCT plus REG-ASSET plus
REG-LOCATION, that is a query. Without them, it is a fire drill with a spreadsheet, and the answer
is "we think so."

**Honest scoping for transportation:** REG-PRODUCT's value density varies enormously by asset
class. For structural concrete, aggregate and earthwork it is close to worthless — there is no
meaningful product model. For signals, lighting, ITS cabinets, cameras, bridge bearings, joints
and coatings it is where the money is. Scope it to the equipment classes that have manufacturers,
model numbers, warranties and failure modes. Do not attempt a universal product library; that
project fails everywhere it is attempted.

---

### 4. REG-MATERIAL — the gap you should know about

**REG-MATERIAL is not a defined component of OIIE.** State this plainly rather than let a reviewer
discover it. The glossary defines REG as *structure, asset, model and index* registries. The
components named across the scenario set are REG-LOCATION, REG-ASSET and REG-PRODUCT (the last
occasionally appearing as REG-MODEL). `MATERIALS` exists in the OIIE landscape, but as the
**Material/Procurement Management System** — an operational transaction system, not a registry.

What does exist:

- **In CCOM**, `MaterialItem`, `MaterialMasterItem` and `MaterialItemOnSegment` are first-class
  entities, with `MaterialMasterItem` serving as the catalog definition and `MaterialItemOnSegment`
  as the assignment to a functional location.
- **In the CIR**, Scenario 32 lists *Material Item* among the suggested categories, so material
  identity is expected to be cross-registered like any other object.
- **In the BOD register**, there is no `SyncMaterialMasterItems` or equivalent. There is no
  commercial, cost, estimate or bill-of-quantities noun anywhere in the OIIE BOD catalogue.

So the entities are modelled, the identity mechanism is anticipated, and the transport is absent.

#### Why this matters for a DOT specifically

OIIE grew out of process industries, where the capital project ends and materials become
procurement transactions. Transportation is different: the **pay item is the contractual and
semantic backbone of the entire project**, it is authored during design, it persists through
construction, and it is the unit in which the work was specified, measured and paid for.

That data currently has no registry home in OIIE. In practice it rides into REG-LOCATION as
property sets attached to functional locations — which is defensible, since pay items genuinely
are attributes of the location and arrive atomically with it. But a strictly conformant receiver
is entitled to record the location and ignore the property sets entirely; the *Publish Functional
Location Data* event explicitly states that receivers are not required to process anything and
that no response is expected.

**The practical consequence for the skeptics in the room:** this is the one area where the
framework will not carry you. If MnDOT needs pay item data to be reliably actionable downstream,
that requires a bilateral agreement and published `PropertySetDefinition`s — work the standard
does not specify and no vendor will supply by default. It is a genuine gap, worth raising with
MIMOSA, and worth planning around rather than assuming.

---

## The five objections, answered

### "The maintenance system already does all of this."

It does — for itself. The question is what happens when there is a second consumer.

A CMMS holds functional locations, assets and models in its own schema, with its own identifiers,
shaped by its own workflows. That is correct and useful. It becomes a problem when the condition
monitoring system, the bridge inspection system and the pavement management system each do the
same thing, independently, from different source extracts taken at different times.

Within eighteen months they disagree. Not dramatically — a location renamed here, an asset
replaced and recorded in one system but not another. Reconciling them is then a permanent
manual process, because there is no longer any system with standing to say which version is
correct.

The registry's job is to be that system. It is not competing with the CMMS; it is what keeps the
CMMS agreeing with everything else.

### "Point-to-point integration is cheaper, and ours already works."

For two systems, this is true, and you should not be talked out of it.

The arithmetic changes fast. Point-to-point requires *n(n−1)* directed integrations; a registry
plus a bus requires *n*. At three systems that is 6 versus 3 — not obviously worth it. At ten
systems, a realistic count for a state DOT once you include design, asset management,
maintenance, inspection, pavement, bridge, signals, materials testing, GIS and finance, it is
**90 versus 10**.

The cost that actually bites is not building them; it is that every schema change in any one
system requires touching every integration that reads it, and there is no test that tells you
which ones you missed.

The developer-facing version of the argument: **without a registry, "which system is
authoritative" is decided by whoever wrote the most recent integration.** With one, it is a
declared, testable property of the architecture.

### "We'll add it later, when someone asks for it."

Some of it, yes. Registries can be populated incrementally, and Use Case 1 explicitly supports
incremental handover as sections are approved and signed off.

But three things cannot be backfilled, because they are *events*, not *state*:

1. **Installation history.** If you did not record that asset 4471 went into LOC-000123 in March,
   there is no later query that recovers it. The asset is now in a different place or a landfill.
2. **The as-designed baseline.** Once field changes accumulate, "what was originally specified"
   is only recoverable by reading drawings by hand.
3. **Identity correspondence at handover.** The moment when engineering, construction and
   operations all have the object in front of them at once is the cheapest moment to record that
   they are the same thing. Every later attempt is a matching exercise against degraded data.

Deferring the registry does not defer the cost. It converts a build cost into a data-archaeology
cost, at roughly ten times the price and with a worse result.

### "OIIE is process-industry stuff. We build roads."

Partly fair. The scenario documents talk about P&IDs, PFDs, refineries and light ends areas, and
the vocabulary does not always land in a transportation context.

But the structures generalise cleanly. A functional location is a functional location whether it
is a pump position or a luminaire position. A breakdown structure decomposes a bridge as readily
as a distillation column. A serialized asset with an installation history is the same object
whether it is a compressor or a traffic signal controller. CCOM's `Segment` carries no
process-industry assumptions.

Where the fit genuinely weakens is the commercial layer — §4 above — and that weakness is worth
stating honestly rather than papering over.

### "This is architecture for its own sake."

The test to apply is simple, and it is fair to apply it hard: **name the question you cannot
answer today, and check whether the registry answers it.**

- Which locations have equipment from the manufacturer that just issued a recall?
- Which luminaires installed in 2024 have already been replaced?
- What did we specify at this location, and does what is installed still meet it?
- When this component fails in 2041, what will a valid replacement need to do?
- What did we pay for the work at this location, and how does that compare to five similar ones?

If your organisation can already answer all five in under an hour, the skeptics are right and you
should not build this. Most cannot answer any of them without a multi-week manual effort.

---

## What you can legitimately defer

Credibility requires conceding the parts that are genuinely optional.

| Defer | Why it is safe |
|---|---|
| **REG-PRODUCT for non-equipment asset classes** | No manufacturer, no model, no warranty. Concrete and aggregate gain nothing |
| **Full ISDD adoption** | Scenario 8 explicitly does not require standardized datasheets; internal formats are acceptable, with mapping deferred |
| **Diagram traceability (position, scale, title block)** | Valuable for HMI applications, marginal for transportation asset management |
| **Topology/interconnection modelling** | Matters where flow matters. For discrete roadside assets, breakdown structure alone carries most of the value |
| **The Transform Engine** | Only needed if the source publishes ISO 15926. Publishing CCOM natively removes it entirely |
| **Notify Listener service** | Optional throughout the scenarios; polling is conformant |

## What is genuinely load-bearing

Three things, in order. Everything else can follow.

1. **REG-LOCATION with a stable, governed identifier per functional location.** Nothing else in
   the framework functions without it, and it is the hardest thing to retrofit.
2. **Identity correspondence recorded at handover, in the CIR.** Cheap at the moment of handover,
   expensive forever after.
3. **REG-ASSET installation and removal events, from first installation.** Pure event data,
   unrecoverable if missed, and the foundation of every reliability question anyone will ask
   later.

A first release that does only these three, with REG-PRODUCT scoped to a single equipment class
as a proof, is a defensible and genuinely useful increment. A first release that does none of them
because "engineering already has the data" produces a system that works until the first field
change and degrades from there.

---

## Appendix — scenario map

| # | Flow | From | To |
|---|---|---|---|
| 1 | Publish As-Designed/As-Built Network/Segment/Tag | ENG | REG-LOCATION |
| 2 | Publish As-Designed/As-Built Network/Segment/Tag | REG-LOCATION | O&M |
| 3 | Publish As-Maintained Network/Segment/Tag | O&M | O&M |
| 4 | Publish As-Built Engineering Asset | CONSTRUCT | REG-ASSET |
| 5 | Publish As-Built Engineering Asset | REG-ASSET | O&M |
| 6 | Publish As-Maintained Engineering Asset | O&M | O&M |
| 7 | Pull OEM Model | MATERIALS, OEM PDM | REG-PRODUCT |
| 8 | Pull OEM Model | REG-PRODUCT | O&M |
| 9 | Publish OEM Model | O&M | O&M |
| 25 | Publish Product/Part Engineering Change Advisories | OEM PDM | REG-PRODUCT |
| 26 | Publish Product/Part Engineering Change Advisories | REG-PRODUCT | O&M |
| 27 | Publish Plant/Process Change Advisories | ENG | REG-LOCATION |
| 28 | Publish Plant/Process Change Advisories | REG-LOCATION | O&M |
| 33 | Pull Asset Removal/Installation | REG-ASSET | O&M |
| 36 | Push Request for Models Meeting Functional Requirements | MATERIALS/PROCURE | OEM PRODUCT |
| 37 | Push Request for Models Matching Known Data | REG (ASSET, LOCATION, PRODUCT) | OEM PRODUCT |
| 38 | Pull Partial Product Data | OEM PRODUCT | MATERIALS/PROCURE |
| 40 | Publish Asset/Package Installation Events | CONSTRUCT | CONSTRUCT, O&M |

Every registry appears on both sides of the arrow. That is the observation to leave with: these
are not archives that data goes into. They are participants that data flows through, and removing
one does not remove the flow — it just removes the place where the flow could have been made
consistent.
