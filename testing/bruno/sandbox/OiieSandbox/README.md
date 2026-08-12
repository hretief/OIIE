# OIIE Sandbox — Bruno collection

Admin routes need `x-sandbox-admin-key`, set once in `collection.bru`. Fetch it with:

```powershell
az keyvault secret show --vault-name mndot --name sandbox-admin-key-demo --query value -o tsv
```

Two environments: `Local` (`http://localhost:5199`) and the collection default,
which points at the deployed demo.

## Sequences

**00-05 — health and reset.** Run `04-full-reset` before a clean demo. After a
schema change use `/admin/reset/day-zero` instead, since only that recreates tables.

**06-10 — the ordinary ENG loop.** Add a tag, promote, watch the outbox drain and
the message archive fill. None of these name an iTwin, so they land in ENG's
default twin and behave exactly as they did before the twin dimension existed.

**20-28 — iTwin isolation.** The reason the twin exists. Run in order:

| | |
|---|---|
| 20 | list twins |
| 21, 22 | register Plant A and Plant B |
| 23, 24 | create `TIC-500` in **both** |
| 25, 26 | read each twin back, by query string and by header |
| 27, 28 | release Plant A, confirm Plant B untouched |

The interesting request is **24**. Before iTwin scoping it either violated the
unique index on `TagNumber` or upserted Plant A's tag, replacing its description
with Plant B's. Its tests assert the two tags are distinct rows with distinct
`FederationId`s — the identity is deliberately *not* twin-scoped, because it is
minted per tag and is what MMS and CIR correlate on.

**27-28** guard the leak that would matter most: promotion selects every
unpublished tag it can see, so an unscoped read would sweep another plant's design
into the release and publish it. 27 asserts `tagCount` is 1; 28 asserts from Plant
B's side that its tag is still `WorkInProgress` with no version stamped on it.

25's third assertion is easy to overlook and worth keeping: `TIC-106` lives in the
default twin, so seeing it in Plant A's list would mean the query filter is not
applying at all.

## Running headless

```powershell
cd testing/bruno/sandbox/OiieSandbox
bru run 20-eng-twins-list.bru 21-eng-twin-a-register.bru 22-eng-twin-b-register.bru `
        23-eng-add-tag-twin-a.bru 24-eng-add-tag-twin-b.bru 25-eng-tags-twin-a.bru `
        26-eng-tags-twin-b-header.bru 27-eng-promote-twin-a.bru `
        28-eng-verify-twin-b-unreleased.bru --env Local
```

Expect 9 requests and 11 tests passing.

Do **not** reach for `bru run . --tests-only` to shorten that. It runs only the
requests that carry tests, skipping 21-24, so 27 promotes a twin that was never
registered and returns 422. The setup requests are the fixture; naming them is the
point.

Running the whole collection with `bru run .` is fine, but note it includes
`04-full-reset` — so anything you want to keep, and any scenario state the run
depends on, is gone. Re-run the scenarios afterwards if the environment is meant to
be left in a post-handover state.

Re-runnable without a reset: registering a twin is idempotent, and adding a tag is
an upsert. Only 27 is order-dependent — a second run finds Plant A's tag already
published and releases nothing, so reset (or a fresh tag number) before repeating
that part.

Note that collection-level `vars:pre-request` are substituted into requests but are
**not** readable from test scripts via `bru.getVar`. Tests assert twin ids
literally for that reason; values captured with `bru.setVar` in a post-response
script do carry across.
