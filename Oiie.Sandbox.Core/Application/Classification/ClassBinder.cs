using SimHost.Domain.Common;

namespace SimHost.Application.Classification;

public sealed record ClassBindingResult(
    ClassDefinition? BoundClass,
    bool IsExactMatch,
    string? RequestedClassKey,
    IReadOnlyList<string> UnknownAncestorKeys)
{
    /// <summary>
    /// True when the sender classified more specifically than this participant can
    /// understand. The entity is still displayed correctly at the ancestor level;
    /// leaf-specific properties are retained as unmapped (spec §6.5.6).
    /// </summary>
    public bool IsDegraded => BoundClass is not null && !IsExactMatch;

    public static ClassBindingResult Unresolved(string requestedKey, IReadOnlyList<string> ancestors) =>
        new(null, false, requestedKey, ancestors);
}

/// <summary>
/// Binds an inbound class reference to the most specific class this participant
/// holds. Partial understanding rather than all-or-nothing: MMS receiving
/// rdl:MagneticDriveCentrifugalPump binds at rdl:CentrifugalPump if that is the
/// deepest ancestor it knows, and says so in the UI.
///
/// This is only possible because classes share ancestors, which is the argument
/// for a governed library over per-participant flat types.
/// </summary>
public sealed class ClassBinder
{
    private readonly IClassificationSource _source;

    public ClassBinder(IClassificationSource source)
    {
        _source = source;
    }

    /// <param name="classKeyChain">
    /// Leaf-first chain as supplied by the sender. Senders include the ancestor
    /// chain precisely so receivers can degrade gracefully; a bare leaf key with
    /// no chain can only ever bind exactly or not at all.
    /// </param>
    public ClassBindingResult Bind(IReadOnlyList<string> classKeyChain)
    {
        if (classKeyChain.Count == 0)
        {
            return ClassBindingResult.Unresolved(string.Empty, []);
        }

        var unknown = new List<string>();

        foreach (var key in classKeyChain)
        {
            var known = _source.FindClassByKey(key);
            if (known is not null)
            {
                return new ClassBindingResult(
                    known,
                    IsExactMatch: unknown.Count == 0,
                    RequestedClassKey: classKeyChain[0],
                    UnknownAncestorKeys: unknown);
            }

            unknown.Add(key);
        }

        return ClassBindingResult.Unresolved(classKeyChain[0], unknown);
    }
}
