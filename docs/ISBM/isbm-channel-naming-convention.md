# ISBM Channel Naming Convention
## Proposal for the Bentley Systems OIIE Implementation

**Author:** Solution Engineering
**Date:** August 2026
**Status:** Proposal for review

---

## Executive Summary

As we deploy the ISBM Service Provider to enable interoperability between Bentley Systems
tools and owner-operator infrastructure systems, we need a channel naming convention that
is stable, scalable, and aligned with how our customers organize their assets and projects.

This proposal defines a four-level hierarchical convention rooted in the **iTwin** as the
natural integration boundary. Channels are identified by immutable UUIDs (iTwin Federation IDs)
rather than human-readable names, ensuring that organizational changes, project renames,
and asset reclassifications never break integrations.

The convention was developed against the MnDOT use case but is designed to work across
any owner-operator using Bentley's Connected Data Environment.

---

## The Convention

### Pattern

```
/{enterprise}/{itwin-federation-id}/{domain}/{type}
```

### Levels

| Level | What it represents | Stable? | Examples |
|---|---|---|---|
| `/{enterprise}` | The owner-operator organization | Yes — rarely changes | `mndot`, `caltrans`, `nysdot`, `networkrail` |
| `/{federation-id}` | The iTwin (facility, project, or portfolio) | Yes — UUID, immutable | `550e8400-e29b-41d4-a716-446655440000` |
| `/{domain}` | The integration function | Yes — spec-defined | `engineering`, `asset-config`, `maintenance`, `cir`, `condition`, `sensors` |
| `/{type}` | Publication or Request | Yes — ISBM-defined | `publication`, `request` |

### Enterprise-wide services

Services that span all iTwins (like the Common Interoperability Registry) sit at the
enterprise level:

```
/{enterprise}/enterprise/{service}/{type}
```

### Human-readable context

The channel **description** (a free-text field on every ISBM channel) carries the
human-readable name. The URI itself is stable and opaque:

```json
{
  "channelUri": "/mndot/550e8400-e29b-41d4-a716-446655440000/engineering/publication",
  "channelType": "Publication",
  "description": "TH-61 Corridor — engineering design updates (Bridge 9340, Road segments, Signage)"
}
```

---

## Why This Convention

### Problem: names change, integrations shouldn't break

In a typical DOT environment:
- Corridors get renamed (TH-61 → US-61)
- Projects get re-scoped and re-numbered
- Asset programs are reorganized (Bridges + Tunnels merged into Structures)
- iTwins are re-labeled as project phases change

If channel URIs contain any of these names, every rename triggers a cascade of
subscription updates across every connected system. Using the iTwin's Federation ID
(a UUID assigned at creation, never changed) eliminates this entire class of failures.

### Problem: asset types create artificial boundaries

An early version of this convention included an "asset program" level (`/mndot/bridges/...`
vs. `/mndot/highways/...`). This was rejected because:

- Bridge 9340 carries TH-61 — they share the same corridor, same crews, same digital twin
- A single iModel can contain the road, the bridge, the signs, and the guardrails
- Forcing a subscriber to choose between `/bridges/` and `/highways/` splits what is
  physically one dataset across two channel hierarchies

The iTwin is where security, ownership, and lifecycle converge. Everything within an
iTwin shares the same owner, the same access control, and the same project context.
That makes it the natural channel boundary.

### Problem: disciplines multiply channels unnecessarily

An iTwin may have multiple iModels representing different engineering disciplines
(Structural, Mechanical, Architectural, Civil). Making each discipline a separate channel
would create 4× the channels and force cross-discipline subscribers to maintain
multiple subscriptions.

Instead, disciplines are **topics** within a channel:

```
Channel: /mndot/{federation-id}/engineering/publication

Topics:
  bentley:imodel/structural/changeset
  bentley:imodel/mechanical/changeset
  bentley:imodel/architectural/changeset
  bentley:imodel/civil/changeset
```

A subscriber picks the topics it cares about. A subscriber that needs everything subscribes
to all topics on one channel. One subscription, not four.

---

## Topic Convention

Topics ride inside channels and identify what kind of data is in the message. Two families:

