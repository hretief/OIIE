# Guidance: Semantic Interoperability Transformation Pattern for ISBM Integrations

## Purpose

This document defines the recommended architectural pattern for exchanging data between systems using ISBM as the transport and a semantic model as the interoperability contract.

The pattern is built on a fundamental principle:

> Transport occurs entirely in the **semantic model**.
> Persistence occurs entirely in the **native model**.
> Each participant transforms only between its own native model and the shared semantic model.

This separation reduces coupling, eliminates point-to-point mappings, simplifies maintenance, improves testability, and supports independent evolution of both endpoints.

---

# Core Principle

There are **two** transformations in every integration, not one.

```text
Source Native Model
        │
        ▼   (Transformation 1: Native → Semantic)
Semantic Model
        │
        ▼   (Transport via ISBM)
Semantic Model
        │
        ▼   (Transformation 2: Semantic → Native)
Target Native Model
```

The publisher owns the first transformation.

The subscriber owns the second transformation.

Neither endpoint is aware of the other's native model.

---

# Conceptual Flow

```text
Source Application
(Native Model)
        │
        ▼
Source Native DTO
        │
        ▼
Native → Semantic Transformation
        │
        ▼
Semantic DTO
        │
        ▼
ISBM Transport
        │
        ▼
Semantic DTO
        │
        ▼
Semantic → Native Transformation
        │
        ▼
Target Native DTO
        │
        ▼
Persistence Adapter
        │
        ▼
Target Application APIs
        │
        ▼
Target Application
(Native Model)
```

---

# The Semantic Model as Contract

The semantic model is the interoperability contract.

Without a semantic model, every integration requires a bespoke point-to-point mapping:

```text
ENG → REG-LOCATION
ENG → Maximo
ENG → GIS
ENG → CMS
```

This scales poorly (an N-to-N problem).

With a semantic model, each participant only maintains a single Native ↔ Semantic mapping:

```text
           Semantic Model
                 ^
                 |
     --------------------------
     |            |           |
    ENG         Maximo       GIS
     |            |           |
     --------------------------
```

This scales linearly (an N-to-1 problem).

---

# Definitions

## Semantic Model

A technology-neutral, shared representation of business information used across systems.

Examples:

- IFC
- MIMOSA CCOM
- ISO 15926
- Digital Twin ontologies
- Enterprise Canonical Models

The semantic model defines the shared vocabulary carried on the bus.

---

## Native Model

The internal data model owned by a specific application.

Examples:

```text
ENG ECSchema / iModel Model
REG-LOCATION Object / Tag Model
Maximo Asset Model
GIS Feature Model
```

The native model is specific to one application and is not exposed to other participants.

---

## Semantic DTO

The in-memory representation of the message as defined by the semantic model.

It contains only semantic concepts. It contains no native concepts from any endpoint.

---

## Native DTO

The in-memory representation of a business object as defined by a single application's native model.

It contains only concepts meaningful to that one application.

---

# Publisher Side (Transformation 1: Native → Semantic)

## Responsibility

The publisher knows:

- Its own native model
- The semantic model

The publisher does **not** know:

- Any subscriber's native model

## Flow

```text
Source Application
(Native Model)
        │
        ▼
Source Native DTO
        │
        ▼
Native → Semantic Mapper
        │
        ▼
Semantic DTO
        │
        ▼
ISBM Publication
```

## Example (ENG)

Native source:

```text
ENG Segment
   ECInstanceId
   FederationId
   CodeValue
   UserLabel
   Width
   Length
   MaterialClass
```

Transformed to the semantic model:

```text
SyncSegments
   Segment.Identifier
   Segment.Code
   Segment.Name
   Segment.Width
   Segment.Length
   Segment.MaterialCategory
```

Example mapping:

```yaml
ENG.FederationId:
  target: Segment.Identifier

ENG.CodeValue:
  target: Segment.Code

ENG.UserLabel:
  target: Segment.Name

ENG.Width:
  target: Segment.Width

ENG.Length:
  target: Segment.Length

ENG.MaterialClass:
  target: Segment.MaterialCategory
```

The publisher's mapping logic is entirely owned by the publisher.

---

# Transport (ISBM)

ISBM carries the Semantic DTO only.

```text
ISBM
   ↓
SyncSegments (Semantic DTO)
```

The transport layer:

- Does not perform mapping
- Does not understand native models
- Routes semantic messages via channels and topics

The semantic model is the only representation present on the wire.

---

# Subscriber Side (Transformation 2: Semantic → Native)

## Responsibility

The subscriber knows:

- The semantic model
- Its own native model

