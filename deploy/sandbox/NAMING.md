# Sandbox naming conventions

`provision.ps1` derives everything below from `environment` and `alias`, so
nothing should be typed by hand twice.

> **Not the master naming standard.** `docs/azure-resource-naming-guidance.md`
> is authoritative for Azure resource names. This file covers sandbox-specific
> detail and records where the two disagree.
>
> Unresolved conflicts with the master standard:
>
> - **Environments.** This file defines `dev`, `ci` and `demo`. The master
>   standard defines only `dev` and `prod`, and has no `prod` sandbox. A name
>   for the CI and demo environments has not been agreed.
>
> Resolved 2026-09 (DR-024): App Service names now follow the master standard —
> `acme-api-sandbox-{env}`.
>
> Resolved (DR-022): the sandbox owns no database. Participant data belongs to
> the provider apps, each of which creates and bootstraps its own schema and
> reference data, so the schema, contained-user and per-participant SQL secret
> conventions that used to live here no longer apply.

## Environments

| Environment | Value of `-Environment` |
|---|---|
| Per-developer | `dev` |
| CI | `ci` |
| Demo | `demo` |

Existing shared resources, referenced rather than created:

| Resource | Name |
|---|---|
| Resource group | `HilmarRetiefRG` |
| SQL server | `acme-sql-server.database.windows.net` |
| Key Vault | `mndot` |
| App Service plan | `acme-plan-{env}` |
| Storage account | `acmestoragedev01` |

The SQL server and Key Vault do not match `acme-*-dev`. Both are shared with
resources outside this solution, and neither can be renamed in place, so they
are deliberately out of scope. The SQL server is listed because the provider
apps live on it; the sandbox itself never connects to it.

## Key Vault secrets

| Secret | Contents |
|---|---|
| `sandbox-isbm-token-{participant}` | ISBM security token for that participant |

`{participant}` uses the hyphenated form, matching `personality.yaml`.

## Blob storage

| Item | Name |
|---|---|
| Container | `sandbox-payloads` |
| Path | `{prefix}/{participantId}/{correlationId}/{messageId}.xml` |
| Prefix — dev | `dev-{alias}` |
| Prefix — CI | `ci-{runId}` |
| Prefix — demo | `demo` |

Lifecycle rule deletes blobs after 7 days.

## ISBM channels

```
/OIIE-SANDBOX/{runId}/Enterprise/{site}/{purpose}      CI, run-scoped
/OIIE-SANDBOX/Enterprise/{site}/{purpose}              dev and demo
```

The `{runId}` segment is what stops parallel CI runs colliding on shared channels —
a failure mode that is intermittent, confusing, and expensive to diagnose.

## CIR registry

| Environment | `Registry.ID` |
|---|---|
| dev | `OIIE-SANDBOX-DEV-{ALIAS}` |
| CI | `OIIE-SANDBOX-CI-{RUNID}` |
| demo | `OIIE-SANDBOX` |


## Running provisioning

```powershell
az login

./deploy/sandbox/provision.ps1 -Environment dev -Alias hretief -StorageAccount <account>
```

Provisioning now only ensures the blob container and reports the settings to put
in `appsettings.Development.json`. Provider databases are provisioned by the ENG
and REG-LOCATION deployments.


## Deployed application

The sandbox is **one App Service**.

| Environment | API |
|---|---|
| dev | `acme-api-sandbox-dev` |
| CI | `acme-api-sandbox-ci` |
| demo | `acme-api-sandbox-demo` |

At `https://{name}.azurewebsites.net`. Application Insights
`appi-acme-sandbox-{env}`, workspace `log-acme-sandbox-{env}`, App Service plan
`acme-plan-sandbox-{env}`.

The plan is dedicated rather than the shared `acme-plan-{env}`, because that one
is a Windows `functionapp` plan and this is a Linux App Service. A plan has an
OS and a site cannot cross it.

| | `acme-api-sandbox-{env}` |
|---|---|
| Project | `Oiie.Sandbox.Api` |
| Serves | `/admin/*`, `/health/*`, and the TypeScript UI from `wwwroot` |
| Message pumps | no — the ENG and REG-LOCATION engines own publish and ingest |
| Always On | recommended |
| Health probe | `/health/participants` |
| Audience | scripts, scenarios, the React app |

The API was `oiie-sandbox-{env}` until 2026-09. That name was kept while other
systems were believed to hold it, but no deployed app did: the only setting
carrying a sandbox URL was the API's own `Isbm__ListenerBaseUrl`, which is
derived from the app name and so followed the rename. See DR-024.

**The Blazor UI has been removed.** `oiie-simhost-{env}` used to host it. The
demo uses the TypeScript UI in `WorkflowOrchestration/`, served by the API from
its own `wwwroot`. Both the Azure sites and the `uiApp` Bicep resource are gone.

`Always On` keeps the API warm so the first admin call after an idle period does
not pay a cold start. That rules out the Free and Shared tiers.

The UI reads participant state through the provider apps rather than any database
of its own, but still gets the Key Vault and Storage grants for ISBM tokens and
payload blobs, under its own system-assigned identity.

`Sandbox__ApiBaseUrl` on the UI points at the API. Without it the UI falls back to
its own base address and the reset and scenario-launch buttons 404 against
themselves.

## Deployment order

```powershell
./deploy/sandbox/provision.ps1 -Environment demo -StorageAccount <account>   # storage
./deploy/sandbox/deploy.ps1    -Environment demo -StorageAccount <account>   # hosting
```

Each publish goes to its own `artifacts/publish-{target}` folder. They are kept
separate because zip deploy never deletes: publishing one app's output into
the other's slot would leave both entry points on the server.

Provisioning creates the blob container. Deployment adds the App Service and
grants its managed identity **Key Vault Secrets User** and **Storage Blob Data
Contributor** — data-plane roles that subscription Owner does not confer, and
whose absence surfaces as a 403 that reads like an application bug.

Role assignments take a few minutes to propagate. A Key Vault 403 immediately after
a first deployment is usually that.

Adding a participant is not finished when its personality pack is. Its ISBM token
secret and channel subscriptions still have to exist before it can take part, and
its own provider owns its schema and reference data. Nothing in the sandbox has to
be recompiled.

### Two failures that report the wrong cause

**Zip deploy never deletes.** Files removed from the project stay on the server
indefinitely. `Sandbox__PersonalitiesPath` must therefore be `PersonalityPacks`,
never `Personalities` — the latter was a folder an earlier deployment left behind,
and because it parsed cleanly the app reported a smaller participant roster with
no error at all. The scenario then failed with "`CMS` is not a known
participant", which points at the scenario file rather than at the deployment that
actually caused it.

**`az webapp deploy` reports failure on successful deployments.** Cold start on
the B1 plan regularly exceeds the CLI's ten-minute budget, and Kudu sometimes
answers 502 after the upload has already been accepted. Confirm against
`/api/deployments/latest` (status 4 is success) before treating the reported error
as real. The cost is that the deploy script's own verification stage never runs on
these paths, so a genuinely broken deployment currently looks identical to a slow
one.

## Identity model

| Resource | How the app authenticates |
|---|---|
| Key Vault | Managed identity |
| Blob Storage | Managed identity |
| Application Insights | Connection string |

The sandbox opens no SQL connection at all. Each provider app authenticates to its
own database independently.
