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
  Oiie.Sandbox.Core/          ── deliverable 3: the engine, shared by both hosts
    PersonalityPacks/         participant config and fixtures, linked into each host
    Scenarios/                scenario definitions
  Oiie.Sandbox.Api/           the REST surface: /admin, /health, message pumps
  SimHost/                    Blazor UI for running and inspecting scenarios
  WorkflowOrchestration/      React UI for driving the sandbox interactively

  schemas/{ccom,cir,oagis}    single source of truth for XSDs
  infra/{isbm,cir,sandbox}    Bicep, per deliverable
  deploy/{isbm,cir,sandbox}   deploy scripts, per deliverable
  testing/                    PowerShell suites + bruno collections
  tests/                      unit test projects
  docs/                       specification, runbook, decision records
```

Flat, with no `src/` level — the convention the source repositories used.
Grouping is handled by solution folders.

The sandbox is three projects, not one. `Oiie.Sandbox.Core` holds the engine;
`Oiie.Sandbox.Api` and `SimHost` are separate hosts over it, and only the API
runs the message pumps. Two UIs are kept deliberately: SimHost drives end-to-end
automated scenario runs, WorkflowOrchestration is for interactive use. See
`docs/decision-records/2026-08-sandbox-host-split.md`.

## What the sandbox shows

SimHost runs a scenario and then lets you check the claim. `/runs/{id}` opens
four tabs: identity lineage, assertion results, message flow, and the rows each
participant actually persisted. Every message in the flow links to a page
showing the source record, the BOD XML, the resulting records and the provenance
trail side by side. The participants page at `/` goes wider than a single run:
each participant expands to show its whole schema, read as that participant's
own SQL user.

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
| `docs/sequence-sc01-sc02-handover.puml` | ENG → REG-LOCATION → MMS handover (Scenarios 1 and 2, UC01) |
| `docs/sequence-sc01-greenfield-allocation.puml` | allocator behaviour on an empty store (Scenario 1, UC02) |
| `docs/sequence-sc11-asset-install.puml` | MMS → OM-RELIABILITY install/removal events (Scenario 11, UC05) |
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
a running sandbox API plus the live Azure ISBM and CIR apps, so it stays a manual
step:

```pwsh
cd Oiie.Sandbox.Api;  dotnet run   # leave running
cd testing;           pwsh -NoProfile -File .\test-sandbox.ps1
```

The suite drives `/admin` and `/health`, which the **API** serves — not SimHost.
Point it at `https://localhost:7241` locally.

To use either UI, run the API and then:

```pwsh
cd SimHost;                 dotnet run   # Blazor, https://localhost:7180
cd WorkflowOrchestration;   npm run dev  # React, http://localhost:8443
```

Both need the API running: SimHost calls it for reset and scenario launch, and
the React app talks to nothing else. `OpenOM.slnLaunch` starts the API and
SimHost together.

Scenarios can also be started and inspected from the browser at `/runs`, which
is usually the faster way to see *why* a step failed.

`appsettings.Development.json` is not committed — copy the `.template` beside it
and set your own database. Without it every connection fails with
`Configuration 'Sandbox:Environment' is not set.`

## Decisions worth reading first

`docs/decision-records/2026-08-managed-identity-and-consolidation.md` covers how
Service Bus authentication works (managed identity, no keys anywhere — including
what a developer needs granted to run locally) and why `CirProvider` shares only
the ISBM contract types rather than the client interface.

`docs/decision-records/2026-08-sandbox-host-split.md` explains why the sandbox is
three projects and two App Services, and why the API is an App Service rather
than a Function App like the other two deliverables.

`docs/decision-records/2026-08-eng-imodel-named-versions.md` explains why
publication from ENG is all-or-nothing. `PromoteAsync` looks like an unfinished
feature and is not one; read this before "improving" it.

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
