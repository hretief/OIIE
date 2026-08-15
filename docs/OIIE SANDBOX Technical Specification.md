# OIIE Sandbox — Technical Specification

**Status:** Draft for review
**Version:** 0.6
**Date:** 2026-08-01
**Changes in 0.6:** Added the repository browser (§8.9) — per-participant schema inspection on the participants page, reading as each participant's own contained SQL user so §6.2 isolation is observable rather than merely asserted.
**Changes in 0.5:** Recorded the UI technology decision (§8.0) — Blazor Server with one JavaScript island for the control tower visualisations (§8.7).
**Changes in 0.4:** Clarified the development inner loop (§6.1) — `SimHost` compiles and runs on the workstation against remote Azure services with no deployment step and no storage emulator; Dev Tunnels for notification callbacks; `DefaultAzureCredential` for local sign-in.
**Changes in 0.3:** Azure-only footprint (§12.4) — no containers, emulators, or local infrastructure in any environment; per-developer Azure SQL databases (§6.1); App Service hosting; notification testing moved to phase 2 as a consequence.
**Changes in 0.2:** Added the property and classification model (§6.5) and its consequences — RDL participant promoted to phase 2, ENG validation gate made concrete (§7.2), class-driven property panels (§8.2), registry-vs-repository distinction sharpened (§9.2), new assertions (§11.2) and scenarios (§11.4).
**Owner:** Hilmar Retief, Solution Engineering, Bentley Systems
**Related systems:** ws-ISBM 2.1 Service Provider, OpenO&M ws-CIR 1.0 Provider, OpenO&M Service Directory 1.0

---

## 1. Purpose and scope

### 1.1 Problem statement

The ws-ISBM and ws-CIR providers are individually conformance-tested and integration-tested against each other. What cannot currently be demonstrated or regression-tested is the thing they exist to enable: **multiple independent business applications exchanging information over the bus, resolving each other's identifiers through the registry, without prior bilateral integration.**

Demonstrating that today would require real instances of an engineering system, a construction management system, an asset registry, a product data library, a material management system, and a maintenance management system — with real data, real licences, and real integration work. That is not available, and would not be reproducible even if it were.

### 1.2 What this tool is

The OIIE Sandbox is a **multi-participant simulator**: a single runtime that hosts several configured "participants," each of which behaves like a distinct OIIE system landscape block. Each participant has its own data store, its own domain vocabulary, its own identifier conventions, its own user interface, and its own ISBM channel bindings. Participants know nothing about each other except through the bus and the registry.

It serves three purposes, in priority order:

1. **Ecosystem regression testing.** Automated, assertable, multi-hop choreographies that exercise ISBM and ws-CIR the way real participants would, run in CI.
2. **Demonstration.** A visual, live, human-drivable illustration of OIIE interoperability for internal and customer audiences.
3. **Development harness.** A realistic client population for exercising the ISBM provider, the CIR provider, and (next) the Service Directory, including paths that are hard to reach from a test script — notifications, session recovery, faults, stale caches.

### 1.3 Non-goals

- **Not a product.** Participants are placeholders. They must be visibly marked as simulators and must not accrete domain features beyond what a scenario requires.
- **Not a replacement for conformance testing.** The ISBM Section 9 suite and the CIR unit/integration suites remain the authority on provider conformance. The Sandbox tests the *ecosystem*, not the provider.
- **Not a data migration or ETL tool.**
- **Not a general-purpose BOD authoring environment.**

### 1.4 Success criteria

| # | Criterion |
|---|---|
| SC-1 | A full multi-participant choreography runs headless in CI, asserts message delivery, payload validity, and resulting state at each participant, and returns a non-zero exit code on failure. |
| SC-2 | A non-technical observer, watching the UI for five minutes with narration, can articulate what the CIR does and why it is required. |
| SC-3 | Adding a new participant requires a configuration folder and a mapper, with no changes to the runtime. |
| SC-4 | A demo environment resets to a known baseline — including purged ISBM channels — in a single command in under 60 seconds. |
| SC-5 | A single correlation identifier reconstructs an end-to-end exchange across all participants plus the ISBM and CIR providers from Application Insights. |
| SC-6 | Failure paths — rejected BODs, ISBM faults, stale identity mappings, validation gate rejections — are demonstrable, not merely handled. |

---

## 2. Terminology

| Term | Meaning in this document |
|---|---|
| **Participant** | A configured instance of the runtime that impersonates one OIIE system landscape block (e.g. REG-ASSET). |
| **Personality** | The configuration, domain schema, mappers, seed fixtures, and UI screens that make the runtime behave as a specific participant. |
| **Choreography** / **Scenario** | An ordered, assertable sequence of steps across multiple participants, corresponding to one or more OIIE Scenarios. |
| **Release event** | The domain-meaningful act that causes a participant to publish. Differs per participant by design. |
| **Container object** | The domain object whose lifecycle transition constitutes a release event (named version, work package, ECN, requisition). |
| **Domain state** | A participant's own system-of-record data. |
| **Provenance** | The recorded link between a domain change and the message that caused or resulted from it. |
| **Identity map** | A participant's local cache of CIRID resolutions for foreign identifiers. |
| **Class** | A definition that entities are classified against, carrying a property set. Taxonomy classes are single and inherited; aspect classes are multiple and orthogonal. |
| **Property definition** | A governed or local definition of an attribute — name, data type, unit, code list — independent of any entity that carries a value for it. |
| **Effective property set** | The union of property definitions an entity inherits from its taxonomy chain plus its aspect classes, after subclass narrowing. |
| **Unmapped property** | A property value received over the bus for which the receiver holds no definition. Retained and flagged, never discarded. |
| **RDL** | Reference Data Library. In this tool, the MIMOSA RDL, impersonated by a dedicated participant. |
| **Control tower** | The cross-participant observability UI. Outside all participant boundaries. |

---

## 3. Architecture

### 3.1 Principle: one runtime, many personalities

There is exactly one executable, `SimHost`. It is a .NET 10 ASP.NET Core application hosting Blazor Server UI, a set of background services, and a headless scenario runner mode. At startup it loads one or more personality packs and instantiates a participant context per pack.

A participant is therefore composed of:

- **Identity** — participant ID, display name, SourceID, SourceOwnerID, colour/branding
- **Domain schema** — EF Core model + migration set, specific to this personality
- **Mappers** — domain entity ↔ BOD, in both directions
- **Handlers** — BOD noun/verb → domain action
- **Release policy** — the container object and lifecycle that triggers publication
- **Channel bindings** — ISBM endpoints, channels, topics, tokens (static config or Service-Directory-resolved)
- **Seed fixtures** — baseline data, including deliberate defects
- **UI screens** — Razor components for this personality's domain views

Everything else — ISBM session management, outbox dispatch, BOD validation, CIR registration and resolution, message archive, provenance ledger, scenario execution, observability — is runtime, shared and written once.

### 3.2 Deployment topology

```
                     ┌───────────────────────────────────────┐
                     │  SimHost (Azure App Service, .NET 10)  │
                     │                                        │
                     │  ┌─────────┐ ┌──────────┐ ┌─────────┐  │
                     │  │   ENG   │ │REG-LOCATN│ │   ...   │  │
                     │  │participnt│ │participnt│ │         │  │
                     │  └────┬────┘ └─────┬────┘ └────┬────┘  │
                     │       │            │           │       │
                     │  ┌────┴────────────┴───────────┴────┐  │
                     │  │   Shared runtime services        │  │
                     │  │  outbox · inbox · BOD · CIR ·    │  │
                     │  │  classification · sessions ·     │  │
                     │  │  scenarios · telemetry           │  │
                     │  └────┬────────────────────┬────────┘  │
                     └───────┼────────────────────┼───────────┘
                             │                    │
              ┌──────────────┴────┐      ┌────────┴──────────┐
              │  ws-ISBM Provider │      │  ws-CIR Provider  │
              │  (Azure Functions)│◄─────┤  (Azure Functions)│
              └──────────┬────────┘      └────────┬──────────┘
                         │                        │
        ┌────────────────┴────────────────────────┴────────────────┐
        │  Azure SQL (acme-sql-server) · Blob Storage ·            │
        │  Key Vault (mndot) · Application Insights · Entra ID     │
        └──────────────────────────────────────────────────────────┘
```

Single process, multiple participants. Rationale: cost, provisioning simplicity, and the ability to debug the entire ecosystem in one Visual Studio session — the process runs on the workstation, but every backing service it touches is Azure (§6.1). Isolation between participants is enforced at the **database schema and grant** level rather than the process level (§6.2), which is the boundary that actually matters for correctness.

Participants are addressed by URL path: `/p/eng`, `/p/reg-asset`, etc. The control tower is at `/tower`, the CIR explorer at `/cir`, the scenario runner at `/scenarios`.

### 3.3 Repository layout

Root `Sandbox/`, sibling to `CIR/`, flattened in the same convention (no `src/` level).

```
Sandbox/
  SimHost/                          ASP.NET Core / Blazor Server host
    Application/
      Participants/                 participant context, registry, lifecycle
      Outbox/                       dispatcher, retry, pause control
      Inbox/                        ISBM poll/notify loop, dedup, dispatch
      Bods/                         dispatcher, validator, generic renderer
      Cir/                          registration, resolution, identity cache
      Scenarios/                    engine, step executors, assertions
      Classification/               class chain resolution, effective property
                                    set, narrowing rules, conformance check
      Reset/                        seed, reset, snapshot/restore
    Domain/
      Common/                       Message, Provenance, Outbox, IdentityMap,
                                    PendingWork, ContainerObject,
                                    ClassDefinition, ClassProperty,
                                    PropertyDefinition, EntityProperty,
                                    EntityClassification base types
    Infrastructure/
      Sql/                          EF Core contexts, migrations, schema factory
      Blob/                         payload store
      Isbm/                         thin wrapper over Oiie.Isbm.Client
      Telemetry/                    correlation, App Insights enrichment
    Components/
      Shared/                       identity chip, BOD viewer, inbox, outbox
      Tower/                        swimlane, topology, cluster graph
      Cir/                          CIR explorer
      Personalities/                per-personality Razor screens
    Program.cs
  Oiie.Isbm.Client/                 extracted from CIR — see §4
  Oiie.Bod/                         BOD read/write/validate primitives
  Personalities/
    eng/
      personality.yaml
      Domain/                       EF entities + migration for eng schema
      Mappers/
      Handlers/
      Fixtures/
      Screens/
    construct/  reg-location/  reg-asset/  reg-product/  reg-material/  mms/
    cms/
    rdl/                            MIMOSA RDL — class and property definitions
  Scenarios/
    sc01-design-release.yaml
    sc02-operations-release.yaml
    sc01-greenfield-allocation.yaml
    sc11-asset-install.yaml
    identity-merge.yaml
    service-directory-bootstrap.yaml
    negative-paths.yaml
  Schemas/
    ccom/                           CCOM BOD XSDs (see §12.4 — open dependency)
    cir/                            ws-CIR BOD XSDs (from project package)
    oagis/                          Meta.xsd etc.
  Testing/
    Ecosystem.Tests/                xUnit — wraps headless scenario runs
    SimHost.Tests/                  runtime unit tests
    Mappers.Tests/                  per-personality mapper round-trip tests
  deploy/
    provision.ps1
  infra/
    main.bicep
  docs/
    architecture.md
    personality-authoring.md
    scenario-authoring.md
    sequence-*.puml
```

