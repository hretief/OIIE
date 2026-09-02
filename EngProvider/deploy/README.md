# Deploying EngProvider

Not yet deployed. These notes are the CMS deployment's, adapted — the traps are
the same because the resource shape is the same, and every one of them cost time
on the first CMS run without failing in an obvious way. Read
[traps](#traps) before the first deployment.

## What this is

`EngProvider` emulates the engineering design tool itself. Under
[azure-resource-naming-guidance.md](../../docs/azure-resource-naming-guidance.md)
that makes it an **`api`** resource, not an `engn` one:

```
acme-api-eng-dev
acme-api-eng-prod
```

`EngEngine` does not exist yet. When it does it deploys separately as
`acme-engn-eng-<env>`. Do not merge the two into one Function App to save cost.
The split is a boundary, not a hosting detail: an engine may use only access a
third party could be granted, whereas this API owns the ENG data directly.
Collapsed into one resource, that distinction stops being visible in the
resource list, which is the only place an auditor would look for it.

**This app has no ISBM or CIR configuration, and that is deliberate.** It is an
emulated customer product. It does not know it is being integrated with, so it
has no channel to publish to and no participant to be. If integration settings
ever appear in `deploy-functionapp.ps1`, the emulation has stopped being one.

## Deploying

Order matters — the database must exist first.

```powershell
cd EngProvider/deploy

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
| Function app | `acme-api-eng-dev` | `acme-api-eng-prod` |
| App Service plan (B1) | `acme-plan-eng-dev` | `acme-plan-eng-prod` |
| User-assigned identity | `acme-id-eng-dev` | `acme-id-eng-prod` |
| Storage account | `acmestoragedev01` | `acmestorageprod01` |
| SQL database | `acme-db-eng-dev` | `acme-db-eng-prod` |

The storage account is shared with `CmsProvider` and is expected to already
exist; the script creates it only if absent.

Application Insights is created automatically by `az functionapp create`, named
after the app. Nothing references it yet.

Basic B1 rather than Consumption, matching the CMS and CIR providers:
Consumption cold starts are long enough to be mistaken for a fault during a demo.

## Traps

### SCM basic auth is disabled on new apps

Azure creates Function Apps with SCM basic publishing credentials **disabled**.
`func azure functionapp publish` authenticates that way, so it fails with:

```
Timed out waiting for SCM to update the Environment Settings
```

This reads like a transient race and is not one — it will fail identically on
every retry until the policy is changed. `deploy-functionapp.ps1` enables it,
but **the setting takes a few minutes to propagate**, so a first deployment of a
brand-new app may still need one publish retry:

```powershell
cd EngProvider
func azure functionapp publish acme-api-eng-dev --dotnet-isolated
```

Both CMS environments needed exactly one retry. Expect the same here.

### SQL access

The app authenticates to SQL as its **user-assigned identity** using
`Authentication=Active Directory Default`, with `AZURE_CLIENT_ID` naming which
identity to use. No secret is stored anywhere; the token is acquired at runtime.

Creating the Azure resources is not sufficient. The identity needs a database
user, created by an Entra admin on the SQL server:

```sql
CREATE USER [acme-id-eng-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [acme-id-eng-dev];
ALTER ROLE db_datawriter ADD MEMBER [acme-id-eng-dev];
ALTER ROLE db_ddladmin  ADD MEMBER [acme-id-eng-dev];
```

Two things to get right:

**The principal is the identity, not the app.** `acme-id-eng-dev`, not
`acme-api-eng-dev`. Under a *system*-assigned identity these names coincide,
which is a common way to get this wrong — a user created for the app name will
never be matched, and the failure looks like a permissions problem rather than
a naming one.

**Restart the app afterwards.** The schema bootstrap runs once at startup, so
on a first deployment it runs *before* the user exists, fails, and logs the
failure without taking the host down (deliberately — see `SchemaInitializer`).
Health then keeps reporting `Invalid object name 'dbo.iTwin'` even after the
grant lands, because nothing has re-run the bootstrap:

```powershell
az functionapp restart -g HilmarRetiefRG -n acme-api-eng-prod
```

CMS prod hit exactly this. The tables were absent, not the permissions.

### Running the grant when sqlcmd will not authenticate

`sqlcmd -G` alone uses integrated auth and fails under MFA with `AADSTS50076`.
Older `sqlcmd` builds have no `--authentication-method` flag. An access token
sidesteps both:

```powershell
$tok = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:acme-sql-server.database.windows.net,1433;Initial Catalog=acme-db-eng-dev;Encrypt=True;"
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
az rest --method post --uri "https://management.azure.com/subscriptions/<sub>/providers/Microsoft.Web/checknameavailability?api-version=2023-12-01" --body '{"name":"acme-api-eng-dev","type":"Microsoft.Web/sites"}'
```

`acme` is a placeholder and is contested. Expect to change it for a real
customer deployment.

## Verifying

`/api/health` is anonymous and reports the iTwin count, which proves the app
reached its database rather than merely started:

```powershell
Invoke-RestMethod https://acme-api-eng-dev.azurewebsites.net/api/health
# { "status": "healthy", "iTwins": 0 }
```

`unhealthy` carries the reason in `detail`. Every other route needs a function
key:

```powershell
$k = az functionapp keys list -g HilmarRetiefRG -n acme-api-eng-dev --query "functionKeys.default" -o tsv
Invoke-RestMethod "https://acme-api-eng-dev.azurewebsites.net/api/classes?code=$k"
```

A fresh database returns **zero** classes, and `/api/health` reports zero
iTwins. The schema creates structure, not content.

To get a usable model, apply [`ENG_BOOTSTRAP.SQL`](../../docs/DDL/ENG_BOOTSTRAP.SQL)
against the database. It is idempotent, so a second run inserts nothing. It
seeds one iTwin, one iModel, the BisCore/Functional/ENG schemas, the EC classes,
and a root element in an open `Initial` draft version. `/api/classes` returning
28 concrete classes confirms it applied.

`ENG.Equipment` must be among them. Elements carry their classification as an
`ECClassId`, so a missing class is not a cosmetic gap: it makes the elements
that reference it unclassifiable, and the properties planned against that class
have nothing to attach to.

This is a **manual** step. The app's startup bootstrap creates the schema only —
structure, not content — so a freshly provisioned ENG database serves an empty
`/api/classes` until this script is run.

The schema ships with this project. `EngProvider.csproj` embeds
[`Infrastructure/Sql/schema.sql`](../Infrastructure/Sql/schema.sql) as a
resource, following the same pattern as `CmsProvider`: the provider owns the
shape it actually creates, and [`docs/DDL/ENG.SQL`](../../docs/DDL/ENG.SQL)
stays reference material. A model change therefore needs applying in both
places — the header of `schema.sql` says which copy runs.

## The routes

All ENG vocabulary. There is no `/segments`, no BOD, and no `/publish`.

There is also no `/tags` route: an element **is** what an engineer calls a tag,
so a separate route would be a second name for one thing.

| Route | Purpose |
|---|---|
| `GET /api/itwins` | The projects ENG holds models for |
| `GET /api/imodels` | Models; filter by `?iTwinId=` |
| `POST /api/imodels` | Mirrors a platform iModel into ENG; idempotent on `iModelId` |
| `GET /api/imodels/{iModelId}` | One iModel |
| `GET /api/classes` | EC classes an element may be created on |
| `GET /api/elements` | Elements; filter by `?iModelId=`, `?namedVersionId=`, `?released=` |
| `GET /api/elements/{ecInstanceId}` | One element |
| `GET /api/imodels/{iModelId}/elements/by-code/{codeValue}` | Element by code, scoped to its iModel |
| `GET /api/named-versions`, `POST /api/named-versions` | Releases; filter by `?iModelId=` |
| `POST /api/named-versions/{id}/elements` | Create/update elements in a draft version |
| `POST /api/named-versions/{id}/release` | The release gate |
| `GET /api/named-versions/{id}/findings` | Open findings; `?all=true` for resolved too |
| `POST /api/named-versions/{id}/findings` | Raise a finding |
| `POST /api/findings/{findingId}/resolve` | Close one |
| `GET /api/health` | Anonymous |

### How the release model works

Elements are created **into** a draft named version — the version is in the
route, not in each item, so a batch cannot span versions and half-succeed.
Maturity is not stored per element; it is read from the version, which is why
there is no `maturity` field to set and no way to assert one.

`POST /api/named-versions/{id}/release` returns **409** with its findings when
the gate refuses, not a 200 carrying `released: false` that a caller might not
read. The version is unchanged, so resolving the findings and retrying is the
natural next step.

Two rules are enforced by database triggers rather than by this app, so they
hold no matter what connects — including an ad-hoc `UPDATE` from a management
tool:

- A version cannot be released while **any** finding is open, whatever its
  severity, and cannot be released empty.
- A released version is immutable. Its elements cannot be added to, changed, or
  moved out. New work belongs in a new draft version.

The API reports these refusals; it does not re-implement them. A second copy of
the rule in application code could disagree with the one actually in force.

## Before this is a real production deployment

- **Drop `db_ddladmin`.** It is granted only because
  `Eng__AutoCreateSchema=true` applies the embedded
  `Infrastructure/Sql/schema.sql` at
  startup. Once the schema is settled, set that to `false` and remove the role —
  a running app has no business altering its own schema.
- **Reconsider function keys.** They are adequate for a demo and are not an
  authorization model.
- **`acme-db-eng-prod` shares a server with the sandbox databases.** Acceptable
  for a demo; a customer system would not sit on the integrator's SQL server.
