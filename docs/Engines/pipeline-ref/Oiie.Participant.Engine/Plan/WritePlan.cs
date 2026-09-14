using System.Xml.Linq;

namespace Oiie.Participant.Engine.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// §4 — the WritePlan. NOT a substitute for the provider's DTOs. It carries what
// a DTO cannot: order, dependency, match key, and per-field update policy.
// The DTO is still built, at the IResourceWriter seam (see Plan/IResourceWriter).
// ─────────────────────────────────────────────────────────────────────────────

public sealed record WritePlan(
    string Participant,
    string Noun,
    string BodId,
    IReadOnlyList<PlanItem> Items);

/// §4.3 rule 3 — the unit of PARTIAL failure. One unresolvable segment skips
/// this item only; one transient failure fails the whole message, because
/// replay is per-message.
public sealed record PlanItem(
    string SourceRef,
    IReadOnlyList<PlanOp> Ops,
    IReadOnlyList<RegisterOp> Registrations);

public sealed record PlanOp(
    string Id,
    string Resource,          // may be "$targetTable" — resolved, not literal
    OpMode Mode,
    string? Match,
    IReadOnlyList<PlanField> Fields,
    IReadOnlyList<ReturnSpec> Returns);

/// Value is exactly one of Literal / ContextRef / OpRef.
///   ContextRef "$ownerId"    → from TransformContext.Resolved (already fetched)
///   OpRef      "unit.nativeKey" → late-bound from a prior op's response
public sealed record PlanField(
    string Name,
    UpdatePolicy OnUpdate,
    string? Literal = null,
    string? ContextRef = null,
    string? OpRef = null);

public sealed record ReturnSpec(string Name, string As);

public sealed record RegisterOp(
    string CirId,
    string Category,
    string IdInSourceOpRef,   // e.g. "unit.nativeKey"
    MergeMode Merge);

public enum OpMode { Upsert, Delete, Lookup }

/// §4.2 — required on every field.
public enum UpdatePolicy
{
    /// Write on insert and on update.
    Set,
    /// Write on insert; leave alone on update. (MMS reclassifies CLASSIFICATION
    /// itself; a republished site must not reset it.)
    SetIfAbsent,
    /// Match key or immutable; insert only.
    Never
}

public enum MergeMode { AdoptExisting, Replace }