---

## 4. Prerequisite: shared ISBM client

**Phase 0 work, blocking everything else.**

Extract from `CIR/CirProvider/Infrastructure/` into `Sandbox/Oiie.Isbm.Client/` (or a shared location above both, if preferred):

- `IsbmRestClient` — all 26 REST operations
- `SessionHelper.OpenAndConfirmAsync` — the Durable Entity eventual-consistency workaround
- Session store abstraction (`IIsbmSessionStore`), with the existing SQL implementation

Requirements:

- **No new HTTP code may be written for the Sandbox.** Every ISBM call goes through this library. Rationale: the wire shapes have cost significant debugging time already (`channelUri` not `uri`; `mediaType` not `contentType`; `inlineContent` not `content`; response posted to `sessions/{id}/requests/{requestMessageId}/response`; absent `filterExpressions` causing a `NullReferenceException`). Reimplementing them in a simulator would reproduce every one of those bugs in a new place.
- The library must be consumed by the CIR provider afterwards, so there is exactly one implementation. This is a refactor of working code, not a fork.
- Multi-tenant capable: one client instance per participant, each with its own credentials and session store partition.
- Both polling and `NotifyListener` push modes, selectable per participant per channel.

Acceptance: the existing CIR 15/15 integration assertions and 104 unit tests pass unchanged after the extraction.

---

## 5. Participants

### 5.1 Roster

| Participant | Role | OIIE Scenarios | Phase |
|---|---|---|---|
| **ENG** | Engineering design authority — model-based tag/segment source | 1, 27 | 1 |
| **REG-LOCATION** | Functional location / breakdown structure registry, with stewardship gate | 1, 2, 27, 28 | 1 |
| **MMS** | Maintenance management system — consumes engineering structure, publishes asset install/removal events | 2, 11, 33 | 1 |
| **CMS** | Condition Monitoring System — O&M system consuming asset installation and removal events | 11 | 1 |
| **CONSTRUCT** | Construction / commissioning — as-built asset source | 4, 40 | 2 |
| **REG-ASSET** | Serialised asset registry, install/remove events | 4, 5, 33 | 2 |
| **RDL** | Reference data library — publishes class and property definitions | 34, 35 | 2 |
| **REG-PRODUCT** | OEM model / product data library | 7, 8, 25, 26, 37 | 3 |
| **REG-MATERIAL** | Material and part master, procurement | 36, 38 | 3 |

Scenario numbers refer to the OIIE Systems Landscape scenario table (`02List_of_Use_Cases`, v1.4).

**Use Case numbers and Scenario numbers are different axes and must not be conflated.** OIIE Use Case 5 is *Asset Installation/Removal Updates*, and it owns Scenarios 10 and 11. Scenario 5 belongs to Use Case 10, not to Use Case 5. An earlier revision of this table read the scenario column as though it were the use case column, which is why MMS was previously described as scenario 5 and as a consumer only.

**MMS publishes.** Under Scenario 11 the maintenance system is the *source* of asset installation and removal events, not merely a receiver of engineering structure. It therefore holds both roles: it subscribes on the engineering provisioning path and publishes on the operational events path.

**CMS exists because the O&M Systems actor had no seat.** Scenario 11 requires a receiver distinct from the publisher, and overloading REG-LOCATION with that role would have made the scenario prove nothing — a participant cannot demonstrate interoperability by publishing to itself. It holds no reference data and no fixtures, because a condition monitoring system receiving its first event genuinely cannot say what the asset is. What it does accumulate is its own asset and location records, built from the events it receives, so that it has a repository rather than only an inbox.

**RDL is in phase 2, not phase 3.** Classification (§6.5) makes graceful degradation and definition propagation demonstrable, and both require a participant that governs and publishes definitions. It impersonates the **MIMOSA RDL**, keeping the demonstration inside the OpenO&M family — the choice of library is deliberately not a proof point at this phase, so no external library content or dependency is taken on. Class and property keys are written as resolvable URIs so the structure is realistic without the fixtures claiming to be the published library.

### 5.2 Identifier conventions

Deliberately incompatible across participants. This is load-bearing: it is what makes the CIR necessary rather than decorative.

| Participant | Convention | Example |
|---|---|---|
| ENG | ISA-5.1 instrument/equipment tags | `TIC-106`, `P-101A` |
| CONSTRUCT | Manufacturer serial numbers | `SN-4471193-B` |
| REG-LOCATION | Registry surrogate | `LOC-000412` |
| REG-ASSET | Registry surrogate | `ASSET-000241` |
| REG-PRODUCT | Model + revision code | `4300-B/Rev3` |
| REG-MATERIAL | Catalogue number | `MAT-88120` |
| MMS | Legacy numeric primary key | `234443` |

Each personality supplies an `IIdentifierGenerator`. Generators must be deterministic under a seeded RNG so CI runs are reproducible, and must draw from a pool large enough that repeated demo sessions do not produce identical objects.

### 5.3 Domain vocabulary

**Requirement: no participant's domain schema may resemble the BOD schema.**

ENG stores `Tag` rows with columns `TagNumber`, `ServiceDescription`, `PidReference`, `LineClass`, `DisciplineCode`. It does not store `Segment`, `IDInSource`, `CodeType`, or `listID`. The mapper from `eng.Tag` to `SyncSegments` is a real artifact, inspectable from the UI, and *that mapping is the interoperability work*. If the source table is already CCOM-shaped, the demo hides the only genuinely hard part of the problem.

The same applies to each personality. MMS stores `EquipmentRecord` with `EquipmentNumber`, `CostCentre`, `PlannerGroup`. REG-MATERIAL stores `MaterialMaster` with `CatalogueNumber`, `UnspscCode`, `StockingUom`.

---

## 6. Persistence

### 6.1 Database

**All infrastructure is Azure-hosted, including development.** There is no local container dependency — no Docker, no SQL Server image, no storage emulator. Developers run `SimHost` from Visual Studio against a per-developer Azure SQL database provisioned by `provision.ps1`, alongside shared Azure Storage and the deployed ISBM and CIR providers. This matches how the CIR and ISBM providers are already worked on, keeps one dialect and one set of behaviours everywhere, and removes an entire class of "works on my machine" divergence.

Two development configurations, both against Azure backing services:

- **Workstation debug (the normal inner loop)** — `SimHost` runs under F5 in Visual Studio on `localhost`, connecting outbound to the developer's own Azure SQL database, the shared Storage account under a per-developer container prefix, Key Vault, and the deployed ISBM and CIR providers. **No deployment step, and no storage emulator.** Blob Storage and Azure SQL are ordinary outbound HTTPS/TDS endpoints; a locally-running process talks to them exactly as a deployed one does. Azurite exists to support offline work and to avoid provisioning a storage account, and neither applies here.

- **Personal deployment slot** — `SimHost` deployed to a developer slot on the shared App Service plan. Needed only when a longer-lived addressable endpoint is wanted; it is *not* required for routine development.

**Inbound `NotifyListener` callbacks are the only case where addressability matters,** because the ISBM provider must reach the participant. Handled in order of preference:

1. **Polling mode** — the development default. No configuration, and it exercises the same message-handling path.
2. **Visual Studio Dev Tunnels** — assigns the F5 session a public HTTPS URL forwarding to `localhost`. Registered as the notification endpoint, the ISBM provider calls directly into the running debugger. This is the preferred way to develop notification handling, since the callback can be stepped through.
3. **Personal deployment slot** — for a stable shared endpoint only.

**Credentials.** `DefaultAzureCredential` picks up the developer's Visual Studio or Azure CLI sign-in for Storage, Key Vault, and Application Insights, so no secrets are held on the workstation. The per-participant SQL logins remain password-based — the grant model of §6.2 is the point of them — and are sourced from Key Vault at startup.

Databases per environment:

| Environment | Database | Tier |
|---|---|---|
| Per-developer | `oiie-sandbox-dev-{alias}` | Serverless, auto-pause 1h, min vCore |
| CI | `oiie-sandbox-ci` (run-scoped schemas per §10) | Serverless, auto-pause 1h |
| Demo | `oiie-sandbox-demo` | Serverless, auto-pause 4h, higher max vCore |

All on the existing `acme-sql-server`, and all separate from the ISBM and CIR databases so Sandbox churn cannot touch provider data.

**One provider, one dialect, one migration set.** SQLite is explicitly rejected for CI — dual providers drift, and the drift surfaces the week of a demo. Serverless auto-pause is acceptable because every session starts from reset (§10), so cold start is absorbed by the reset step; the CI pipeline issues a warm-up query before the run begins.

Cost control: per-developer databases are provisioned on request and deprovisioned by a scheduled job after 14 days of no connections.

### 6.2 Schema isolation

One SQL schema per participant, with a dedicated SQL login per participant granted access **only** to its own schema.

```
reg_location  reg_asset  reg_product  reg_material  eng  construct  mms  cms  rdl
sandbox       (orchestration, scenario runs, assertions)
tower         (read-only cross-schema views — the single sanctioned exception)
```

Rationale: without enforced grants, a cross-schema join will eventually be used to resolve a foreign identifier instead of a CIR call. It will work, nobody will notice, and the demo will then prove nothing, because the "independent systems" are sharing a database. Grants make that shortcut fail at development time.

`tower` contains read-only views only, is documented in code as the god view, and must never be referenced from participant code. A test asserts that no participant `DbContext` references it.

### 6.3 Common tables

Applied by the runtime into every participant schema from a single migration set.

