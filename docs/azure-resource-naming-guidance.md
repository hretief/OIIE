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

The API tier representing the business application itself.

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

## Storage

### Storage account

Storage account names are lowercase, 3–24 characters, alphanumeric only, and
globally unique. Hyphens are not permitted, so the general pattern cannot apply:

```
<account>storage<environment>
```

| Examples |
|---|
| `acmestoragedev` |
| `acmestorageprod` |

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
| Storage account *(shared)* | `acmestoragedev` | `acmestorageprod` |

Service Bus and storage are shared across all systems in an environment, so they
carry no system code. The engine is `CmsEngine`; the LOB API is `CmsProvider`,
which emulates the Meridium product itself.

## Globally unique names

These must be unique across all of Azure, not merely within the subscription.
Check availability before use.

- Function App / App Service
- Storage account
- Service Bus namespace
- SQL logical server
