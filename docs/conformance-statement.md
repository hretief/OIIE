# ws-CIR 1.0 Conformance Statement

**Implementation:** CIR Provider (Azure Functions, REST binding)  
**Endpoint:** https://cir-func-44p2f3n6.azurewebsites.net/api  
**Assessed:** 2026-07-30 13:25:28 -07:00  
**Specification:** OpenO&M ws-CIR 1.0 (Candidate Standard, 19 June 2015)

Assessed against the six qualifications required by ws-CIR 1.0 §5. Conformance
under this specification is declarative: an implementation states what it
supports, and §5 item 6 requires any non-conformance to be stated explicitly.

## 1. Support for Command Services — Supported

| Clause | Operation | Support | HTTP |
|---|---|---|---|
| §3.1.1 | CreateRegistry | Supported | 201 |
| §3.1.2 | CreateEquivalentEntries | Supported | 201 |
| §3.1.3 | UpdateRegistry | Supported | 204 |
| §3.1.4 | UpdateEntryCIRID | Supported | 204 |
| §3.1.5 | DeleteRegistry | Supported | 404 |
| §3.1.6 | DeleteCategory | Supported | 404 |
| §3.1.7 | DeleteEntries | Supported | 204 |
| §3.1.8 | DeleteProperties | Supported | 204 |

## 2. Support for Query Services — Supported

| Clause | Operation | Support | HTTP |
|---|---|---|---|
| §3.2.1 | GetRegistry | Supported | 200 |
| §3.2.2 | GetEquivalentEntries | Supported | 200 |
| §3.2.3 | GetEntriesByCIRID | Supported | 200 |

## 3. Support for Wildcard Specification — Supported

The §4 POSIX subset was verified empirically against TargetSourceID. All of
`.`, `*`, `+`, `?` and the backslash escape behave as specified, and patterns
are implicitly anchored at both ends: the pattern `Alpha` does not match `Alpha A`.

## 4. Support for SOAP 1.1 and SOAP 1.2 services — Not supported

This implementation provides a REST/JSON binding only. No WSDL endpoint, no SOAP 1.1 or SOAP 1.2 envelope handling.

## 5. Support for specific BODs — Supported

All 11 ws-CIR request BODs are accepted at POST /bods and dispatched to
the corresponding service. releaseID is 1.2.1 and versionID is
1.0, as required by Annex A. Faults are returned in the
Acknowledge and Respond nouns, and the model permits several per response.
ProcessType acknowledgeCode and ChangeType responseCode are honoured: Never
suppresses the response entirely and OnChange emits one only on fault.

Transport: this implementation exposes the BOD model over HTTP. A ws-ISBM
channel binding, in which these documents travel as ISBM message content, is not
yet provided.

## 6. Statement of conformance — Partial conformance

This implementation claims **partial conformance** to ws-CIR 1.0. The
following areas are explicitly non-conformant:

- SOAP 1.1 and SOAP 1.2 services (§5 item 4) - not supported; REST binding only
- OAGIS-Based Message Model BODs (Annex A) - not supported

The following interpretations were made where the specification is silent
or ambiguous:

- **§3.2.3 GetEntriesByCIRID** states that the existing Entry is not returned.
  The input is a bare CIRID, so there is no specified Entry to exclude; the
  sentence appears to be carried over from §3.2.2. All Entries carrying the
  CIRID are returned.
- **§3.1.2 CreateEquivalentEntries** does not say what happens when the
  existing Entry and the supplied Entry carry *different* CIRIDs. The
  existing CIRID wins and the supplied value is discarded, consistent with
  the stated precedence. Merging two clusters is left to §3.1.4, which is
  explicit rather than implicit.
- **§3.1.3 UpdateRegistry** is a snapshot replace, so omitted attributes are
  cleared. Children that are not supplied are left alone rather than deleted,
  since a separate Delete family exists and the alternative would make
  partial updates impossible. CIRID is preserved when omitted, because
  §3.1.4 is a dedicated operation for it.

All other assessed services conform to the behaviour defined in §3, including
the atomicity requirement of §3.1 (no partial creates, updates or deletes when
a fault is raised) and the fault set of §3.3. Fault names are preserved
verbatim in the `faults[]` member of the RFC 9457 problem+json response body.


