# Runbook

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
sc11-asset-install         resets, then provisions the location inline before
                           publishing the install and removal events
```

REG-LOCATION is a release gate, not a relay. `sc01` deliberately asserts that
*nothing* reached MMS; if that assertion passes trivially, suspect the gate has
been bypassed rather than that the scenario is weak.

A scenario declaring `setup.reset: true` now resets the sandbox itself before the
run row is created, and reopens the subscriptions its participants declare. That
matters because a reset closes sessions: without the reopen, the run's own
subscription precondition would abort it. `sc01-design-release` therefore passes
twice in a row with no manual reset in between. `sc02-operations-release` sets
`reset: false` precisely because it consumes the queue `sc01` leaves behind.

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


## From cold

```
POST /admin/schema/init             create tables
POST /admin/isbm/channels/ensure    create channels
```

Then the session sequence above.

## After a model change

`/admin/schema/init` short-circuits on a sentinel table and will not add new ones.
Use `/admin/reset`, or `/admin/schema/reset` if ISBM state is known clean.

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
