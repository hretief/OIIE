# ISBM Service Provider — Azure Functions

A cloud-native implementation of the **ISBM (Information Service Bus Model)** as defined by
the OpenO&M **ISA-95.00.06 Messaging Service Model**, built for the **OIIE (Open Industrial
Interoperability Ecosystem)** use cases. Runs on **Azure Functions (.NET 10 isolated worker)**
with **Durable Entities** for session state and **Azure Service Bus** as the messaging backbone.

## What this is

ISBM is a standardized broker that sits between industrial systems (MMS, O&M, CONTROL/CMS,
ORM, REG, EDGE/IIoT, ERP) and gives each one a single interoperability interface instead of
N custom point-to-point integrations. The OIIE defines 15 Use Cases decomposed into 42
Scenarios, and the mapping rule is simple:
- **Publish** scenarios → ISBM Publish-Subscribe services
- **Push / Pull** scenarios → ISBM Request-Response services

Once those two message exchange patterns are built, all 42 scenarios become channel topology
and CCOM BOD schema configuration — not new code.

### Conformance

This deployment implements the **REST / OpenAPI 3.0.1** interface only. SOAP 1.1/1.2 is
**not supported** (declared partial conformance per the spec's mechanism, surfaced via
`GET /api/configuration/supported-operations`). Every other conformance requirement — channel
management, notifications, expiration listeners, content filtering, security tokens, message
forwarding/traceability — is implemented.

## Architecture

```
OIIE Participant Systems (REST clients)
        │
   Azure Front Door / API Management (TLS, OAuth2, mTLS at Level 3)
        │
   Azure Functions (.NET 10 isolated worker)
   ├── HTTP-triggered functions (26 ISBM REST operations)
   ├── Durable Entities (per-session read cursor + request/response correlation)
   ├── Service Bus-triggered functions (notification + expiration dispatch)
   └── Content Filter Engine (XPath 1.0 + JSONPath)
        │
   ┌────┴────────────────────────────────────────────┐
   │  Azure Service Bus    (topics for pub-sub,      │
   │                        queues for request-resp)  │
   │  Azure Table Storage  (channels, sessions,       │
   │                        request/response          │
   │                        correlation)              │
   │  Azure Blob Storage   (claim-check for large     │
   │                        CCOM BODs)                │
   │  Azure Key Vault      (encrypted security        │
   │                        tokens, Level 2+)         │
   └──────────────────────────────────────────────────┘
```

### Key design decisions

1. **Settle-on-read.** ISBM's `Read` and `Remove` are two separate HTTP calls that can be
   minutes apart, but a Service Bus peek-lock is short-lived. So `Read` completes the broker
   message and records the read in the Durable Entity cursor; `Remove` operates on that cursor.
   No broker lock is held across the two calls.

2. **Session-type gating.** Every operation checks the session type and returns `SessionFault`
   (422) when the session exists but is the wrong kind, per the spec's fault tables.

3. **Global fault middleware.** `IsbmFaultException` thrown anywhere is caught by `FaultMiddleware`
   and returned as structured JSON: `{ "fault": "Channel", "message": "..." }` with the spec's
   status codes (404 ChannelFault, 422 OperationFault, 400 NamespaceFault, 401 SecurityTokenFault).

4. **ChannelUri routing.** Azure Functions doesn't support catch-all `{*param}` mid-route, so
   channelUri is in the request body for session-open and security-token operations, and catch-all
   `{*channelUri}` (last segment) for GetChannel and DeleteChannel.

5. **Token validation at the gate.** Auth is checked when opening a session on a secured channel.
   Once the session is open, the server-assigned sessionId (a GUID) is proof of authorization.
   Subsequent operations use the sessionId in the URL — no repeated auth headers needed.

6. **No SQL Server.** All persistence uses Azure Table Storage on the existing storage account —
   zero additional resources, zero additional cost. Table Storage handles the access patterns
   (point reads by key, partition scans by channelUri) efficiently at any scale.

7. **Service Bus connection flexibility.** The app accepts either a full connection string
   (local dev / SharedAccessKey) or a bare namespace hostname (production / managed identity).

## Project layout