The subscriber does **not** know:

- Any publisher's native model

## Flow

```text
ISBM Subscription
        │
        ▼
Semantic DTO
        │
        ▼
Semantic → Native Mapper
        │
        ▼
Target Native DTO
        │
        ▼
Persistence Adapter
        │
        ▼
Target APIs
```

## Example (REG-LOCATION)

Semantic input:

```text
SyncSegments
   Segment.Identifier
   Segment.Code
   Segment.Name
   Segment.Width
   Segment.Length
   Segment.MaterialCategory
```

Transformed to the native model:

```text
REG-LOCATION DTO
   object.GUID
   tag.code
   tag.name
   eWIDTH
   eLENGTH
   eMAT_CAT
```

Example mapping:

```yaml
Segment.Identifier:
  target: object.GUID

Segment.Code:
  target: tag.code

Segment.Name:
  target: tag.name

Segment.Width:
  target: eWIDTH

Segment.Length:
  target: eLENGTH

Segment.MaterialCategory:
  target: eMAT_CAT
```

The subscriber's mapping logic is entirely owned by the subscriber.

---

# Persistence Adapter

Many commercial products expose multiple low-granularity APIs that must be orchestrated to persist a single business object.

The persistence adapter receives only the Native DTO.

```text
REG-LOCATION DTO
      ↓
CreateObject()
      ↓
CreateTag()
      ↓
AssignClass()
      ↓
SetProperty(eWIDTH)
      ↓
SetProperty(eLENGTH)
      ↓
SetProperty(eMAT_CAT)
```

The persistence adapter:

- Does not see the semantic message
- Does not perform semantic mapping
- Only persists a native business object

---

# Ownership of Responsibilities

## Publisher

Owns:

- Source Native DTO
- Native → Semantic mapping

Does not own:

- Subscriber native models
- Transport routing

---

## Semantic Model / ISBM

Owns:

- Interoperability contract
- Message transport
- Channel and topic routing

Does not own:

- Any native mapping

---

## Subscriber

Owns:

- Semantic → Native mapping
- Native DTO
- Persistence orchestration

Does not own:

- Publisher native models

---

# Anti-Pattern

Avoid direct native-to-native coupling.

Avoid:

```text
ENG Native
      ↓
REG-LOCATION Native
```

This creates bespoke point-to-point integrations that do not scale and must be rebuilt for every new participant.

Avoid embedding semantic mapping inside API orchestration.

Avoid:

```text
Call API A using semantic property
Call API B using semantic property
Call API C using semantic property
```

This scatters mapping logic across the persistence layer and destroys traceability.

---

# Recommended Pattern

```text
Source Native DTO
      ↓
Native → Semantic Transformation
      ↓
Semantic DTO
      ↓
ISBM Transport
      ↓
Semantic DTO
      ↓
Semantic → Native Transformation
      ↓
Target Native DTO
      ↓
Persistence Adapter
      ↓
Target APIs
```

---

# Benefits

## Loose Coupling

Publishers and subscribers evolve independently.

- A change in a publisher's native model affects only its Native → Semantic mapping.
- A change in a subscriber's native model affects only its Semantic → Native mapping.
- A change in a vendor API affects only the persistence adapter.

---

## Scalable Reuse

A single semantic message can drive many subscribers.

```text
Semantic DTO
      ↓
REG-LOCATION Native DTO

Semantic DTO
      ↓
Maximo Native DTO

Semantic DTO
      ↓
GIS Native DTO
```

Each subscriber adds only one Native ↔ Semantic mapping.

---

## Independent Testability

Each transformation can be tested in isolation.

Publisher test:

```text
Input:   Source Native DTO
Expect:  Semantic DTO
```

Subscriber test:

```text
Input:   Semantic DTO
Expect:  Target Native DTO
```

No end-to-end environment is required to validate mappings.

---

## Traceability and Reconciliation

Clear lineage exists from source to target:

```text
Source Native Object
      ↓
Semantic Object
      ↓
Target Native Object
      ↓
Persisted Object
```

Reconciliation can compare endpoints through the shared semantic representation.

---

# Summary

For all ISBM-based integrations:

- The semantic model is the interoperability contract.
- Publishers transform **Native → Semantic**.
- Transport occurs entirely in the **semantic model**.
- Subscribers transform **Semantic → Native**.
- Persistence occurs entirely in the **native model**.
- The persistence adapter never sees the semantic message.

This pattern provides the strongest separation of concerns, avoids point-to-point coupling, and is the preferred approach for integrating semantic interoperability standards with source systems and commercial off-the-shelf target applications.