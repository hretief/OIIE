# Schemas

Packaged schema zips are authoritative over the published PDFs — both the ws-CIR
package and Service Directory 1.0 have known namespace defects in their documents.

- `ccom/`  — CCOM 4.x schemas and BODs (`CCOM.xsd`, `bod/`)
- `cir/`   — ws-CIR 1.0 registry schema and BODs
- `oagis/` — Meta.xsd, Fields.xsd, CodeLists.xsd and the referenced code lists

Where no schema is held for a namespace, `BodValidator` returns `NotValidated`
rather than `Valid`. Silently passing unvalidated documents would hide exactly
the gap a missing schema package represents.

## Local repairs applied on import

The published packages do not compile as supplied. Two changes were made, and
both must be reapplied if these schemas are ever refreshed from source:

1. **Removed a duplicate.** The ws-CIR package ships the same
   `http://www.openoandm.org/ws-cir/` schema twice, as `XSD/CommonInteroperabilityRegistry.xsd`
   and `BOD/CommonInteroperability.xsd`. Loading both makes `XmlSchemaSet.Compile()`
   fail with "The global element 'CreateRegistry' has already been declared."
   Only the `cir/CommonInteroperabilityRegistry.xsd` copy is kept.

2. **Converted self-namespace imports to includes.** Sixty BOD schemas used
   `xs:import` to pull in a schema whose namespace equals their own
   `targetNamespace`, which XSD forbids. These are now `xs:include`.

`schemaLocation` values were also rewritten to match this flat layout.

## Known remaining defects

Some CCOM BODs reference types the package never defines — `UUIDFilter`,
`TextFilter`, `UTCDateTimeFilter`. Those schemas fail to load, so their
namespaces degrade to `NotValidated`. This is a gap in the supplied package,
not something to work around by relaxing validation.

The CCOM BODs also import `../CCOMElements.xsd`, a file absent from the
package; these were repointed at `CCOM.xsd`. **Unconfirmed** — verify against
an authoritative CCOM distribution before relying on CCOM BOD validation.
