# Deploying the ws-ISBM provider

Notes from the real deployment of `acme-api-isbm-dev` and `acme-api-isbm-prod`.
Read this before running `deploy-functionapp.ps1`.

## What gets created

| Resource | dev | prod |
| --- | --- | --- |
| Function App | `acme-api-isbm-dev` | `acme-api-isbm-prod` |
| App Service plan | `acme-plan-isbm-dev` | `acme-plan-isbm-prod` |
| Service Bus | `acme-sb-dev` | `acme-sb-prod` |
| Key Vault | `acme-kv-isbm-dev` | `acme-kv-isbm-prod` |
| Storage *(shared)* | `acmestoragedev01` | `acmestorageprod01` |

Infrastructure comes from `infra/isbm/main.bicep`. That template historically
derived every name from `${baseName}-<kind>-${suffix}`, which cannot produce
the agreed convention, so it now takes optional explicit name parameters. They
default to the old expressions, so existing deployments are unaffected.

## Relationship to the old provider

`isbm-func-44p2f3n6dv7p4` is untouched and still serving. It keeps its own
Service Bus (`mndotdev`), storage (`mndot`) and Key Vault (`mndot`). The new
apps share nothing with it, so the sandbox keeps working during cutover.

The new provider starts with **no channels and no sessions**. Nothing is
migrated. Subscription state is tied to session ids the old provider issued,
and replaying them would hand consumers sessions the new provider cannot
honour. Consumers re-open sessions against the new host at cutover.

## Traps

**Purge protection.** The template used to set `enablePurgeProtection: false`,
which Azure rejects outright: *"cannot be set to false"*, because enabling it
is irreversible. The property has to be omitted entirely, not set false.

**Core Tools publish can fail on a brand-new app.** `func azure functionapp
publish` dies with *"Timed out waiting for SCM to update the Environment
Settings"* and does not recover on retry. This is not the SCM basic-auth
problem — the script already enables that. The script falls back to
`az functionapp deployment source config-zip`, which works. If the zip deploy
itself returns 502, restart the app and retry; the SCM site needs a moment on
a freshly created host.

**Basic auth is disabled on new apps.** Publishing needs it, so the script
turns it on. The old app predates that default, which is why it never needed
the step.

**The plan must not be Y1.** Notification dispatch and expiry are Service Bus
and timer triggered. On a consumption plan they wait for the scale controller,
which makes expiry look broken. The script defaults to B1.

## Channels

CIR needs both of these, matching the legacy provider. The provider does not
create them for you, and a missing publication channel shows up as a CIR drain
error rather than anything visible on the ISBM side:

    POST /api/channels  {"channelUri":"/OIIE/CIR/Request","channelType":"Request"}
    POST /api/channels  {"channelUri":"/OIIE/CIR/Publication","channelType":"Publication"}

## Verify

    GET /api/configuration/supported-operations?code=<key>

Expect `securityLevelConformance: 3` and `isChannelCreationEnabled: true`.
Creating a channel is the better smoke test, since it exercises Service Bus
through the managed identity rather than just the host.

## Known defect

`GET` and `DELETE` on `/api/channels/{channelUri}` return 404 for every
encoding tried, on both the new and the old apps. Listing and creating work.
This is pre-existing and not caused by the new deployment, but it means
channels currently cannot be deleted through the API.
