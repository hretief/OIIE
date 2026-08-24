# Deploying RegLocationProvider

Notes from the first real deployment (dev, 2026-08-24). Dev is live and
verified end to end, including the write path. Prod has not been deployed.

Read the [traps](#traps) before deploying to a new environment — both of them
cost time on the first run and neither fails in an obvious way.

## What this is

`RegLocationProvider` emulates the asset registry that governs functional
locations. Under
[azure-resource-naming-guidance.md](../../docs/azure-resource-naming-guidance.md)
that makes it an **`api`** resource:

```
acme-api-reglocation-dev
acme-api-reglocation-prod
```

The system code is `reglocation`, not `reg-location`. An internal hyphen would
break the `<account>-<kind>-<system>-<environment>` convention.

## Deploying

Order matters — the database must exist first.

```powershell
cd RegLocationProvider/deploy

./provision-databases.ps1 -Databases acme-db-reglocation-dev
./deploy-functionapp.ps1 -Environment dev
```

`provision-databases.ps1` creates an **empty** database on purpose. It does not
run any DDL: the app applies its own schema and seed at startup, and having the
script also create tables would mean two sources of truth for the same schema.

Useful switches:

| Switch | Effect |
|---|---|
| `-WhatIf` | Dry run. Prints every resource it would create and touches nothing. |
| `-InfraOnly` | Creates/updates Azure resources but skips the code publish. |

Both scripts are idempotent and skip anything that already exists, so a partial
failure is safe to re-run.

## What gets created

Per environment, in `HilmarRetiefRG`:

| Resource | Dev | Prod |
|---|---|---|
| Function app | `acme-api-reglocation-dev` | `acme-api-reglocation-prod` |
| App Service plan (B1) | `acme-plan-reglocation-dev` | `acme-plan-reglocation-prod` |
| User-assigned identity | `acme-id-reglocation-dev` | `acme-id-reglocation-prod` |
| Storage account | `acmestoragedev01` | `acmestorageprod01` |
| SQL database | `acme-db-reglocation-dev` | `acme-db-reglocation-prod` |

Basic B1 rather than Consumption, matching the other providers: Consumption
cold starts are long enough to be mistaken for a fault during a demo.

## Schema ownership

The runtime schema lives with the code, not in `docs`:

| File | Applied when |
|---|---|
| `Infrastructure/Sql/schema.sql` | `RegLocation__AutoCreateSchema=true` |
| `Infrastructure/Sql/bootstrap.sql` | `RegLocation__AutoBootstrap=true` |

Both are embedded resources loaded by `SchemaInitializer` at startup, and both
are re-runnable, so a restart against an already-populated database is a no-op.
`docs/DDL/REG-LOCATION*.SQL` are reference copies for reading and are not what
the app executes — do not edit them expecting a deployment to change.

The bootstrap seed is what makes the registry usable on a cold database: the
Global scope, namespaces, units, classes, sample items and tags, plus the
`objects` rows those all depend on.

## Traps

### `sqlcmd -G` alone fails under MFA

The deployment grants the app's identity access to its database. The obvious
form of that command:

```powershell
sqlcmd -S acme-sql-server.database.windows.net -d acme-db-reglocation-dev -G -Q "..."
```

resolves `-G` to **ActiveDirectoryIntegrated**, which cannot prompt, so on an
MFA-enforced tenant it fails with:

```
AADSTS50076: Due to a configuration change made by your administrator...
```

Adding `-U <upn>` selects the interactive flow instead, which can complete the
prompt:

```powershell
sqlcmd -S acme-sql-server.database.windows.net -d acme-db-reglocation-dev `
       -G -U "you@example.com" -Q "..."
```

The script now does this. Whoever runs it must be an Entra admin on
`acme-sql-server`.

### A fresh app can fail its first publish

Newly created Function Apps take time to settle their SCM endpoint, and
`func azure functionapp publish` reports that as:

```
Timed out waiting for SCM to update the Environment Settings
```

The app and its configuration are fine; only the publish failed. The script
retries three times with a 30-second backoff. If all three fail, wait a minute
and re-run with `-InfraOnly:$false`.

### The app deploys healthy-looking but uninitialised

`SchemaInitializer` logs a failure rather than crashing the host. If the SQL
grant has not been applied yet, the app starts, cannot create its schema, and
serves `503` from every data route while looking deployed.

The fix is ordering, not code: grant SQL access, then **restart** the app so
the initializer runs again.

```powershell
az functionapp restart -g HilmarRetiefRG -n acme-api-reglocation-dev
```

This is worth revisiting before prod. Logging the failure is right for dev,
where an unreachable database should not stop the host from starting, but in
prod a schema that never applied should probably fail the health check loudly
instead of quietly returning errors.

## Verifying

`/api/health` is anonymous and reports row counts plus two integrity figures:

```powershell
Invoke-RestMethod https://acme-api-reglocation-dev.azurewebsites.net/api/health
```

```json
{"status":"healthy","scopes":1,"items":5,"tags":6,"orphanedRows":0,"untrustedConstraints":0}
```

`orphanedRows` and `untrustedConstraints` must both be `0`. A non-zero value
means the registry has drifted — usually a restore or a manual edit that
bypassed the triggers — and no route will tell you that as directly.

Every other route needs a function key:

```powershell
$k = az functionapp keys list -g HilmarRetiefRG -n acme-api-reglocation-dev --query "functionKeys.default" -o tsv
Invoke-RestMethod "https://acme-api-reglocation-dev.azurewebsites.net/api/items?code=$k"
```

Health only proves the app can *read*. The identity grant covers writes
separately, so a write is worth exercising before calling a deployment good:

```powershell
$body = '{"itemId":1,"classId":1702,"code":"SMOKE-001","revision":1,"name":"Smoke","scopeId":1}'
Invoke-RestMethod -Method POST "https://acme-api-reglocation-dev.azurewebsites.net/api/tags?code=$k" `
                  -Body $body -ContentType application/json
```

Expect `201` with a minted federation GUID, and `409` if you repeat it — a tag
code is unique per item and revision. Delete anything you create this way so
the environment stays at its seeded baseline.
