# Implementation Brief: GetTaxonomySet / ShowTaxonomySet BODs

**Audience:** coding agent / developer implementing two CCOM BODs that have no published MIMOSA schema.
**Inputs provided:** `GetTaxonomySet.xsd`, `ShowTaxonomySet.xsd` (reconstructions — treat as drafts, not authority).

---

## 0. Read this first — the single most important constraint

These two BODs are **not** published by MIMOSA. They are named in OIIE Scenario 34
(Standard RDL → Enterprise RDL) and Scenario 35 (Enterprise RDL → O&M) as BODs that
"should be supported," but no released XSD exists publicly; MIMOSA holds in-progress
BOD work to members only.

Therefore:

- **Do not invent CCOM vocabulary.** Every element name, type name, and attribute you
  use must either (a) resolve to a real declaration in the `CCOM.xsd` of your target
  release, or (b) live in *our* extension namespace and be explicitly flagged as ours.
- If you cannot find a CCOM type to carry a piece of data, **stop and report it** rather
  than inventing a plausible-sounding element. A list of unresolved fields is a
  successful outcome for this task; a schema full of guessed names is not.
- The drafts supplied contain known-unverified names (see §2). Assume they are wrong
  until proven right.

---

## 1. Pin the baseline

1. Determine the target CCOM release. Do not proceed on ambiguity — confirm which of
   the following you are building against and record it in the repo README:
   - CCOM 4.0.0, or
   - CCOM 4.1.0-RC1
2. Obtain the official distribution zip for that release and commit the schema set to
   the repo under `schemas/ccom/<version>/` **unmodified**. This is the reference copy;
   nothing in it may be edited.
3. Record the OAGIS platform version in use. CCOM 4.0 references OAGIS Platform
   Specification **1.2.1**; confirm the `oa:` schema files shipped in the distribution
   match, and note the exact `oa:` target namespace URI as declared in those files.
4. **Verify the namespace URI.** The drafts use `https://www.mimosa.org/ccom4` as both
   `targetNamespace` and default. Open the official `CCOM.xsd` and copy its
   `targetNamespace` verbatim. If it differs from the draft, the draft is wrong.

---

## 2. Reconciliation pass (do this before writing any code)

Open the official `CCOM.xsd` and confirm or correct each of the following. Produce a
table of findings.

| Draft reference | Where used | What to verify |
|---|---|---|
| `TaxonomySet` (complex type) | base type in `ShowTaxonomySet` | Exists as a named, **extensible** complex type. Confirm it is not `final="extension"`. Confirm its actual child particle — the draft assumes UUID / ShortName / FullName / Description / EffectiveStatus and Taxonomy associations. |
| `Taxonomy` (complex type) | `TaxonomySetsType` | Exists under this exact name. |
| `Type` (complex type) | `TaxonomySetsType` | **High risk.** `Type` is a suspiciously generic name; the real class may be `EntityType`, `AssetType`, or similar, or may be abstract with concrete derivations. |
| `UUIDType` | `GetTaxonomySet` selector | Exists under this name; check whether CCOM uses a restriction on `xs:string` with a UUID pattern, and reuse it rather than declaring `xs:string`. |
| `oa:ApplicationArea`, `oa:Get`, `oa:Show` | both BODs | Confirm these are **global element declarations** in the OAGIS schema (so `ref=` is legal). If OAGIS exposes them only as types, switch to `name=`/`type=`. |
| Root attributes | both BODs | Confirm `releaseID` / `versionID` / `systemEnvironmentCode` / `languageCode` names and cardinality against an existing released CCOM BOD, e.g. the `SyncAssetSegmentEvents` reference example. **Copy the released BOD's attribute block exactly** rather than trusting the draft. |

**Structural rule to preserve while correcting:** every CCOM BOD is
`Root(Verb+Noun) → oa:ApplicationArea + DataArea(oa:Verb + Noun)`. Do not restructure.
Take an existing released BOD XSD from the distribution as the literal template and make
ours differ only in the noun.

---

## 3. Namespace and versioning decision

Do **not** publish into the MIMOSA namespace. A future official `GetTaxonomySet` will
collide with ours and there will be no way to tell them apart on the wire.

- `targetNamespace` for our two BODs: an internal URI under our own domain, versioned,
  e.g. `.../ccom-ext/taxonomyset/1.0`.
- Import the official CCOM namespace; do **not** `include` it (include requires the same
  target namespace, and ours differs). The drafts use `xs:include` — **this is a bug and
  must be changed to `xs:import` with a namespace prefix** once the namespace is split out.
