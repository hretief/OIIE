# Handoff — 2026-08-30
session: fn-asset-api-http    branch: feature/asset-api-skeleton

## Where it stands
Function app skeleton runs locally and returns real 200/400/404 responses
from hardcoded fixtures. Nothing touches a database yet.

- `src/functions/getAsset.ts`, `listAssets.ts` — HTTP triggers, wired,
  routes registered via the v4 programming model in `src/index.ts`
- `src/contracts/` — hand-written TS types matching `openapi/assets.yaml`
- `src/http/` — request parsing, error-to-ProblemDetails mapping. Done.
- `src/repository/assetRepository.ts` — INTERFACE ONLY. The single
  implementation is `InMemoryAssetRepository` returning fixtures from
  `test/fixtures/assets.json`. This is the thing to replace next.

## Tried and rejected
- **Prisma for the data layer.** Rejected on cold start — measured ~900ms
  added to first invocation on the consumption plan. Not acceptable for a
  synchronous read API. Going with a thin query layer instead.
- **Generating TS types from the OpenAPI spec at build time.** Tried
  openapi-typescript; output nests everything under `components.schemas.*`
  and the ergonomics were bad enough that the handlers got noisy. Kept the
  types hand-written for now, with the spec as the reviewed source of
  truth. Revisit if the contract starts drifting.
- **Validation inside each trigger.** Moved to `src/http/validate.ts` after
  the second handler duplicated the same twenty lines.

## In-flight / known-broken
- `listAssets` accepts `?page=` and `?pageSize=` and ignores both. The
  contract documents them. Fixtures are small enough that nothing fails
  loudly — easy to miss.
- Two tests in `getAsset.test.ts` are skipped; they assert 404 semantics
  that only make sense against a real store.
- `openapi/assets.yaml` has an `updatedAt` field no fixture populates.

## Environment
- Needs Azurite running before `func start`, or the host fails on init
  with an unhelpful storage error.
- `local.settings.json` is gitignored. Template is in `docs/local-setup.md`.
- Node 20. The v4 model silently misbehaves on 18 — routes register but
  don't resolve.

## Next
Build the real data layer behind `AssetRepository` and connect it to the
contract types. Concretely: SQL-backed implementation, a mapping layer
between row shape and contract shape, then unskip the 404 tests and make
pagination real.

## Open questions — decide before implementing
1. Does the contract shape drive the schema, or does the schema drive the
   contract? We assumed contract-first but never wrote it down, and the
   mapping layer is where that assumption gets tested.
2. Pagination: offset or cursor? The spec currently implies offset. Cursor
   is better under concurrent writes but changes the documented params.
3. Managed identity vs connection string for the SQL connection. Nothing
   provisioned yet, so this is still free to choose.