```
src/IsbmProvider/
  Program.cs                          # Isolated host + DI wiring
  host.json                           # Durable Task hub + Service Bus options
  local.settings.json                 # Config template (no secrets)

  Models/
    Enums.cs                          # ChannelType, SessionType, SecurityLevel
    Channel.cs                        # Channel record
    Messages.cs                       # MessageContent, IsbmMessage, ResponsePost
    Sessions.cs                       # SessionMetadata, SessionState, FilterNamespaces
    Configuration.cs                  # SupportedOperations, SecurityDetails
    Faults.cs                         # IsbmFaultException + fault kinds

  Abstractions/
    Ports.cs                          # IChannelStore, IMessageBroker, IPayloadStore,
                                      #   ITokenVault, IFilterEngine, INotificationDispatcher
    ICorrelationStore.cs              # Request→consumer session correlation
    ISessionRegistry.cs              # Session lookup for notification dispatch

  Infrastructure/
    TableChannelStore.cs              # Azure Table Storage (IsbmChannels + IsbmTokens)
    ServiceBusMessageBroker.cs        # Azure Service Bus (topics + queues + notifications)
    BlobPayloadStore.cs               # Azure Blob Storage (claim-check for large BODs)
    KeyVaultTokenVault.cs             # Azure Key Vault (encrypted tokens, Level 2+)
    ContentFilterEngine.cs            # XPath 1.0 (BCL) + JSONPath (Newtonsoft)
    TableSessionRegistry.cs           # Azure Table Storage (IsbmSessions)
    TableCorrelationStore.cs          # Azure Table Storage (IsbmCorrelations)
    HttpNotificationDispatcher.cs     # HTTP PUT to subscriber ListenerURLs
    InMemoryChannelStore.cs           # In-memory fallback (offline dev)
    InMemoryCorrelationStore.cs       # In-memory fallback (offline dev)
    InMemorySessionRegistry.cs        # In-memory fallback (offline dev)
    EntityNaming.cs                   # ChannelURI → valid Service Bus entity names
    Stubs.cs                          # Stub adapters (none active in production)

  Durable/
    SessionEntity.cs                  # One Durable Entity per session (read cursor)

  Http/
    Responses.cs                      # JSON + ISBM fault helpers, channelUri decode
    FaultMiddleware.cs                # Global IsbmFaultException → structured JSON
    TokenValidator.cs                 # Validates auth on secured channel operations

  Functions/
    ChannelManagementFunctions.cs     # §5.2  Channels + security tokens
    ProviderPublicationFunctions.cs   # §5.5  Publisher side of pub-sub
    ConsumerPublicationFunctions.cs   # §5.6  Subscriber side (settle-on-read)
    ProviderRequestFunctions.cs       # §5.7  Provider side of request-response
    ConsumerRequestFunctions.cs       # §5.8  Consumer side of request-response
    ConfigurationDiscoveryFunctions.cs    # §5.9  Conformance + security details
    NotificationDispatchFunctions.cs      # Service Bus-triggered notification dispatch

infra/
  main.bicep                          # Azure deployment (greenfield + brownfield)
  main.parameters.json                # Parameter defaults
  README.md                           # Deployment guide

test-isbm.ps1                         # End-to-end PowerShell test script
conformance-tests.ps1                 # ISBM 2.1 Section 9 conformance suite (18 items)
```

## Implementation status

Every port has a real, production-ready implementation:

| Port | Implementation | Storage |
|---|---|---|
| `IChannelStore` | `TableChannelStore` | Table Storage (`IsbmChannels` + `IsbmTokens`) |
| `IMessageBroker` | `ServiceBusMessageBroker` | Service Bus (topics + queues) |
| `IPayloadStore` | `BlobPayloadStore` | Blob Storage (claim-check) |
| `IFilterEngine` | `ContentFilterEngine` | XPath 1.0 (BCL) + JSONPath (Newtonsoft) |
| `ITokenVault` | `KeyVaultTokenVault` | Key Vault (falls back to stub without KV) |
| `ICorrelationStore` | `TableCorrelationStore` | Table Storage (`IsbmCorrelations`) |
| `ISessionRegistry` | `TableSessionRegistry` | Table Storage (`IsbmSessions`) |
| `INotificationDispatcher` | `HttpNotificationDispatcher` | Direct HTTP PUT to subscriber endpoints |

## REST route map

### Channel Management (§5.2)

| Operation | Method | Route | Auth | Notes |
|---|---|---|---|---|
| CreateChannel | POST | `/api/channels` | No | Optional `securityTokens` array in body |
| GetChannels / GetChannel | GET | `/api/channels/{*channelUri}` | No | Empty URI = list all |
| DeleteChannel | DELETE | `/api/channels/{*channelUri}` | If secured | Also removes Service Bus entities |
| AddSecurityToken | POST | `/api/security-tokens` | If already secured | channelUri + securityTokens in body |
| RemoveSecurityToken | DELETE | `/api/security-tokens` | Yes | channelUri + securityTokens in body |