```sql
-- Message archive: every BOD in or out, with ISBM envelope metadata
CREATE TABLE <schema>.Message (
    MessageId          UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    Direction          VARCHAR(8)       NOT NULL,  -- Inbound | Outbound
    Pattern            VARCHAR(16)      NOT NULL,  -- Publication | Request | Response
    ChannelUri         NVARCHAR(400)    NOT NULL,
    Topic              NVARCHAR(400)    NULL,
    Verb               NVARCHAR(64)     NOT NULL,  -- Sync, Get, Show, Process, Acknowledge...
    Noun               NVARCHAR(64)     NOT NULL,  -- Segments, Assets, Models...
    BodId              NVARCHAR(128)    NOT NULL,  -- ApplicationArea/BODID
    CorrelationBodId   NVARCHAR(128)    NULL,      -- BODID being responded to
    IsbmMessageId      NVARCHAR(128)    NULL,
    IsbmSessionId      NVARCHAR(128)    NULL,
    IsbmRequestId      NVARCHAR(128)    NULL,      -- for response correlation
    ScenarioRunId      UNIQUEIDENTIFIER NULL,
    CorrelationId      NVARCHAR(64)     NOT NULL,  -- end-to-end trace id
    ContentRef         NVARCHAR(400)    NOT NULL,  -- blob path
    ContentBytes       INT              NOT NULL,
    ValidationStatus   VARCHAR(16)      NOT NULL,  -- Valid | Invalid | NotValidated
    ValidationDetail   NVARCHAR(MAX)    NULL,
    ProcessingStatus   VARCHAR(16)      NOT NULL,  -- Pending | Applied | Rejected | Failed
    ProcessingDetail   NVARCHAR(MAX)    NULL,
    OccurredAt         DATETIME2        NOT NULL,
    INDEX IX_Message_Correlation (CorrelationId),
    INDEX IX_Message_Run (ScenarioRunId, OccurredAt),
    INDEX IX_Message_Channel (ChannelUri, OccurredAt)
);

-- Append-only ledger linking domain changes to messages
CREATE TABLE <schema>.Provenance (
    Id             BIGINT IDENTITY PRIMARY KEY,
    MessageId      UNIQUEIDENTIFIER NULL,     -- null for user-originated changes
    EntityType     NVARCHAR(64)     NOT NULL,
    EntityKey      NVARCHAR(200)    NOT NULL,
    Action         VARCHAR(16)      NOT NULL, -- Created | Updated | Rejected | Ignored | Superseded
    Actor          NVARCHAR(128)    NOT NULL, -- user id, 'system', or scenario step id
    ChangeSummary  NVARCHAR(MAX)    NULL,     -- JSON: field-level before/after
    At             DATETIME2        NOT NULL,
    INDEX IX_Prov_Entity (EntityType, EntityKey, At),
    INDEX IX_Prov_Message (MessageId)
);

-- Transactional outbox: publication intent, committed with the domain change
CREATE TABLE <schema>.Outbox (
    Id                BIGINT IDENTITY PRIMARY KEY,
    ContainerType     NVARCHAR(64)  NULL,      -- NamedVersion, WorkPackage, Ecn...
    ContainerKey      NVARCHAR(200) NULL,
    EntityType        NVARCHAR(64)  NOT NULL,
    EntityKeys        NVARCHAR(MAX) NOT NULL,  -- JSON array — may be many nouns per BOD
    ChangeKind        VARCHAR(16)   NOT NULL,  -- Add | Change | Delete
    Verb              NVARCHAR(64)  NOT NULL,
    Noun              NVARCHAR(64)  NOT NULL,
    Pattern           VARCHAR(16)   NOT NULL,  -- Publication | Request
    ChannelUri        NVARCHAR(400) NOT NULL,
    Topic             NVARCHAR(400) NULL,
    ScenarioRunId     UNIQUEIDENTIFIER NULL,
    CorrelationId     NVARCHAR(64)  NOT NULL,
    State             VARCHAR(16)   NOT NULL,  -- Pending | Building | Posted | Failed | Held
    Attempts          INT           NOT NULL DEFAULT 0,
    LastError         NVARCHAR(MAX) NULL,
    MessageId         UNIQUEIDENTIFIER NULL,   -- set on success
    CreatedAt         DATETIME2     NOT NULL,
    PostedAt          DATETIME2     NULL,
    INDEX IX_Outbox_Pending (State, CreatedAt)
);

-- Local cache of CIR identity resolution
CREATE TABLE <schema>.IdentityMap (
    Id                 BIGINT IDENTITY PRIMARY KEY,
    LocalEntityType    NVARCHAR(64)  NULL,      -- null if foreign id not yet bound locally
    LocalKey           NVARCHAR(200) NULL,
    Cirid              UNIQUEIDENTIFIER NULL,
    ForeignSourceId    NVARCHAR(200) NOT NULL,
    ForeignIdInSource  NVARCHAR(200) NOT NULL,
    ForeignName        NVARCHAR(400) NULL,
    ResolvedAt         DATETIME2     NOT NULL,
    StaleAfter         DATETIME2     NOT NULL,
    Invalidated        BIT           NOT NULL DEFAULT 0,
    InvalidatedReason  NVARCHAR(400) NULL,
    UNIQUE (ForeignSourceId, ForeignIdInSource),
    INDEX IX_IdMap_Cirid (Cirid)
);

-- Human-in-the-loop queue (accept/reject of inbound requests, stewardship review)
CREATE TABLE <schema>.PendingWork (
    Id            UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    MessageId     UNIQUEIDENTIFIER NULL,
    Kind          NVARCHAR(64)  NOT NULL,   -- AssetEventReview, LocationStewardship...
    Subject       NVARCHAR(400) NOT NULL,
    Payload       NVARCHAR(MAX) NULL,       -- JSON snapshot of proposed change
    State         VARCHAR(16)   NOT NULL,   -- Queued | Accepted | Rejected | Expired
    DecidedBy     NVARCHAR(128) NULL,
    DecidedAt     DATETIME2     NULL,
    RejectReason  NVARCHAR(MAX) NULL,
    CreatedAt     DATETIME2     NOT NULL
);

-- ISBM runtime state (per participant partition of the existing pattern)
CREATE TABLE <schema>.IsbmSession (...);   -- as per SqlIsbmSessionStore
CREATE TABLE <schema>.IsbmCursor (...);    -- last-read message per session
```

**Design notes.**

*Provenance as an append-only ledger, not a `CreatedByMessageId` column on domain rows.* One BOD typically creates or touches many rows, and one row is touched by many BODs across a scenario. The ledger gives the "history of this record" panel for free and is the table most assertions query.

*Outbox commits in the same transaction as the domain change.* This gives three things: user work is not lost if ISBM is briefly unavailable (demonstrable behaviour); the dispatcher can be paused from the control tower so several changes release as a visible burst; and assertions can check that intent was recorded independently of whether the wire call succeeded.

*`EntityKeys` as a JSON array* because a release event typically publishes many nouns in one BOD.

*`Held` outbox state* is the paused-dispatcher state, distinct from `Pending`.

### 6.4 Domain tables — the typed spine

Each personality's domain tables carry a **typed spine**: identity, hierarchy, lifecycle, and the relationships the runtime itself reasons about. `Asset.AssetNumber`, `Asset.ParentAssetId`, `AssetSegmentEvent.EventType`, `Tag.TagNumber`. These are stable, indexed, and determine the shape of the screens.

Everything beyond the spine — insulation class, design pressure, NACE compliance, criticality ranking, coating system — is **not** a typed column. It is a class-governed property, stored per §6.5, added as data rather than as a migration.

This split replaces the blanket "typed, not generic" rule of version 0.1, which was wrong as stated. Typed columns everywhere would imply that CCOM is a closed schema an organisation either fits into or does not — which is the most common objection to standards-based interoperability and the one this tool most needs to defuse. The honest picture is a stable core plus governed extension, and it has to be visible. The original justification for typed storage (generic storage produces generic-looking screens) is preserved by the split: the spine drives layout, properties fill a class-driven panel.

The BOD catalogue assumes this already — `mim_5021`–`5029` are attribute queries against segments, assets, and models, split three ways into numeric, character, and blob data. The property mechanism is part of the noun set, not an extension to it.

Spine tables, indicative and not exhaustive:

| Personality | Core tables |
|---|---|
| `eng` | `Tag`, `TagRelationship`, `Changeset`, `ChangesetItem`, `NamedVersion`, `ValidationFinding` |
| `construct` | `WorkPackage`, `InstalledItem`, `PunchItem`, `Signoff` |
| `reg_location` | `Location`, `LocationParent`, `StewardshipItem` |
| `reg_asset` | `Asset`, `AssetSegmentEvent` |
| `reg_product` | `Model`, `ModelRevision`, `ModelRelationship`, `Ecn`, `EcnAffectedModel` |
| `reg_material` | `MaterialMaster`, `MaterialModelLink`, `Requisition`, `RequisitionLine` |
| `mms` | `EquipmentRecord`, `FunctionalLocationRecord`, `WorkOrder` |
| `cms` | `AssetInstallationEvent`, `MonitoredLocationRecord`, `MonitoredAssetRecord` |
| `rdl` | `PublishedClass`, `PublishedProperty`, `LibraryVersion` (authoring side; see §6.5) |

Per-personality attribute tables (`TagAttribute`, `AssetAttribute`, `LocationAttribute` in version 0.1) are removed — attributes are now handled uniformly by the property model.

### 6.5 Property and classification model

#### 6.5.1 Principle

Tags, assets, and models do not have fixed schemas. A centrifugal pump carries flow rate, head, and NPSH; a gate valve carries body rating, seat material, and face-to-face dimension. Neither set is enumerable in advance, and both are what make the data practically useful rather than merely conformant.

Properties therefore attach to entities **via classes**, not ad hoc. A class carries a property set; classifying an entity confers that set. This is how CCOM is used in practice, and the BOD catalogue treats class as a query dimension in its own right — `GetSegmentsBySegmentType` (5018), `GetAssetsByAssetType` (5019), `GetModelsByModelType` (5020).

#### 6.5.2 Tables

Applied by the runtime into every participant schema, alongside the common tables of §6.3.

