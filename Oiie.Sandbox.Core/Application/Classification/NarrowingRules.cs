using SimHost.Domain.Common;

namespace SimHost.Application.Classification;

/// <summary>
/// A subclass may narrow an inherited ClassProperty: tighten a numeric range,
/// promote Optional to Required, restrict a code list, fix a unit of measure.
/// It may not widen, contradict, or remove (spec §6.5.4).
/// </summary>
public static class NarrowingRules
{
    public static void EnsureNarrows(
        ClassProperty inherited,
        ClassProperty candidate,
        string propertyName,
        string classKey)
    {
        if (Rank(candidate.Requirement) < Rank(inherited.Requirement))
        {
            throw new NarrowingViolationException(
                $"Class '{classKey}' weakens '{propertyName}' from " +
                $"{inherited.Requirement} to {candidate.Requirement}. " +
                "A subclass may promote a requirement but not relax it.");
        }

        if (inherited.MinValue is { } inheritedMin &&
            candidate.MinValue is { } candidateMin &&
            candidateMin < inheritedMin)
        {
            throw new NarrowingViolationException(
                $"Class '{classKey}' widens the lower bound of '{propertyName}' " +
                $"from {inheritedMin} to {candidateMin}.");
        }

        if (inherited.MaxValue is { } inheritedMax &&
            candidate.MaxValue is { } candidateMax &&
            candidateMax > inheritedMax)
        {
            throw new NarrowingViolationException(
                $"Class '{classKey}' widens the upper bound of '{propertyName}' " +
                $"from {inheritedMax} to {candidateMax}.");
        }

        if (inherited.MaxCardinality is { } inheritedCardinality &&
            candidate.MaxCardinality is { } candidateCardinality &&
            candidateCardinality > inheritedCardinality)
        {
            throw new NarrowingViolationException(
                $"Class '{classKey}' widens the cardinality of '{propertyName}' " +
                $"from {inheritedCardinality} to {candidateCardinality}.");
        }

        if (!string.IsNullOrEmpty(inherited.DefaultUom) &&
            !string.IsNullOrEmpty(candidate.DefaultUom) &&
            !string.Equals(inherited.DefaultUom, candidate.DefaultUom, StringComparison.Ordinal))
        {
            throw new NarrowingViolationException(
                $"Class '{classKey}' contradicts the unit of measure for '{propertyName}': " +
                $"inherited '{inherited.DefaultUom}', declared '{candidate.DefaultUom}'.");
        }
    }

    /// <summary>Takes the tighter of each constraint. Assumes EnsureNarrows has passed.</summary>
    public static ClassProperty Merge(ClassProperty inherited, ClassProperty candidate) =>
        new()
        {
            Id = candidate.Id,
            ClassId = candidate.ClassId,
            DefinitionId = candidate.DefinitionId,
            Requirement = Rank(candidate.Requirement) >= Rank(inherited.Requirement)
                ? candidate.Requirement
                : inherited.Requirement,
            MaxCardinality = Tighter(inherited.MaxCardinality, candidate.MaxCardinality, takeLower: true),
            DefaultUom = candidate.DefaultUom ?? inherited.DefaultUom,
            CodeListId = candidate.CodeListId ?? inherited.CodeListId,
            MinValue = Tighter(inherited.MinValue, candidate.MinValue, takeLower: false),
            MaxValue = Tighter(inherited.MaxValue, candidate.MaxValue, takeLower: true),
            DisplayGroup = candidate.DisplayGroup ?? inherited.DisplayGroup,
            DisplayOrder = candidate.DisplayOrder ?? inherited.DisplayOrder
        };

    /// <summary>Required is the strongest, Optional the weakest.</summary>
    private static int Rank(PropertyRequirement requirement) => requirement switch
    {
        PropertyRequirement.Required => 2,
        PropertyRequirement.Recommended => 1,
        _ => 0
    };

    private static decimal? Tighter(decimal? left, decimal? right, bool takeLower)
    {
        if (left is null) return right;
        if (right is null) return left;
        return takeLower ? Math.Min(left.Value, right.Value) : Math.Max(left.Value, right.Value);
    }

    private static int? Tighter(int? left, int? right, bool takeLower)
    {
        if (left is null) return right;
        if (right is null) return left;
        return takeLower ? Math.Min(left.Value, right.Value) : Math.Max(left.Value, right.Value);
    }
}
