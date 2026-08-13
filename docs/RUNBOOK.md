# Runbook

## Hosts

`{sandbox}` throughout this document is the **Sandbox API** (`Oiie.Sandbox.Api`),
which owns every `/admin/*` and `/health/*` route and runs the ISBM message pumps.
Locally that is `https://localhost:7241`.

`SimHost` is the Blazor operator UI (`https://localhost:7180`). It shares the same
engine in-process but serves no API routes, so its buttons call the Sandbox API over
HTTP using `Sandbox:ApiBaseUrl`. **The API must be running for the UI's reset and
scenario-launch actions to work** — that is the one dependency the split introduces.

`WorkflowOrchestration` is the React interactive UI (`http://localhost:8443`, via
`npm run dev`). It talks only to the Sandbox API, never to SimHost, and Vite proxies
`/admin` to `https://localhost:7241` so the browser stays on one origin in
development. Point it elsewhere with `SANDBOX_API`. The two UIs serve different
audiences: SimHost drives end-to-end automated scenario runs, this one is for
interactive use.

## Day zero

Everything back to a clean slate across all three systems. Order matters, and two
steps are outside the Sandbox.

```
1. POST {sandbox}/admin/reset/day-zero
      closes Sandbox sessions, deletes and recreates every channel
      (including the CIR provider's), rebuilds tables and fixtures

2. POST {cir}/api/isbm/reset                     x-functions-key required
      the CIR provider re-opens its sessions. REQUIRED: step 1 destroyed
      its old ones, and a poll loop against a dead session id looks healthy
      while consuming nothing

3. clear the CIR registry database                (manual)
      entries registered earlier keep their CIRIDs otherwise, so a "first"
      registration silently attaches to an identity from a previous run

4. POST {sandbox}/admin/reset
      only if step 1 reported channel errors
```

Verify before going further:

```
GET {cir}/api/isbm/status      sessions openedUtc should be seconds old
POST {sandbox}/admin/cir/loopback   request/response path healthy
```

Also confirm the CIR is *consuming*, not merely reachable. A CIR that answers
`/api/health` but whose `IsbmPoll` timer is not firing will accept every request
and answer none:

```powershell
az functionapp function list -g <rg> -n <cir-app> --query "[].{name:name,trigger:config.bindings[0].type}" -o table
```

`IsbmPoll` must be present as a `timerTrigger`. If it is missing, the function
failed to index at startup \u2014 usually the `IsbmPollSchedule` app setting. If it is
present but nothing is consumed, the host is not resident: the CIR plan must be
`B1` or higher with Always On.

**A session's `openedUtc` is the most useful field in the whole system.** If it
predates the last channel rebuild or the last ISBM deployment, that session is dead
and its owner does not know. This cost a long debugging session: the CIR provider
was polling a session opened six days earlier, swallowing the fault, and reporting
itself enabled and configured correctly the whole time.

## Every session

```
POST /admin/reset            close sessions, purge channels, recreate tables
POST /admin/eng/tags         add a tag
POST /admin/eng/promote      release
GET  /admin/eng/outbox       check it posted
GET  /admin/eng/messages     two rows, one correlationId
```

`/admin/reset` recreates the channels, so `/admin/isbm/channels/ensure` is only
needed on a genuinely fresh provider.

## Running scenarios

Scenarios are named after the OIIE scenario they realise; the use case is
cross-reference metadata in the file header, not part of the name.

| Scenario | Trigger | Reaches |
|---|---|---|
| `sc01-design-release` | ENG releases a named version | REG-LOCATION only |
| `sc02-operations-release` | a steward approves at REG-LOCATION | MMS |
| `sc01-greenfield-allocation` | ENG publishes without an authored identity | REG-LOCATION |
| `sc11-asset-install` | MMS publishes an install or removal event | OM-RELIABILITY |

Run order matters, and the engine enforces it rather than papering over it:

```
sc01-design-release        run first
sc02-operations-release    requires sc01; fails if the tags are not in REG-LOCATION
sc11-asset-install         requires sc02; fails immediately if MMS does not already
                           hold P-101, because a maintenance process does not
                           perform its own engineering handover
```

