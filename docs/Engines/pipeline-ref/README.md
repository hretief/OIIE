# Pipeline Abstraction — Reference Implementation

Companion to `pipeline-abstraction-spec.md`. Illustrative, not compiled: this
container has no .NET SDK and no NuGet access, so treat it as a design artifact
to read and adapt, not a project to `dotnet build`.

## Layout

```
Oiie.Participant.Engine/          THE ENGINE — grep it for "mms", "cms",
  Contracts/                      "Segment", "Site", "LIGHT_UNIT" and find
    Contracts.cs                  nothing. That is the §1 acceptance test.
    ResolutionPlan.cs
  Plan/
    WritePlan.cs                  §4 model
    WritePlanParser.cs            stylesheet output → model
    WritePlanValidator.cs         §4.3 rules, before any HTTP call
    TopologicalSort.cs            §4.3 rule 2
    IResourceWriter.cs            ← where the plan meets the typed DTO
    ResourceBindings.cs           §4.4
    PlanExecutor.cs               E5 + E6
  Resolution/ResolutionRunner.cs  E4, and the three-outcome table
  Failures/FailureClassifier.cs   §6, default-transient
  Transforms/XsltInboundTransform.cs   §3.3, SaxonCS-HE 13
  Pipeline/IngestPipeline.cs      E1–E8

MmsEngine/                        THE PARTICIPANT — what a vendor authors
  Transforms/SyncSites/           transform.xslt + resolutions.yaml
  Transforms/SyncSegments/        transform.xslt + resolutions.yaml
  Transforms/MmsSyncSitesTransform.cs   the compiled escape hatch
  bindings.yaml                   resource → endpoint
  failures.yaml                   terminal conditions only

Tests/                            §8 obligations
```

## Read in this order

1. `Contracts/Contracts.cs` — the two plug points and the verdict type.
2. `MmsEngine/Transforms/SyncSegments/` — what a human actually authors.
3. `Plan/PlanExecutor.cs` — `BuildPayload` is where `onUpdate` bites.
4. `Resolution/ResolutionRunner.cs` — the LTP-4 branch, commented.

## Five things to check before adapting

- **SaxonCS API names** in `XsltInboundTransform` are from memory. Verify
  `Load30` / `SetStylesheetParameters` / `ApplyTemplates` against the SaxonCS 13
  surface, and confirm HE (no schema awareness, no streaming) runs your maps.
- **`onUpdate` is implemented as read-then-merge** in the executor, using the
  `read:` route from `bindings.yaml`. That is a race against a concurrent writer.
  Acceptable in the sandbox; before production, either the provider takes a field
  mask or you accept last-write-wins explicitly.
- **`ItemSkipped` is control flow by exception.** Fine at this size; if the plan
  grows branches, make it a result type.
- **The XPaths in `resolutions.yaml`** (`//Segment/Type/UUID`) are written
  against the CCOM shape as described in DR-033, not against a real XSD. They
  will need correcting once §12.4 resolves.
- **`Register` categories** — `ASSET` vs `RDL-CLASS`, and `adopt-existing` merge.
  Check against DR-033 directly; an error here propagates into every artifact.
