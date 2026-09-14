using System.Xml.Linq;

namespace Oiie.Participant.Engine.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// §3.1 — the two plug points. Nothing in this file names a participant or a noun.
// ─────────────────────────────────────────────────────────────────────────────

public interface IInboundTransform
{
    /// Declared statically and read by the engine BEFORE Apply runs (§3.2).
    ResolutionPlan Resolutions { get; }

    /// MUST be pure: no I/O, no clock, no randomness. Everything it needs
    /// arrives in `context`. This is what makes an XSLT artifact substitutable
    /// for a compiled class.
    WritePlan Apply(XDocument bod, TransformContext context);
}

public interface IOutboundTransform
{
    ResolutionPlan Resolutions { get; }
    XDocument Apply(XDocument nativeAggregate, TransformContext context);
}

/// <param name="Resolved">key → resolved value, populated by E4.</param>
/// <param name="Unresolved">
/// Keys with a DEFINITE miss. Distinct from "absent from Resolved", which the
/// engine never produces: an unknown (CIR did not answer) fails the message as
/// transient before the transform is reached. Collapsing the two is LTP-4.
/// </param>
public sealed record TransformContext(
    IReadOnlyDictionary<string, string> Resolved,
    IReadOnlySet<string> Unresolved,
    DateTimeOffset AsOfUtc);

// ─────────────────────────────────────────────────────────────────────────────
// §6 — verdict. You cannot construct Failed without answering "transient?".
// ─────────────────────────────────────────────────────────────────────────────

public abstract record PersistOutcome
{
    public sealed record Applied(IReadOnlyList<PersistedItem> Items) : PersistOutcome;
    public sealed record Rejected(string Reason) : PersistOutcome;
    public sealed record Failed(bool Transient, string Reason) : PersistOutcome;

    private PersistOutcome() { }
}

public sealed record PersistedItem(
    string SourceRef,
    IReadOnlyDictionary<string, string> NativeKeys,
    ItemDisposition Disposition,
    string? SkipReason = null);

public enum ItemDisposition { Written, Skipped }