REG-LOCATION is a release gate, not a relay. `sc01` deliberately asserts that
*nothing* reached MMS; if that assertion passes trivially, suspect the gate has
been bypassed rather than that the scenario is weak.

Relationships travel on the same two legs as the segments they join. `sc01`
publishes the edge to REG-LOCATION, which retains it *unresolved* — it is held
against the sender's tag numbers because the registry has no codes of its own to
state it with until the endpoints are approved. `sc02`'s approval mints those
codes, resolves the edge, and republishes it to MMS. An edge sitting at
`IsResolved = 0` after `sc02` means the endpoint lookup failed, not that the edge
was rejected.

A scenario declaring `setup.reset: true` now resets the sandbox itself before the
run row is created, and reopens the subscriptions its participants declare. That
matters because a reset closes sessions: without the reopen, the run's own
subscription precondition would abort it. `sc01-design-release` therefore passes
twice in a row with no manual reset in between. `sc02-operations-release` and
`sc11-asset-install` set `reset: false` precisely because each consumes what the
previous one leaves behind.

A reset also purges run history. Running a resetting scenario after a
non-resetting one deletes the earlier run's evidence, and `/admin/scenarios/runs/{id}`
then answers `404` for a run that genuinely completed. Read a run's result before
starting the next scenario, or sequence the resetting scenarios first.

An approval step that approves zero items is a failure, not a no-op. A scenario
that "passed" while approving nothing was previously indistinguishable from one
that worked.

### Inspecting a run

`/runs` lists runs; `/runs/{id}` opens one. The **Results** tab lists each step
with the BODs it emitted, and each links through to the message detail page which
renders the BOD XML and the correlated source, result and audit records. Use that
before reaching for the database: the payload body shown there is the one that
actually crossed the wire, retrieved from the payload store.

| Symptom | Check |
|---|---|
| A step shows no BODs | Either it emitted none, or its result envelope carried the correlation id somewhere the timeline does not look. Compare against the **Message flow** tab, which is built from the participant stores independently |
| A scenario aborts before its first step | The subscription precondition. Declared subscriptions were not open — usually a reset that did not reopen them |
| `sc02` fails on its stewardship precondition | `sc01` has not run, or its queue was consumed by an earlier `sc02` |
| `sc11` reports MMS holds no functional location | Its inline handover did not complete. The scenario provisions the location itself; if that failed, the later steps have nothing to attach to |
| `/admin/schema/seed` reports `0 class(es)` for every participant | The fixture path resolved somewhere the packs are not. Personality packs live in `Oiie.Sandbox.Core` and are *linked* into each host's build output, so under `dotnet run` they sit beside the assembly rather than under the content root. `SandboxCoreRegistration.ResolveContentPath` tries the content root and then the output directory — anything resolving `Sandbox:PersonalitiesPath` must go through it. Symptom is doubly confusing because the registry loads correctly at startup, so `/admin/eng/class-catalog` returns classes while seeding reports none |


## From cold

```
POST /admin/schema/init             create tables
POST /admin/isbm/channels/ensure    create channels
```

Then the session sequence above.

## After a model change

`/admin/schema/init` short-circuits on a sentinel table and will not add new ones.
Use `/admin/reset`, or `/admin/schema/reset` if ISBM state is known clean.

A change that adds a **table or a column** needs `/admin/reset/day-zero`. The other
resets clear rows within the shape that is already there; only day zero rebuilds the
shape. Skipping it after a schema change fails at the first query against the new
column rather than at reset, so the error surfaces a long way from its cause.

## iTwins

ENG scopes its design data by iTwin, so a tag number is unique *within a plant*
rather than globally. Two twins can each hold their own `TIC-500` and they are two
different instruments.

```
GET  /admin/eng/twins              list
POST /admin/eng/twins              register { iTwinId, code, name, description }
GET  /admin/eng/tags?iTwinId=...   read one twin
```