### Publication Sessions (§5.5 – §5.6)

| Operation | Method | Route | Auth |
|---|---|---|---|
| OpenPublicationSession | POST | `/api/publication-sessions` | If secured |
| PostPublication | POST | `/api/sessions/{sessionId}/publications` | No (session authorized) |
| ExpirePublication | DELETE | `/api/sessions/{sessionId}/publications/{messageId}` | No |
| ClosePublicationSession | DELETE | `/api/publication-sessions/{sessionId}` | No |
| OpenSubscriptionSession | POST | `/api/subscription-sessions` | If secured |
| ReadPublication | GET | `/api/sessions/{sessionId}/publication` | No |
| RemovePublication | DELETE | `/api/sessions/{sessionId}/publication` | No |
| CloseSubscriptionSession | DELETE | `/api/subscription-sessions/{sessionId}` | No |

### Request-Response Sessions (§5.7 – §5.8)

| Operation | Method | Route | Auth |
|---|---|---|---|
| OpenProviderRequestSession | POST | `/api/provider-request-sessions` | If secured |
| ReadRequest | GET | `/api/sessions/{sessionId}/request` | No |
| RemoveRequest | DELETE | `/api/sessions/{sessionId}/request` | No |
| PostResponse | POST | `/api/sessions/{sessionId}/response` | No |
| CloseProviderRequestSession | DELETE | `/api/provider-request-sessions/{sessionId}` | No |
| OpenConsumerRequestSession | POST | `/api/consumer-request-sessions` | If secured |
| PostRequest | POST | `/api/sessions/{sessionId}/requests` | No |
| ExpireRequest | DELETE | `/api/sessions/{sessionId}/requests/{messageId}` | No |
| ReadResponse | GET | `/api/sessions/{sessionId}/requests/{requestMessageId}/response` | No |
| RemoveResponse | DELETE | `/api/sessions/{sessionId}/requests/{requestMessageId}/response` | No |
| CloseConsumerRequestSession | DELETE | `/api/consumer-request-sessions/{sessionId}` | No |

### Configuration Discovery (§5.9)

| Operation | Method | Route |
|---|---|---|
| GetSupportedOperations | GET | `/api/configuration/supported-operations` |
| GetSecurityDetails | GET | `/api/configuration/security-details` |

## Service Bus mapping

| ISBM concept | Azure Service Bus |
|---|---|
| Publication channel | Topic `pub-{hash(channelUri)}` |
| Subscription session | Subscription named by SessionID with SQL rule on `isbm.topics` |
| Request channel (requests) | Queue `req-{hash}` (providers compete to read) |
| Request channel (responses) | Topic `resp-{hash}`, one subscription per consumer session |
| Expiry (xs:duration) | `ServiceBusMessage.TimeToLive` via `XmlConvert.ToTimeSpan` |
| Topics | ApplicationProperty `isbm.topics = "\|A\|B\|"` for broker-side fan-out |
| Read → Remove | Settle-on-read: peek-lock completed within one HTTP call |
| Response routing | `ICorrelationStore` maps request MessageID → consumer SessionID |
| Large CCOM BOD | Claim-checked to Blob above 192 KB |
| Notification events | Published to `isbm-notifications` topic on every post |
| Channel deletion | Removes topics/queues from Service Bus (no orphans) |

## Security model

### How channel security works

Channels can be **open** (no tokens — anyone can operate) or **secured** (one or more tokens — callers must authenticate).

**Securing a channel at creation time:**
```json
POST /api/channels
{
  "channelUri": "/Enterprise/Site/AssetConfig",
  "channelType": "Publication",
  "securityTokens": [
    { "username": "MMS-App", "password": "s3cret123" },
    { "username": "OM-System", "password": "an0therP@ss" }
  ]
}
```

Tokens are stored encrypted in Azure Key Vault (Level 2+ requirement). The response returns
opaque `securityTokenIds` (Key Vault secret names), never the credentials.

**Authenticating to a secured channel:**

Pass the token as a standard HTTP `Authorization` header when opening a session:

```
Authorization: Basic BASE64(username:password)
```

For example, `Authorization: Basic TU1TLUFwcDpzM2NyZXQxMjM=` is `Base64("MMS-App:s3cret123")`.

Auth is checked **once at session-open time**. The returned `sessionId` (a server-assigned GUID)
is proof of authorization — subsequent operations use the sessionId in the URL with no repeated
auth headers.

