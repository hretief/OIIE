# Sandbox host split: API, Blazor UI, and why neither is a Function App

Date: 2026-08
Status: implemented and building; not yet deployed or verified in Azure.

This record exists because the reasoning below is not visible in the resulting
code. The outcome — three projects where there were two — looks arbitrary without
it, and the two questions it settles ("why is the sandbox not a Function App like
the other two?" and "why are there two App Services?") will be asked again by
anyone reviewing the Azure footprint and noticing the odd one out.

## 1. SimHost was split into engine, API, and UI

### Why

`SimHost` had become two things at once: the REST surface for the sandbox
(`/admin/*`, `/health/*`, scenario execution) and the Blazor Server operator UI.
`Program.cs` carried roughly 1,900 lines of endpoint definitions ahead of the
Razor component wiring.

That was tolerable while the only client was the UI in the same process. It stopped
being tolerable once a second, external client — the React Workflow Orchestration
app — needed the same operations over HTTP, because the API surface had no
existence independent of the app that happened to host it.

### What changed

- **`Oiie.Sandbox.Core`** (new) — the engine. Everything under `Application`,
  `Domain`, `Infrastructure`, `Personalities`, `PersonalityPacks` and `Scenarios`
  moved here via `git mv`, so history follows the files.
- **`Oiie.Sandbox.Api`** (new) — the REST host. Owns `SandboxAdminEndpoints`
  (extracted verbatim from `Program.cs`), `AdminKeyMiddleware`, and the hosted
  message pumps.
- **`SimHost`** — now Blazor only. References Core; defines no routes.
- `SandboxCoreRegistration` — a shared composition root, so both hosts register an
  identical object graph. Two hosts that build the engine slightly differently
  would be a very expensive bug to find.
- `SandboxCapabilities` — shared predicates for "is storage configured", "is ISBM
  configured", so registration and the health endpoints cannot disagree.
- `SandboxAdminKey` — the header and configuration names. Once the caller and the
  middleware live in different projects, the header name is protocol, not an
  implementation detail of the middleware.

### Two things this cost that were not obvious in advance

**Implicit usings disappeared.** Engine code compiled under
`Microsoft.NET.Sdk.Web`, which supplies `ILogger<>`, `IConfiguration`,
`IWebHostEnvironment` and others implicitly. A plain class library does not, and
the first build produced 44 `CS0246` errors. Fixed with a `FrameworkReference` to
`Microsoft.AspNetCore.App` plus explicit `<Using>` items in the csproj, rather
than editing dozens of files.

**The UI kept calling itself.** `Runs.razor` passed `Nav.BaseUri` into
`SandboxResetService` and `ScenarioLauncher`, both of which POST to `/admin/*`.
Before the split that was self-referential and correct. Afterwards it pointed at a
host that no longer serves those routes — and it still compiled. Resolved with
`SandboxApiEndpoint`, which reads `Sandbox:ApiBaseUrl` and falls back to the
caller's own address when unset, so a recombined host would still work.

This is the characteristic failure mode of the whole exercise: **the compiler
cannot see a host boundary.** Every remaining defect from this work was of the
same shape — code that builds and then addresses the wrong process.

## 2. Two App Services, not one

### Why

Both UIs are kept, because they serve different audiences: the Blazor app drives
end-to-end automated testing, the React app is for interactive users. That means
the API and the Blazor UI are separately addressable, and a single site cannot
host both entry points.

### The division

| | `oiie-sandbox-{env}` | `oiie-simhost-{env}` |
|---|---|---|
| Project | `Oiie.Sandbox.Api` | `SimHost` |
| Serves | `/admin/*`, `/health/*` | Blazor Server UI |
| Message pumps | yes | **no** |
| WebSockets | off | required |
| Health probe | `/health/participants` | `/` |

The API keeps the historic name. `Isbm__ListenerBaseUrl`, the CIR's configuration
and existing scripts all hold `oiie-sandbox-{env}`; renaming it would break them
without an error message pointing at the cause.

### The rule that matters

**Only the API runs the message pumps**, and that is enforced in code —
`SimHost/Program.cs` calls `AddSandboxCore` but never `AddSandboxMessagePumps` —
not in Bicep, where it could be edited into existence by someone making the two
site definitions "consistent".

Two pumping hosts would mean two consumers racing the same ISBM sessions. Messages
would be delivered to whichever host polled first and appear to vanish from the
other, intermittently, under load. That is a much worse failure than the cold-start
cost of getting it right.

The Blazor UI is deliberately **not** a thin client: it reads participant tables
and payload blobs directly through Core. So it needs the same Key Vault and Storage
data-plane grants as the API, under its own system-assigned identity. Two sites
cannot share one.

## 3. The API stays an App Service, not a Function App

The ISBM and CIR providers are isolated-worker Function Apps. The sandbox API is
not, and the asymmetry is deliberate rather than an oversight.

Three reasons, in order of weight:

**The pumps are resident poll loops.** `InboxPump` polls every participant and
channel binding every three seconds, indefinitely; `OutboxDispatcher` is the same
shape. On Functions this becomes a timer trigger, which reintroduces a failure the
CIR has already suffered and that `docs/RUNBOOK.md` documents: a host that answers
`/api/health` while its `IsbmPoll` timer is not firing accepts every request and
answers none. Avoiding it requires B1 or higher with Always On — a resident host,
at which point nothing has been gained over App Service.

**Scenario runs outlast the execution cap.** A run takes a minute or more. Functions
bounds execution (five minutes by default on Consumption). A scenario growing past
that limit would fail in a way that is hard to distinguish from a hang.

**In-memory state is read across requests.** `CirTelemetry` and `InboxPump` hold
`ConcurrentDictionary` state; `ParticipantRegistry` and `IsbmClientAccessor` hold
loaded personality graphs as singletons. `/admin/cir/last` reads telemetry the pump
wrote — the runbook calls it "durable evidence of the last exchange", and it is
durable only because the writer and reader are the same resident process. Under
Functions scale-out, a later request can land on an instance that never saw it.

The providers are event-driven edge adapters: stateless, short, bursty. Functions
fits them. The sandbox is an orchestrator with resident state and a UI behind it.
Different workload, different host. The consistency worth having is between the
hosting model and the work, not between the projects.

The one inconsistency worth revisiting is authentication: the providers use
`x-functions-key`, the sandbox uses a custom `x-sandbox-admin-key` guard. That
difference is incidental rather than principled.

## Known gaps

- Not deployed. The Bicep compiles and the scripts parse, but the two-site
  topology has not been through a real `deploy.ps1` run. First deployment will
  need role-assignment propagation time before Key Vault calls succeed.
- Adding a second site changes the role-assignment GUIDs, so first deployment
  creates new assignments rather than updating existing ones.
- Two sites now share a B1 plan. The pumps plus a Blazor circuit plus API traffic
  on one small plan may need a larger SKU; nothing has been measured.
- The React app's CORS origin is not yet known, so `allowedCorsOrigins` defaults
  to empty and the deployed React app will fail preflight until it is set.
