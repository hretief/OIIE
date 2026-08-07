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

  schemas/{ccom,cir,oagis}    single source of truth for XSDs
  infra/{isbm,cir,sandbox}    Bicep, per deliverable
  deploy/{isbm,cir,sandbox}   deploy scripts, per deliverable
  testing/                    PowerShell suites + bruno collections
  tests/                      unit test projects
  docs/                       specification, runbook, decision records
```

Flat, with no `src/` level — the convention the source repositories used.
Grouping is handled by solution folders.

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