**Where auth is enforced:**

| Operation | Auth required? | Why |
|---|---|---|
| CreateChannel | No | Creating something new; tokens in body establish security |
| GetChannel / GetChannels | No | Discovery — clients find channels before authenticating |
| DeleteChannel | If secured | Proves authorization for destructive action |
| AddSecurityToken | If already secured | Prevents unauthorized lockout |
| RemoveSecurityToken | Always | Proves authorization to weaken security |
| Open*Session | If secured | **The gate** — proves caller is allowed on this channel |
| All session operations | No | SessionId is proof of authorization |
| Configuration discovery | No | Public capability advertisement |

### ISBM Security Levels

| Level | What it means | Azure implementation |
|---|---|---|
| 1 – None | Dev/test only | No TLS, self-signed certs |
| 2 – Core | Intra-enterprise | TLS + tokens stored encrypted in Key Vault |
| 3 – Inter-Enterprise | Cross-org (default) | mTLS + Entra RBAC + B2B federation |
| 4 – Defense | Highly secure | Managed HSM + per-message envelope encryption |

## Notification pipeline

When a message is published, subscribers with a `ListenerURL` are notified automatically:

1. **Broker publishes notification event** to the `isbm-notifications` Service Bus topic
2. **`NotifyOnMessage` trigger** fires, queries `ISessionRegistry` for sessions with ListenerURLs
3. **`HttpNotificationDispatcher`** makes the spec-defined callback:
   ```
   PUT {listenerUrl}/notifications/{sessionId}/{messageId}
   Body: { "topics": ["AssetSegmentEvent"], "requestMessageId": null }
   ```
4. Subscriber receives the notification and calls `ReadPublication` to get the message

Retry is handled by Service Bus: if the HTTP call fails, the trigger doesn't complete the
message, Service Bus redelivers up to `maxDeliveryCount` times, then dead-letters.

The expiration listener works the same way via the `isbm-expired` queue:
```
PUT {expirationListenerUrl}/expirations/{sessionId}/{messageId}
```

## Running locally

### Prerequisites

- .NET 10 SDK
- Azure Functions Core Tools v4
- Azure Service Bus namespace (Standard or Premium)
- Azure Storage account (or Azurite for local emulation)
- Azure Key Vault (optional — stub fallback for local dev without KV)

### Setup

1. Update `local.settings.json` with your Azure resources:
   ```json
   {
     "Values": {
       "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
       "AzureWebJobsStorage": "UseDevelopmentStorage=true",
       "ServiceBusConnection__fullyQualifiedNamespace": "your-namespace.servicebus.windows.net",
       "BlobPayloadStore__serviceUri": "https://your-storage.blob.core.windows.net/",
       "KeyVault__uri": "https://your-vault.vault.azure.net",
       "AzureWebJobs.NotifyOnMessage.Disabled": "true",
       "AzureWebJobs.NotifyOnExpiry.Disabled": "true"
     }
   }
   ```

   - `AzureWebJobsStorage` — use `UseDevelopmentStorage=true` for Azurite, or a real
     storage connection string. This is also used for Table Storage (channels, sessions,
     correlations).
   - `ServiceBusConnection` — full connection string with SharedAccessKey for local dev.
   - `KeyVault__uri` — set to your vault URI, or leave as `REPLACE` to use the stub
     (always validates, good for dev without Key Vault).
   - Notification triggers — disable locally unless you've created the `isbm-notifications`
     topic and `isbm-expired` queue on your Service Bus namespace.

2. Build and run:
   ```powershell
   dotnet restore
   dotnet build
   func start
   ```

3. Verify startup — all 26 functions plus `SessionEntity` should be listed, with
   `NotifyOnMessage` and `NotifyOnExpiry` shown as disabled (if configured).

### Enabling notifications locally

1. Create the Service Bus entities (or let the Bicep deployment do it):
   ```powershell
   az servicebus topic create --namespace-name your-ns --resource-group your-rg --name isbm-notifications
   az servicebus topic subscription create --namespace-name your-ns --resource-group your-rg --topic-name isbm-notifications --name dispatch
   az servicebus queue create --namespace-name your-ns --resource-group your-rg --name isbm-expired
   ```

2. Set in `local.settings.json`:
   ```json
   "AzureWebJobs.NotifyOnMessage.Disabled": "false",
   "AzureWebJobs.NotifyOnExpiry.Disabled": "false"
   ```

