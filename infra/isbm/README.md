# ISBM Provider — Azure Deployment (Bicep)

## Overview

Single Bicep template that deploys the complete ISBM Service Provider infrastructure.
Supports **greenfield** (create everything), **brownfield** (reuse existing resources),
and any mix of the two. SQL Server is **fully optional** (skipped by default — channel
persistence uses Azure Table Storage on the existing storage account).

## What it provisions

| Resource | Purpose | Created when |
|---|---|---|
| **Function App** (.NET 10 isolated) | ISBM Service Provider | Always |
| **App Service Plan** (`B1` default) | Hosting (Windows default) | Always — **see *Plan choice in production*** |
| **Log Analytics** + **App Insights** | Tracing, OriginalMessageID correlation | `existingAppInsightsName` empty |
| **Storage Account** | Functions host + Table Storage + Blob claim-check | `existingStorageAccountName` empty |
| **Service Bus** namespace | Pub-sub topics + request queues | `existingServiceBusName` empty |
| **Key Vault** | Encrypted security token storage | `existingKeyVaultName` empty |
| **SQL Server** (optional) | Only if `skipSql=false` | `skipSql=false` AND `existingSqlServerName` empty |

### Auto-created Service Bus entities

Created on whichever namespace is active (new or existing):
- Topic `isbm-notifications` + subscription `dispatch`
- Queue `isbm-expired`

### Auto-created Table Storage tables

Created on first access by the application (no deployment step needed):
- `IsbmChannels` — channel registry
- `IsbmTokens` — security token assignments
- `IsbmSessions` — active session metadata
- `IsbmCorrelations` — request/response correlation

## RBAC assignments (automatic)

The Function App's system-assigned managed identity gets these roles:

| Resource | Role | Purpose |
|---|---|---|
| Storage Account | Storage Blob Data Contributor | Claim-check read/write |
| Key Vault | Key Vault Secrets Officer | Token store/read/delete |
| Service Bus | Azure Service Bus Data Owner | Send/receive/manage |

No connection-string secrets needed for data-plane operations.

## Deploying

### Using the deployment script (recommended)

The `deploy/deploy.ps1` script handles infrastructure, build, publish, and verification:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass

# Greenfield
.\deploy\deploy.ps1 -ResourceGroup rg-isbm -Location eastus2

# Brownfield (auto-fetches Service Bus connection string)
.\deploy\deploy.ps1 -ResourceGroup hilmarretiefrg `
  -ExistingServiceBusName mndotdev `
  -ExistingStorageAccountName mndot `
  -ExistingKeyVaultName mndot

# Code-only republish
.\deploy\deploy.ps1 -ResourceGroup hilmarretiefrg `
  -FunctionAppName isbm-func-44p2f3n6dv7p4 -SkipInfra
```

### Using az CLI directly

#### Prerequisites

- Azure CLI (`az`) logged in with sufficient permissions
- Bicep CLI (bundled with recent Azure CLI versions)
- Azure Functions Core Tools v4 (for `func publish`)

### Greenfield (everything new)

Creates all resources from scratch. No parameters required beyond optional password
(only needed if `skipSql=false`).

```powershell
# PowerShell
az group create --name rg-isbm --location eastus2

az deployment group create `
  --resource-group rg-isbm `
  --template-file infra/main.bicep

func azure functionapp publish <functionAppName-from-output>
```

```bash
# Bash
az group create -n rg-isbm -l eastus2
az deployment group create -g rg-isbm -f infra/main.bicep
func azure functionapp publish <functionAppName-from-output>
```

### Brownfield (reuse existing resources)

Pass the names of resources you already have. Any you leave empty get created new.

```powershell
az deployment group create `
  --resource-group hilmarretiefrg `
  --template-file infra/main.bicep `
  --parameters `
    existingServiceBusName='mndotdev' `
    existingStorageAccountName='mndot' `
    existingKeyVaultName='mndot'
```

### Mix-and-match

Reuse Service Bus and Key Vault, create new Storage:

```powershell
az deployment group create `
  --resource-group rg-isbm `
  --template-file infra/main.bicep `
  --parameters `
    existingServiceBusName='mndotdev' `
    existingKeyVaultName='mndot'
```

The `mode` output tells you what was created vs. reused:
```
existing SB | new Storage | skipped SQL | existing KV | new AI
```

### With SQL Server (optional)

If you need SQL for other purposes, pass `skipSql=false`:

```powershell
az deployment group create `
  --resource-group rg-isbm `
  --template-file infra/main.bicep `
  --parameters `
    skipSql=false `
    sqlAdminPassword='YourStr0ng!Pass'
```

## Publishing the Function App

After infrastructure deployment succeeds, publish the code from your project directory:

```powershell
cd D:\Working\ISBM\ISBMProvider
func azure functionapp publish <functionAppName-from-output>
```

The function app name is in the deployment output (e.g., `isbm-func-44p2f3n6dv7p4`).

## Post-deploy verification

```powershell
# Quick smoke test
curl https://<appName>.azurewebsites.net/api/configuration/supported-operations

# End-to-end tests against the deployed instance
.\Testing\test-isbm.ps1 -BaseUrl "https://<appName>.azurewebsites.net/api"

# ISBM 2.1 conformance suite (53 tests across 18 conformance items)
.\Testing\conformance-tests.ps1 -BaseUrl "https://<appName>.azurewebsites.net/api"

