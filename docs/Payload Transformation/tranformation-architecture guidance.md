# Transformation Architecture Guidance: Pluggable XSLT Definitions for the ISBM Interoperability Solution

## Document Purpose

This paper recommends a transformation approach for the ISBM-based interoperability solution. It is written to satisfy a specific architectural requirement:

> **Transformation definitions must be plug-and-play** — they must be authored, versioned, deployed, and swapped independently of the runtime code, without redeploying the integration engine.

It covers three things:

1. The recommended transformation approach (why XSLT as a pluggable definition).
2. The recommended tooling (authoring, engine, and governance).
3. The end-to-end workflow showing how the pieces fit into the interop solution.

---

# 1. Requirements

| # | Requirement | Implication |
|---|---|---|
| R1 | Transformation definitions must be **pluggable** (hot-swappable artifacts) | Definitions live outside the compiled code |
| R2 | Definitions must be **versioned and governed** | Stored in Git, tagged with a `mappingVersion` |
| R3 | Definitions must be **authorable by non-developers** where possible | A visual authoring option is required |
| R4 | Must **scale to large release payloads** (1,000+ objects) | Streaming-capable execution |
| R5 | Must run on the existing **.NET 10 Azure Functions** stack | .NET-compatible engine |
| R6 | BODs are **XML** | XML-native transformation technology |
| R7 | Must support **two-way transformation** (Native→Semantic and Semantic→Native) | Definition language must be bidirectional-capable (two artifacts) |

---

# 2. Recommended Approach

## 2.1 Use XSLT as the Pluggable Transformation Definition

The transformation definition should be **XSLT 3.0**.

The critical architectural insight is the separation of three layers:

```text
XSLT   = the transformation DEFINITION   (W3C standard language, versioned in Git)
Saxon  = the ENGINE that executes XSLT    (runtime inside the Azure Function)
Mapper = an optional GUI that authors XSLT (design-time convenience)
```

XSLT satisfies the pluggability requirement (R1) directly because:

- It is a **declarative artifact**, not compiled code. The integration engine loads it at runtime.
- It is **engine-agnostic and portable** — the same stylesheet runs on any conformant processor.
- It is a **single file per direction**, making it trivial to version, diff, review, and swap.
- It is the **W3C interoperability standard** for XML-to-XML transformation, so it is not tied to any one vendor.

This means a new or corrected mapping can be deployed by **publishing a new XSLT artifact**, not by rebuilding and redeploying the Function app.

## 2.2 Why Not Hard-Coded Mapping in Code

Embedding mapping logic in C# would violate R1 and R2:

- Every mapping change requires a **code change, build, and redeploy**.
- Mapping knowledge is **scattered across the persistence orchestration**.
- Business analysts **cannot read or own** the mapping.
- There is **no clean version boundary** for a given mapping revision.

XSLT externalizes the mapping as a governed, swappable artifact — which is exactly the plug-and-play behavior required.

## 2.3 Two Definitions per Participant (R7)

Because there are two transformations in the pattern, each participant maintains **two XSLT artifacts**:

```text
Native → Semantic   (publisher-side stylesheet)
Semantic → Native   (subscriber-side stylesheet)
```

XSLT is inherently one-directional, so the inverse is a **separate stylesheet**, kept consistent through round-trip tests.

---

# 3. Recommended Tooling

The stack separates cleanly into **authoring**, **execution**, and **governance**.

## 3.1 Execution Engine — Saxon (Saxon-EE via SaxonCS)

**Recommendation: standardize on Saxon-EE, executed via SaxonCS inside the .NET 10 Azure Functions.**

| Reason | Detail |
|---|---|
| XML-native | Purpose-built for XML tree transformation (R6) |
| Streaming | Saxon-EE implements **XSLT 3.0 streaming** → constant-memory processing of large release payloads (R4) |
| .NET support | SaxonCS runs XSLT 3.0 / XQuery 3.1 on modern .NET (R5) |
| Standards | Full XSLT 3.0 conformance, `xsl:key`, maps, user functions for enum/unit/reference logic |