### OIIE-standard topics (CCOM BODs)

```
oiie:s{scenario}/ccom:{bod-type}
```

Examples:
```
oiie:s5/ccom:ProcessRegistry              # Asset install/remove
oiie:s11/ccom:AssetSegmentEvent           # Configuration change
oiie:s16/ccom:SyncWorkRequests            # Work request sync
oiie:s15/ccom:GetWorkOrders               # Pull work orders
```

### Bentley-specific topics

```
bentley:{source}/{data-type}
```

Examples:
```
bentley:imodel/structural/changeset        # Structural iModel changes
bentley:imodel/civil/changeset             # Civil iModel changes
bentley:itwin/sensor-reading               # IoT sensor data
bentley:itwin/inspection-report            # Condition assessment
```

Both families coexist on the same channels. The ISBM provider doesn't interpret topics —
they're strings for subscription matching.

---

## Scenarios

### Scenario 1: Single Facility iTwin (Steady-State Operations)

MnDOT operates bridge 9340 through its lifecycle. One iTwin, six channels.

```
/mndot/aaa-111-.../engineering/publication      # Design updates from iModel
/mndot/aaa-111-.../asset-config/publication     # Asset install/remove events
/mndot/aaa-111-.../maintenance/publication      # Work orders, work status
/mndot/aaa-111-.../maintenance/request          # Pull work history
/mndot/aaa-111-.../condition/publication         # Inspection data
/mndot/enterprise/cir/request                    # Identity registration (enterprise-wide)
```

Subscribers:
- **AssetWise ALIM** subscribes to `engineering/publication` → receives tag updates from iModel
- **AssetWise ALIM** subscribes to `condition/publication` → receives inspection results
- **Maintenance system** subscribes to `asset-config/publication` → updates maintenance records
- **Monitoring dashboard** subscribes to `condition/publication` with filter `//Severity[text()='Critical']`

### Scenario 2: Brownfield Project (Design + As-Built)

A rehabilitation project for bridge 9340. Two iTwins: the permanent facility and the
temporary project. Data flows between them.

```
Facility iTwin (permanent, as-built):       Federation ID: aaa-111-...
Project iTwin (temporary, rehab design):    Federation ID: bbb-222-...
```

Channels:
```
/mndot/aaa-111-.../engineering/publication      # Current as-built state
/mndot/aaa-111-.../engineering/request          # Project pulls current state
/mndot/aaa-111-.../asset-config/publication     # Updates when approved changes land

/mndot/bbb-222-.../engineering/publication      # Design changes from project
/mndot/bbb-222-.../construction/publication     # Field progress, as-installed

/mndot/enterprise/cir/request                    # Links project IDs ↔ facility IDs
```

Cross-iTwin subscriptions:
- **Project REG** subscribes to facility's `engineering/publication` → reads current state
- **Facility REG** subscribes to project's `engineering/publication` → receives approved changes
- **Facility as-built** subscribes to project's `construction/publication` → updates when construction completes
- **CIR** links all identities: project ID ↔ facility ID ↔ maintenance ID

When the project completes:
- Project iTwin channels are deleted (or archived)
- CIR retains the identity mappings permanently
- Facility iTwin continues on its own channels
- Next project gets a new federation ID and the cycle repeats

### Scenario 3: Portfolio-Level Operations

MnDOT manages 20,000 bridges. Each has its own iTwin and channels. Enterprise-wide
services aggregate across all of them.

```
/mndot/{bridge-1-fed-id}/condition/publication
/mndot/{bridge-2-fed-id}/condition/publication
/mndot/{bridge-3-fed-id}/condition/publication
... (20,000 bridges)

/mndot/enterprise/cir/request                    # One CIR for all of MnDOT
/mndot/enterprise/analytics/publication          # Aggregated KPIs published here
```

A state-wide analytics system doesn't subscribe to 20,000 channels. Instead:
- Each bridge's condition subscriber publishes summary KPIs to a shared analytics channel
- Or the analytics system queries the CIR for bridges matching criteria and reads their channels selectively

### Scenario 4: Multi-Organization Collaboration