```sql
CREATE TABLE <schema>.ClassDefinition (
    Id            UNIQUEIDENTIFIER PRIMARY KEY,
    ClassKey      NVARCHAR(200) NOT NULL,    -- RDL URI or local key
    Origin        VARCHAR(16)   NOT NULL,    -- Rdl | Local | Inferred
    RdlSourceId   NVARCHAR(200) NULL,
    Version       NVARCHAR(32)  NOT NULL,
    Name          NVARCHAR(200) NOT NULL,
    Description   NVARCHAR(MAX) NULL,
    AppliesTo     NVARCHAR(64)  NOT NULL,    -- Segment | Asset | Model | Material
    Kind          VARCHAR(16)   NOT NULL,    -- Taxonomy | Aspect
    ParentClassId UNIQUEIDENTIFIER NULL,
    ValidFrom     DATETIME2     NULL,
    ValidTo       DATETIME2     NULL,
    ReceivedFrom  NVARCHAR(64)  NULL,        -- participant, if it arrived over the bus
    ReceivedAt    DATETIME2     NULL,
    UNIQUE (ClassKey, Version)
);

CREATE TABLE <schema>.PropertyDefinition (
    Id             UNIQUEIDENTIFIER PRIMARY KEY,
    DefinitionKey  NVARCHAR(200) NOT NULL,   -- RDL URI or local key
    Origin         VARCHAR(16)   NOT NULL,   -- Rdl | Local | Inferred
    RdlSourceId    NVARCHAR(200) NULL,
    Name           NVARCHAR(200) NOT NULL,
    Description    NVARCHAR(MAX) NULL,
    DataType       VARCHAR(16)   NOT NULL,   -- Numeric | Character | DateTime | Boolean | Blob
    UnitOfMeasure  NVARCHAR(64)  NULL,
    UomListId      NVARCHAR(128) NULL,
    CodeListId     NVARCHAR(128) NULL,
    ReceivedFrom   NVARCHAR(64)  NULL,
    ReceivedAt     DATETIME2     NULL,
    UNIQUE (DefinitionKey)
);

CREATE TABLE <schema>.ClassProperty (
    Id             BIGINT IDENTITY PRIMARY KEY,
    ClassId        UNIQUEIDENTIFIER NOT NULL,
    DefinitionId   UNIQUEIDENTIFIER NOT NULL,
    Requirement    VARCHAR(16)   NOT NULL,   -- Required | Recommended | Optional
    MaxCardinality INT           NULL,
    DefaultUom     NVARCHAR(64)  NULL,
    CodeListId     NVARCHAR(128) NULL,       -- narrowed allowed values
    MinValue       DECIMAL(38,10) NULL,
    MaxValue       DECIMAL(38,10) NULL,
    DisplayGroup   NVARCHAR(128) NULL,
    DisplayOrder   INT           NULL,
    UNIQUE (ClassId, DefinitionId)
);

CREATE TABLE <schema>.EntityClassification (
    Id              BIGINT IDENTITY PRIMARY KEY,
    EntityType      NVARCHAR(64)  NOT NULL,
    EntityKey       NVARCHAR(200) NOT NULL,
    ClassId         UNIQUEIDENTIFIER NOT NULL,
    IsPrimary       BIT           NOT NULL,  -- exactly one primary taxonomy class
    AssignedBy      NVARCHAR(128) NOT NULL,
    SourceMessageId UNIQUEIDENTIFIER NULL,
    ValidFrom       DATETIME2     NOT NULL,
    ValidTo         DATETIME2     NULL,
    INDEX IX_Class_Entity (EntityType, EntityKey)
);

CREATE TABLE <schema>.EntityProperty (
    Id              BIGINT IDENTITY PRIMARY KEY,
    EntityType      NVARCHAR(64)  NOT NULL,
    EntityKey       NVARCHAR(200) NOT NULL,
    DefinitionId    UNIQUEIDENTIFIER NOT NULL,
    ViaClassId      UNIQUEIDENTIFIER NULL,   -- which class sanctioned this value
    NumericValue    DECIMAL(38,10) NULL,
    CharacterValue  NVARCHAR(MAX)  NULL,
    DateTimeValue   DATETIME2      NULL,
    BooleanValue    BIT            NULL,
    BlobRef         NVARCHAR(400)  NULL,
    UnitOfMeasure   NVARCHAR(64)   NULL,     -- as supplied; may differ from definition
    CodeValue       NVARCHAR(200)  NULL,
    CodeListId      NVARCHAR(128)  NULL,
    Mapped          BIT            NOT NULL, -- false = retained but not understood locally
    Orphaned        BIT            NOT NULL DEFAULT 0, -- no longer sanctioned after reclassification
    SourceMessageId UNIQUEIDENTIFIER NULL,
    ValidFrom       DATETIME2      NOT NULL,
    ValidTo         DATETIME2      NULL,
    INDEX IX_Prop_Entity (EntityType, EntityKey),
    INDEX IX_Prop_Def (DefinitionId)
);
```

Typed value columns rather than a single serialised `nvarchar` mirror CCOM's own numeric / character / blob split, and keep the attribute BODs (`mim_5021`–`5029`) mappable without inventing a serialisation.

#### 6.5.3 Two kinds of class

**Taxonomy** — an entity has exactly one primary taxonomy class, and it inherits from its ancestor chain. A pump is one thing.

**Aspect** — orthogonal, multiple, non-inherited. *Safety-critical*, *NACE-compliant*, *rotating equipment under vibration monitoring*. Each carries a property set and can apply in any combination.

Modelling everything as single inheritance forces a combinatorial taxonomy that no real library has. The distinction is required for the fixtures to look plausible.

#### 6.5.4 Effective property set and narrowing

Resolution: walk the primary taxonomy chain root-downward, union in all active aspect classes, then apply overrides.

A subclass may **narrow** an inherited `ClassProperty` — tighten a numeric range, promote `Optional` to `Required`, restrict a code list, fix a unit of measure. A subclass may **not** widen, contradict, or remove. The runtime enforces this at fixture load and on inbound class definitions, failing with an explicit error rather than silently resolving.

This constraint is what makes inheritance predictable, and it is the rule most often got wrong in practice, so it is worth enforcing rather than documenting.

#### 6.5.5 Definition provenance

`Origin` on both `ClassDefinition` and `PropertyDefinition` takes three values, and each has a different interoperability consequence:

| Origin | Meaning | Receiver behaviour |
|---|---|---|
| `Rdl` | Governed, shared, resolvable from the library | Bound and displayed normally; both parties understand it |
| `Local` | A participant invented it because it needed it | Travels in the BOD; receiver has no definition |
| `Inferred` | A stub created by a receiver on encountering an unknown definition | Value retained, `Mapped = false`, displayed in an *Unmapped* section with an origin chip |

**Unmapped properties are never discarded and never silently absorbed.** A receiver retaining an attribute it does not understand, visibly flagged, is the honest answer to "what happens when my system has fields yours does not," and it makes the case for reference-data governance without anyone having to assert it.

#### 6.5.6 Graceful degradation

The behaviour that justifies the hierarchy, and a required capability.

MMS receives an asset classified `rdl:MagneticDriveCentrifugalPump` — a class it has never seen. It does hold `rdl:CentrifugalPump`, two levels up the chain. It therefore:

- binds every inherited property it recognises (flow rate, head, NPSH, impeller diameter)
- creates `Inferred` stubs for the leaf-specific ones (containment shell material, eddy-current loss rating), retains the values, flags them unmapped and attributes them to REG-ASSET
- displays the asset correctly as a centrifugal pump, with a chip reading *classified more specifically upstream*

Partial understanding rather than all-or-nothing. This is possible only because classes share ancestors, which is the argument for a governed library over per-participant flat types.

The follow-through is the RDL participant publishing the leaf class definition; receivers bind it, the chips clear, and previously orphaned values slot into place with **no data re-sent**. This is OIIE Use Case 11 and scenarios 34/35, and it is the reason RDL is a phase-2 participant.

#### 6.5.7 Reclassification

Reclassifying an entity does **not** delete property values. Any value no longer sanctioned by the new effective set is marked `Orphaned = 1` and surfaced in the same *Unmapped* section. Deleting data on reclassification is unrealistic and destroys the audit story.

#### 6.5.8 Scope control

The risk here is building an ontology editor. Fixed constraints:

- **One shallow hierarchy: four to five levels, 20–30 taxonomy classes**, covering only what the scenarios touch — rotating equipment, valves, instruments, static equipment — plus three or four aspect classes.
- **No class authoring UI before phase 4.** Classes arrive as fixtures or over the bus from the RDL participant. A local class editor is added only if a scenario requires it.
- **No reasoning engine.** Chain walking, set union, and the narrowing rules of §6.5.4. No inference, no equivalence axioms, no restriction logic.
- **Coarse versioning** — a version string and effectivity dates, not full change management.

### 6.6 Payload storage

BOD XML bodies live in Azure Blob Storage; `Message.ContentRef` holds the blob path. Rationale: payloads are XML, some will be large (a realistic handover publication is not small), and the `Message` table is queried on every SignalR push for the swimlane. `nvarchar(max)` bodies would make those queries progressively worse over a session.

Container per scenario run: `sandbox-payloads/{runId}/{participantId}/{messageId}.xml`. Lifecycle rule deletes containers after 7 days.

Development uses the same Azure Storage account as CI, with a per-developer container prefix. No storage emulator.

### 6.7 Configuration

Personality configuration, channel bindings, class and property fixtures, and seed data are **files in git**, not tables. A scenario is then reproducible from a commit hash, which is what makes CI results meaningful.

---

## 7. Release triggers

### 7.1 Principle

Publication is a deliberate, domain-meaningful act, and it is **different for every participant**. A uniform "publish" button across all seven would be visibly synthetic and would misrepresent what OIIE requires: the bus imposes no workflow on participants; it only needs them to publish when their own business says publish.

Publish-on-save is explicitly rejected as unrealistic.

The common mechanism underneath is a **container object with a lifecycle**. A state transition on the container writes outbox rows. One mechanism, seven domain vocabularies. Each container has its own number series, and that number is carried in the BOD `ApplicationArea` and recorded on the `Message` row — so "this publication came from named version `Rev C — Unit 101 reroute`" is answerable, which is exactly the traceability an owner-operator asks for.

### 7.2 Per-participant triggers

**ENG — named version promotion (model-based, not document-based).**

ENG is data-centric: tags are extracted from the model and published independently of any document process. The P&ID is a derived view; the tag is the object of record. Modelling ENG around drawing transmittals would model a workflow that current engineering tooling exists to replace.

