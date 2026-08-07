# ws-CIR Bruno collection

Full functional pass over every deployed endpoint. Fifteen requests, ordered so
that seeded state is asserted against and then torn down — a complete run leaves
the database exactly as it found it, so the collection is re-runnable.

## Setup

Open `CIR/Testing/bruno` as a collection in Bruno, select the **azure** environment,
then set the secret variable `functionKey`:

```powershell
az functionapp keys list -g HilmarRetiefRG -n cir-func-44p2f3n6 `
    --query functionKeys.default -o tsv
```

`functionKey` is declared under `vars:secret`, so Bruno keeps it out of the file
and out of source control. `Health` is anonymous; everything else needs it.

## Running

Use **Run Collection** so the sequence is honoured. Individual requests work too,
but 04–09 depend on 02 having seeded, and 15 depends on 14.

Headless, if you want this in CI:

```powershell
npm install -g @usebruno/cli
cd CIR\Testing\bruno
bru run --env azure --env-var functionKey=$key
```

## Expected result

12 requests green, 3 returning 501 by design — `GetRegistry`,
`CreateEquivalentEntries` and `UpdateEntryCIRID` are not implemented yet, and
their tests assert the 501 plus the spec clause in the problem detail. Flip each
to the success case as it lands.

## Coverage

| # | Request | Asserts |
|---|---|---|
| 01 | Health | 200, SQL reachable, schema applied |
| 02 | CreateRegistry seed | 201, nested graph insert, explicit CIRIDs |
| 03 | CreateRegistry duplicate | 409 DuplicateEntryFault, problem+json |
| 04 | GetEntriesByCIRID | 200, both equivalents, graph shape |
| 05 | GetEntriesByCIRID exact target | narrows to one source |
| 06 | GetEntriesByCIRID wildcard | §4 anchored POSIX subset |
| 07 | GetEntriesByCIRID unknown | 200 empty, not 404 |
| 08 | GetEntriesByCIRID malformed | 400 |
| 09 | GetEquivalentEntries | 200, includes specified entry, de-duplicates |
| 10 | GetRegistry | 501 (§3.2.1 pending) |
| 11 | CreateEquivalentEntries | 501 (§3.1.2 pending) |
| 12 | UpdateEntryCIRID | 501 (§3.1.4 pending) |
| 13 | DeleteRegistry unknown | 404 RegistryNotFoundFault |
| 14 | DeleteRegistry | 204, cascade |
| 15 | Verify teardown | 200 empty |

## Fixture

One registry `CIR-Test`, one category `Asset` / `MIMOSA OSA-EAI V3`, three entries:

| IdInSource | SourceId | CIRID |
|---|---|---|
| 234443 | System A | ciridA |
| 423ABC | System B | ciridA |
| TIC-8106 | System C | ciridB |

The two entries sharing `ciridA` are the equivalence assertion the spec exists to
express. `TIC-8106` carries a `ParentEntityID` property so the Property child
table and JSON round-trip are covered.

CIRIDs are supplied explicitly rather than minted with `createCirid`, because
CreateRegistry returns no body — the spec's AcknowledgeRegistry BOD carries no
generated identifiers either. Fixed UUIDs keep the suite deterministic.

## Not covered

The ws-ISBM channel binding. That needs a live broker and is covered by
`../test-isbm-roundtrip.ps1` instead.

Also note `+` in a wildcard cannot be tested from a query string here — a bare `+`
decodes to a space. Use `%2B`, or rely on the conformance script, which encodes it
properly.
