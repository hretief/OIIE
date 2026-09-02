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

## If the dropdown is empty

Day zero has not been run on that environment. `Program.cs` refreshes
classification at startup but catches the failure and logs a warning, so an
unseeded app boots healthy and serves an empty catalog. `POST /admin/schema/seed`
reloads it without a full reset.