MnDOT hires an engineering consultant who uses their own Bentley tools and their own iTwin
for the design work. The consultant's iTwin is under the consultant's enterprise.

```
MnDOT channels:
/mndot/{facility-fed-id}/engineering/publication       # As-built data
/mndot/{facility-fed-id}/engineering/request           # Consultant pulls current state

Consultant channels:
/acme-eng/{project-fed-id}/engineering/publication     # Design deliverables

Enterprise CIR:
/mndot/enterprise/cir/request                          # Links MnDOT + consultant IDs
```

Security tokens gate access:
- Consultant gets a token for MnDOT's facility engineering channels (read only)
- MnDOT gets a token for the consultant's project engineering channel (read only)
- Both register identities in MnDOT's enterprise CIR

When the contract ends, MnDOT revokes the consultant's tokens. Channels and CIR
mappings persist.

---

## Channel Provisioning

Channels are created when an iTwin is onboarded to the OIIE ecosystem. A bootstrap
script creates the standard set for each iTwin:

```powershell
function New-OiieChannels {
    param(
        [string]$Enterprise,
        [string]$FederationId,
        [string]$Description,
        [string[]]$Domains = @("engineering", "asset-config", "maintenance", "condition")
    )

    foreach ($domain in $Domains) {
        # Most domains get a publication channel
        New-IsbmChannel `
            -ChannelUri "/$Enterprise/$FederationId/$domain/publication" `
            -ChannelType "Publication" `
            -Description "$Description — $domain events"

        # Some domains also get a request channel
        if ($domain -in @("engineering", "maintenance")) {
            New-IsbmChannel `
                -ChannelUri "/$Enterprise/$FederationId/$domain/request" `
                -ChannelType "Request" `
                -Description "$Description — $domain queries"
        }
    }
}

# Example: onboard bridge 9340
New-OiieChannels `
    -Enterprise "mndot" `
    -FederationId "550e8400-e29b-41d4-a716-446655440000" `
    -Description "TH-61 Corridor (Bridge 9340)"
```

---

## Security Model

ISBM security tokens map naturally to the convention:

| Scope | Who gets a token | What they can access |
|---|---|---|
| Enterprise-wide | MnDOT central IT | All `/mndot/...` channels |
| Per-iTwin | District engineer | All `/mndot/{their-itwin}/...` channels |
| Per-domain | Maintenance contractor | Only `/mndot/{itwin}/maintenance/...` channels |
| Cross-org | External consultant | Specific channels with read-only tokens |

Tokens are assigned at channel creation time or added later. The ISBM provider stores
them encrypted in Azure Key Vault. Authentication is via standard HTTP Basic auth
when opening a session.

---

## Summary of Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Channel identifier | iTwin Federation ID (UUID) | Immutable — renames don't break integrations |
| Asset type in URI? | No | iTwins contain mixed asset types; classification belongs in topics/filters |
| Discipline in URI? | No | Disciplines are topics within the engineering channel |
| Human-readable name | Channel description field | Free text, changeable, not part of the identifier |
| Topic format | `oiie:s{n}/ccom:{bod}` and `bentley:{source}/{type}` | Coexistence of standard and proprietary on same channels |
| Enterprise-wide services | `/{enterprise}/enterprise/{service}/{type}` | CIR, product catalog — shared across all iTwins |
| Cross-iTwin flows | Subscribe to the other iTwin's channels | No orchestrator, no custom routing |
| Project lifecycle | New federation ID per project | Channels created at start, deleted/archived at end |

---

## Appendix: Comparison with Alternatives Considered

| Approach | Channels for MnDOT (20K bridges × 6 domains) | Problem |
|---|---|---|
| **Flat names** (`asset-config-bridge-9340`) | 120,000 unstructured channels | No hierarchy, no security scoping, ungovernable |
| **Asset-type hierarchy** (`/mndot/bridges/9340/...`) | Same count but false boundaries | Bridge + road on same corridor split across hierarchies |
| **Name-based** (`/mndot/th-61-corridor/...`) | Same count but fragile | Corridor rename breaks all subscriptions |
| **Federation ID** (`/mndot/{uuid}/...`) | Same count, stable and scopeable | ✓ Recommended |
