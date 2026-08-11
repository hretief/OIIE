*[Scenarios](https://miro.com/app/board/uXjVItVgG_g=/?moveToWidget=3458764680064854482&cot=14)*

| No\. | Method | Data | From System/s | To System/s |
| --- | --- | --- | --- | --- |
| 1 | Publish | As\-Designed/As\-Built Engineering Network/Segment/Tag | ENG | REG\-LOCATION |
| 2 | Publish | As\-Designed/As\-Built Engineering Network/Segment/Tag | REG\-LOCATION | O&amp;M |
| 11 | Publish | Asset Removal/Installation | MMS | O&amp;M |

## How these map to scenario files

| Scenario | File | Trigger |
| --- | --- | --- |
| 1 | `SimHost/Scenarios/sc01-design-release.yaml` | ENG promotes a named version |
| 1 | `SimHost/Scenarios/sc01-greenfield-allocation.yaml` | ENG promotes a named version, with the code allocated rather than authored |
| 2 | `SimHost/Scenarios/sc02-operations-release.yaml` | A steward approves the tag at REG-LOCATION |
| 11 | `SimHost/Scenarios/sc11-asset-install.yaml` | A planner signs off a completed work order |

### REG-LOCATION is a release gate

Scenarios 1 and 2 are deliberately separate files, and the split is the point rather
than an organisational convenience.

Scenario 1 carries early design data as far as REG-LOCATION's stewardship queue and
stops. Design at this stage is a proposal about a plant that may not be built as
drawn, so pushing it through to MMS would put speculative rows into the system of
record maintenance planners work from. Scenario 2 is what a steward's approval
triggers, and approval — not an engineer's publish — is what admits a tag to
operations.

Because the two have different triggers, they also have different failure modes worth
detecting separately. Scenario 1 closes on negative assertions: nothing arrived at
MMS, no `Location` row exists yet. A single combined scenario could not make that
claim, since 'the tag reached MMS' would be a passing outcome either way and a gate
that had stopped working would look identical to one that was holding.

Scenario 2 declares scenario 1 as a prerequisite and does not reset, because it
consumes the queue scenario 1 leaves behind. Run on its own it fails on its first
assertion, naming the missing proposal, instead of approving nothing and reporting
success.

