# Runbook

## Hosts

`{sandbox}` throughout this document is the **Sandbox API** (`Oiie.Sandbox.Api`),
which owns every `/admin/*` and `/health/*` route and runs the ISBM message pumps.
Locally that is `https://localhost:7241`.

`SimHost` is the Blazor operator UI (`https://localhost:7180`). It shares the same
engine in-process but serves no API routes, so its buttons call the Sandbox API over
HTTP using `Sandbox:ApiBaseUrl`. **The API must be running for the UI's reset and
scenario-launch actions to work** — that is the one dependency the split introduces.

`WorkflowOrchestration` is the React interactive UI (`https://localhost:3000`, via
`npm run dev`). It talks only to the Sandbox API, never to SimHost, and Vite proxies
`/admin` to `https://localhost:7241` so the browser stays on one origin in
development. HTTPS and port 3000 are not incidental: the Bentley IMS redirect URI is
registered against that exact origin, so authentication fails on any other. Point the
API elsewhere with `SANDBOX_API`. The two UIs serve different audiences: SimHost
drives end-to-end automated scenario runs, this one is for interactive use.

### Deployed demo environment

The `demo` environment is deployed and verified healthy:

```
API : https://oiie-sandbox-demo.azurewebsites.net
UI  : https://oiie-simhost-demo.azurewebsites.net
```

Redeploy both with:

```
cd deploy/sandbox
.\deploy.ps1 -Environment demo -StorageAccount mndotsandbox
```

The script verifies the result rather than just reporting a successful upload — the
last run reported 5 participants, ISBM configured, storage reachable, and 5
participants connected as their own SQL users. The admin key is reused across
deployments (`sandbox-admin-key-demo` in the `mndot` vault), so an existing key stays
valid:

```
$key = az keyvault secret show --vault-name mndot --name sandbox-admin-key-demo --query value -o tsv
```

`Isbm__ListenerBaseUrl` is set on the deployed API, so ISBM NotifyListener callbacks
are testable against it — they are not against a workstation, which has no address the
provider can reach.

### MMS inventory panel

Selecting the MMS persona shows what `LIGHT_SYSTEM_INVENTORY` actually holds for the
selected iTwin, from `GET /admin/mms/locations?twin=…`. This is independent of the
segment handover workflows: the rows exist whether or not anything was ever handed
over, so an empty segment queue does not mean an empty repository.

The twin is resolved to an `OWNER_ID` through ws-CIR rather than matched on a column
(DR-008, DR-009). Three distinct states are shown, and the difference matters:

- **resolved with rows** — the normal case.
- **resolved, no rows** — the owner exists but holds no inventory.
- **unresolved** — no MMS owner is related to this twin in the registry. No rows are
  shown; falling back to unfiltered would leak one district's inventory to another.
  Fix by relating the owner: `POST /admin/mms/owners/relate`.

Only two seeded owners have inventory: `OWNER_ID` 2 (7200 - Metro Traffic, 9 rows) and
`OWNER_ID` 8 (9600 - District 6, 4 rows). A twin mapped to any other owner correctly
shows resolved-but-empty.

Scoped reads take ~1.9s. That is a known ISBM round-trip cost, not a database problem
— see DR-010 before attempting to optimise it.


## Day zero

Everything back to a clean slate across all three systems. Order matters, and two
steps are outside the Sandbox.

```
1. POST {sandbox}/admin/reset/day-zero
      closes Sandbox sessions, deletes and recreates every channel
      (including the CIR provider's), rebuilds tables and fixtures,
      and seeds CMS owners, CMS sites and ENG's four iTwins

2. POST {cir}/api/isbm/reset                     host key required
      the CIR provider re-opens its sessions. REQUIRED: step 1 destroyed
      its old ones, and a poll loop against a dead session id looks healthy
      while consuming nothing.
      Use the HOST (master) key, not the default function key -- the
      default key returns 401 here:
        az functionapp keys list -g <rg> -n <cir-app> --query masterKey -o tsv

3. POST {sandbox}/admin/cir/registry/delete?confirm=OIIE-SANDBOX
      entries registered earlier keep their CIRIDs otherwise, so a "first"
      registration silently attaches to an identity from a previous run.
      Destroys the registry for every system in it, not just this one, and
      cannot be undone. Re-register afterwards with
      POST {sandbox}/admin/{participantId}/cir/register

4. POST {sandbox}/admin/cir/bootstrap
      registers ENG, CMS and MMS, then relates each seeded twin twice: to
      the CMS site of the same district number, and to the MMS owner of the
      same name. Expect 8 relations, all ok, and zero faults.
      Must follow step 2: relating anything before the provider's sessions
      are back faults on every pair.
      Both relations are needed. The CMS one makes ?twin= reads scope; the
      MMS one makes MMS admit an approved location. With only the CMS
      relation, an approved segment reaches MMS, validates, and is then
      rejected as belonging to no owner it knows.

5. POST {sandbox}/admin/reset
      only if step 1 reported channel errors
```

The four seeded iTwins, which are the ENG-side context the ENG→CMS workflow runs
against. The GUIDs were issued by iTwin and are pinned in `ContextOwnerSeeder`:

| Twin | GUID |
| --- | --- |
| 9100 - District 1 | `523099d2-4291-4d0f-ad7c-65429109ef81` |
| 9200 - District 2 | `d543ebf6-7f25-4c07-a8cf-cc43410b780d` |
| 7200 - Metro Traffic | `02c9fdd8-645d-4d97-8d95-70be46a58345` |
| 9600 - District 6 | `c86c9c10-4487-48f6-8f5b-89701307725c` |

District 6 is **9600**, matching the CMS and MMS owner lists. It is worth noting
because the twins were originally handed over with it numbered 9500: that number
provisions no CMS site, so the relate would have found nothing and District 6
would have stayed unscoped without anything reporting an error.

Each twin is related to two local keys, because CMS and MMS number the same
district differently and each resolves inbound context through its own:

| Twin | CMS site | MMS `OWNER_ID` |
| --- | --- | --- |
| 9100 - District 1 | `9100` | 4 |
| 9200 - District 2 | `9200` | 5 |
| 7200 - Metro Traffic | `7200` | 2 |
| 9600 - District 6 | `9600` | 8 |

The `OWNER_ID` column is derived, not configured: the seeder assigns it from
position in `ContextOwnerSeeder.OwnerNames`, and `MmsOwnerId` reads it back from
the same list. A second hard-coded table would be correct only until someone
inserted a district into that list.

Unlike CMS, MMS caches nothing. CMS writes the CIRID onto its own owner row and
so survives a registry delete; MMS has no column to hold one, so the registry is
the only place its relations exist and **step 4 must be re-run after every step
3**.

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
| `sc11-asset-install` | MMS publishes an install or removal event | CMS |

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

The same applies to **indexes**. The composite index backing the outbox idempotency
guard (DR-011) is absent from any database created before it was added, because
`/admin/schema/init` short-circuits on the sentinel table. Nothing breaks — the guard
is a query, not a schema dependency — so this will not announce itself; the lookup
simply is not index-backed until a day zero runs.

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

Only ENG is twin-scoped today. REG-LOCATION, MMS and CMS are not.

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
