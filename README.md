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

## Known gaps

- The ISBM client is still implemented twice, in `Oiie.Isbm.Client` and in
  `CirProvider/Infrastructure/Isbm`. Consolidating them is the next task.
- There is no CI yet. Path-filtered build and deploy pipelines are still to be
  written.

## History

This repository supersedes three earlier ones, now archived:
`OIIE-Sandbox`, `CirProvider`, and `ISBM`. History was not carried across.
