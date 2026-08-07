# Managed identity migration and ISBM contract consolidation

Date: 2026-08
Status: complete, deployed and verified in the dev environment.

This record exists because the work below was done in a chat session that cannot be
carried into a new IDE window. It captures the reasoning, not just the outcome —
particularly the two findings that were expensive to establish and would be expensive
to rediscover.

## 1. Service Bus moved from SAS key to managed identity

### Why

Publishing this monorepo to GitHub as `OIIE` was blocked by push protection. A live
Azure Service Bus root key was embedded in
`docs/decision-records/ISBM Design Chat.txt`, pasted there as part of a deploy example
during the original ISBM design work.

The key was confirmed still in use: it matched the `ServiceBusConnection` app setting on
the deployed `isbm-func-44p2f3n6dv7p4` Function App. So it could not simply be deleted.

Key Vault was considered and rejected. Storing the key in a vault would still leave a
long-lived credential to manage and rotate. Azure Functions supports identity-based
Service Bus connections directly, so removing the credential entirely was both simpler
and stronger.

### What changed

- `ISBMProvider/Program.cs` — Service Bus client prefers
  `ServiceBusConnection:fullyQualifiedNamespace` and uses `DefaultAzureCredential`.
- `infra/isbm/main.bicep` — no longer reads a SAS rule. Sets
  `ServiceBusConnection__fullyQualifiedNamespace` plus
  `ServiceBusConnection__credential = managedidentity`, and grants the Function App
  **Azure Service Bus Data Owner** on both new and existing namespaces.
- `deploy/isbm/deploy.ps1` — the
  `az servicebus namespace authorization-rule keys list` call is gone.
- Both `local.settings.json` copies — namespace `mndotdev.servicebus.windows.net`,
  no connection string.
- Repository history was squashed before first push, so the key is absent from all
  commits.

### Live cutover sequence

Order mattered. The role assignment was verified to exist *before* the working
credential was removed, so there was never a window where the app had neither:

1. Confirmed principal `98fd8afe-16fe-4386-a029-50927387f592` already held
   Azure Service Bus Data Owner on `mndotdev`.
2. Backed up all 16 app settings.
3. Added the identity settings, then deleted `ServiceBusConnection`.
4. Restarted; all 29 functions loaded. End-to-end suite: 33 passed, 0 failed.
5. Only then rotated **both** SAS keys.

Both keys were rotated, not just the primary, because the leaked connection string could
have carried either. Verified by SHA-256 fingerprint before and after.

### Consequence for local development

There is no Service Bus key to hand out any more. A developer running ISBMProvider
locally authenticates as themselves and needs **Azure Service Bus Data Owner** on the
namespace. A missing role assignment presents as an auth failure at first Service Bus
call, not as a configuration error.

## 2. ISBM contract types shared between CirProvider and Oiie.Isbm.Client

### Why

`IsbmMessage`, `IsbmSessionKind` and `IsbmException` were defined identically in
`CirProvider/Application/IIsbmClient.cs` and `Oiie.Isbm.Client/IIsbmClient.cs`, a
leftover from the three-repository era.

### What was deliberately *not* shared

`CirProvider` keeps its own `IIsbmClient` and `IIsbmSessionStore`.

An initial attempt aliased those to the shared versions and was reverted. The shared
`IIsbmClient` covers the entire Messaging Service Model — channel management,
publication, consumer-request, `SessionExistsAsync` — roughly 25 members, against the
8 that ws-CIR implements. The shared `IIsbmSessionStore` additionally declares
`GetCursorAsync`/`SetCursorAsync`. Adopting either would have forced ws-CIR to implement
ISBM routes it never calls, purely to satisfy an interface.

`SqlIsbmSessionStore` also stays in CirProvider: it is backed by the `cir` schema and is
not reusable.

### The enum ordinal question — the important part

CIR's enum was:

```
IsbmSessionKind { ProviderRequest = 0, Subscription = 1 }
```

The shared enum is:

```
IsbmSessionKind { Publication = 0, Subscription = 1, ConsumerRequest = 2, ProviderRequest = 3 }
```

`ProviderRequest` moves from ordinal 0 to ordinal 3.

This is safe **only because** `SqlIsbmSessionStore` persists the kind by name —
`kind.ToString()` on write, `Enum.Parse<IsbmSessionKind>` on read — and both names it
writes exist in the wider shared enum. Had it stored the ordinal, every existing
`cir.IsbmSession` row recording a `ProviderRequest` session would have silently been
reinterpreted as `Publication`, pointing the provider-request loop at the wrong session.

Compiling proves nothing here. This was verified against live data: the deployed CIR's
persisted sessions were captured before and after redeployment via
`GET /api/isbm/status`, and both rows came back with identical kinds, session ids and
`openedUtc` timestamps.

**If a future change touches this enum, or changes how session kind is persisted, re-check
this.** Reordering the shared enum is harmless today; switching the store to ordinal
persistence would not be.

## 3. Continuous integration

Two workflows, both build-and-verify only, neither requiring Azure credentials:

- `.github/workflows/build.yml` — Release build of `OpenOM.slnx` plus unit tests.
- `.github/workflows/infra.yml` — compiles every `infra/**/main.bicep`, path filtered.

The end-to-end suite in `testing/` is deliberately excluded. It requires a running
SimHost plus the live ISBM and CIR apps, so putting it in CI would mean granting Actions
access to live infrastructure. It stays manual:

```pwsh
cd SimHost;  dotnet run --launch-profile SimHost   # leave running
cd testing;  pwsh -NoProfile -File .\test-sandbox.ps1
```

Note the launch profile is `SimHost`, not `https`. There is no `https` profile; using a
non-existent profile name silently skips `ASPNETCORE_ENVIRONMENT=Development`, which
means `appsettings.Development.json` is never loaded and every participant database
connection fails with `Configuration 'Sandbox:Environment' is not set.` That failure
reads like a config bug but is a bad profile name.

## Verification status

| Check | Result |
|---|---|
| `dotnet build OpenOM.slnx -c Release` | succeeded, 1 pre-existing warning (CS8602, `ConsumerPublicationFunctions.cs:66`) |
| `dotnet test OpenOM.slnx -c Release` | 55 passed |
| GitHub Actions `build` and `infra` | green |
| `testing/test-sandbox.ps1` after ISBM cutover | 33 passed, 0 failed |
| `testing/test-sandbox.ps1` after key rotation | 33 passed, 0 failed |
| `testing/test-sandbox.ps1` after CIR redeploy | 33 passed, 0 failed |
| CIR persisted sessions across redeploy | unchanged |

## Still open

- The ISBM REST *implementation* remains duplicated:
  `Oiie.Isbm.Client/IsbmRestClient.cs` and
  `CirProvider/Infrastructure/Isbm/IsbmRestClient.cs`. Only the contract types were
  shared. Consolidating the implementation means reconciling the wide shared interface
  against ws-CIR's eight operations — a design decision, not a mechanical move.
- No deploy pipelines. Deployment is still driven by the scripts under `deploy/`.
- `SimHost/appsettings.Development.json` has an empty `Storage:BlobServiceUri`, so the
  sandbox reports `storageConfigured is false` and BOD payload bodies are not retained
  locally. Pre-existing; does not affect the suite.
- The GitHub secret-scanning alert on this repository can be closed as revoked.
