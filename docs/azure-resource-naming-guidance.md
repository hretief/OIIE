# Azure resource naming guidance

Master naming standard for this solution. The convention separates account,
resource type, system, and environment so additional customers can be onboarded
without changing the standard itself.

Where `deploy/sandbox/NAMING.md` disagrees with this document, this document
wins. That file records sandbox-specific detail and any conflicts still to be
resolved.

## General pattern

```
<account>-<resource-type>-<system>-<environment>
```

| Segment | Meaning | Example |
|---|---|---|
| `account` | Customer / account identifier | `acme` |
| `resource-type` | Azure resource role | `db`, `api` |
| `system` | Business system identifier | `cms`, `cir` |
| `environment` | Deployment environment | `dev`, `prod` |

Storage accounts are the documented exception; see [Storage](#storage).

### Resource type codes

| Code | Resource |
|---|---|
| `db` | Azure SQL database |
| `engn` | Integration engine (Azure Function App) |
| `api` | LOB application API |
| `sb` | Service Bus namespace |

### System codes

| Code | System |
|---|---|
| `cms` | Condition Monitoring System |
| `eng` | Engineering |
| `mms` | Maintenance Management System |
| `cir` | Common Interoperability Registry |
| `isbm` | ws-ISBM message broker |
| `reg-asset` | Asset Register |
| `reg-location` | Functional Location Register |

System codes may contain hyphens (`reg-asset`), so names have a variable number
of segments. Parse from the left, never by splitting on `-` and counting.

## Azure SQL databases

```
<account>-db-<system>-<environment>
```

| Examples |
|---|
| `acme-db-cms-dev` |
| `acme-db-cms-prod` |
| `acme-db-cir-dev` |
| `acme-db-cir-prod` |
| `acme-db-mms-prod` |

**ISBM has no database.** All ISBM state lives in Azure Storage; see
[ISBM storage](#isbm-storage).

The logical server is `acme-sql-server`, shared by all databases in all
environments. It predates this convention, and **Azure SQL servers cannot be
renamed** — aligning it would mean a new server plus migration of every database
on it. Treat as grandfathered.

## Integration engines

```
<account>-engn-<system>-<environment>
```

| Examples |
|---|
| `acme-engn-cms-dev` |
| `acme-engn-cms-prod` |

The integration layer between the solution and the target LOB system. Holds the
ISBM session, parses BODs, and calls the LOB API over its published interface.

## LOB application APIs

```
<account>-api-<system>-<environment>
```

| Examples |
|---|
| `acme-api-cms-dev` |
| `acme-api-cms-prod` |
| `acme-api-cir-dev` |
| `acme-api-cir-prod` |
| `acme-api-isbm-dev` |
| `acme-api-isbm-prod` |
| `acme-api-reglocation-dev` |

The API tier representing the business application itself.

`reglocation` is deliberately unhyphenated even though the participant is
written `REG-LOCATION` everywhere else. The system code is a single segment of
`<account>-<kind>-<system>-<environment>`, so an internal hyphen would make
`acme-api-reg-location-dev` parse as a different system in every script that
splits on `-`.

`engn` and `api` are both Function Apps. They are kept separate because the
distinction is a boundary, not a hosting detail: an engine may use only access a
third party could be granted, whereas the API owns its system's data directly.
One shared code would make that boundary invisible in the resource list.

**All of these names are globally unique** — each becomes
`<name>.azurewebsites.net`. Verify availability before committing a name to
source. `acme` is a common placeholder and may already be taken.

## Service Bus

```
<account>-sb-<environment>
```

| Examples |
|---|
| `acme-sb-dev` |
| `acme-sb-prod` |

Shared messaging infrastructure for all systems within an environment. Globally
unique.

## Supporting resources

These are per-system rather than shared, and follow the same
`<account>-<kind>-<system>-<environment>` shape as the tiers above.

```
<account>-plan-<system>-<environment>
<account>-id-<system>-<environment>
<account>-kv-<system>-<environment>
```

| Kind | Resource | Examples |
|---|---|---|
| `plan` | App Service plan | `acme-plan-cir-dev`, `acme-plan-isbm-prod` |
| `id` | User-assigned identity | `acme-id-cir-dev`, `acme-id-cms-prod` |
| `kv` | Key Vault | `acme-kv-isbm-dev`, `acme-kv-isbm-prod` |

A user-assigned identity is preferred over a system-assigned one wherever the
app authenticates to SQL: the database user is keyed on the principal, so a
system-assigned identity orphans that user if the app is ever recreated.

Key Vault names are globally unique and capped at 24 characters, which the
longer system names can exceed.

## Storage

### Storage account

Storage account names are lowercase, 3–24 characters, alphanumeric only, and
globally unique. Hyphens are not permitted, so the general pattern cannot apply:

```
<account>storage<environment><nn>
```

| Examples |
|---|
| `acmestoragedev01` |
| `acmestorageprod01` |

The two-digit suffix is not decoration. Storage account names are globally
unique across all of Azure and the unsuffixed `acmestoragedev` is already taken
by another tenant, so the pattern needs somewhere to move without abandoning
the convention. Start at `01`.

There is no Azure resource between the storage account and an individual table
or container. A per-system storage resource name would name nothing that can be
provisioned, so systems are separated by table and container naming below, or by
a dedicated storage account where stronger isolation is required.

### Table names

Alphanumeric only (no hyphens), 3–63 characters, cannot begin with a digit.
`PascalCase`, prefixed with the owning system:

```
<System><Entity>
```

| Examples |
|---|
| `IsbmChannels` |
| `CmsSessions` |

### Blob container names

Lowercase, 3–63 characters, hyphens permitted, must start and end
alphanumerically:

```
<system>-<purpose>
```

| Examples |
|---|
| `isbm-payloads` |
| `cms-payloads` |

Neither table nor container names carry the account or environment. Both are
already implied by the storage account, and repeating them means a query or path
cannot move between environments unchanged.

### ISBM storage

ISBM keeps all of its state in Azure Storage. These names are already in use:

| Name | Kind | Holds |
|---|---|---|
| `IsbmChannels` | Table | Channel definitions |
| `IsbmTokens` | Table | Channel security tokens |
| `IsbmSessions` | Table | Open publication / subscription sessions |
| `IsbmCorrelations` | Table | Request-response correlation |
| `isbm-payloads` | Blob container | Message payload bodies |

## Example: CMS deployment

| Role | Dev | Prod |
|---|---|---|
| SQL database | `acme-db-cms-dev` | `acme-db-cms-prod` |
| Integration engine | `acme-engn-cms-dev` | `acme-engn-cms-prod` |
| LOB API | `acme-api-cms-dev` | `acme-api-cms-prod` |
| Service Bus *(shared)* | `acme-sb-dev` | `acme-sb-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

Service Bus and storage are shared across all systems in an environment, so they
carry no system code. The engine is `CmsEngine`; the LOB API is `CmsProvider`,
which emulates the Meridium product itself.

## Example: CIR and ISBM deployment

| Role | Dev | Prod |
|---|---|---|
| CIR API | `acme-api-cir-dev` | `acme-api-cir-prod` |
| CIR database | `acme-db-cir-dev` | `acme-db-cir-prod` |
| CIR plan / identity | `acme-plan-cir-dev` / `acme-id-cir-dev` | `acme-plan-cir-prod` / `acme-id-cir-prod` |
| ISBM API | `acme-api-isbm-dev` | `acme-api-isbm-prod` |
| ISBM plan / Key Vault | `acme-plan-isbm-dev` / `acme-kv-isbm-dev` | `acme-plan-isbm-prod` / `acme-kv-isbm-prod` |
| Service Bus *(shared)* | `acme-sb-dev` | `acme-sb-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

## Example: REG-LOCATION deployment

| Role | Dev | Prod |
|---|---|---|
| LOB API | `acme-api-reglocation-dev` | `acme-api-reglocation-prod` |
| SQL database | `acme-db-reglocation-dev` | `acme-db-reglocation-prod` |
| Plan / identity | `acme-plan-reglocation-dev` / `acme-id-reglocation-dev` | `acme-plan-reglocation-prod` / `acme-id-reglocation-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

Only dev exists today. The prod names are reserved by the convention rather
than provisioned.

These were deployed alongside the original `cir-func-44p2f3n6` and
`isbm-func-44p2f3n6dv7p4` apps, which predate this convention and are
grandfathered until cutover.

## Globally unique names

These must be unique across all of Azure, not merely within the subscription.
Check availability before use.

- Function App / App Service
- Storage account
- Service Bus namespace
- SQL logical server
