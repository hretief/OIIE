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
| `acme-db-mms-dev` |
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
| `acme-api-mms-dev` |
| `acme-api-mms-prod` |
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

Identities and Key Vaults are per-system, and follow the same
`<account>-<kind>-<system>-<environment>` shape as the tiers above.

```
<account>-id-<system>-<environment>
<account>-kv-<system>-<environment>
```

| Kind | Resource | Examples |
|---|---|---|
| `id` | User-assigned identity | `acme-id-cir-dev`, `acme-id-cms-prod` |
| `kv` | Key Vault | `acme-kv-isbm-dev`, `acme-kv-isbm-prod` |

### App Service plans are shared

```
<account>-plan-<environment>
```

| Examples |
|---|
| `acme-plan-dev` |
| `acme-plan-prod` |

**One plan per environment, not one per system**, so the name carries no system
code — the same reasoning as `acme-sb-<env>` and `acmestorage<env>01`.

This was originally a plan per provider, which meant six B1 plans each billing
continuously to host a single app. A plan is a billing and compute boundary,
not an isolation boundary: the apps on it stay independently deployable,
separately keyed, and individually restartable. Splitting them bought nothing
and cost roughly five times what it needed to.

The tradeoff is real and worth stating. Everything on the plan shares its CPU
and memory, so one app under load affects the others, and the Basic tier has no
autoscale. That is acceptable for a demo environment and would not be for
production — the answer there is a larger SKU, not a plan per system.

`acme-plan-dev` runs **B3**. It began at B1, which was adequate for six
providers but not for ten AlwaysOn sites once the four `engn` apps joined:
`func azure functionapp publish` began failing with "Timed out waiting for SCM
to update the Environment Settings", which reads like a broken SCM policy and
is actually CPU starvation on the shared worker. If that error reappears after
adding apps, check the plan before debugging the deployment.

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
| Plan *(shared)* | `acme-plan-dev` | `acme-plan-prod` |
| Service Bus *(shared)* | `acme-sb-dev` | `acme-sb-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

Service Bus and storage are shared across all systems in an environment, so they
carry no system code. The engine is `CmsEngine`; the LOB API is `CmsProvider`,
which emulates the Meridium product itself.

## Example: MMS deployment

| Role | Dev | Prod |
|---|---|---|
| LOB API | `acme-api-mms-dev` | `acme-api-mms-prod` |
| Integration engine | `acme-engn-mms-dev` | `acme-engn-mms-prod` |
| SQL database | `acme-db-mms-dev` | `acme-db-mms-prod` |
| Plan *(shared)* | `acme-plan-dev` | `acme-plan-prod` |
| Identity | `acme-id-mms-dev` | `acme-id-mms-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

**This previously read "there is no `acme-engn-mms-*`".** That was accurate when
MMS held no ISBM session and was reached only by direct publication. MMS now
subscribes to `SyncSites` in its own right, and the code that does it —
`MmsEngine` — holds a session, parses BODs, and calls `MmsProvider` over its
published REST API. That is a participant by the §5.2 definition, so it gets an
`engn` resource like any other.

The asymmetry described in
[participant-abstraction-spec.md §5.2](participant-abstraction-spec.md) is still
real for `CmsProvider` versus `CmsEngine` — a customer system versus the vendor
artefact that fronts it. What changed is that MMS is no longer the counter-example
to it. Dev is deployed; prod is not.

## Example: CIR and ISBM deployment

| Role | Dev | Prod |
|---|---|---|
| CIR API | `acme-api-cir-dev` | `acme-api-cir-prod` |
| CIR database | `acme-db-cir-dev` | `acme-db-cir-prod` |
| CIR identity | `acme-id-cir-dev` | `acme-id-cir-prod` |
| ISBM API | `acme-api-isbm-dev` | `acme-api-isbm-prod` |
| ISBM Key Vault | `acme-kv-isbm-dev` | `acme-kv-isbm-prod` |
| Plan *(shared)* | `acme-plan-dev` | `acme-plan-prod` |
| Service Bus *(shared)* | `acme-sb-dev` | `acme-sb-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

## Example: REG-LOCATION deployment

| Role | Dev | Prod |
|---|---|---|
| LOB API | `acme-api-reglocation-dev` | `acme-api-reglocation-prod` |
| Integration engine | `acme-engn-reglocation-dev` | `acme-engn-reglocation-prod` |
| SQL database | `acme-db-reglocation-dev` | `acme-db-reglocation-prod` |
| Plan *(shared)* | `acme-plan-dev` | `acme-plan-prod` |
| Identity | `acme-id-reglocation-dev` | `acme-id-reglocation-prod` |
| Storage account *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

Only dev exists today. The prod names are reserved by the convention rather
than provisioned.

No `acme-engn-*` app is provisioned yet for any system; the four engine
projects (`EngEngine`, `RegLocationEngine`, `CmsEngine`, `MmsEngine`) exist in
source but have no cloud host. `deploy/engines/deploy-engine.ps1` creates them.
Engines get no database and no SQL identity — an engine that needs either has
broken the §5.2 boundary it exists to demonstrate.

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
