# iTwins API Overview

The iTwins API provides a comprehensive set of capabilities for creating, managing, and governing digital twins on the Bentley iTwin Platform.

Using the iTwins API, you can:

- Create and manage iTwins
- Control iTwin lifecycle states
- Discover repositories associated with an iTwin
- Query recently used iTwins
- Manage favorite iTwins

---

## What is an iTwin?

An **iTwin** is Bentley's digital twin object and serves as the primary container for representing infrastructure assets, projects, programs, portfolios, and other engineering-related entities.

For non-technical guidance on managing iTwins, see the Twin Platform documentation on Digital Twin Management.

---

## iTwin Classification

Digital twins exist across many engineering disciplines and lifecycle phases. To support this diversity, each iTwin includes classification properties that describe its purpose and context.

### Classification Fields

| Field | Purpose |
|---------|---------|
| `class` | High-level category of the digital twin |
| `subClass` | More specific classification within a class |
| `type` | Refinement of the digital twin's purpose |
| `displayName` | Human-readable name |
| `number` | Engineering identifier or reference number |
| `status` | Lifecycle state of the iTwin |

### Lifecycle Status Values

| Status | Description |
|----------|-------------|
| `active` | Currently in use |
| `inactive` | Not currently active |
| `trial` | Trial or evaluation state |

> **Note:** Both `displayName` and `number` must be unique within a given `subClass`.

---

# Example iTwins

## Operating Asset Digital Twin

A digital twin representing the **US Route 202 highway**:

```json
{
  "id": "051b685f-4168-4e2d-900e-49d57e171513",
  "class": "Thing",
  "subClass": "Asset",
  "type": "Highway",
  "displayName": "US Route 202",
  "number": "HWYUSR202",
  "status": "active"
}
```

### Interpretation

| Property | Value |
|-----------|---------|
| Class | Thing |
| SubClass | Asset |
| Type | Highway |
| Purpose | Operating infrastructure asset |

---

## Capital Project Digital Twin

A digital twin representing a project to widen a section of US Route 202:

```json
{
  "id": "01a5b73e-d5d3-47fc-ae3e-253365d93eb9",
  "class": "Endeavor",
  "subClass": "Project",
  "type": "Construction",
  "displayName": "US Route 202 Section 61N Expansion",
  "number": "HWYUSR202S61NEXP",
  "status": "active"
}
```

### Interpretation

| Property | Value |
|-----------|---------|
| Class | Endeavor |
| SubClass | Project |
| Type | Construction |
| Purpose | Capital improvement project |

---

# iTwin Classes and SubClasses

All iTwins share the same underlying capabilities and data structure. Developers use the `class` and `subClass` properties to categorize and organize digital twins according to their purpose.

---

## Class: Thing

Use this class when the iTwin represents a physical asset, infrastructure object, or real-world entity.

### SubClass: Asset

Use when the iTwin represents a specific piece of infrastructure or property.

**Examples:**

- Building
- Bridge
- Highway
- Tunnel
- Pump Station
- Generator
- Manufacturing Equipment

An Asset typically has its own dedicated digital twin.

### SubClass: Portfolio

Use when the iTwin represents a collection of assets or a geographic region.

A Portfolio may reference other iTwins, but this is not required.

**Examples:**

- City
- State
- Rail Network
- Airport System
- Utility Territory
- Property Portfolio

---

## Class: Endeavor

Use this class when the iTwin represents work associated with planning, engineering, construction, operation, or maintenance.

### SubClass: Project

Use when the iTwin represents a specific initiative that produces a deliverable.

**Examples:**

- Design Project
- Engineering Project
- Construction Project
- Rehabilitation Project

### SubClass: Program

Use when the iTwin represents the coordinated management of multiple related efforts.

A Program may reference other iTwins, but this is not required.

**Examples:**

- Regional Rail Expansion Program
- Highway Modernization Program
- Utility Upgrade Program

### SubClass: WorkPackage

Use when the iTwin represents a collection of related tasks within a project.

**Examples:**

- Engineering Work Package
- Construction Work Package
- Procurement Package
- Supply Chain Package
- Sub-Project

---

# Recommended Hierarchies

iTwins may be related using parent-child relationships.

The following hierarchies are commonly used.

## Portfolio → Asset

```text
Portfolio
└── Asset
```

**Example**

```text
California Transportation Network
└── US Route 202
```

---

## Program → Project

```text
Program
└── Project
```

**Example**

```text
Highway Modernization Program
└── US Route 202 Expansion Project
```

---

## Asset → Program → Project

```text
Asset
└── Program
    └── Project
```

**Example**

```text
US Route 202
└── Capacity Expansion Program
    └── Section 61N Expansion Project
```

---

## Program → Project → WorkPackage

```text
Program
└── Project
    └── WorkPackage
```

**Example**

```text
Regional Rail Expansion Program
└── Station Upgrade Project
    └── Electrical Systems Work Package
```

---

## Project → WorkPackage

```text
Project
└── WorkPackage
```

**Example**

```text
US Route 202 Expansion Project
└── Bridge Construction Work Package
```

---

# Summary

The iTwin classification model allows organizations to consistently organize digital twins across infrastructure domains.

At a high level:

- **Thing** represents physical assets and real-world entities.
- **Endeavor** represents work performed on or around those assets.
- **Asset** represents a specific piece of infrastructure.
- **Portfolio** represents a collection of assets or regions.
- **Project** represents a specific initiative.
- **Program** coordinates multiple related initiatives.
- **WorkPackage** represents a manageable set of tasks within a project.

This classification structure enables consistent navigation, governance, reporting, and lifecycle management across an organization's digital twin ecosystem.