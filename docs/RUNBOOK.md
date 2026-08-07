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