# With notification callback verification
.\Testing\conformance-tests.ps1 `
  -BaseUrl "https://<appName>.azurewebsites.net/api" `
  -ListenerUrl "https://webhook.site/your-id"
```

## Troubleshooting

### "LinuxDynamicWorkersNotAllowedInResourceGroup"

Your resource group has Windows-based resources. The template defaults to
`functionAppOs='windows'` which avoids this. If you explicitly set `linux`,
use a fresh resource group.

### 500 errors after deploy

Check Application Insights for the actual exception:

```powershell
az monitor app-insights query `
  --app <appInsightsName> `
  --resource-group <resourceGroup> `
  --analytics-query "exceptions | top 5 by timestamp desc | project timestamp, outerMessage, innermostMessage"
```

Common causes:
- **Storage account not accessible** — verify the connection string in app settings
- **Key Vault access denied** — verify the managed identity has "Key Vault Secrets Officer" role
- **Service Bus connection** — verify the connection string is correct and the namespace exists

### Notification trigger errors (MessagingEntityNotFound)

The `isbm-notifications` topic or `isbm-expired` queue doesn't exist on the Service Bus
namespace. The Bicep creates them, but if you deployed before this version, run:

```powershell
az servicebus topic create --namespace-name <ns> --resource-group <rg> --name isbm-notifications
az servicebus topic subscription create --namespace-name <ns> --resource-group <rg> `
  --topic-name isbm-notifications --name dispatch
az servicebus queue create --namespace-name <ns> --resource-group <rg> --name isbm-expired
```

Or re-run the Bicep deployment (it's idempotent).

## Parameters reference

| Parameter | Default | Required | Description |
|---|---|---|---|
| `baseName` | `isbm` | No | Prefix for new resource names |
| `location` | RG location | No | Azure region |
| `sqlAdminUser` | `isbmadmin` | No | SQL admin (only if `skipSql=false`) |
| `sqlAdminPassword` | `""` | Only if `skipSql=false` | SQL admin password |
| `sqlSku` | `Basic` | No | SQL Database tier |
| `serviceBusSku` | `Standard` | No | Service Bus tier |
| `securityLevel` | `3` | No | ISBM conformance level (2/3/4) |
| `functionAppOs` | `windows` | No | `windows` or `linux` |
| `planSku` | `B1` | No | App Service plan SKU: `Y1`/`B1`/`B2`/`EP1`. See *Plan choice in production* |
| `alwaysOn` | `true` | No | Keep the host resident. Forced `false` on `Y1` |
| `skipSql` | `true` | No | Skip SQL Server entirely |
| `existingServiceBusName` | `""` | No | Reuse existing SB namespace |
| `existingServiceBusConnectionString` | — | Removed | The Function App uses its managed identity; no key is needed |
| `existingStorageAccountName` | `""` | No | Reuse existing Storage |
| `existingSqlServerName` | `""` | No | Reuse existing SQL Server |
| `existingSqlDatabaseName` | `IsbmProvider` | No | DB name on existing server |
| `existingKeyVaultName` | `""` | No | Reuse existing Key Vault |
| `existingAppInsightsName` | `""` | No | Reuse existing App Insights |

## Plan choice in production

**This is a correctness decision, not a cost one.** The provider does real work
outside the request path, and that work only happens while the host is resident:

- `NotifyOnMessage` / `NotifyOnExpiry` in `ISBMProvider/Functions/NotificationDispatchFunctions.cs`
  are **Service Bus triggered**. They deliver notification callbacks and expiry
  events. A cold host defers them; a host that never warms never runs them.
- Consumers of this provider poll it. Anything timer-driven on their side (the
  ws-CIR provider's `IsbmPoll`, for example) compounds the latency.

The template therefore defaults to **`planSku: 'B1'` with `alwaysOn: true`**:

| `planSku` | Always On | Behaviour |
|---|---|---|
| `B1` / `B2` (default) | `true` | Host resident, background work runs on time. Correct for production. |
| `Y1` (Consumption) | forced `false` by the template | Notifications and expiry only run while the host happens to be warm. Evaluation only. |
| `EP1` (Elastic Premium) | `true` | Also correct; use when VNet integration or larger scale-out is needed. |

`alwaysOn` is forced to `false` on `Y1` because Azure rejects the combination.
The tier is derived from the SKU (`Y1`→Dynamic, `EP*`→ElasticPremium, otherwise
Basic), so only `planSku` needs setting:

```powershell
.\deploy\deploy.ps1 -ResourceGroup <rg> -PlanSku B1        # default
.\deploy\deploy.ps1 -ResourceGroup <rg> -PlanSku Y1        # evaluation only
```

Do not set the plan by hand with `az appservice plan update`. Bicep is
declarative, so the next deployment reverts it — pass `-PlanSku` instead.

Symptoms of getting this wrong are indirect and point nowhere useful:
notifications arrive minutes late or not at all, and consumers report request
timeouts while every ISBM HTTP call returns 200.

## Upgrade paths

| From | To | When |
|---|---|---|
| Basic (B1) | **Flex Consumption** or **Premium** | VNet integration for Level 3 — set `planSku` |
| Service Bus Standard | **Premium** | VNet, larger messages, dedicated capacity |
| Key Vault Standard | **Managed HSM** | Level 4 per-message envelope encryption |
| Windows Function App | **Linux** | Set `functionAppOs='linux'` in a Linux-compatible RG |
| Table Storage | **Cosmos DB** | Global distribution, single-digit-ms at any scale |
