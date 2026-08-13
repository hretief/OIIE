# Sandbox naming conventions

Single source of truth for names. `provision.ps1` derives everything below from
`environment` and `alias`, so nothing should be typed by hand twice.

## Environments

| Environment | Value of `-Environment` | Database |
|---|---|---|
| Per-developer | `dev` | `oiie-sandbox-dev-{alias}` |
| CI | `ci` | `oiie-sandbox-ci` |
| Demo | `demo` | `oiie-sandbox-demo` |

Existing shared resources, referenced rather than created:

| Resource | Name |
|---|---|
| Resource group | `HilmarRetiefRG` |
| SQL server | `acme-sql-server.database.windows.net` |
| Key Vault | `mndot` |
| Storage account | (set `-StorageAccount`; blob container created per environment) |

One server, one database per environment. All three databases coexist on
`acme-sql-server` and are isolated from each other by being separate databases, and
internally by the schema and grant model below.

## Participants, schemas, logins

Participant ids use hyphens (route segments); schemas and logins use underscores.

| Participant | Schema | Contained database user |
|---|---|---|
| `eng` | `eng` | `sb_eng` |
| `construct` | `construct` | `sb_construct` |
| `reg-location` | `reg_location` | `sb_reg_location` |
| `reg-asset` | `reg_asset` | `sb_reg_asset` |
| `reg-product` | `reg_product` | `sb_reg_product` |
| `reg-material` | `reg_material` | `sb_reg_material` |
| `mms` | `mms` | `sb_mms` |
| `om-reliability` | `om_reliability` | `sb_om_reliability` |
| `rdl` | `rdl` | `sb_rdl` |

Two non-participant schemas:

| Schema | Purpose | Access |
|---|---|---|
| `sandbox` | Scenario runs and assertions | `sb_orchestrator` — read/write |
| `tower` | Read-only cross-schema views for the control tower | `sb_tower` — SELECT only |

**Users are contained database users, not server logins.** A server-level login is
shared by every database on the server, so `sb_eng` would be one principal across
dev, CI and demo — and since provisioning skips an existing login, the second
environment's Key Vault secret would silently disagree with the real password.
Contained users are scoped to their database, so each environment is independent.
Connection strings must therefore always name the database explicitly.

`sb_tower` is the single sanctioned cross-schema principal. No participant login
may read another participant's schema; that constraint is the whole point of the
per-login model, because without it a cross-schema join will eventually be used to
resolve a foreign identifier instead of a CIR call — it will work, nobody will
notice, and the demonstration will stop proving anything.

## Key Vault secrets

| Secret | Contents |
|---|---|
| `sandbox-sql-{env}-{participant}` | Password for contained user `sb_{schema}` in that environment's database |
| `sandbox-sql-{env}-orchestrator` | Password for `sb_orchestrator` |
| `sandbox-sql-{env}-tower` | Password for `sb_tower` |
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
Install-Module SqlServer -Scope CurrentUser   # once
az login

./deploy/provision.ps1 -Environment dev -Alias hretief -AddFirewallRule -SkipStorage
```

The signed-in identity must be **Entra admin on the SQL server**, because contained
users and grants are applied over an Entra access token.

T-SQL runs through `Invoke-Sqlcmd`. Azure CLI has no T-SQL execution command —
`az sql db query` does not exist, and an earlier version of this script called it,
reporting success for ten users that were never created. Every Azure call is now
checked, and the run ends by verifying schemas, users, and the absence of
cross-schema grants.


## Deployed application

The sandbox is **two App Services sharing one plan**, not one.

| Environment | API | Blazor UI |
|---|---|---|
| dev | `oiie-sandbox-dev` | `oiie-simhost-dev` |
| CI | `oiie-sandbox-ci` | `oiie-simhost-ci` |
| demo | `oiie-sandbox-demo` | `oiie-simhost-demo` |

Both at `https://{name}.azurewebsites.net`. Plan `plan-oiie-sandbox-{env}`,
Application Insights `appi-oiie-sandbox-{env}`, workspace `log-oiie-sandbox-{env}`
— shared, so one correlation id still reconstructs an exchange across both.

