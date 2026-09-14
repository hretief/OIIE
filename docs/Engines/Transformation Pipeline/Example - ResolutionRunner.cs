using System.Xml.Linq;
using System.Xml.XPath;
using Oiie.Participant.Engine.Contracts;

namespace Oiie.Participant.Engine.Resolution;

/// E4. Runs the transform's declared lookups and builds the TransformContext.
/// This is the class that keeps the transform pure.
public sealed class ResolutionRunner(ICirClient cir, TimeProvider clock)
{
    public async Task<ResolutionResult> RunAsync(
        ResolutionPlan plan, XDocument bod, CancellationToken ct)
    {
        var resolved   = new Dictionary<string, string>();
        var unresolved = new HashSet<string>();

        foreach (var req in plan.Requests)
        {
            var cirId = bod.XPathSelectElement(req.XPath)?.Value;
            if (cirId is null)
            {
                if (req.OnMiss is OnMiss.RejectMessage)
                    return ResolutionResult.Reject($"'{req.Key}': {req.XPath} matched nothing");
                unresolved.Add(req.Key);
                continue;
            }

            CirLookup lookup;
            try
            {
                lookup = await cir.ResolveAsync(req.Category, cirId, ct);
            }
            catch (Exception ex)
            {
                // §6 — the three-outcome table. THIS is the branch that must not
                // collapse into the miss branch below. Catching, logging a warning
                // and returning null here is LTP-4, reproduced exactly.
                return ResolutionResult.Transient($"'{req.Key}': registry unreachable — {ex.GetType().Name}");
            }

            switch (lookup)
            {
                case CirLookup.Found f:
                    resolved[req.Key] = f.IdInSource;
                    break;

                case CirLookup.NotFound when req.OnMiss is OnMiss.UseDefault:
                    resolved[req.Key] = req.Default!;
                    break;

                case CirLookup.NotFound when req.OnMiss is OnMiss.RejectMessage:
                    return ResolutionResult.Reject($"'{req.Key}': no {req.Category} entry for {cirId}");

                case CirLookup.NotFound:
                    // Definite miss. The item is skipped, not guessed at.
                    unresolved.Add(req.Key);
                    break;
            }
        }

        return ResolutionResult.Ok(new TransformContext(
            resolved, unresolved, clock.GetUtcNow()));
    }
}

public abstract record ResolutionResult
{
    public sealed record Ok(TransformContext Context) : ResolutionResult;
    public sealed record Reject(string Reason) : ResolutionResult;
    public sealed record Transient(string Reason) : ResolutionResult;

    private ResolutionResult() { }

    public static ResolutionResult Ok(TransformContext c) => new Ok(c);
    public static ResolutionResult Reject(string r) => new Reject(r);
    public static ResolutionResult Transient(string r) => new Transient(r);
}

public interface ICirClient
{
    /// Throws on unreachable. Returns NotFound on a definite miss.
    /// The distinction is the whole point of this interface.
    Task<CirLookup> ResolveAsync(string category, string cirId, CancellationToken ct);
}

public abstract record CirLookup
{
    public sealed record Found(string IdInSource, string SourceId) : CirLookup;
    public sealed record NotFound : CirLookup;
    private CirLookup() { }
}

public interface ICirRegistrar
{
    Task RegisterAsync(
        string cirId, string category, string idInSource, MergeMode merge, CancellationToken ct);
}