- Edits accrue automatically into `Changeset` rows. Changesets are **not** the trigger — nobody decides to make one.
- Tags carry object-level maturity state: `WorkInProgress → Shared → Published`.
- The deliberate act is creating or promoting a **named version** — a person asserting that this state of the model is fit for others to consume.
- A **validation gate** runs before promotion. With classification in place (§6.5) this is checkable rather than gestural, and every rule below is mechanical:
  - every tag carries a primary taxonomy class
  - every `Required` property in the tag's effective property set has a value
  - numeric values fall within any narrowed range; coded values fall within the narrowed code list; units are in the sanctioned UoM list
  - no property values are `Orphaned` from an earlier reclassification
  - no orphan relationships or unresolvable parent references
  - class and property definitions used are `Rdl`-origin, or `Local` with an explicit acknowledgement — publishing local definitions is allowed but flagged, since it is exactly the situation the RDL exists to reduce

  Findings are written to `ValidationFinding` and block promotion. This is the realistic replacement for a checker's red pen and gives a demonstrable failure with a specific, legible message.
- Promotion writes outbox rows for the changed tags only. Delta publication is therefore natural — eleven changed tags out of four thousand, which is a far more convincing demonstration of ongoing operations than a bulk dump.
- The BOD carries the named version identifier and the changeset range, so a receiver can ask "what changed between named version 4 and 5" and get a precise answer.

*Open question — see §14.1: whether the selection unit is a named version of the whole model or a scoped promotion (discipline, unit, or an explicit WIP set).*

**CONSTRUCT — mechanical completion sign-off.** Serial numbers and as-built attributes are captured against a work package over time, punch-list style. A supervisor signs the package as mechanically complete; that signature publishes every item in it. Physical field verification is genuinely a signed human event, so the document-ish shape is correct here.

**REG-LOCATION — stewardship approval.** Inbound ENG data lands in a `StewardshipItem` review queue. A steward approves it into the authoritative model, and *that* republishes to the O&M channel. This is where REG-LOCATION earns its existence: it is a governance gate, not a relay. Reject-back is supported.

**REG-ASSET — field event, with separate acceptance.** An install or removal is a field occurrence, so creation of the `AssetSegmentEvent` row is the business event. What is not automatic is acceptance of an inbound `ProcessAssetSegmentEvent` from another participant: that lands in `PendingWork`, a data steward accepts or rejects, and the decision emits `AcknowledgeAssetSegmentEvent`. This makes request / response / confirm legible as three distinct acts.

**REG-PRODUCT — revision approval and ECN.** Two triggers. Model revision approval publishes `SyncModels`. An engineering change notice — with effectivity dates and an affected-models list — is a separate, higher-ceremony trigger publishing the change advisory (scenario 25/26). ECNs remain formal because contracts depend on them.

**REG-MATERIAL — material master activation, or requisition submission.** The RFI of scenarios 36/37 releases when a buyer submits the request, not while requirements are still being assembled.

Keeping the two distinct is what lets `sc11-asset-install` assert that nothing left the building while the order sat completed.

### 7.3 Auto-release toggle

Each participant exposes a per-personality setting `releaseMode: manual | auto`. `auto` performs the container transition immediately on the underlying change, so CI scenarios need not script UI-equivalent approval steps unless the scenario is specifically testing the approval path. Demo environments run `manual`.

Scenario steps may override the mode per step.

---

## 8. User experience

### 8.0 Technology decision: Blazor Server

**Decided.** The UI is Blazor Server, with one deliberate JavaScript island for the control tower visualisations (§8.7).

Reasons it wins here:

- **No DTO layer.** Seven personalities each carry their own spine tables plus the shared classification model. A separate TypeScript client would need a serialisation boundary and a second definition of every shape — a cost that compounds precisely because participants are meant to have *different* data shapes. Components read `EntityPropertyValue` and `EffectivePropertySet` directly.
- **The recursive BOD renderer (§8.4) stays trivial.** A component that takes an `XElement` and renders itself recursively. The alternatives are shipping XML to the browser and re-parsing, or serialising to JSON server-side and losing the attribute fidelity — `listID`, `unitCode`, `schemeAgencyID` — that makes the rendering readable in the first place.
- **SignalR is the transport, not an addition.** §8.5 requires live push; a Blazor Server circuit already is one.
- **One project, one build, one deploy**, consistent with the ISBM and CIR providers and with the Azure-only footprint of §12.4.

Known weaknesses, accepted with mitigations:

| Weakness | Mitigation |
|---|---|
| Every interaction is a round trip to Azure; grid work may feel soft on customer-site wifi | Measure during phase 4 rehearsal. Fallback is server-rendered Razor Pages plus htmx for domain screens — no circuit, no DTO layer either |
| Circuit reconnect banners appear across all tiled windows at once after an App Service restart | Azure SignalR Service in the demo environment (§12.4); rehearse a restart |
| Force-directed cluster graph and swimlane are genuinely client-side visualisation work | Not attempted in Blazor. One JS island (§8.7), fed by a JSON endpoint — a single deliberate boundary rather than interop scattered through the app |

The decision is also low-risk to revisit: phases 1–3 have no UI at all. The scenario runner is headless and assertions query the database directly, so SC-1 is met before a single component exists.

### 8.1 Principle: three layers, defaulting to the shallowest

The recipient's landing surface must be its **own domain screens**, not a rendered BOD. If the primary surface is a formatted `SyncSegments` document, what the audience learns is "XML can be styled as HTML" — which is not the claim being made. The claim is that a functional location *appeared in the maintenance system* without anyone touching it. In real deployments no MMS user ever sees a BOD; it dissolves into the receiving system's model.

| Layer | Surface | Audience |
|---|---|---|
| **1 — Domain** | Hierarchy browsers, asset registers, model catalogues, part masters. No messaging concepts visible. | Customer, business stakeholder |
| **2 — Provenance** | Origin chip on every record → human-readable rendering of the BOD that produced it, plus ISBM envelope details, plus the mapping applied. | Solution engineer, technical evaluator |
| **3 — Wire** | Raw XML, XSD validation badge, session/message/request IDs, faults, retry history. | Developer, debugging |

Each layer is one click deeper than the last. This layering exists because the two audiences are irreconcilable on one screen: an account team demonstrating to an owner-operator needs layer 1; debugging a namespace mismatch needs layer 3.

### 8.2 Domain screens

Per personality, styled to look like the class of product it impersonates:

- **ENG** — model tree, tag list with maturity state, changes-since-last-published panel, validation findings, named version history
- **CONSTRUCT** — work package list, item capture form, punch items, sign-off action
- **REG-LOCATION** — functional location hierarchy browser, stewardship review queue
- **REG-ASSET** — asset register, install/remove history timeline per asset, review queue
- **REG-PRODUCT** — model catalogue, revision tree, ECN register
- **REG-MATERIAL** — part master grid, model linkage view, requisition list
- **MMS** — equipment register, functional location tree, work order list
- **RDL** — class hierarchy browser, property definition register, class property template editor, library version history, publish action

**The property panel is shared across all personalities and is class-driven.** On any entity detail screen:

- properties grouped by the class in the chain that contributes them, with the class name as the group header — so it is directly visible which properties come from *Pump* and which from *Centrifugal Pump*, and which came from an aspect class
- `Required` properties with no value render as a visible gap, not an absence — the class asserts they should be there
- unit of measure and code list shown from the effective definition; as-supplied units flagged where they differ
- an *Unmapped properties* section for `Mapped = false` and `Orphaned = 1` values, each with an origin chip naming the participant it came from
- an *Add property* action offering a definition picker: `Rdl` definitions first, existing `Local` definitions second, *define new local property* last — the ordering is itself a nudge toward governed definitions
- a *Classify* action for assigning or changing the primary taxonomy class and toggling aspect classes

Each participant gets a distinct visual identity — accent colour, wordmark, slightly different layout density. When four browser windows are tiled, the viewer must know which is which in under a second. Cheap; disproportionate payoff.

All screens carry persistent `SIMULATOR` marking. Non-negotiable: a convincing REG-ASSET screen will otherwise be read as a product roadmap item.

### 8.3 Data entry

Five input modes, **all landing in the same domain tables through the same service methods**. Autopilot must not have a separate code path; if CI and the demo diverge, CI stops being evidence.

1. **Seed fixtures** — bulk baseline loaded at reset from git. Loaded directly into domain tables *without* outbox rows: this is "what existed before today."
2. **Quick actions** — the workhorse. Buttons pre-fill a form with plausible data from a fixture pool (*"Add pump to Unit 101"*, *"Issue ECN for model 4300-B"*). One click populates; the operator edits a field or two and submits. ~90% of live demo interaction.
3. **Full form** — same form, empty. Reserved for the one object you want to be seen typing, usually the star of the identity-merge scenario.
4. **Paste / import** — textarea accepting CSV for bulk creation. Cheap to build; answers "what about 5,000 tags?" immediately.
5. **Scenario autopilot** — the headless runner invoking the same service methods.

Form validation is deliberately loose. Enough that the app feels real, with gaps left on purpose — a missing manufacturer, an unresolvable parent reference, a code value outside the list — so that a submission can pass the form and then fail XSD validation or be rejected downstream. Those are the paths that exercise faults and BOD confirmations.

Fixture pools must contain enough variety that a fourth rehearsal does not create `P-101A` for the fourth time. Quick actions consume the next unused pool item.

### 8.4 BOD rendering

One **recursive renderer**, driven by document shape. Not per-BOD templates — that would be forty templates that rot.

- Header from `ApplicationArea`: sender, `BODID`, `CreationDateTime`, plus the container reference (§7.1)
- Verb and noun from the root element
- Recursive descent through `DataArea`; repeated elements render as tables, singletons as definition lists
- **Special-cased UN/CEFACT core component types**, which is where readability actually comes from: `TextType` with `languageID`, `CodeType` with `listID`/`listAgencyID`, `MeasureType` with `unitCode`, `IDType` with `schemeAgencyID`. Rendered naively these read as noise; rendered properly they read as a document.
- A small set of noun-specific overrides (Segment, Asset, Model) for the BODs that appear in demo scenarios. The generic renderer handles everything else acceptably — including BODs not yet implemented, which matters when someone asks about `ShowHealthAssessments`.

**Implemented as a recursive Razor component, not XSLT.** XSLT is the heritage answer and is wrong here for one reason: the rendering must be *interactive*. An `IDInSource` in a rendered BOD must be clickable and resolve through the CIR to show its equivalents; a segment reference must link to the record it created. Static transformation cannot do that, and wrapping XSLT output in JavaScript to add it removes the reason to use XSLT.

Alongside the rendering, the provenance view shows the **mapping applied** — source table columns on the left, BOD elements on the right — since that mapping is the actual interoperability work. For each property carried in the document it also shows whether the definition resolved, from which library, and whether it was bound or retained as unmapped.

### 8.5 Live updates

