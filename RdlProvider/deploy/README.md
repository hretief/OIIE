# Deploying RdlProvider

`deploy-functionapp.ps1` provisions and publishes the RDL API tier.

```powershell
./deploy-functionapp.ps1 -Environment dev
./deploy-functionapp.ps1 -Environment dev -WhatIf
./deploy-functionapp.ps1 -Environment prod -SkipSqlGrant
```

| Resource | Name |
|---|---|
| Function app | `acme-api-rdl-dev` |
| Identity | `acme-id-rdl-dev` |
| Plan | `acme-plan-dev` (shared) |
| Storage | `acmestoragedev01` (shared) |
| Database | `acme-db-eis-dev` (**owned by REG-LOCATION**) |

## RDL does not own its database

This is the one way this script must not converge with the other provider
scripts. There is no `provision-databases.ps1` here, and there must not be one.

RDL is the *second* surface over `acme-db-eis-dev`. The database is named for
the product rather than for either app precisely because it has two consumers:
`RegLocationProvider` classifies tags against the class vocabulary that
`RdlProvider` curates in `dbo.class_objects`. Same persistence, different apps —
see `RegLocationProvider/deploy/README.md`, "Schema ownership".

`RegLocationProvider` creates the database, owns the DDL, and applies
`schema.sql` and `bootstrap.sql` at startup under its own `AutoCreateSchema` /
`AutoBootstrap` flags. `RdlOptions` deliberately has no equivalent flags — two
apps applying the same DDL would race on a cold start, and worse, would let the
two definitions drift apart without anything reporting it.

So the deployment order is:

1. `RegLocationProvider/deploy/provision-databases.ps1` — creates `acme-db-eis-dev`
2. `RegLocationProvider/deploy/deploy-functionapp.ps1` — applies the schema on start
3. `RdlProvider/deploy/deploy-functionapp.ps1` — this script
4. `deploy/engines/deploy-engine.ps1 -Engine rdl` — the channel-facing engine

Step 3 fails fast with a message naming REG-LOCATION if the database is absent,
rather than deploying an app that starts cleanly and fails on every request.

## The grant is read-only

The identity gets `db_datareader` and nothing else.

Not `db_ddladmin`: REG-LOCATION owns the schema, and granting RDL rights to
change it would turn the ownership boundary above from something enforced into
something merely documented. Not `db_datawriter` either — `IRdlStore` has
`CreateClassAsync` and `UpdateClassAsync`, but nothing on the request/response
path calls them. If a write path is genuinely wanted later, add the role
deliberately and note why here.

## Deploy the provider before the engine

`deploy/engines/deploy-engine.ps1 -Engine rdl` reads this app's function key to
configure `RdlEngine__RdlApiKey`, and throws if `acme-api-rdl-dev` does not
exist. Deploying the engine first produces an app that starts cleanly and 401s
on every drain.

## Verifying

```powershell
curl https://acme-api-rdl-dev.azurewebsites.net/api/health
curl "https://acme-api-rdl-dev.azurewebsites.net/api/classes?code=<key>"
```

The Bruno collection under `testing/bruno/rdl` carries the same requests with
the function key bound as an environment variable.
