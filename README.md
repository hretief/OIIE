# OIIE

Monorepo for the OpenO&M interoperability stack: three independently deployable
providers over a shared set of contracts and schemas.

| Deliverable | Project | What it is |
|---|---|---|
| **ISBM** | `ISBMProvider` | ws-ISBM Service Provider — channels, sessions, pub/sub and request/response over Service Bus |
| **CIR** | `CirProvider` | ws-CIR registry — cross-system identity registration and equivalence resolution |
| **Sandbox** | `SimHost` | Multi-participant simulator that exercises both providers end to end |

The flow under test is **Sandbox → CIR → ISBM**. A single solution means that
whole path is visible at once; independent deployment is preserved through
per-deliverable `infra/` and `deploy/`.

## Layout

```
OIIE/
  OpenOM.slnx                 everything

  Oiie.Isbm.Abstractions/     channel, session and message contracts
  Oiie.Isbm.Client/           the ISBM REST client
  Oiie.Ccom/                  CCOM domain model and BOD validation
  ISBMProvider/               ── deliverable 1
  CirProvider/                ── deliverable 2
  SimHost/                    ── deliverable 3
    PersonalityPacks/         participant config and fixtures, copied to output
    Components/               Blazor UI for running and inspecting scenarios

  schemas/{ccom,cir,oagis}    single source of truth for XSDs
  infra/{isbm,cir,sandbox}    Bicep, per deliverable
  deploy/{isbm,cir,sandbox}   deploy scripts, per deliverable
  testing/                    PowerShell suites + bruno collections
  tests/                      unit test projects
  docs/                       specification, runbook, decision records
```

Flat, with no `src/` level — the convention the source repositories used.
Grouping is handled by solution folders.

## What the sandbox shows

SimHost runs a scenario and then lets you check the claim. `/runs/{id}` opens
four tabs: identity lineage, assertion results, message flow, and the rows each
participant actually persisted. Every message in the flow links to a page
showing the source record, the BOD XML, the resulting records and the provenance
trail side by side.

The behaviour worth watching is what happens to a property value across three
systems holding different reference data. ENG classifies a tag against
`rdl:TemperatureIndicatingController` and authors a control action.
REG-LOCATION lacks that leaf class, binds at `rdl:Instrument`, and records the
classification as degraded — but it holds the property *definition*, so the value
arrives mapped. MMS holds no reference data at all, so the same value is retained
unmapped: stored, flagged, not discarded.

That last part is the claim. A receiver that drops what it cannot classify makes
federation all-or-nothing, and it is not: MMS keeps the value against the day
someone gives it a definition. Holding a definition and holding the class that
sanctions it are separate questions.

## Documentation

| Where | What |
|---|---|
| `SimHost/README.md` | running the sandbox, the UI, reset semantics, scenarios |
| `SimHost/PersonalityPacks/README.md` | how to add a new personality |
| `docs/sequence-end-to-end.puml` | the whole path, current state |
| `docs/sequence-uc01-handover.puml` | ENG → REG-LOCATION → MMS handover |
| `docs/sequence-uc02-greenfield.puml` | allocator behaviour on an empty store |
| `docs/sequence-uc05-asset-install.puml` | MMS → OM-RELIABILITY install/removal events (Scenario 11) |
| `docs/sequence-uc10-as-built-handover.puml` | as-built asset handover to REG-ASSET (Scenarios 4 and 5) |
| `docs/decision-records/` | why things are the way they are |
| `schemas/README.md` | the XSD packages and their provenance |

## Why one repo

The three components are developed together and only make sense together: a CIR
request is a BOD on an ISBM channel, and the Sandbox is the only thing that
proves the round trip. Splitting them across repositories meant the ISBM client
was implemented twice and drifted, and the Sandbox solution referenced the other
two through relative paths that resolved on exactly one machine.

One solution does **not** mean one deployment. Each deliverable keeps its own
infrastructure and deploy script.

## Continuous integration

Two workflows run on push and pull request against `main`:

- `.github/workflows/build.yml` restores, builds `OpenOM.slnx` in Release, and
  runs the unit tests, publishing a `.trx` artifact.
- `.github/workflows/infra.yml` compiles every `infra/**/main.bicep`. It is path
  filtered, so it only runs when infrastructure changes.

Both are build-and-verify only. Neither deploys, and neither needs Azure
credentials. The end-to-end suite under `testing/` is **not** in CI: it requires
a running SimHost plus the live Azure ISBM and CIR apps, so it stays a manual
step:

```pwsh
cd SimHost;  dotnet run --launch-profile SimHost   # leave running
cd testing;  pwsh -NoProfile -File .\test-sandbox.ps1
```

Scenarios can also be started and inspected from the browser at `/runs`, which
is usually the faster way to see *why* a step failed.

The launch profile is `SimHost`. There is no `https` profile, and passing a name
that does not exist silently skips `ASPNETCORE_ENVIRONMENT=Development`, so
`appsettings.Development.json` is never loaded and every database connection
fails with `Configuration 'Sandbox:Environment' is not set.`

## Decisions worth reading first

`docs/decision-records/2026-08-managed-identity-and-consolidation.md` covers how
Service Bus authentication works (managed identity, no keys anywhere — including
what a developer needs granted to run locally) and why `CirProvider` shares only
the ISBM contract types rather than the client interface.

## Known gaps

- The ISBM *contract* types (`IsbmMessage`, `IsbmSessionKind`, `IsbmException`)
  are now shared from `Oiie.Isbm.Client`, but the REST *implementation* is still
  written twice: `Oiie.Isbm.Client/IsbmRestClient.cs` and
  `CirProvider/Infrastructure/Isbm/IsbmRestClient.cs`. Sharing the implementation
  as well means reconciling the two interfaces — the shared `IIsbmClient` covers
  the whole Messaging Service Model, while ws-CIR needs eight operations — so it
  was left as a separate change.
- Deploy pipelines are still to be written. CI currently builds and tests only;
  deployment is still driven by the scripts under `deploy/`.

## History

This repository supersedes three earlier ones, now archived:
`OIIE-Sandbox`, `CirProvider`, and `ISBM`. History was not carried across.