SignalR push, not polling. A row appearing while someone is looking at the screen is worth more than a sequence diagram. Changed records highlight briefly on update.

### 8.6 Demonstrating absence first

Seed fixtures include deliberate gaps: a missing hierarchy branch, `Model: unknown`, an asset with no functional location, one duplicate identity, one mapping that will go stale.

Classification gives this a natural expression. Seed entities that are **classified but incomplete** — `Required` properties with no value. The screen then shows the gaps *because the class says they should be there*, rather than showing an absence nobody can perceive. Filling them via inbound BODs reads as visible progress rather than rows appearing from nowhere.

Also seed one entity classified against a leaf class that MMS does not hold, so graceful degradation (§6.5.6) is present from the first minute rather than needing to be provoked.

Demos that open fully populated have nowhere to go; contrast is the entire payload.

### 8.7 Control tower

At `/tower`, outside all participant boundaries, reading the `tower` schema views.

The swimlane and cluster graph are the one **JavaScript island** in the application (§8.0): a standalone TypeScript bundle using D3 or Cytoscape, fed by a small JSON endpoint. Everything else on the page is Blazor. Placing the boundary here deliberately is better than interop calls scattered through components that would otherwise be pure C#.

- **Swimlane** — live message flow across participants, one lane per participant, arrows on publication and request/response, colour-coded by validation and processing status
- **Topology** — channels, topics, and which participants are bound to each in which role
- **CIR cluster graph** — see §9.5
- **Dispatcher control** — pause/release the outbox globally or per participant
- **Run log** — scenario steps and assertion results as they execute

### 8.8 Kiosk mode

Single page: one live tile per participant plus the swimlane underneath. This is the customer-facing configuration for a screen or projector. Separate browser windows per participant is the workshop configuration, where different people drive different systems — which is also what makes cross-window causality (§9.4) work.

### 8.9 Repository browser

Each participant on the participants page expands to show the contents of its own schema: domain tables first, the infrastructure tables every participant carries grouped below.

The point is to make §6.2 isolation observable rather than merely asserted. Reads go through the participant's own contained SQL user, so the page shows exactly what that participant can see and nothing more; a table it cannot read is reported as unreadable rather than skipped, because under this model that is a finding. Reading through a privileged login would have been simpler and would have shown rows no participant can actually reach, which would quietly contradict the property being demonstrated.

Tables and columns are discovered from the EF model, so the screen tracks the schema without a maintained list going stale beside it. Rows are capped with the true count displayed and the capped read ordered by primary key — an unordered `TOP` may return a different subset per read, which a view claiming to show what is stored cannot afford.

---

## 9. CIR integration

### 9.1 Principle

When a registry works, nothing appears to happen — everything simply lines up. Demonstrating its value therefore requires engineering moments where its **absence** is visible.

### 9.2 Registration

Every participant registers its own objects into ws-CIR as a side effect of normal domain writes, not as a separate administrative act.

- Mapping: `Registry.ID` = environment registry (e.g. `OIIE-SANDBOX`); `Category.ID` = `Segment` | `Asset` | `Model` | `Material`; `Category.SourceID` = a stable specification identifier; `Entry.SourceID` = participant ID; `Entry.IDInSource` = the participant's native key; `Entry.SourceOwnerID` = the impersonated organisation; `Entry.Name` and `Entry.Description` from domain fields.
- A small, deliberate set of `Property` values is registered to support candidate matching (§9.6): manufacturer, model number, serial number, primary class key, functional location reference.

**ws-CIR properties and CCOM class-governed properties are not the same thing and must not be conflated in the UI.** The ws-CIR specification is explicit that its `Property` set is a small linking aid for identifying equivalent entries, not a global property master. The full attribute set therefore stays in the participant (§6.5) and travels in CCOM BODs; only the handful of discriminating values above are registered. Making that distinction visible is itself a teaching moment — registry and repository are routinely conflated.

  Richer local attributes do improve the registry, though, indirectly: shared primary class plus matching discriminating properties is a far stronger duplicate signal than name similarity, which makes candidate matching (M3, §9.6) look like judgement rather than a scripted reveal.
- Registration goes through the Annex A BOD bridge (`ProcessRegistry` → `AcknowledgeRegistry`) rather than the REST command services, so the demo exercises the same path the integration tests do.
- The participant page shows a ticker — *"registered 12 entries to CIR"* — linking to the `ProcessRegistry` BOD in the message archive. This keeps the registry continuously present rather than appearing only at dramatic moments.

### 9.3 Resolution

On receiving a BOD containing a foreign `SourceID`/`IDInSource` it does not recognise:

1. Check `IdentityMap` for a live, non-invalidated entry.
2. On miss, call `GetRegistry` (or `GetEquivalentEntries`) against the CIR provider.
3. Cache the result with a TTL (`StaleAfter`), default 5 minutes in demo mode, configurable.
4. Bind to a local record if one exists with the same CIRID; otherwise create and register.

**The resolution call must be visible.** The record shows a brief inline `resolving identity…` state before the identity chip appears, and both the `GetRegistry` request and its response are logged into the message archive alongside the BODs, with the actual filter expression shown. Engineers will ask how it knew; the answer must be one click away, not a claim.

The cache is a **feature, not an optimisation** — it is what makes stale-mapping correction (§9.6) demonstrable. Cache state (`resolved 09:31 · expires 09:36`) is shown in the identity panel.

### 9.4 The identity panel

A chip on every domain record — *"4 identities"* — opening a panel showing the CIRID cluster laid out by source:

```
CIRID  550e8400-e29b-41d4-a716-446655440000

ENG              TIC-106            Top Temp Control      registered 09:14
CONSTRUCT        SN-4471193-B       Pump, Centrifugal     registered 09:22
REG-ASSET        ASSET-000241       P-101A                registered 09:22
MMS              234443             Loop 106              resolved   09:31
```

The same component on every participant, so the reading is learned once. Registration timestamps included — watching a row *appear* in that panel during a scenario is a small, effective moment.

### 9.5 Cluster graph

In the control tower: force-directed graph, nodes are CIR entries coloured by source system, edges are shared CIRIDs, clusters are equivalence sets. Nodes appear and snap into clusters as scenarios run. Unregistered entries float unattached, making registration coverage visually obvious at a glance.

This is the one screen where the CIR is the subject rather than a supporting service.

### 9.6 The demonstrable moments

These are specified as required capabilities, not optional polish. Each corresponds to a scenario in §11.

**M1 — Resolution off.** A toggle at MMS disables CIR resolution. The screen then shows what integration without a registry looks like: four records for one pump, raw foreign identifiers, `Manufacturer: unknown`, a duplicate asset a planner would have to reconcile by hand. Enabling resolution collapses the four into one with an equivalence chip. A toggle, not a scripted step, so it can be flipped back mid-question.

**M2 — The merge.** Seed fixtures deliberately register ENG's `TIC-106` and CONSTRUCT's `SN-4471193-B` — the same physical pump — under different CIRIDs. MMS therefore shows two assets, and a planner would raise a duplicate work order. A steward opens the CIR explorer, sees two clusters with matching properties (manufacturer, model, functional location), and merges via `ChangeEntryCIRID`. **In the other browser window, MMS's two rows become one, live.** Cross-window causality is what makes this land: one person clicking in the registry, another watching their maintenance system correct itself.

**M3 — Candidate matching.** The CIR explorer surfaces likely-duplicate clusters by property comparison, so the merge is discovered rather than known in advance.

**M4 — Stale cache correction.** After M2, MMS still holds the old mapping. Either the TTL expires or a `CancelRegistry` publication invalidates it, and the screen corrects itself a beat later. This separates people who have deployed a registry from people who have read about one.

**M5 — Split.** The inverse of M2, for completeness: two entries wrongly merged, separated by reassigning CIRIDs.

### 9.7 CIR explorer

A dedicated surface at `/cir`, presented as a seventh window alongside the participants with its own visual identity:

- Search across registries, categories, entries, properties
- Browse by category; cluster view
- Property inspector
- Merge / split tools (issuing `ChangeEntryCIRID`)
- Candidate duplicate list
- Audit trail of every CIR operation with the BOD behind it

This also gives the CIR provider a face. It is currently an Azure Functions endpoint; putting it on screen next to the systems it serves changes how it is perceived.

---

## 10. Reset, seed, snapshot

Every session starts from reset. Three operations, required from day one because they are painful to retrofit.

**Seed** — fixtures from git with fixed GUIDs, so assertions can reference `SEG-0001` by literal. Loaded into domain tables without outbox rows. Includes the deliberate defects of §8.6 and §9.6.

Class and property definitions are part of the fixture set, and are **distributed asymmetrically on purpose**: the RDL participant holds the full library; each other participant holds a subset, with MMS deliberately missing the leaf classes that REG-ASSET uses. That asymmetry is what makes graceful degradation reproducible rather than contrived.

**Reset** — a single command, target under 60 seconds:
1. Truncate all participant schemas and `sandbox`
2. Close and delete all Sandbox ISBM sessions
3. **Purge Sandbox ISBM channels** — stale publications leaking into the next run is a real and confusing failure mode
4. Clear Sandbox CIR registries
4a. Purge all `Inferred`-origin class and property definitions — without this, the "unknown definition arrives" moment will not reproduce on the second run of a session
5. Delete payload blob containers for prior runs
6. Reseed

**Snapshot / restore** — capture full Sandbox state (SQL bacpac or scripted export, plus blob container, plus CIR registry export) before a customer demo; restore after. This reads as polish; in practice it is what saves a session when something goes wrong and a reset is needed with an audience watching.

**CI namespacing** — every parallel run gets a run-scoped namespace: schema suffix or ephemeral database, ISBM channel URIs prefixed with the run ID, CIR `Registry.ID` suffixed with the run ID. Parallel runs colliding on shared channels is the failure mode that costs a day to diagnose.

---

## 11. Scenario engine

### 11.1 Definition format

YAML in `Sandbox/Scenarios/`. Steps are executed against the same service methods as the UI.

