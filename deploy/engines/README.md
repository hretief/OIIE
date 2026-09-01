# Deploying the integration engines

`deploy-engine.ps1` provisions and publishes the four engine Function Apps.

```powershell
./deploy-engine.ps1 -Engine cms -Environment dev
./deploy-engine.ps1 -Engine all -Environment dev
./deploy-engine.ps1 -Engine all -Environment dev -InfraOnly -WhatIf
```

| Project | Function app |
|---|---|
| `EngEngine` | `acme-engn-eng-dev` |
| `RegLocationEngine` | `acme-engn-reglocation-dev` |
| `CmsEngine` | `acme-engn-cms-dev` |
| `MmsEngine` | `acme-engn-mms-dev` |

The type code is `engn`, not `eng`, because `eng` is already the *system* code
for Engineering — `acme-eng-eng-dev` would be ambiguous. See
[azure-resource-naming-guidance.md](../../docs/azure-resource-naming-guidance.md).

## One script, four engines

The providers have a copy of `deploy-functionapp.ps1` each, and those copies
have already drifted. The engines differ only in name, peer provider, and
settings prefix, so they are table-driven from a single script instead. Adding a
fifth engine is a new entry in `$engines`, not a new file.

## Engines get no database

There is no `provision-databases.ps1` here and no `CREATE USER` step at the end.
An engine holds an ISBM session, reads BODs, and calls its provider over that
provider's published HTTP API. If an engine ever needs a connection string, the
boundary it exists to demonstrate has already been broken —
[participant-abstraction-spec.md §5.2](../../docs/participant-abstraction-spec.md).

This is the one way the engine scripts must not converge with the provider ones.

## Order matters

Providers first. The script fails fast if `acme-api-<system>-<env>` is missing,
because it reads that app's function key to configure the engine. Deploying an
engine against an absent provider would otherwise produce an app that starts
cleanly and 401s on every poll.

## Settings the script does not decide

`EngEngine__Enabled` and the `RegLocationEngine` ingest flags are set to
`false`. Both derive their channel from `IModelId`, and enabling them before an
iTwin exists gives a poll loop that fails on a timer. Set `IModelId`, then flip
the flag:

```powershell
az functionapp config appsettings set -g HilmarRetiefRG -n acme-engn-eng-dev `
    --settings EngEngine__IModelId=<guid> EngEngine__Enabled=true
```

The `SitesIngestEnabled` legs on CMS, MMS and REG-LOCATION are enabled from the
start. Their channel is enterprise-level rather than iTwin-derived, which is the
whole point of `SyncSites` — it is the message that brings an iTwin context into
being, so it cannot require one to already exist.

## ISBM endpoint

The script points every engine at `acme-api-isbm-<env>`. The `local.settings.json`
files still point at the older `isbm-func-44p2f3n6dv7p4` app. Subscribers and
publishers must share a broker, so if you move one, move all of them — a split
broker shows up as a publication that is never received, with no error anywhere.

## If the publish times out

`func azure functionapp publish` can fail with:

```
Timed out waiting for SCM to update the Environment Settings
```

This reads like a transient race or a disabled SCM basic-auth policy. It is
usually neither — it is CPU starvation on the shared plan. `acme-plan-dev` runs
B3 because B1 could not carry ten AlwaysOn sites, and the symptom of the
undersized plan was this message rather than anything mentioning capacity.

Check `az appservice plan show -g HilmarRetiefRG -n acme-plan-dev` first. Zip
deploy is also more tolerant than `func publish`, because it does not wait on
the SCM settings sync:

```powershell
cd CmsEngine
dotnet publish -c Release -o bin\publish
Compress-Archive -Path bin\publish\* -DestinationPath $env:TEMP\cmsengine.zip -Force
az functionapp deployment source config-zip -g HilmarRetiefRG `
    -n acme-engn-cms-dev --src $env:TEMP\cmsengine.zip --timeout 900
```

A `status` of `4` means success.

## Do not set the topic list in app settings

Configuration array binding **appends** to the value a property was initialised
with rather than replacing it. Setting `CmsEngine__SitesTopics__0` to the value
the code already defaults to produces:

```json
"sitesTopics": ["oiie/ccom:SyncSites", "oiie/ccom:SyncSites"]
```

That is a double subscription — every publication read and applied twice. The
upserts are idempotent so nothing corrupts, which is precisely why it can sit
there unnoticed. `engine/status` reports the bound list; check it after any
settings change. To genuinely override a topic list, set every index *and* make
the code default empty.

## Verifying

```powershell
$key = az functionapp keys list -g HilmarRetiefRG -n acme-engn-cms-dev `
    --query 'functionKeys.default' -o tsv
curl "https://acme-engn-cms-dev.azurewebsites.net/api/engine/status?code=$key"
```

`engine/status` reports configuration and readiness without touching ISBM.
`POST engine/ingest-sites` forces a drain rather than waiting for the timer.