| | `oiie-sandbox-{env}` (API) | `oiie-simhost-{env}` (UI) |
|---|---|---|
| Project | `Oiie.Sandbox.Api` | `SimHost` |
| Serves | `/admin/*`, `/health/*` | Blazor Server UI |
| Message pumps | **yes** | **no** |
| Always On | required | on (cold start only) |
| WebSockets | off | required (SignalR circuit) |
| Health probe | `/health/participants` | `/` |
| Audience | scripts, scenarios, React app | end-to-end automated testing |

The API keeps the historic `oiie-sandbox-{env}` name deliberately. That value is
already baked into `Isbm__ListenerBaseUrl`, the CIR's configuration and every
script holding a sandbox URL; renaming it would break those silently. The UI is
new as a separate address, so it takes the new name.

**Only the API runs the pumps.** This is enforced in code, not in Bicep:
`SimHost/Program.cs` calls `AddSandboxCore` but never `AddSandboxMessagePumps`.
If both hosts pumped, two consumers would race the same ISBM sessions and
messages would appear to vanish at random. Do not add the pumps to the UI.

`Always On` is required on the API, not optional: the inbox pump and outbox
dispatcher are hosted services, and an unloaded app stops consuming in a way that
looks exactly like a provider that has stopped delivering. That rules out the Free
and Shared tiers. It is also the reason the API is an App Service rather than a
Function App like the ISBM and CIR providers — the workload is a resident poll
loop, not a burst of events.

The UI reads participant tables and payload blobs directly through Core, so it is
not a thin client: it gets the same Key Vault and Storage grants as the API, under
its own system-assigned identity.

`Sandbox__ApiBaseUrl` on the UI points at the API. Without it the UI falls back to
its own base address and the reset and scenario-launch buttons 404 against
themselves.

## Deployment order

```powershell
./deploy/provision.ps1 -Environment demo -StorageAccount <account>   # data
./deploy/deploy.ps1    -Environment demo -StorageAccount <account>   # hosting, both apps
```

Deploy one app at a time with `-Target`:

```powershell
./deploy/deploy.ps1 -Environment demo -StorageAccount <account> -Target api
./deploy/deploy.ps1 -Environment demo -StorageAccount <account> -Target ui
```

Each target publishes to its own `artifacts/publish-{target}` folder. They are
kept separate because zip deploy never deletes: publishing one app's output into
the other's slot would leave both entry points on the server.

Provisioning creates the database, schemas, contained users and secrets.
Deployment adds the App Services and grants **both** managed identities **Key Vault
Secrets User** and **Storage Blob Data Contributor** — data-plane roles that
subscription Owner does not confer, and whose absence surfaces as a 403 that reads
like an application bug.

Role assignments take a few minutes to propagate. A Key Vault 403 immediately after
a first deployment is usually that.

Adding a participant is not finished when the code is. Its Key Vault secret,
contained user, schema and ISBM subscription all have to exist before a scenario
naming it can run, and `schema/reset` must run afterwards to create its tables:
provisioning creates the schema, the app creates what is in it. A missing table
surfaces as `Invalid object name '<schema>.IsbmSession'` during the subscription
check, which reads like a broker fault rather than a provisioning gap.

### Two failures that report the wrong cause

**Zip deploy never deletes.** Files removed from the project stay on the server
indefinitely. `Sandbox__PersonalitiesPath` must therefore be `PersonalityPacks`,
never `Personalities` — the latter was a folder an earlier deployment left behind,
and because it parsed cleanly the app reported a smaller participant roster with
no error at all. The scenario then failed with "`om-reliability` is not a known
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
| Azure SQL | Per-participant **contained users**, passwords from Key Vault |

SQL deliberately does not use the managed identity. One identity would collapse
eight participants into a single principal and take the schema grants out of force,
and those grants are what stop a cross-schema join quietly replacing a CIR call.