> Note: the built-in `System.Xml.Xsl.XslCompiledTransform` supports **only XSLT 1.0** — no streaming, no 3.0 features. It is **not** sufficient. Saxon-EE is required for the scale and feature set.

## 3.2 Authoring — Choose Based on Priority

There is a genuine trade-off between "Saxon-native authoring" and "best low-code UX + guaranteed XSLT 3.0". Both emit standard XSLT that Saxon runs.

| Tool | Emits standard XSLT | Saxon as design engine | XSLT 3.0 / streaming | Best for |
|---|---|---|---|---|
| **Stylus Studio** | ✅ (XSLT *is* the source of truth; true round-trip) | ✅ Saxon-EE embedded | ⚠️ Historically 1.0/2.0-focused | Saxon-native authoring, developer-adjacent authors |
| **Altova MapForce** | ✅ (but editable design is proprietary `.mfd`) | ❌ Own engine; exports XSLT | ✅ XSLT 3.0 | Non-technical authors, guaranteed 3.0 output |
| **oXygen XML Editor** | ✅ | ✅ Saxon-EE embedded | ✅ 3.0 | Developer authoring/debugging |

**Guidance:**

- If **non-technical authorship (R3) and guaranteed XSLT 3.0 (R4)** dominate → **MapForce**, treating Saxon purely as the runtime executor. Commit **both** the `.mfd` design and the generated `.xslt`.
- If **Saxon-native, standard-XSLT round-trip** dominates → **Stylus Studio**, validating that generated stylesheets meet 3.0/streaming needs (or accept 2.0 where payload size allows).
- **oXygen + Saxon-EE** is the developer's choice for hand-refining and debugging any stylesheet regardless of origin.

**Regardless of authoring tool, the runtime is always XSLT executed by Saxon.** The GUI is optional; the XSLT artifact is the portable, Git-versioned source of truth.

## 3.3 Governance — Git + CI + Validation

| Concern | Tooling |
|---|---|
| Version control | **Git** — one repo (or folder) per participant, holding both direction stylesheets |
| Regeneration (MapForce path) | **MapForce Server** in CI regenerates XSLT from `.mfd` so design and artifact never drift |
| Input/output validation | **XSD** validation stages before and after transform (fail-loud, not silent) |
| Unit testing | **XSpec** (open-source XSLT unit-test framework; works with Saxon) |
| Reference data (enums/units) | Externalized lookup tables loaded via `document()` / `xsl:key`, versioned alongside the stylesheet |

---

# 4. Pluggable Definition Design

The plug-and-play behavior (R1) is realized by treating each XSLT as a **versioned, dynamically-loaded artifact** keyed by message context.

## 4.1 Artifact Naming Convention

```text
{participant}.{direction}.{version}.xslt

eng.native-to-semantic.v3.2.0.xslt
reg-location.semantic-to-native.v3.2.0.xslt
```

## 4.2 Definition Registry

A lightweight registry (config table or blob index) maps message context to the correct stylesheet:

```text
Participant     Direction            MappingVersion   Artifact
-----------     ------------------   --------------   ---------------------------------
ENG             Native→Semantic      v3.2.0           eng.native-to-semantic.v3.2.0.xslt
REG-LOCATION    Semantic→Native      v3.2.0           reg-location.semantic-to-native.v3.2.0.xslt
```

The message envelope carries the `mappingVersion`, so the engine can select the exact stylesheet used — essential for reconciliation lineage.

## 4.3 Runtime Loading

The Azure Function:

1. Reads the message context (participant, direction, `mappingVersion`).
2. Resolves the artifact from the registry.
3. Loads the stylesheet from the artifact store (e.g., Blob Storage / mounted Git-synced share).
4. Compiles