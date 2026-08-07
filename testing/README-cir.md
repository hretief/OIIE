# ws-CIR Provider tests

Four assets, three layers.

| Asset | Purpose |
|---|---|
| `test-cir.ps1` | Functional pass over every operation and the BOD model. Exit code drives CI. |
| `test-isbm-roundtrip.ps1` | Integration test: BODs over a live ws-ISBM broker. |
| `conformance-cir.ps1` | Emits a ws-CIR 1.0 §5 conformance statement. |
| `bruno/` | Same functional coverage, interactive, for exploring and debugging. |

Run `Get-ChildItem -Recurse -Include *.ps1 | Unblock-File` after extracting an
archive, or `RemoteSigned` will block all of them.

---

## Functional test

```powershell
.\test-cir.ps1 -FunctionApp cir-func-xxxxxxxx -ResourceGroup <rg>
```

Fetches the function key itself. `-Detailed` echoes request and response bodies.
`-BaseUrl http://localhost:7071/api -FunctionKey local` runs against `func start`.

Exits non-zero on any failure. Deletes its fixture registries first, so an aborted
run cannot poison the next one.

### Fixture registries

Three, deliberately separate so their counts stay independent:

| Registry | Section |
|---|---|
| `CIR-Test` | core CRUD, queries, GetRegistry filters |
| `CIR-Test-Equiv` | §3.1.2 CIRID merge rules |
| `CIR-Test-Mut` | §3.1.3/3.1.4 and the Delete family |
| `CIR-Test-Bod` | Annex A BOD dispatch |

### The assertions that matter most

Most of the suite is straightforward. These four catch things that would otherwise
pass while being wrong:

- **Anchored wildcards.** `System .` returning 2 proves the regex works; `System`
  returning **0** proves it is anchored per §4. Only the pair together
  demonstrates conformance — a substring match passes the first alone.
- **Same-type OR, cross-type AND.** Two `entryFilter`s must OR; a `registryFilter`
  plus `categoryFilter` plus `propertyFilter` must AND. §3.2.1 is easy to
  implement backwards.
- **PropertyFilter co-occurrence.** `key=IDInSource` with `value=NOT-UNIT101` must
  return 0. Without it, nothing distinguishes "key and value in the same
  PropertyValue" from "both appear somewhere".
- **Batch atomicity.** A two-element batch whose second member faults must leave
  the first **uncommitted**. §3.1 requires it, and it is the kind of thing that
  works on day one and quietly breaks in a later refactor.

### Registry scoping

Every `GetRegistry` assertion folds a `registryFilter` into each `Filter` element.
A CIR is a *shared* registry server — assertions that assume an otherwise-empty
database break as soon as anyone else stores anything. This bit us once already: a
leftover `OGI-Pilot` fixture from manual testing produced exactly doubled counts
and looked like a JOIN fan-out.

One test stays deliberately unscoped and asserts `>= 3` rather than `== 3`, to
prove the absent-filter rule is logical TRUE without depending on what else exists.

---

## ISBM round trip

```powershell
.\test-isbm-roundtrip.ps1 -CirApp cir-func-xxxxxxxx -IsbmApp isbm-func-xxxxxxxxxxxxx `
    -ResourceGroup <rg> -Detailed
```

Acts as an ISBM **consumer** and **publisher** against the channels the CIR
provider listens on, exercising both halves of the Annex A catalogue:

- **Request-response** — posts `ProcessRegistry`, drains the CIR side, reads back
  an `AcknowledgeRegistry`, and asserts `OriginalApplicationArea` sits inside the
  `oa:Acknowledge` verb echoing the original sender.
- **Publication** — posts `CancelRegistry`, drains, and asserts the registry is
  gone **and that no response was posted**. That second half is the point: it
  proves the no-response BODs behave as publications rather than silently
  expecting a reply.

Sessions are opened **lazily on the first drain**, so section 01 drains once to
establish them before asserting they exist. Running `/isbm/reset` immediately
before the test is therefore safe.

Prerequisites: `Isbm__Enabled` true, and both channels created on the ISBM
provider with the correct types. See `../docs/isbm-integration-notes.md`, and
`../docs/isbm-roundtrip-sequence.puml` for a sequence diagram of what this test
actually exercises.

**This test drains explicitly, so it passes even when the `IsbmPoll` timer is
dead.** That is intentional \u2014 it isolates the message contract from the host
schedule \u2014 but it means a green run here does *not* prove the CIR will answer a
real consumer. Verify the timer separately before trusting a production deploy:

```powershell
az functionapp function list -g <rg> -n <app> --query "[].{name:name,trigger:config.bindings[0].type}" -o table
```

`IsbmPoll` must appear as a `timerTrigger`, and App Insights must show `IsbmPoll`
requests at roughly the `IsbmPollSchedule` interval. See the README section
*Hosting the ISBM poller*.

It waits 3s after opening a session and retries on `{"fault":"Session"}` — the
provider acknowledges the open before its Durable Entity commits, and a
human-paced client never notices. It also warms up the CIR app, since a 503
immediately after publish is just the host starting.

Discarded payloads are printed in yellow. That output is how the `inlineContent`
field-name bug was found; if a message cannot be parsed, its first 200 characters
appear there rather than in a log somewhere.

---

## Conformance statement

```powershell
.\conformance-cir.ps1 -FunctionApp cir-func-xxxxxxxx -ResourceGroup <rg> `
    -OutputPath .\conformance-statement.md
```

Conformance under ws-CIR is **declarative**, not pass/fail. §5 requires an
assessment to be qualified by six items, the last being an explicit statement of
non-conformance — so this produces a *document*, and partial conformance exits 0.

Items 1–3 are probed live. Operations classify three ways:

| Result | Meaning |
|---|---|
| Supported | route exists and the store executes it |
| Not supported | route exists, store raises `NotImplemented` → 501 |
| No route | host has no binding at all |

The discriminator is subtle: `DeleteRegistry` on a nonexistent registry returns
404, and so does a missing route. They are told apart by whether the body carries
a problem+json `title`.

Item 3 seeds `conformance-probe-xxxxxxxx`, runs seven pattern checks, and removes
it in a `finally`. `-SkipDataProbes` suppresses that. The checks cover literal,
`.`, `*`, `+`, `?`, the backslash escape, and anchoring. The fixture is
`Alpha A` / `Alpha B` / `Alpha.A` — the third exists so `Alpha\.A` is
discriminating; without a literal-dot value the escape test proves nothing.

`+` is testable here because `EscapeDataString` encodes it as `%2B`. The Bruno
collection cannot cover it in a query string, since a bare `+` decodes to a space.

---

## Known non-conformance

Structural, not a defect list:

- **§5 item 4, SOAP 1.1/1.2** — this is a REST binding. Never claimable without a
  second binding.
- Three spec-package defects and three interpretations are recorded in the root
  `README.md` and emitted in the generated statement.
