# Bruno collections

Every REST endpoint in the solution, one collection per host.

## Layout

| Collection | Prefix | Azure app | Requests |
|---|---|---|---|
| `eng/` | `ENG` | `acme-api-eng-dev` | 17 |
| `eng-engine/` | `ENG-ENGINE` | `acme-engn-eng-dev` | 7 |
| `reglocation/` | `REGLOCATION` | `acme-api-reglocation-dev` | 28 |
| `reglocation-engine/` | `REGLOCATION-ENGINE` | `acme-engn-reglocation-dev` | 5 |
| `cir-api/` | `CIR` | `acme-api-cir-dev` | 17 |
| `isbm/` | `ISBM` | `acme-api-isbm-dev` | 26 |
| `mms/` | `MMS` | `acme-api-mms-dev` | 10 |
| `mms-engine/` | `MMS-ENGINE` | `acme-engn-mms-dev` | 2 |
| `cms/` | `CMS` | `acme-api-cms-dev` | 10 |
| `cms-engine/` | `CMS-ENGINE` | `acme-engn-cms-dev` | 2 |
| `sandbox-api/` | `SANDBOX` | `acme-api-sandbox-dev` | 56 |

180 requests. One collection per host rather than one for everything, because
each host has its own base URL and its own key — a single collection would need
a variable per request to express that, which is what an environment file is
for.

## Generated, not hand-written

`tools/generate-bruno.ps1` reads the routes out of the source: `[HttpTrigger]`
attributes in the Functions projects, `app.Map*` calls in `Oiie.Sandbox.Api`.

```powershell
./tools/generate-bruno.ps1
```

**Do not hand-edit these collections.** Rerun the generator and commit the diff.
A request list typed once starts drifting from the routes the moment one
changes, and the drift is invisible — a stale request 404s and looks like a
broken service rather than a stale file.

## Curated collections

Two collections are **not** generated and are not touched by the generator:

| Collection | Why |
|---|---|
| `cir/` | Hand-written ws-CIR conformance run, with ordered seed/verify/teardown steps and real assertions |
| `sandbox/` | Hand-written workflow walkthrough with seeded twins and narrative docs |

Those encode a *sequence* — register, then resolve, then tear down — which a
per-endpoint generator cannot express. `cir-api/` and `sandbox-api/` are the
generated per-endpoint counterparts; the two kinds serve different purposes and
both are worth having.

## Running

Set the secret in the collection's environment before the first run.

Function App hosts take the key on the query string, which every generated
request already carries as `?code={{functionKey}}`:

```powershell
az functionapp keys list -g HilmarRetiefRG -n acme-api-eng-dev `
  --query functionKeys.default -o tsv
```

The sandbox is an App Service, not a Function App: its routes are not under
`/api`, and it authenticates with a header set once on the collection:

```powershell
az keyvault secret show --vault-name mndot --name sandbox-admin-key-dev `
  --query value -o tsv
```

## What a generated request does and does not give you

Each file records **that an endpoint exists and how to reach it**. It does not
claim to be a working call.

- **Route parameters** are Bruno variables. `{scopeId:int}` becomes
  `{{scopeId}}`; the ASP.NET constraint is a server-side parse rule and sending
  it literally produces a URL that 404s for a reason the caller cannot see.
- **Bodies are empty objects.** A `POST` will 400 until a real payload is
  supplied. That is deliberate: a fabricated body that looks authoritative is
  worse than an obvious blank, because the first person to run it debugs the
  wrong thing. Request shapes are in each provider's own documentation.
- **Nothing is asserted.** These are addressability checks, not tests. The
  curated collections above are where assertions live.

One quirk worth knowing: ISBM's `GET /channels/{{channelUri}}` serves both the
single-channel lookup and the list-all — leave the variable empty for the list.
It is a catch-all route (`{*channelUri}`) because channel URIs contain slashes.
