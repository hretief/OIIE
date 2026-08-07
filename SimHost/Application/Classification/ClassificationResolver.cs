using SimHost.Domain.Common;

namespace SimHost.Application.Classification;

/// <summary>
/// A property definition as it applies to an entity after the taxonomy chain and
/// aspect classes have been resolved and narrowing applied.
/// </summary>
public sealed record EffectiveProperty(
    PropertyDefinition Definition,
    ClassProperty Constraint,
    Guid ContributedByClassId,
    string ContributedByClassName,
    ClassKind ContributedByKind,
    int ChainDepth);

public sealed record EffectivePropertySet(
    IReadOnlyList<EffectiveProperty> Properties,
    IReadOnlyList<ClassDefinition> TaxonomyChain,
    IReadOnlyList<ClassDefinition> AspectClasses)
{
    public static EffectivePropertySet Empty { get; } = new([], [], []);

    public IEnumerable<EffectiveProperty> Required =>
        Properties.Where(p => p.Constraint.Requirement == PropertyRequirement.Required);

    public EffectiveProperty? Find(Guid definitionId) =>
        Properties.FirstOrDefault(p => p.Definition.Id == definitionId);

    public bool Sanctions(Guid definitionId) => Find(definitionId) is not null;
}

/// <summary>
/// Thrown when an inbound or fixture-loaded class definition widens, contradicts,
/// or removes an inherited constraint. Failing loudly here is deliberate: this is
/// the rule most often got wrong in practice, and silently resolving it would
/// make inheritance unpredictable (spec §6.5.4).
/// </summary>
public sealed class NarrowingViolationException : Exception
{
    public NarrowingViolationException(string message) : base(message)
    {
    }
}

/// <summary>
/// Chain walking and set union only. No inference, no equivalence axioms, no
/// restriction logic beyond the narrowing rules — the scope cap in spec §6.5.8
/// exists to stop this becoming an ontology engine.
/// </summary>
public sealed class ClassificationResolver
{
    private readonly IClassificationSource _source;

    public ClassificationResolver(IClassificationSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Walks the primary taxonomy chain root-downward, unions in active aspect
    /// classes, then applies narrowing overrides.
    /// </summary>
    public EffectivePropertySet Resolve(string entityType, string entityKey, DateTimeOffset asOf)
    {
        var classifications = _source
            .GetClassifications(entityType, entityKey)
            .Where(c => c.ValidFrom <= asOf && (c.ValidTo is null || c.ValidTo > asOf))
            .ToList();

        if (classifications.Count == 0)
        {
            return EffectivePropertySet.Empty;
        }

        var primary = classifications.FirstOrDefault(c => c.IsPrimary);
        IReadOnlyList<ClassDefinition> chain = primary is null
            ? Array.Empty<ClassDefinition>()
            : BuildTaxonomyChain(primary.ClassId);

        var aspects = classifications
            .Where(c => !c.IsPrimary)
            .Select(c => _source.GetClass(c.ClassId))
            .Where(c => c is not null && c.Kind == ClassKind.Aspect)
            .Select(c => c!)
            .ToList();

        return Compose(chain, aspects);
    }

    /// <summary>
    /// Root-downward chain for a class, so that subclass entries are applied
    /// after the ancestors they narrow.
    /// </summary>
    public IReadOnlyList<ClassDefinition> BuildTaxonomyChain(Guid classId)
    {
        var chain = new List<ClassDefinition>();
        var seen = new HashSet<Guid>();
        var current = _source.GetClass(classId);

        while (current is not null)
        {
            if (!seen.Add(current.Id))
            {
                throw new NarrowingViolationException(
                    $"Cycle detected in taxonomy chain at class '{current.ClassKey}'.");
            }

            chain.Add(current);
            current = current.ParentClassId is null ? null : _source.GetClass(current.ParentClassId.Value);
        }

        chain.Reverse();
        return chain;
    }

    public EffectivePropertySet Compose(
        IReadOnlyList<ClassDefinition> taxonomyChain,
        IReadOnlyList<ClassDefinition> aspectClasses)
    {
        var accumulated = new Dictionary<Guid, EffectiveProperty>();

        for (var depth = 0; depth < taxonomyChain.Count; depth++)
        {
            ApplyClass(accumulated, taxonomyChain[depth], depth, enforceNarrowing: true);
        }

        foreach (var aspect in aspectClasses)
        {
            // Aspects are orthogonal and non-inherited, so they contribute rather
            // than narrow. A collision with a taxonomy-contributed definition is
            // still subject to the narrowing rules.
            ApplyClass(accumulated, aspect, depth: -1, enforceNarrowing: true);
        }

        var ordered = accumulated.Values
            .OrderBy(p => p.Constraint.DisplayGroup ?? p.ContributedByClassName, StringComparer.Ordinal)
            .ThenBy(p => p.Constraint.DisplayOrder ?? int.MaxValue)
            .ThenBy(p => p.Definition.Name, StringComparer.Ordinal)
            .ToList();

        return new EffectivePropertySet(ordered, taxonomyChain, aspectClasses);
    }

    private void ApplyClass(
        Dictionary<Guid, EffectiveProperty> accumulated,
        ClassDefinition classDefinition,
        int depth,
        bool enforceNarrowing)
    {
        foreach (var candidate in _source.GetClassProperties(classDefinition.Id))
        {
            var definition = _source.GetPropertyDefinition(candidate.DefinitionId);
            if (definition is null)
            {
                continue;
            }

            if (!accumulated.TryGetValue(candidate.DefinitionId, out var existing))
            {
                accumulated[candidate.DefinitionId] = new EffectiveProperty(
                    definition,
                    candidate,
                    classDefinition.Id,
                    classDefinition.Name,
                    classDefinition.Kind,
                    depth);
                continue;
            }

            if (enforceNarrowing)
            {
                NarrowingRules.EnsureNarrows(
                    existing.Constraint,
                    candidate,
                    definition.Name,
                    classDefinition.ClassKey);
            }

            accumulated[candidate.DefinitionId] = existing with
            {
                Constraint = NarrowingRules.Merge(existing.Constraint, candidate),
                ContributedByClassId = classDefinition.Id,
                ContributedByClassName = classDefinition.Name,
                ContributedByKind = classDefinition.Kind,
                ChainDepth = depth
            };
        }
    }
}
