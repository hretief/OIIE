# Two class stores: why the ENG panel's dropdown does not read `dbo.ECClass`

Date: 2026-09
Status: describes existing behaviour, and defers a change that was considered
and not made.

This record exists because the ENG panel's `CLASS KEY` dropdown looks like it
should be reading the ENG provider's class table and is not. The obvious
"fix" — point it at `acme-db-eng-dev.dbo.ECClass` — removes behaviour the
sandbox exists to demonstrate. Written after that change was proposed, costed,
and deliberately deferred.

## The two stores

| | Sandbox participant DB | ENG provider DB |
| --- | --- | --- |
| Where | `acme-db-sandbox-dev`, schema per participant | `acme-db-eng-dev` |
| Table | `ClassDefinition` / `ClassProperty` / `PropertyDefinition` | `dbo.ECClass` / `dbo.ECClassBase` |
| Keys | `rdl:Equipment` | `ENG.Equipment` |
| Source | `PersonalityPacks/<id>/Fixtures/classes.yaml` | `docs/DDL/ENG_BOOTSTRAP.SQL` |
| Seeded by | `POST /admin/reset/day-zero` | manual, on database provisioning |
| Serves | `/admin/{id}/class-catalog` — the dropdown | `/api/classes` |

Both are legitimate. They answer different questions: the sandbox store is
*what a participant can bind and validate*, the ENG store is *what an ENG
element actually is*.

## Why the dropdown uses the sandbox store

`EngProvider/Infrastructure/Sql/schema.sql` says it plainly: *"ECProperty is not
modelled. Elements carry identity and classification and nothing else.
Attributes would need a property or aspect table."*

So `dbo.ECClass` offers a name, a schema and an inheritance edge. The dropdown
and everything downstream of it need three things it cannot supply:

- **`isAspect`.** The dropdown renders `classes.filter(c => !c.isAspect)`.
  ENG has `ClassModifier` (`None`/`Abstract`/`Sealed`), which is a different
  concept — it controls instantiability, not whether a class applies *alongside*
  a taxonomy. `rdl:SafetyCritical` is an aspect; nothing in `dbo.ECClass` could
  say so.
- **Requirement levels and bounds.** `ClassProperty` carries
  `Required`/`Recommended` plus min/max, which `PropertyIngestor` uses to decide
  whether a binding is complete or degraded.
- **Narrowing.** `NarrowingRules` enforces spec §6.5.4 — a subclass may tighten
  an inherited property but never widen it. `rdl:TemperatureIndicatingController`
  narrowing `RangeMaximum` to 1200 under `rdl:Instrument`'s 2000 is unrepresentable
  in ENG's schema.

## The asymmetry is the point

`ClassFixtureLoader` states the reason fixtures are files rather than rows:
*"deliberately asymmetric across participants: a library everyone held completely
would make graceful degradation untestable, and that behaviour is the argument
for governed reference data."*

REG-LOCATION holds **fewer** classes than ENG on purpose. Sourcing every
participant from ENG's table would give everyone the same vocabulary and delete
the degraded-binding and unbound-proposal paths the sandbox is built to show.

## Why "ENG as system of record" was deferred

The proposal was to make `dbo.ECClass` authoritative and seed the sandbox from
it at day zero. It does not currently work, for two reasons:

1. **ENG cannot express the model.** No property, requirement, or aspect
   concept. Seeding from it means either losing those, or keeping
   `classes.yaml` for the parts ENG lacks — which is two sources plus a merge,
   not one system of record.
2. **A key rename.** Fixtures, `ClassificationResolverTests`,
   `CcomAttributeMapper` and REG-LOCATION are all keyed `rdl:*`. Moving to
   `ENG.*` is a cross-cutting rename, not a seeding change.

The prerequisite is extending ENG's schema with property and aspect tables so it
can hold the model, and only then reversing the flow. Treated as separate work.

## Meanwhile

`ENG.Equipment` is kept in `dbo.ECClass` and in `ENG_BOOTSTRAP.SQL`. It is not
redundant with the `rdl:Equipment` fixture: elements carry classification as an
`ECClassId`, and planned element properties attach via the element's assigned
class. Both rows are needed, for different reasons.

## Which store owns the identity

The two stores hold class *definitions*; only one mints class *identity*.

**RDL mints all reference-data UUIDs.** Classes today, and property
definitions, relationship definitions and enumeration values when those are
modelled. The GUID lives in `dbo.objects.guid`, is seeded and back-filled by
`bootstrap.sql`, and travels on `ShowTaxonomySet` via `TaxonomySetBods`. No
other participant mints or derives one — a derived UUID is well-formed and
silently fails to match the library.

This is the opposite of the instance-data rule. A segment's FederationGuid is
minted by ENG because ENG creates the physical thing; a class's UUID is minted
by RDL because RDL governs the vocabulary. Both are federation identities; they
differ in who has the authority to create them.

`ENG.Equipment` and `rdl:Equipment` therefore remain separate rows, as
["Meanwhile"](#meanwhile) describes. The *schemas* differ permanently; the
config files translating between them do not. ENG's `OutboundRdlClassMap` and
REG-LOCATION's `InboundClassMap` are the current, private, per-participant way
of expressing the correspondence, and both are transitional.

The intended end state is a **complete ENG→RDL mapping done out-of-band**, then
**pre-loaded into CIR**. Once those entries exist, a participant resolves
`ENG.Streetlight` to the RDL class GUID by registry lookup and matches on the
UUID, so the local code map becomes unnecessary. Until that pre-load happens the
maps stay, and `RdlTaxonomyValidator` at least makes them checkable rather than
merely asserted.

CIR's role here is narrow and worth stating plainly. For instance data CIR
asserts that independently-minted keys denote the same thing. For reference data
there is no competing identity to reconcile, so registering
`(IDInSource=ENG.Streetlight, SourceID=ENG, CIRID=<RDL class GUID>)` publishes a
**cross-reference between vocabulary terms** — it does not confer identity,
because RDL already did. That registration is nonetheless the thing that retires
code matching, so it is required work rather than an optional nicety. See
[federation-guid-guideline](../FederationId/federation-guid-guideline.md#instance-data-vs-reference-data--two-minting-regimes)
and [cir-provider](../cir-provider.md#instance-data-and-reference-data-enter-differently).

Not yet closed: `SegmentType` is still matched on the string code rather than the
UUID, so a name change in RDL breaks the binding that the GUID exists to make
stable (DR-030).

## If the dropdown is empty

Day zero has not been run on that environment. `Program.cs` refreshes
classification at startup but catches the failure and logs a warning, so an
unseeded app boots healthy and serves an empty catalog. `POST /admin/schema/seed`
reloads it without a full reset.