3. Use [webhook.site](https://webhook.site) or a local listener as the `listenerUrl`
   when opening a subscription session.

## Testing

An end-to-end PowerShell test script (`test-isbm.ps1`) exercises all ISBM flows.

### Prerequisites

Allow unsigned scripts for the current PowerShell session:
```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

### Running tests

**Basic (local, no notifications):**
```powershell
.\test-isbm.ps1
```

**With notification callbacks:**
```powershell
.\test-isbm.ps1 -ListenerUrl "https://webhook.site/your-unique-id"
```

**Against a deployed Azure instance:**
```powershell
.\test-isbm.ps1 -BaseUrl "https://your-func.azurewebsites.net/api"
```

**Keep test data after the run:**
```powershell
.\test-isbm.ps1 -SkipCleanup
```

### What the script tests

| Section | What it exercises |
|---|---|
| **1. Configuration Discovery** | `GetSupportedOperations` — conformance statement, security level, filtering capabilities. `GetSecurityDetails` — TLS, KMS, RBAC status. |
| **2. Channel Management** | Create Publication + Request channels. Duplicate channel rejection (422). GetChannel by URI. GetChannels (list all). |
| **3. Pub-Sub Flow** | OpenPublicationSession → OpenSubscriptionSession → PostPublication → ReadPublication (verify topics, content) → RemovePublication → ReadPublication again (verify 404 = empty) → close both sessions. |
| **4. Request-Response Flow** | OpenProviderRequestSession → OpenConsumerRequestSession → PostRequest → ReadRequest → RemoveRequest → PostResponse → ReadResponse (verify correlation) → RemoveResponse → close both sessions. |
| **5. Notifications** | Open subscription with ListenerURL → publish → verify PUT callback arrives at listener with `{ "topics": [...] }` body. Only runs with `-ListenerUrl`. |
| **6. Secured Channel** | CreateChannel with initial `securityTokens` → open session WITHOUT auth (verify 401 rejection) → open session WITH `Authorization: Basic` header (verify 201) → publish on secured channel → delete secured channel with auth. Requires Key Vault to be configured. |
| **7. Cleanup** | Delete test channels (which also removes Service Bus topics/queues). Skip with `-SkipCleanup`. |

### Manual testing with Bruno / curl

**Create an open channel and publish:**
```bash
# Create channel
curl -X POST http://localhost:7253/api/channels \
  -H "Content-Type: application/json" \
  -d '{"channelUri":"demo","channelType":"Publication"}'

# Open pub session
curl -X POST http://localhost:7253/api/publication-sessions \
  -H "Content-Type: application/json" \
  -d '{"channelUri":"demo"}'
# → {"sessionId":"abc-123-..."}

# Open sub session
curl -X POST http://localhost:7253/api/subscription-sessions \
  -H "Content-Type: application/json" \
  -d '{"channelUri":"demo","topics":["Test"]}'
# → {"sessionId":"def-456-..."}

# Publish (use pub sessionId)
curl -X POST http://localhost:7253/api/sessions/abc-123-.../publications \
  -H "Content-Type: application/json" \
  -d '{"messageContent":{"mediaType":"application/xml","inlineContent":"<Test>Hello</Test>"},"topics":["Test"]}'

# Read (use sub sessionId)
curl http://localhost:7253/api/sessions/def-456-.../publication

# Remove
curl -X DELETE http://localhost:7253/api/sessions/def-456-.../publication

# Cleanup
curl -X DELETE http://localhost:7253/api/channels/demo
```

**Create a secured channel:**
```bash
# Create with token
curl -X POST http://localhost:7253/api/channels \
  -H "Content-Type: application/json" \
  -d '{"channelUri":"secure","channelType":"Publication","securityTokens":[{"username":"App1","password":"secret"}]}'

# Open session (requires auth)
curl -X POST http://localhost:7253/api/publication-sessions \
  -H "Content-Type: application/json" \
  -H "Authorization: Basic $(echo -n 'App1:secret' | base64)" \
  -d '{"channelUri":"secure"}'
```

## Deploying to Azure

A deployment script (`deploy/deploy.ps1`) handles infrastructure provisioning, building,
publishing, and post-deploy verification in one command.

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

**Greenfield (everything new):**
```powershell
.\deploy\deploy.ps1 -ResourceGroup rg-isbm -Location eastus2
```

**Brownfield (reuse existing resources):**
```powershell
.\deploy\deploy.ps1 -ResourceGroup hilmarretiefrg `
  -ExistingServiceBusName mndotdev `
  -ExistingStorageAccountName mndot `
  -ExistingKeyVaultName mndot
```

The script auto-fetches the Service Bus connection string — no need to paste it manually.

**Code-only republish (skip infrastructure):**
```powershell
.\deploy\deploy.ps1 -ResourceGroup hilmarretiefrg `
  -FunctionAppName isbm-func-44p2f3n6dv7p4 -SkipInfra
```

**Infrastructure-only (skip code publish):**
```powershell
.\deploy\deploy.ps1 -ResourceGroup hilmarretiefrg `
  -ExistingServiceBusName mndotdev `
  -ExistingStorageAccountName mndot `
  -ExistingKeyVaultName mndot `
  -SkipPublish
```

**Hosting plan \u2014 read before deploying to production.** `-PlanSku` defaults to
`B1` with Always On, and that default is deliberate. `NotifyOnMessage` and
`NotifyOnExpiry` are Service Bus triggered background work; on a Consumption
(`Y1`) plan the host is not resident, so notifications and expiry events are
deferred or never delivered while every HTTP call still returns 200. Use
`-PlanSku Y1` for evaluation only, and `EP1` when VNet integration is required.
Do not change the plan by hand \u2014 Bicep is declarative and the next deployment
reverts it. See `infra/README.md`.

See `infra/README.md` for full parameter reference, manual `az` commands, and troubleshooting.

### Running tests against the deployed instance

After deployment, run the same test scripts against the Azure endpoint:

```powershell
# End-to-end tests
.\Testing\test-isbm.ps1 -BaseUrl "https://<functionAppName>.azurewebsites.net/api"

# ISBM 2.1 conformance suite
.\Testing\conformance-tests.ps1 -BaseUrl "https://<functionAppName>.azurewebsites.net/api"

# With notifications
.\Testing\conformance-tests.ps1 `
  -BaseUrl "https://<functionAppName>.azurewebsites.net/api" `
  -ListenerUrl "https://webhook.site/your-id"
```

## Table Storage schema

All tables are auto-created on first access. No migration step needed.

| Table | PK | RK | Purpose |
|---|---|---|---|
| `IsbmChannels` | `"channels"` | Base64(channelUri) | Channel registry |
| `IsbmTokens` | Base64(channelUri) | tokenId (KV secret name) | Token assignments |
| `IsbmSessions` | Base64(channelUri) | sessionId | Active sessions (notification lookup) |
| `IsbmCorrelations` | `"corr"` | requestMessageId | Request→consumer session mapping |

## Conformance testing

A dedicated conformance test script (`conformance-tests.ps1`) validates all 18 items from the
ISBM 2.1 Section 9 conformance checklist:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\conformance-tests.ps1                                          # local
.\conformance-tests.ps1 -ListenerUrl "https://webhook.site/id"   # with notifications
.\conformance-tests.ps1 -BaseUrl "https://func.azurewebsites.net/api"  # Azure
```

| # | Conformance Item | Status |
|---|---|---|
| 1 | Channel Management Service | ✓ Tested |
| 2 | Notification Service | ✓ Tested (requires `-ListenerUrl`) |
| 3 | Expiration Listener Service | ✓ Configured (dead-lettering enabled) |
| 4 | Provider Publication Service | ✓ Tested |
| 5 | Consumer Publication Service | ✓ Tested |
| 6 | Provider Request Service | ✓ Tested |
| 7 | Consumer Request Service | ✓ Tested |
| 8 | Message Forwarding (OriginalMessageID) | ✓ Tested |
| 9 | SOAP 1.1/1.2 | Declared non-conformant (REST-only) |
| 10 | HTTP 1.1 | ✓ Tested (status codes) |
| 11 | OpenAPI 3.0.1 | ✓ Tested |
| 12 | XPath 1.0 filtering (XML) | ✓ Tested (match + reject) |
| 13 | JSONPath filtering (JSON) | ✓ Tested |
| 14 | Transport Layer Security | ✓ Tested (HTTPS in Azure) |
| 15 | WS-Security UsernameToken | ✓ Tested (via HTTP Basic mapping) |
| 16 | HTTP Basic auth | ✓ Tested (secured channel lifecycle) |
| 17 | Other token formats | ○ Bearer support implemented |
| 18 | Conformance statement | ✓ Tested (partial conformance declared) |

## Remaining work

1. **Machine-generated contract tests** — generate client stubs from the ISBM 2.1 OpenAPI YAML
   files (published at `openoandm.org/isbm/2.1/openapi/`) and run schema-level validation
   against every endpoint to complement the behavioral conformance tests
