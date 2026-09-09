# GetTaxonomySet / ShowTaxonomySet — reconciliation findings

Response to `schemas/ccom/BOD/Messages/New/TaxonomySet-BOD-Implementation-Brief.md`.

Baseline: the vendored `schemas/ccom/XSD/CCOM.xsd`, OAGIS platform 1.2.1
(`http://www.openapplications.org/oagis/9`).

Per §0 of the brief, no CCOM vocabulary was invented. One field could not be
resolved and is reported rather than guessed, which is why only the request BOD
ships.

## §2 Reconciliation table

| Draft reference | Finding | Action |
|---|---|---|
| `targetNamespace="https://www.mimosa.org/ccom4"` | **Wrong.** `CCOM.xsd` line 7 declares `http://www.mimosa.org/ccom4` — `http`, not `https`. Matches `Namespaces.Ccom`. | Moot after the namespace split; CCOM is imported under `ccom:` with the correct URI. |
| `TaxonomySet` complex type | **Confirmed.** `CCOM.xsd` line 1890. Extends `Entity`, carries the `Nameable` group and `Taxonomy*`. Not `final="extension"`, so it is extensible. | Usable when the response BOD is built. |
| `Taxonomy` complex type | **Confirmed.** `CCOM.xsd` line 1872. Extends the abstract `TypeNetwork`, adds `CCOMClass` and `TaxonomySet`. | Usable. |
| `Type` complex type | **Does not exist.** There is no global `Type` declaration. Every `Type` match in `CCOM.xsd` is a *local element* with a concrete type — `SegmentType`, `AssetType`, `PropertyType`, `AgentType`, and so on. The brief flagged this as high risk; the risk is confirmed. | **Unresolved — see below.** |
| `UUIDType` | **Wrong name.** `CCOM.xsd` line 165 declares the complex type `UUID`. | Request schema uses `ccom:UUID`. |
| `oa:ApplicationArea`, `oa:Get` | **Confirmed as global elements** (`OAGIS/Meta.xsd` lines 96 and 469), so `ref=` is legal. But the draft imported `OAGIS/Fields.xsd`, which does **not** declare them — schema compilation failed with "element is not declared" until the import was pointed at `Meta.xsd`. | `ref=` retained; import corrected to `OAGIS/Meta.xsd`. |
| Empty `oa:Get` verb element | **Invalid.** `GetType` extends the abstract `RequestVerbType` (`Meta.xsd` line 524), whose content model requires `oa:Expression` with `maxOccurs="unbounded"` — minimum one. An empty verb element does not validate. | Builder emits an `oa:Expression` carrying the XPath to the selectors, matching the `ActionExpression` idiom in `SyncBodBase`. |
| `xs:include ../CCOM.xsd` | **Bug**, as the brief predicted: `include` requires an identical target namespace, and ours now differs. | Changed to `xs:import`. |

## §3 Namespace decision

Both BODs are unofficial reconstructions, so they are **not** published into the
MIMOSA namespace. `schemas/sandbox/GetTaxonomySet.xsd` targets the existing
Sandbox extension namespace `http://www.openoandm.org/sandbox/extensions/1.0`
(`Namespaces.SandboxExtensions`) and imports CCOM. If MIMOSA later publishes an
official `GetTaxonomySet`, the two remain distinguishable on the wire.

## Unresolved: the ShowTaxonomySet noun container

The draft's `TaxonomySetsType` carries `TaxonomySet`, `Taxonomy` and `Type`
children. The first two resolve; `Type` does not exist as a global CCOM type,
so there is **no confirmed CCOM type to carry an individual RDL class** in the
response.

Per §0 and the §9 escalation trigger, this is reported rather than papered over.
`ShowTaxonomySet` is therefore **not implemented**. Resolving it is a decision,
not a lookup, and the candidates are:

- `ccom:SegmentType` — the concrete class ENG already publishes for
  `rdl:LightingUnit`, so the response would speak the vocabulary the participant
  mappings already consume. Narrower than "any RDL class".
- `ccom:CCOMClass` — referenced by `Taxonomy` itself, but describes CCOM
  metamodel classes rather than RDL classes.
- The `TypeConnection` network inherited via `TypeNetwork` — carry the hierarchy
  as connections and no standalone class elements.
- Obtain the official schema from the MIMOSA member area and discard the drafts.

## Consequence for the RDL class-list work

The participants (ENG, REG-LOCATION, MMS) were to fetch the RDL class list and
**fail closed** if it could not be obtained, using it to validate their
hard-coded mappings. That validation needs a response payload to validate
against, so it is blocked on the decision above. The request leg and its schema
are in place; no consumer-side validation or RDLEngine responder has been built,
because either would have to invent the response vocabulary.

The provisional hard-coded mappings recorded in DR-030 remain the source of
truth in the meantime.

## §1 / §4 / §5 status

Baseline pinned and vendored (`schemas/ccom/XSD/CCOM.xsd`).

For the request BOD, §4 and §5 are delivered by
`tests/Oiie.Sandbox.Tests/TaxonomySetBodTests.cs`, which runs in the normal test
pass:

- The schema compiles against the full vendored CCOM + OAGIS set with every
  import resolving from local disk — no network fetches.
- Positive instances validate: empty selector ("everything available"),
  ShortName + Version, and UUID + `ChangedSince` for an incremental request.
- Negative instances fail, proving the schema is not lax: selector placed before
  the verb, and a missing `releaseID`.

Only the .NET `XmlSchemaSet` engine is exercised, not Saxon/`xmllint` as §4.3
asks — that is the runtime that actually parses these, but engine-specific
leniency is therefore not ruled out.

The response-side samples and the §6 round-trip/codegen check are **not**
delivered, because they depend on the unresolved response payload above.
