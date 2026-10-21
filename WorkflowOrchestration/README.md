# Workflow Orchestration

Interactive UI for driving the OIIE sandbox — for a person working a workflow a
step at a time, seeing what a participant sees while it happens.

This is the only UI. A Blazor operator UI (`SimHost`) preceded it and has been
removed: it was a useful stepping stone, but the demo runs on this app. Scenario
assertions still run end-to-end, as tests rather than through a UI.

## Running it

The sandbox API must be running first — this app talks to nothing else.

```pwsh
cd Oiie.Sandbox.Api;        dotnet run   # https://localhost:7241
cd WorkflowOrchestration;   npm install; npm run dev
```

Then <http://localhost:8443>.

Vite proxies `/admin` to the API, so the browser stays on one origin and there is
no CORS to debug while building screens. Point it elsewhere with `SANDBOX_API`.

| Variable | Purpose |
|---|---|
| `SANDBOX_API` | API the dev-server proxy forwards to. Default `https://localhost:7241`. |
| `VITE_SANDBOX_API` | Call an API directly instead of through the proxy. That instance must name this app's origin in `Sandbox:AllowedCorsOrigins`. |
| `VITE_SANDBOX_ADMIN_KEY` | Shared key for `/admin/*`. Not needed locally, where the endpoints are open. |

## What is real and what is not

Only part of this app is wired to the sandbox. The rest is still the Figma
strawman it started as, kept so the shape of the workflow is visible.

**Backed by the API:**

- iTwin selector — `GET /admin/eng/twins`
- ENG segments: list, author, edit — `GET`/`POST /admin/eng/tags`
- Class picker — `GET /admin/eng/class-catalog`
- Publish Design — `POST /admin/eng/promote`
- Stewardship queue — `GET /admin/reg-location/stewardship`
- MMS inventory (SC01/SC02, MMS/TAMS persona) — `GET /admin/mms/lightunits?twin={uuid}`

**Still local state:** the other INBOX tables (including CMS), and the
SC04/SC05/SC11 workflows.

MMS inventory shows persisted `LIGHT_UNIT_INVENTORY` rows, not approved registry
proposals. The MMS engine automatically ingests approved streetlights; there is
no manual Store Assets action in SC02's MMS panel. The panel refreshes every
10 seconds while open, with a manual refresh button and a live sidebar count.
The API and UI must be deployed together; Azure dev received both on 2026-10-21,
replacing the previous mock MMS view.

Real panels are labelled `LIVE` with the endpoint they call, and are kept in
their own sections rather than folded into the mock inbox — mixing the two would
make it impossible to tell on screen which rows the sandbox actually holds.

## Vocabulary

The UI says **segment**; the API calls the same record a `Tag`, which is the
process-industry term. Infrastructure does not use "tag", so the translation
happens once in `src/api.ts` and the screens speak only in segments.

## Two behaviours worth knowing

**Publication is all-or-nothing.** A Named Version releases every pending segment
in the iModel together, so there is no per-row selection on the publish path.
Checkboxes there would promise a partial release that a Named Version is not.
See `docs/decision-records/2026-08-eng-imodel-named-versions.md`.

**Editing sends the whole record.** `POST /admin/eng/tags` is an upsert that
assigns every field unconditionally, so a partial payload blanks what it omits.
The edit form loads all current values for exactly this reason.

## Filters that the server applies

Two panels look like they filter client-side and do not.

**The stewardship state toggle re-fetches.** `PROPOSED / APPROVED / ALL` maps to
`?state=` on the endpoint, which defaults to `Proposed`. Approved rows are not in
an unfiltered response at all, so filtering the array the client already holds
would show nothing — the registry has to be asked for them.

**MMS inventory is always scoped by twin.** The API resolves the selected UUID
through CIR's active `ITWIN-SITE/MMS` mapping and only returns light units with
that native `OWNER_ID`. Missing mappings are shown explicitly and never fall
back to all assets; unavailable or malformed provider responses produce a 502
and an error on screen. Switching twins hides the previous site's rows while
the next request completes. Provider and CIR keys stay on the API server;
configure `Providers:Mms` and `Providers:Cir` with their base URLs and keys.

## Layout

```
src/
  api.ts     typed client: every call to the sandbox goes through here
  App.tsx    the whole app
```

One file, as generated. Worth splitting when the wired surface grows past the
mock one.
