# Deploying CmsProvider

Notes from the first real deployment (dev and prod, 2026-08-21). Both
environments are live and verified. Read the [traps](#traps) before deploying
to a new subscription — every one of them cost time on the first run and none
of them fail in an obvious way.

## What this is

`CmsProvider` emulates the Meridium CMS product itself. Under
[azure-resource-naming-guidance.md](../../docs/azure-resource-naming-guidance.md)
that makes it an **`api`** resource, not an `engn` one:

```
acme-api-cms-dev
acme-api-cms-prod
```

`CmsEngine` is a separate deployment as `acme-engn-cms-<env>`. Do not merge the
two into one Function App to save cost. The split is a boundary, not a hosting
detail: an engine may use only access a third party could be granted, whereas
this API owns the CMS data directly. Collapsed into one resource, that
distinction stops being visible in the resource list, which is the only place
an auditor would look for it.

## Deploying

Order matters — the database must exist first.

```powershell
cd CmsProvider/deploy

./provision-databases.ps1                      # both environments, idempotent
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

Both scripts are idempotent and skip anything that already exists, so a partial
failure is safe to re-run.

## What gets created

Per environment, in `HilmarRetiefRG`:

| Resource | Dev | Prod |
|---|---|---|
| Function app | `acme-api-cms-dev` | `acme-api-cms-prod` |
| App Service plan (B1) | `acme-plan-cms-dev` | `acme-plan-cms-prod` |
| User-assigned identity | `acme-id-cms-dev` | `acme-id-cms-prod` |
| Storage account | `acmestoragedev01` | `acmestorageprod01` |
| SQL database | `acme-db-cms-dev` | `acme-db-cms-prod` |

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
cd CmsProvider
func azure functionapp publish acme-api-cms-dev --dotnet-isolated
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
CREATE USER [acme-id-cms-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [acme-id-cms-dev];
ALTER ROLE db_datawriter ADD MEMBER [acme-id-cms-dev];
ALTER ROLE db_ddladmin  ADD MEMBER [acme-id-cms-dev];
```

Two things to get right:

**The principal is the identity, not the app.** `acme-id-cms-dev`, not
`acme-api-cms-dev`. Under a *system*-assigned identity these names coincide,
which is a common way to get this wrong — a user created for the app name will
never be matched, and the failure looks like a permissions problem rather than
a naming one.

**Restart the app afterwards.** The schema bootstrap runs once at startup, so
on a first deployment it runs *before* the user exists, fails, and logs the
failure without taking the host down (deliberately — see `SchemaInitializer`).
Health then keeps reporting `Invalid object name 'dbo.Site'` even after the
grant lands, because nothing has re-run the bootstrap:

```powershell
az functionapp restart -g HilmarRetiefRG -n acme-api-cms-prod
```

Prod hit exactly this. The tables were absent, not the permissions.

### Running the grant when sqlcmd will not authenticate

`sqlcmd -G` alone uses integrated auth and fails under MFA with `AADSTS50076`.
Older `sqlcmd` builds have no `--authentication-method` flag. An access token
sidesteps both:

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:acme-sql-server.database.windows.net,1433;Initial Catalog=acme-db-cms-dev;Encrypt=True;"
$conn.AccessToken = $tok
$conn.Open()
```

This works because the signed-in user is the server's Entra admin.

### Globally unique names

Function App and storage account names are unique across **all** of Azure, not
just the subscription. `acmestoragedev` was already taken by another tenant,
which is why the convention carries a two-digit suffix (`acmestoragedev01`).
Check before committing a name to source:

```powershell
az storage account check-name --name acmestoragedev01
az rest --method post --uri "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.Web/checknameavailability?api-version=2023-12-01" --body '{"name":"acme-api-cms-dev","type":"Microsoft.Web/sites"}'
```

`acme` is a placeholder and is contested. Expect to change it for a real
customer deployment.

## Verifying

`/api/health` is anonymous and reports the site count, which proves the app
reached its database rather than merely started:

```powershell
Invoke-RestMethod https://acme-api-cms-dev.azurewebsites.net/api/health
# { "status": "healthy", "sites": 2 }
```

`unhealthy` carries the reason in `detail`. Every other route needs a function
key:

```powershell
$k = az functionapp keys list -g HilmarRetiefRG -n acme-api-cms-dev --query "functionKeys.default" -o tsv
Invoke-RestMethod "https://acme-api-cms-dev.azurewebsites.net/api/assettypes?code=$k"
```

A fresh database returns exactly one asset type, `UNCLASSIFIED`, with the fixed
id `00000000-0000-0000-0000-0000000000FF`. Seeing it confirms `schema.sql`
applied, since it is seeded by the same script.

## Before this is a real production deployment

- **Drop `db_ddladmin`.** It is granted only because
  `Cms__AutoCreateSchema=true` applies `schema.sql` at startup. Once the schema
  is settled, set that to `false` and remove the role — a running app has no
  business altering its own schema.
- **Reconsider function keys.** They are adequate for a demo and are not an
  authorization model.
- **`acme-db-cms-prod` shares a server with the sandbox databases.** Acceptable
  for a demo; a customer system would not sit on the integrator's SQL server.