```yaml
id: sc01-design-release
name: Design release to the location registry
# The OpenO&M scenario this file realises. Scenario number is the primary identity
# because it names an exchange between two systems, which is what actually runs.
scenario: 1
# Cross-reference only. Several use cases collapse onto the same exchange, so the use
# case is recorded for readers of the OpenO&M catalogue but never selects a run.
useCase: UC01
# Scenarios that must have run first. Declared so a dependent run fails naming the
# missing prerequisite instead of failing deep in its own assertions.
requires: [sc01-design-release]
participants: [eng, reg-location, mms]
setup:
  reset: true
  channels:
    - uri: /Enterprise/Site/Eng
      type: Publication
      subscribers: [reg-location]
    - uri: /Enterprise/Site/OandM
      type: Publication
      subscribers: [mms]
steps:
  - id: s1
    at: eng
    action: create_tags
    args:
      unit: "101"
      count: 11
  - id: s2
    at: eng
    action: promote_named_version
    args:
      name: "Rev C — Unit 101 reroute"
    expect_release: true

  - assert: message_received
    at: reg-location
    channel: /Enterprise/Site/Eng
    verb: Sync
    noun: Segments
    within: 30s
  - assert: bod_valid            # XSD validation, not merely well-formed
    at: reg-location
    of: last

  - id: s3
    at: reg-location
    action: approve_stewardship
    args: { all: true }

  - assert: message_received
    at: mms
    channel: /Enterprise/Site/OandM
    verb: Sync
    noun: Segments
    within: 30s
  - assert: store_contains
    at: mms
    entity: FunctionalLocationRecord
    where: "SourceTag = 'TIC-106'"
  - assert: cir_equivalent
    entries:
      - { source: eng,          id: "TIC-106" }
      - { source: reg-location, id: "LOC-000412" }
      - { source: mms,          id: "234443" }
```

A step that carries an `id` publishes its result to the steps that follow it. An
action argument may then name a step instead of a literal, which is how a scenario
refers to a value it cannot know in advance — an allocated code, or a minted
identity. `relate_tags` accepts `fromStep`/`toStep` in place of `from`/`to` for
exactly this reason: in the greenfield case the tag numbers are issued by the
identity service during the run, so writing them into the file would assert the very
thing the scenario exists to test. Naming both a literal and a step for the same end
is rejected rather than resolved by precedence.

### 11.2 Assertion vocabulary
| Assertion | Checks |
|---|---|
| `message_received` | A message matching channel/verb/noun/topic arrived at a participant within a timeout |
| `message_not_received` | Negative — used for filter and topic tests |
| `bod_valid` | XSD validation against the packaged schemas |
| `bod_invalid` | Expected-failure path |
| `store_contains` / `store_not_contains` | Participant domain state |
| `provenance_links` | A domain change is attributed to a specific message |
| `cir_registered` | An entry exists in the CIR for a given source/id |
| `cir_equivalent` | A set of source/id pairs share a CIRID |
| `identity_resolved` | A participant's `IdentityMap` holds a live binding |
| `identity_stale` | A participant's cached binding is invalidated |
| `pending_work` | An item is queued for human decision |
| `outbox_state` | Publication intent recorded, held, or posted |
| `isbm_fault` | An expected fault code was returned |
| `confirm_bod` | A `ConfirmBOD` was returned, with expected success/error |
| `classified_as` | An entity carries an expected primary taxonomy class, or aspect class |
| `effective_property_set` | The resolved set for an entity contains/excludes named definitions with expected requirement level |
| `has_property` | An entity holds a property value, optionally with expected value, unit, and `ViaClassId` |
| `property_unmapped` | A property was retained with `Mapped = false` and attributed to an expected source participant |
| `property_orphaned` | A value survived reclassification and is flagged rather than deleted |
| `definition_resolved` | A class or property definition is held locally with an expected `Origin` |
| `definition_narrowing_rejected` | An inbound class definition that widens or contradicts an inherited constraint was refused |
| `validation_finding` | The ENG promotion gate produced an expected finding, blocking release |

Any assertion may carry `on_failure: concern`, which downgrades its verdict from a
failure to a concern. The assertion is still evaluated and what it observed is still
reported; only the effect on the run's outcome changes. It is for conditions that are
genuinely optional in a correct run — for example, a publication that a participant
legitimately suppresses when it has nothing new to say — and is not a way to quieten
an assertion that is failing for an unexplained reason. The default is `fail`, and
anything other than `fail` or `concern` is rejected at load.

### 11.3 Run modes

- `--mode ci` — headless, seeded deterministic RNG, injected clock, `releaseMode: auto` unless overridden, non-zero exit on assertion failure. JUnit XML output for pipeline reporting.
- `--mode demo` — Blazor UI, `releaseMode: manual`, manual stepping with pause-on-step, narration text per step displayed in the tower.

Identical scenario files, identical service methods, identical assertions.

### 11.4 Scenario roster

| Scenario | OIIE mapping | Demonstrates | Phase |
|---|---|---|---|
| `sc01-design-release` | Scenario 1 (UC01) | Pub/sub fan-out; the stewardship gate holding, asserted by showing nothing reached MMS | 1 |
| `sc02-operations-release` | Scenario 2 (UC01) | Approval as the release trigger; CIR as identity bridge; scenario prerequisites | 1 |
| `sc01-greenfield-allocation` | Scenario 1 (UC02) | Identity and code both allocated by the service; re-run safety via relative assertions | 1 |
| `sc11-asset-install` | Scenario 11 (UC05) | MMS as publisher; event timestamp distinct from message time; append-only install/removal history at the receiver; identity unresolved on arrival | 1 |
| `identity-merge` | — | M1–M4; `ChangeEntryCIRID`; cross-window causality; stale cache correction | 2 |
| `rdl-graceful-degradation` | — | Unknown leaf class bound at a known ancestor; leaf properties retained as unmapped; asset still displayed correctly (§6.5.6) | 2 |
| `rdl-definition-propagation` | Scenarios 34, 35 | RDL publishes the leaf class; receivers bind it; unmapped chips clear and orphaned values slot in with no data re-sent | 2 |
| `eng-validation-gate` | — | Promotion blocked by missing `Required` properties and an out-of-range value; findings shown; fixed; promotion succeeds | 2 |
| `local-property-extension` | — | A participant defines a `Local` property, publishes it, and receivers retain it flagged — the "not a fixed schema" argument, and the case for governance | 2 |
| `uc04-product-pull` | Scenarios 7, 8, 25, 26 | Request/response with filters; change advisory fan-out | 3 |
| `reclassification` | — | Reclassifying an asset orphans rather than deletes non-sanctioned values | 3 |
| `uc12-rfi-models` | Scenarios 36, 37, 38 | Multi-participant RFI across REG-MATERIAL, REG-PRODUCT, REG-ASSET, REG-LOCATION | 3 |
| `eng-delta-publish` | Scenario 27, 28 | Incremental publication of 11 changed tags out of 4,000 | 3 |
self-configures, joins scenario 1 mid-flight | 5 |
| `negative-paths` | — | Expired publications; abandoned and recovered sessions; filters matching nothing; XSD-invalid BOD rejected with a fault; duplicate delivery; validation gate blocking promotion; reject-back on stewardship | 5 |
| `notifications` | — | `NotifyListener` push delivery, closing the ISBM conformance skips. Phase 2 rather than 5 because Azure-hosted development makes the callback endpoint addressable from the start (§12.4). | 2 |

### 11.5 Orchestration tables

In the `sandbox` schema:

```sql
CREATE TABLE sandbox.ScenarioRun (
    RunId        UNIQUEIDENTIFIER PRIMARY KEY,
    ScenarioId   NVARCHAR(128) NOT NULL,
    Mode         VARCHAR(8)    NOT NULL,   -- ci | demo
    GitSha       NVARCHAR(64)  NULL,
    StartedAt    DATETIME2     NOT NULL,
    CompletedAt  DATETIME2     NULL,
    Result       VARCHAR(16)   NULL        -- Passed | Failed | Aborted
);

CREATE TABLE sandbox.Assertion (
    Id               BIGINT IDENTITY PRIMARY KEY,
    RunId            UNIQUEIDENTIFIER NOT NULL,
    StepId           NVARCHAR(64)  NULL,
    Name             NVARCHAR(256) NOT NULL,
    Status           VARCHAR(16)   NOT NULL,  -- Passed | Failed | Skipped
    ExpectedSummary  NVARCHAR(MAX) NULL,
    ObservedSummary  NVARCHAR(MAX) NULL,
    At               DATETIME2     NOT NULL
);
```

Cheap to add, and it turns the scenario runner page into a run-history view — flaky-choreography trends without wiring up anything external.

---

## 12. Cross-cutting concerns

### 12.1 Observability and correlation

A `CorrelationId` is generated at the originating release event and carried:

- In the BOD `ApplicationArea/BODID` (or an extension element, if `BODID` semantics must be preserved)
- As an ISBM message topic component or message property
- On every `Message` and `Outbox` row
- As an Application Insights custom dimension on every telemetry event from every participant

The ISBM and CIR providers must propagate it from the inbound message onto their own telemetry. This is a small change to each provider and is the single highest-value observability investment: one KQL query then reconstructs an entire multi-hop exchange as a timeline across all seven participants plus both providers — which is also, incidentally, the artifact worth showing a customer.

W3C `traceparent` is used for HTTP-level correlation within the Sandbox; the `CorrelationId` is the business-level identifier that survives the store-and-forward hop through ISBM, where HTTP tracing does not.

### 12.2 BOD validation

Every inbound and outbound BOD is validated against the packaged XSDs, and the result is recorded on the `Message` row and shown as a badge in the UI. Validation failure does not necessarily abort processing — for negative-path scenarios it must be possible to send an invalid document and observe the receiver's fault behaviour.

Schema resolution is from `Sandbox/Schemas/`, treating the packaged schema zips as authoritative over the published PDFs, consistent with the known defects in the ws-CIR package and the Service Directory 1.0 document.

### 12.3 Channel and topic model

Channel URIs follow the ISBM hierarchical convention, e.g. `/Enterprise/Site/Area/WorkCenter`. The Sandbox uses a consistent prefix so channels are identifiable and purgeable:

```
/OIIE-SANDBOX/{runId?}/Enterprise/{site}/{purpose}
```

Topics are used to distinguish noun types on shared channels, enabling filter-based negative tests.

Phase 1–4: channel configuration is static, from `personality.yaml`. Phase 5: resolved at startup from the Service Directory via `GetIsbmService`.

### 12.4 Azure footprint

The solution is Azure-only end to end. No component depends on local infrastructure, containers, or emulators, and there is no non-Azure runtime dependency in any environment.

