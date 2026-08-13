# ENG is an iModel: why publication is all-or-nothing

Date: 2026-08
Status: describes existing behaviour. No code changed as a result of writing this.

This record exists because `EngService.PromoteAsync` looks like an unfinished
feature and is not one. It gathers *every* unpublished tag in the twin, offers no
way to promote a subset, and lets one invalid tag block the rest. Read as a
generic "publish selected rows" function it appears crude, and the obvious
improvement — add a list of ids — would quietly break the thing it is modelling.

## The domain

The ENG participant stands for **Bentley's iModel**, which belongs to an
**iTwin**. Design-tool output is aggregated into the iModel. Segments (tags) are
added to enrich it, and are later associated with their respective geometries —
that association is outside this sandbox.

Two different activities, deliberately not the same act:

- **Enrichment** is incremental and unceremonious. Segments are added one at a
  time, corrected, and added again. `AddTagAsync` is an upsert for exactly this
  reason: the second edit of a segment must not be an error.
- **Publication** is a single deliberate release: the pending segments go out
  together as one **Named Version**.

## What follows from it

**No subset promotion.** A Named Version is the design as it stands at a moment,
not a hand-picked selection. Releasing part of one would send REG-LOCATION a
model with holes in it — and relationships between segments would dangle, since
an edge whose endpoint was withheld refers to something the receiver does not
have. So `PromoteAsync` takes a version name and a twin, and nothing else.

**One invalid segment holds back the batch.** The validation gate requires a
reference-data class and a service description on every pending segment. That is
not per-segment strictness applied at the wrong scope; it is the batch declining
to be a release until it is coherent.

**Editing a Published segment returns it to WorkInProgress.** `Apply` clears
`Maturity` and `PublishedInVersionId` on every write. A changed segment has not
been released in its new form, so it correctly rejoins the pending set for the
next Named Version.

## In the UI

`WorkflowOrchestration` reports "N PENDING" derived from maturity, and
deliberately has no per-row selection on the publish path. Checkboxes there would
promise a partial release that a Named Version is not. The panel says so on
screen, because an absent control reads as a missing feature otherwise.

## A trap this creates

`Apply` assigns every editable field unconditionally, nulls included. A partial
`POST /admin/eng/tags` therefore blanks the fields it omits — verified: posting
only `tagNumber` and `classKey` to a segment cleared its service description and
unit number, with no error.

Any edit UI must send the full current record rather than a diff. Worth
remembering that the two fields most easily blanked this way are precisely the
two the publication gate requires, so a careless edit turns a publishable segment
into one that blocks the batch.