- Consequence: `TaxonomySetResponseType` extending `ccom:TaxonomySet` becomes a
  cross-namespace extension. Confirm this is legal for that type and that the resulting
  instance documents carry the right prefixes on inherited children (`elementFormDefault`
  in the *defining* schema governs, not ours).
- Add a header comment block to each XSD stating: reconstruction, not official, source
  scenarios, target CCOM version, date, and the extension-namespace rationale.

---

## 4. Build the validation harness

Deliver a script that runs in CI and fails the build on any schema or instance error.

1. **Schema compilation.** Compile both XSDs together with the full official CCOM +
   OAGIS schema set. Every import must resolve from local disk — no network fetches.
   Use an XML catalog or explicit `schemaLocation` rewriting so builds are hermetic.
2. **Instance validation.** Validate sample instances (§5) against the compiled set.
3. Run this two ways to catch engine-specific leniency:
   - Saxon (or `xmllint --schema`) for the standards-conformant check.
   - The .NET `XmlSchemaSet` / `XmlReader` path, since that is the runtime that will
     actually parse these. Both must pass.
4. Fail on warnings, not just errors. Unresolved `schemaLocation` often surfaces as a
   warning and silently produces a lax validation that passes everything.

---

## 5. Sample instances (required deliverables)

Author these by hand, then validate. They are the acceptance evidence.

**Positive cases**
- `GetTaxonomySet` — by UUID only.
- `GetTaxonomySet` — by ShortName + Version, no UUID.
- `GetTaxonomySet` — incremental: UUID + `ChangedSince`.
- `GetTaxonomySet` — empty selector (means "everything available").
- `ShowTaxonomySet` — one set, several taxonomies, several types, with `oa:Show`
  echoing the originating BODID from the corresponding Get.
- `ShowTaxonomySet` — paged response, exercising whatever record-count/paging fields
  `oa:Show` actually provides in your OAGIS version. **Verify these exist before
  writing the sample**; do not assume field names.

**Negative cases** (must fail validation — proves the schema is not lax)
- Noun element placed before the verb in `DataArea`.
- Missing `releaseID` on the root.
- `ShowTaxonomySet` with zero `TaxonomySet` children.
- A misspelled child element inside `TaxonomySet`.

---

## 6. Round-trip / codegen check

If the solution binds these BODs to classes:

- Generate bindings from the corrected XSDs and confirm the generated `TaxonomySet`
  type reuses the official CCOM binding rather than emitting a duplicate class in a
  parallel namespace. Duplicate CCOM types is the classic failure here and it will not
  show up until integration.
- Serialise → validate → deserialise → re-serialise and assert the two XML documents
  are canonically equal.

---

## 7. Transport wiring (ISBM)

The scenarios specify the ISBM topic and channel conventions. Wire them as
configuration, not hard-coded strings, and confirm against the scenario documents before
release. Documented forms include:

- Topic: `OIIE:S35:V1.0/CCOM:GetTaxonomySet:R1.0`
- Channel: `\Enterprise\TaxonomySet\Get`

Request/response correlation is via the OAGIS `BODID` — the `ShowTaxonomySet` must echo
the originating `GetTaxonomySet` BODID in `oa:Show`. Implement and test that correlation
explicitly; it is the only link between the two messages.

---

## 8. Definition of done

- [ ] Target CCOM release pinned and official schemas vendored unmodified.
- [ ] Reconciliation table (§2) completed, with every draft name either confirmed
      against `CCOM.xsd` or corrected — and any unresolvable field reported, not invented.
- [ ] `xs:include` replaced with `xs:import`; extension namespace applied.
- [ ] Both XSDs compile against the full official schema set, offline, in CI.
- [ ] All positive samples validate; all negative samples fail, on both engines.
- [ ] Round-trip test passes with no duplicate CCOM types in generated bindings.
- [ ] README records: these are unofficial reconstructions, the scenarios that motivate
      them, the CCOM version targeted, and a migration note for replacing them when
      MIMOSA publishes official schemas.

---

## 9. Escalation triggers — stop and ask

- `TaxonomySet` turns out to be non-extensible, abstract, or absent in the target release.
- The target release ships a query schema (CCOM 4.0 dropped Query-By-Example in favour of
  "a different query schema"). If present, the `GetTaxonomySet` selector in §5 must be
  **replaced** by that query schema rather than hand-rolled — do not build both.
- An official `GetTaxonomySet` / `ShowTaxonomySet` is found in the distribution or the
  MIMOSA member area. In that case discard these drafts entirely and use the official ones.
