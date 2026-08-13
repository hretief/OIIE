# Workflow Orchestration

Interactive UI for driving the OIIE sandbox. Where `SimHost` runs scenarios
end-to-end and checks assertions, this app is for a person working a workflow a
step at a time.

Both are kept deliberately: the audiences differ, and so does what "done" means.
SimHost proves a scenario passes; this shows what a participant sees while it
happens.

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

**Still local state:** the INBOX tables, and every SC04 and SC11 action.

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

## Layout

```
src/
  api.ts     typed client: every call to the sandbox goes through here
  App.tsx    the whole app
```

One file, as generated. Worth splitting when the wired surface grows past the
mock one.