The twin is taken from the request body, or from an `x-itwin-id` header, or — when
neither is given — from ENG's default twin. That fallback is why every route that
predates the twin dimension still behaves as it did.

Two properties of the model are deliberate and easy to undo by accident:

- **`FederationId` stays globally unique.** It is minted per tag and is what MMS and
  CIR correlate on, so scoping it by twin would break identity resolution across
  participants.
- **Isolation is enforced by EF global query filters, not by a `WHERE` clause per
  query.** The filter must reference the context *property*, not a captured field:
  the compiled model is cached per schema, so a captured field freezes the first
  instance's twin into every later context.

The leak worth guarding is promotion, which selects every unpublished tag it can
see. Unscoped, it sweeps another plant's design into the release and publishes it.
Bruno requests 27-28 assert exactly that.

Only ENG is twin-scoped today. REG-LOCATION, MMS and OM-RELIABILITY are not.

## When something is wrong

| Symptom | Check |
|---|---|
| Outbox `state: 3` | `lastError` carries the provider's own fault text, which is usually more precise than anything this side could infer |
| `Login failed for user 'sb_*'` | Almost always the wrong database, not the wrong password. Contained users exist only inside their own database, so the server reports "no such login". `GET /health/secrets` echoes the effective `Sandbox:Database` |
| 404 with `{"fault":...}` | Missing channel or session |
| 404 with no body | No such route on this provider |
| `DeserializationError` | The provider names the exact property and DTO. Copy member names from a verified route rather than from the specification |
| Inbound message never arrives | The inbox polls every 3s. If nothing appears, check the subscriber binding exists in `personality.yaml` and that the channel was not recreated after the publication was posted |
| `No handler registered` | Expected until a handler exists for that verb and noun. The message is archived, not dropped |
| CIR call times out | Work in this order. **(1)** `GET /admin/cir/last?participantId=eng` — durable evidence of the last exchange, safe to call repeatedly. **(2)** `POST /admin/cir/await-response` — re-reads the still-open consumer session; if the response appears, the CIR did reply and only the wait window was short. **(3)** `POST {cir}/api/isbm/drain` — if that completes the exchange, the message path is fine and the CIR's `IsbmPoll` timer is not running: check the plan is `B1`+ with Always On and that App Insights shows `IsbmPoll` requests. **(4)** only then `GET /admin/cir/diagnose`, which *consumes* from the queue and destroys the evidence |
| A consumer looks healthy but consumes nothing | Its session predates the last channel deletion. Channels take their sessions with them, and a poll loop that swallows session faults will not notice |
| `NotValidated` | No XSD held for that namespace. `Schemas/ccom` is empty by design |
| A tag is missing from `/admin/eng/tags` | Almost always the wrong twin, not a lost row. A request naming no twin reads ENG's default, so a tag created under `x-itwin-id` will not appear in it. `GET /admin/eng/twins` lists what exists |
| Invalid column name `ITwinId` | The schema predates the twin columns. `/admin/reset` will not add them — use `/admin/reset/day-zero` |
| A participant's **Repository contents** expander reports a table unreadable | A grant, not a UI fault. The browser reads as that participant's own contained user, so it shows exactly what the participant can see. Compare against `provision.ps1` for that schema before assuming the page is broken |

## State that outlives `/admin/reset`

Deliberately not cleared, and worth knowing about:

- **Key Vault secrets.** Rotate with `provision.ps1 -RotatePasswords`, then restart —
  the configuration provider snapshots at startup, so a running instance keeps the
  old values.
- **Blob payload bodies.** Prefixed per environment and expire on a lifecycle rule.
- **Channels belonging to participants not currently configured.** `/admin/reset`
  only purges channels the loaded personalities bind. `GET /admin/isbm/channels`
  lists what the provider actually holds.
- **The CIR provider's channel.** Ensured but never deleted by `/admin/reset`,
  because deleting it destroys the provider's long-lived session. Only
  `/admin/reset/day-zero` removes it, and that requires the follow-up call.
- **The CIR registry's own data.** Nothing in the Sandbox clears it.
