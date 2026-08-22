# Deploying the ws-CIR provider

Notes from the real deployment of `acme-api-cir-dev` and `acme-api-cir-prod`.
Deploy ISBM first — CIR needs its base URL and function key.

## Order

1. `provision-databases.ps1` — creates `acme-db-cir-dev` / `acme-db-cir-prod`.
2. `ISBMProvider/deploy/deploy-functionapp.ps1` — CIR points at it.
3. `deploy-functionapp.ps1 -Environment dev -IsbmBaseUrl https://acme-api-isbm-dev.azurewebsites.net/api`
4. The SQL grant below, then a restart.
5. Create the two CIR channels on the ISBM provider.

The ISBM function key is read automatically from the matching ISBM app; pass
`-IsbmApiKey` to override.

## What gets created

| Resource | dev | prod |
| --- | --- | --- |
| Function App | `acme-api-cir-dev` | `acme-api-cir-prod` |
| App Service plan | `acme-plan-cir-dev` | `acme-plan-cir-prod` |
| Identity | `acme-id-cir-dev` | `acme-id-cir-prod` |
| Database | `acme-db-cir-dev` | `acme-db-cir-prod` |

## Relationship to the old provider

`cir-func-44p2f3n6` is untouched: still on the legacy `cir` database, still
pointing at the legacy ISBM host. The new registries start **empty**. No
CIRIDs are migrated, which is only safe while none have been issued from the
legacy registry — if that changes, cutover becomes a data migration.

## Required manual step

The app cannot reach SQL until its identity has a database user. Connect to
the database as an Entra admin:

    CREATE USER [acme-id-cir-dev] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [acme-id-cir-dev];
    ALTER ROLE db_datawriter ADD MEMBER [acme-id-cir-dev];
    ALTER ROLE db_ddladmin  ADD MEMBER [acme-id-cir-dev];

The principal is the **user-assigned identity**, not the function app. The app
authenticates as it via `AZURE_CLIENT_ID`, so a user created for the app name
would never match.

`db_ddladmin` is only needed while `Cir__AutoCreateSchema` is true. Drop it
once the schema is settled.

## Traps

**Restart after the grant, and check the tables.** The schema bootstrap runs
once at startup, so on a first deployment it runs before the SQL user exists,
fails, and logs it without taking the host down. Health then keeps reporting
`Login failed for user '<token-identified principal>'` even after the grant
lands, because the failed token is cached. On prod this needed a restart to
clear the login, and the schema bootstrap then needed the app to come up
cleanly before `cir.*` existed. Confirm with:

    SELECT TABLE_SCHEMA+'.'+TABLE_NAME FROM INFORMATION_SCHEMA.TABLES;

Expect `cir.Registry`, `cir.Entry`, `cir.Category`, `cir.Property`,
`cir.IsbmSession`. A missing `cir.IsbmSession` shows up as
`Invalid object name 'cir.IsbmSession'` on drain.

**`Isbm__Enabled` defaults to false** in code, so the listener stays dormant
until an ISBM provider exists. The script sets it true. Without it the poll
timer runs and drains nothing, which looks like a working but idle system.

**`Isbm__ApiKey` is required.** It is sent as `x-functions-key`. Without it
every ISBM call is 401 and the listener silently drains nothing.

**Publish may need the zip fallback.** Same SCM timeout as ISBM; the script
falls back automatically. A 502 from the zip deploy means restart and retry.

## Verify

    GET  /api/health          -> {"status":"healthy","sql":true}
    GET  /api/isbm/status     -> enabled true, hasApiKey true
    POST /api/isbm/drain      -> "errors": []

A clean drain should leave two sessions open, `ProviderRequest` on
`/OIIE/CIR/Request` and `Subscription` on `/OIIE/CIR/Publication`. If the
publication channel is missing on the ISBM side, drain reports a 404 Channel
fault rather than failing outright.
