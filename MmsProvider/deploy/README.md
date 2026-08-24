# Deploying MmsProvider

**Dev deployed and verified, 2026-08-24. Prod not deployed.** The traps below
were paid for on the CMS and REG-LOCATION deployments and all of them applied
here unchanged, except where noted against the MMS run.

Read the [traps](#traps) before deploying — every one of them cost time on the
first run and none of them fail in an obvious way.

## What this is

`MmsProvider` emulates the customer's maintenance management system. Under
[azure-resource-naming-guidance.md](../../docs/azure-resource-naming-guidance.md)
that makes it an **`api`** resource, not an `engn` one:

```
acme-api-mms-dev
acme-api-mms-prod
```

There is no `MmsEngine`. MMS deploys as a single Function App where CMS is
split into engine and provider, and that asymmetry is deliberate — see
[participant-abstraction-spec.md §5.2](../../docs/participant-abstraction-spec.md).
Two participants built two different ways, both joining the same channel and
neither requiring a change to the other, demonstrates more than two identical
ones would. Do not add an engine here to make it match CMS.

## Deploying

Order matters — the database must exist first.

```powershell
cd MmsProvider/deploy

./provision-databases.ps1 -Databases acme-db-mms-dev   # omit to do both, idempotent
./deploy-functionapp.ps1 -Environment dev
```

Then grant the app's identity access to its database and restart. The script
prints the exact statements; see [SQL access](#sql-access) for why both steps
are needed.

Useful switches:

| Switch | Effect |
|---|---|
| `-WhatIf` | Dry run. Prints every resource it would create and touches nothing. |
| `-InfraOnly` | Creates/updates Azure resources but skips the code publish. |
| `-SkipSqlGrant` | Skips the grant and prints the SQL to run by hand. See [the MFA trap](#the-grant-cannot-run-unattended). |

Both scripts are idempotent and skip anything that already exists, so a partial
failure is safe to re-run.

## What gets created

Per environment, in `HilmarRetiefRG`:

| Resource | Dev | Prod |
|---|---|---|
| Function app | `acme-api-mms-dev` | `acme-api-mms-prod` |
| App Service plan (B1) | `acme-plan-mms-dev` | `acme-plan-mms-prod` |
| User-assigned identity | `acme-id-mms-dev` | `acme-id-mms-prod` |
| Storage account | `acmestoragedev01` | `acmestorageprod01` |
| SQL database | `acme-db-mms-dev` | `acme-db-mms-prod` |

Application Insights is created automatically by `az functionapp create`, named
after the app. Nothing references it yet.

Basic B1 rather than Consumption, matching the CIR provider: Consumption cold
starts are long enough to be mistaken for a fault during a demo.

## Traps

### SCM basic auth is disabled on new apps

Azure now creates Function Apps with SCM basic publishing credentials
**disabled**. `func azure functionapp publish` authenticates that way, so it
fails with:

```
Timed out waiting for SCM to update the Environment Settings
```

This reads like a transient race and is not one — it will fail identically on
every retry until the policy is changed. `deploy-functionapp.ps1` now enables
it, but **the setting takes a few minutes to propagate**, so a first deployment
of a brand-new app may still need one publish retry:

```powershell
cd MmsProvider
func azure functionapp publish acme-api-mms-dev --dotnet-isolated
```

Both environments needed exactly one retry. The older `cir-func-*` app predates
this default, which is why it publishes without the step and is a misleading
thing to compare against.

### SQL access

The app authenticates to SQL as its **user-assigned identity** using
`Authentication=Active Directory Default`, with `AZURE_CLIENT_ID` naming which
identity to use. No secret is stored anywhere; the token is acquired at runtime.

Creating the Azure resources is not sufficient. The identity needs a database
user, created by an Entra admin on the SQL server:

```sql
CREATE USER [acme-id-mms-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [acme-id-mms-dev];
ALTER ROLE db_datawriter ADD MEMBER [acme-id-mms-dev];
ALTER ROLE db_ddladmin  ADD MEMBER [acme-id-mms-dev];
```

Two things to get right:

**The principal is the identity, not the app.** `acme-id-mms-dev`, not
`acme-api-mms-dev`. Under a *system*-assigned identity these names coincide,
which is a common way to get this wrong — a user created for the app name will
never be matched, and the failure looks like a permissions problem rather than
a naming one.

**Restart the app afterwards.** The schema bootstrap runs once at startup, so
on a first deployment it runs *before* the user exists, fails, and logs the
failure without taking the host down (deliberately — see `SchemaInitializer`).
Health then keeps reporting `Invalid object name 'dbo.Site'` even after the
grant lands, because nothing has re-run the bootstrap:

```powershell
az functionapp restart -g HilmarRetiefRG -n acme-api-mms-dev
```

The MMS dev run hit exactly this, for the same reason: the grant landed after
the first startup, so the tables were absent, not the permissions.

### The grant cannot run unattended

`sqlcmd -G -U <upn>` opens an interactive MFA prompt. That prompt cannot
surface in a background or non-interactive session, so the deploy script hangs
at the grant with no error — it is waiting for a browser nobody can see. This
is inherent to interactive Entra auth, not a script defect, and it will stop
any attempt to run this from CI.

Either run `deploy-functionapp.ps1` in a foreground terminal, or pass
`-SkipSqlGrant` and apply the printed SQL yourself:

```powershell
sqlcmd -S acme-sql-server.database.windows.net -d acme-db-mms-dev -G -U <your-upn> -i grant.sql
```

That worked on the MMS run. If a `sqlcmd` build is too old for `-G -U`, or
fails with `AADSTS50076`, an access token sidesteps the prompt entirely:

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:acme-sql-server.database.windows.net,1433;Initial Catalog=acme-db-mms-dev;Encrypt=True;"
$conn.AccessToken = $tok
$conn.Open()
```

Both paths work only because the signed-in user is the server's Entra admin.

Creating the user is not the whole grant. Verify the role memberships landed
separately — the user existing says nothing about what it can do:

```sql
SELECT r.name FROM sys.database_role_members m
  JOIN sys.database_principals r ON r.principal_id = m.role_principal_id
  JOIN sys.database_principals u ON u.principal_id = m.member_principal_id
 WHERE u.name = 'acme-id-mms-dev';
```

### Globally unique names

Function App and storage account names are unique across **all** of Azure, not
just the subscription. `acmestoragedev` was already taken by another tenant,
which is why the convention carries a two-digit suffix (`acmestoragedev01`).
Check before committing a name to source:

```powershell
az storage account check-name --name acmestoragedev01
az rest --method post --uri "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.Web/checknameavailability?api-version=2023-12-01" --body '{"name":"acme-api-mms-dev","type":"Microsoft.Web/sites"}'
```

`acme` is a placeholder and is contested. Expect to change it for a real
customer deployment.

## Verifying

`/api/health` is anonymous and reports the site count, which proves the app
reached its database rather than merely started:

```powershell
Invoke-RestMethod https://acme-api-mms-dev.azurewebsites.net/api/health
# { "status": "healthy", "sites": 0 }
```

`unhealthy` carries the reason in `detail`. Every other route needs a function
key:

```powershell
$k = az functionapp keys list -g HilmarRetiefRG -n acme-api-mms-dev --query "functionKeys.default" -o tsv
Invoke-RestMethod "https://acme-api-mms-dev.azurewebsites.net/api/assettypes?code=$k"
```

A fresh database returns exactly one asset type, `UNCLASSIFIED`, with the fixed
id `00000000-0000-0000-0000-0000000000FF`. Seeing it confirms `schema.sql`
applied, since it is seeded by the same script.

A write is worth proving too, because health only reads. Note that MMS does not
mint identifiers — `siteId` and `siteName` are both required, and omitting
either is rejected by design rather than defaulted:

```powershell
$k = az functionapp keys list -g HilmarRetiefRG -n acme-api-mms-dev --query "functionKeys.default" -o tsv
$sid = [guid]::NewGuid().ToString()
Invoke-RestMethod -Method Post -Uri "https://acme-api-mms-dev.azurewebsites.net/api/sites" `
  -Headers @{ "x-functions-key" = $k } -ContentType 'application/json' `
  -Body "[{""siteId"":""$sid"",""siteCode"":""SMOKE-01"",""siteName"":""Smoke Test Site""}]"
```

Delete the row afterwards. The dev database is expected to be empty.

## Before this is a real production deployment

- **Drop `db_ddladmin`.** It is granted only because
  `Mms__AutoCreateSchema=true` applies `schema.sql` at startup. Once the schema
  is settled, set that to `false` and remove the role — a running app has no
  business altering its own schema.
- **Reconsider function keys.** They are adequate for a demo and are not an
  authorization model.
- **`acme-db-mms-prod` shares a server with the sandbox databases.** Acceptable
  for a demo; a customer system would not sit on the integrator's SQL server.
