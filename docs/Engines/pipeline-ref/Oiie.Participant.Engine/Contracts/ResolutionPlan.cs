namespace Oiie.Participant.Engine.Contracts;

/// §3.2 — the transform declares what it needs; the engine fetches it.
public sealed record ResolutionPlan(IReadOnlyList<ResolutionRequest> Requests)
{
    public static readonly ResolutionPlan None = new([]);
}

public sealed record ResolutionRequest(
    string Key,
    ResolutionSource Source,
    string Category,
    string XPath,
    OnMiss OnMiss,
    string? Default = null)
{
    public ResolutionRequest
    {
        // DR-030: there is no implicit default. Guessing a target table is
        // corruption, not degradation.
        if (OnMiss is OnMiss.UseDefault && Default is null)
            throw new ArgumentException($"Resolution '{Key}' declares use-default with no default.");
        if (OnMiss is not OnMiss.UseDefault && Default is not null)
            throw new ArgumentException($"Resolution '{Key}' supplies a default it can never use.");
    }
}

public enum ResolutionSource { Cir, Local }

public enum OnMiss
{
    /// Record and continue; the item is reported Skipped in the verdict.
    SkipItem,
    /// The whole message is Rejected — terminal, never retried.
    RejectMessage,
    /// Legal only with an explicit Default.
    UseDefault
}