| Concern | Service | Notes |
|---|---|---|
| Application host | Azure App Service (Linux, .NET 10) | One plan, one web app per environment. WebSockets enabled for SignalR. Chosen over Container Apps for consistency with the existing provider deployments and to avoid a container build step; revisit only if per-participant process isolation is ever required. |
| Data | Azure SQL, `acme-sql-server` | Per-developer, CI, and demo databases per §6.1. Serverless with auto-pause. |
| BOD payloads | Azure Blob Storage | Container per scenario run; lifecycle rule at 7 days (§6.6). |
| Secrets | Azure Key Vault (`mndot`) | Referenced from app configuration. Managed identity for SQL and Storage where possible; SQL logins per participant remain password-based because the grant model (§6.2) is the point. |
| Identity | Microsoft Entra ID | Single application role on the Sandbox UI. |
| Telemetry | Application Insights | Shared with the ISBM and CIR providers so one correlation ID spans all three (§12.1). |
| Real-time UI | SignalR — in-process for dev and CI, Azure SignalR Service for demo | Demo environment only, to survive App Service scale and restarts during a session. |
| Notifications inbound | App Service HTTPS endpoint, or Dev Tunnels during development | `NotifyListener` callbacks from the ISBM provider. Development uses polling by default, or Visual Studio Dev Tunnels to route callbacks into an F5 session — no deployment needed per change (§6.1). |
| Provisioning | Azure Bicep + `provision.ps1` | Matches existing CIR practice; carries ISBM and CIR settings forward automatically. |
| CI | Azure DevOps or GitHub Actions against the CI database | Run-scoped namespacing per §10; warm-up query before the run. |

Two consequences worth noting:

**Azure-backed development removes the notification blind spot.** `NotifyListener` push delivery is one of the three outstanding ISBM conformance skips, largely because a workstation behind NAT cannot receive the callback. Dev Tunnels or a personal slot make the endpoint addressable without leaving the debugger, so push mode is testable from phase 2 rather than deferred to phase 5.

**The developer inner loop depends on network latency to Azure SQL.** Acceptable, but it makes the reset time target (§10, under 60 seconds) worth measuring early rather than assuming. If reset proves slow against serverless, the mitigation is set-based truncation and bulk fixture load rather than reintroducing a local database.

### 12.5 Security

- Participants authenticate to ISBM with distinct credentials, so the message archive reflects genuinely separate identities and token add/remove operations are exercisable.
- Secrets in Azure Key Vault (`mndot`), referenced from app configuration; nothing in the repository.
- The Sandbox UI itself is protected by Entra ID with a single application role. No per-participant user accounts — the "current operator" is a display-name selector only, recorded in `Provenance.Actor`.
- Sandbox databases and channels are entirely separate from production ISBM/CIR data.

### 12.6 Testing the tool itself

| Suite | Scope |
|---|---|
| `SimHost.Tests` | Runtime units — outbox dispatcher, inbox dedup, identity cache TTL and invalidation, correlation propagation, reset completeness |
| `Mappers.Tests` | Per-personality domain ↔ BOD mapping, including round-trip and schema validation of generated documents |
| `Ecosystem.Tests` | xUnit wrappers invoking the headless scenario runner; one test per scenario file |

Consistent with existing practice: tests run immediately after each substantive change, with raw output shared.

---

## 13. Delivery plan

| Phase | Scope | Exit criteria |
|---|---|---|
| **0** | Extract `Oiie.Isbm.Client` from CIR; shared session helper; CIR refactored to consume it | Existing CIR 15/15 integration and 104 unit tests pass unchanged |
| **1** | SimHost runtime; persistence including the §6.5 tables and chain-resolution engine; outbox/inbox; BOD dispatcher and validator; headless runner; ENG, REG-LOCATION, MMS, **CMS**; `sc01-design-release`, `sc02-operations-release`, `sc11-asset-install` | scenarios 1, 2 and 11 pass in CI end to end; entities classify and resolve an effective property set, with a minimal fixture hierarchy |
CIR merge tooling; `identity-merge`, `rdl-graceful-degradation`
| **3** | REG-PRODUCT, REG-MATERIAL; attribute BODs; `uc04-product-pull`, `uc12-rfi-models`, `eng-delta-publish`, `reclassification` | All phase-3 scenarios pass in CI |
| **4** | Full UI: domain screens, BOD renderer, identity panel, control tower, cluster graph, CIR explorer, kiosk mode, SignalR; snapshot/restore | SC-2 and SC-4 met; demo rehearsed end to end |
| **5** | Service Directory bootstrap; `negative-paths` | SC-6 met; remaining ISBM conformance skips closed |

Phases 1–2 alone constitute a defensible ecosystem regression suite. Phase 4 is what is shown to people. Phase 5 doubles as the acceptance harness for the Service Directory 1.0 implementation.

---

## 14. Open questions and risks

### 14.1 Open questions

**Q1 — CCOM BOD schemas (blocking, highest priority).** The project package supplies the ws-CIR BODs and `Meta.xsd`, but not the CCOM 4.x schemas for the REG nouns: `SyncSegments`, `Get/ShowAssets`, `ProcessAssetSegmentEvent`, `AcknowledgeAssetSegmentEvent`, `Get/ShowModels` and related. Without them the `bod_valid` assertion has nothing to validate against, and given the defect history in the published packages, inferring these shapes is exactly the guessing that has cost time before.

Options: (a) obtain the CCOM package and validate from day one; (b) scope phases 1–2 to BODs for which schemas are held and treat CCOM payloads as opaque-but-shaped until the package lands; (c) reduce phase 1 to ws-CIR BODs only, which weakens the demo considerably. Preference is (a); (b) is the workable fallback.

**Q2 — ENG selection unit.** Is the realistic promotion unit a named version of the whole model, or a scoped promotion (discipline, unit, or an explicit work-in-progress set)? This determines whether the ENG screen is version-centric or selection-centric, and it will read as wrong to a customer immediately if it is off.

**Q3 — Audience weighting.** If the primary audience is Bentley-internal engineering, layers 2 and 3 carry the weight and layer 1 can stay thin. If it is demo-led, layer 1 absorbs most of phase 4 and the phasing shifts earlier. This materially changes phase 4 sizing.

**Q4 — REG-MATERIAL versus MATERIALS.** The OIIE acronym list defines `MATERIALS` as the material/procurement system and `REG-*` as registry components. The roster here treats REG-MATERIAL as a registry-flavoured material master. Worth confirming this matches how the term is used in the audiences being addressed.

**Q5 — Site/enterprise fiction.** A single coherent fictional site with realistic unit numbering, equipment types, and a plausible P&ID structure will make every screen more convincing. Is there an existing Bentley demo dataset that could be reused rather than invented?

**Q6 — Reference data distribution BOD.** Scenarios 34/35 specify reference data moving from an external RDL to an enterprise RDL to O&M systems, but the BOD catalogue's REG rows do not obviously supply a class-and-property-template noun. Options: an existing CCOM noun that fits, a ws-CIR `Category`/`Property` structure pressed into service (imperfect — see §9.2 on registry versus repository), or a clearly-marked local extension. Resolving this depends partly on Q1.

**Q7 — Class fixture depth.** §6.5.8 caps the hierarchy at 20–30 taxonomy classes across four domains. Is that enough to look credible to an owner-operator audience, or does the fixture library need to be visibly larger even if only a subset is exercised? Larger fixtures are cheap to author and free at runtime, so this is a judgement about perception rather than cost.

### 14.2 Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Simulator mistaken for product capability | Customer expectation damage | Persistent `SIMULATOR` marking; explicit scope statement in demo narration; refuse domain feature creep |
| Domain features crowd out harness work | Phase 4 becomes a maintenance-system build | Every domain feature must be justified by a scenario assertion |
| Classification work expands into an ontology build | Phase 2 overruns; effort diverted from the harness | Hard constraints of §6.5.8: shallow fixed hierarchy, no authoring UI before phase 4, no reasoning engine, coarse versioning |
| Registry and repository conflated in the UI | Misrepresents what ws-CIR is for | §9.2 — only discriminating properties registered; full attribute set stays in the participant and travels in CCOM BODs |
| CCOM schemas unavailable | Weakened validation, guessed wire shapes | Q1 options (b)/(c); explicit `NotValidated` status rather than silent assumption |
| Cross-schema shortcut defeats the demonstration | Demo proves nothing | Enforced SQL grants; test asserting no participant context references `tower` |
| Shared channel collisions between parallel CI runs | Intermittent, expensive-to-diagnose failures | Run-scoped channel and registry namespacing (§10) |
| Divergence between demo path and CI path | CI stops being evidence | Single set of service methods; autopilot has no separate code path |
| Sandbox churn affecting provider data | Production impact | Separate database, separate registries, separate channel prefix, separate credentials |
| No offline development path | Work blocked by connectivity or an Azure regional incident | Accepted deliberately in exchange for dialect and behaviour parity. Note the inner loop is still local compile and debug (§6.1) — only the backing services are remote — so the cost is connectivity, not turnaround time. Per-developer databases prevent one developer's state disturbing another's. |
| Reset time degrades against serverless SQL | Demo turnaround suffers; SC-4 missed | Measure reset duration from phase 1; mitigate with set-based truncation and bulk fixture load rather than reintroducing local infrastructure |

---

## 15. Appendix — BOD coverage by phase

Referenced against the REG rows of the BOD catalogue (`mim_5xxx`).

| Phase | BODs |
|---|---|
| 1 | `SyncSegments` (5038, 5042); `ProcessRegistry` / `AcknowledgeRegistry`; `GetRegistry`; `CancelRegistry` |
| 2 | `SyncAssets` (5039, 5043); `Get/ShowAssets` (5005); `ProcessAssetSegmentEvent` / `AcknowledgeAssetSegmentEvent` / `SyncAssetSegmentEvent` (5040, 5041); `GetEquivalentEntries` / `ShowEquivalentEntries`; `ChangeEntryCIRID`; `ConfirmBOD`; reference data distribution from the RDL participant (scenarios 34, 35) — **BOD selection to be confirmed**, see Q6 |
| 3 | `Get/ShowModels` (5006); `GetModelChildRevisions` (5011); `GetModelParentRevisions` (5017); `SyncModels`; `GetManufacturers` → `ShowOrganizations` (5003); `Get/ShowSegments` (5004, 5015, 5018); `GetBreakdownStructureChildren` (5047); attribute queries — `Get/ShowSegmentAttributes` (5021, 5024, 5027), `Get/ShowAssetAttributes` (5022, 5025, 5028), `Get/ShowModelAttributes` (5023, 5026, 5029); type-scoped queries — `GetSegmentsBySegmentType` (5018), `GetAssetsByAssetType` (5019), `GetModelsByModelType` (5020) |
| 5 | `GetIsbmService` / `ShowIsbmService` |

---

**End of specification.**
