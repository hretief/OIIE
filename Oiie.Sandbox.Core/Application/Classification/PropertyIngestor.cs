using SimHost.Domain.Common;

namespace SimHost.Application.Classification;

/// <summary>A property value as it arrived in a BOD, before local interpretation.</summary>
public sealed record IncomingProperty(
    string DefinitionKey,
    string? Name,
    PropertyDataType DataType,
    decimal? NumericValue = null,
    string? CharacterValue = null,
    DateTimeOffset? DateTimeValue = null,
    bool? BooleanValue = null,
    string? UnitOfMeasure = null,
    string? CodeValue = null,
    string? CodeListId = null);

public sealed record PropertyIngestionResult(
    IReadOnlyList<EntityPropertyValue> Values,
    IReadOnlyList<PropertyDefinition> InferredDefinitions,
    int MappedCount,
    int UnmappedCount);

/// <summary>
/// Applies incoming property values against the receiver's effective property set.
///
/// Values with no local definition are retained with Mapped = false and an
/// Inferred stub definition — never discarded, never silently absorbed. A
/// receiver keeping an attribute it does not understand, visibly flagged, is the
/// honest answer to "what happens when my system has fields yours does not"
/// (spec §6.5.5).
/// </summary>
public sealed class PropertyIngestor
{
    private readonly IClassificationSource _source;

    public PropertyIngestor(IClassificationSource source)
    {
        _source = source;
    }

    public PropertyIngestionResult Ingest(
        string entityType,
        string entityKey,
        IReadOnlyList<IncomingProperty> incoming,
        EffectivePropertySet effectiveSet,
        string fromParticipant,
        Guid? sourceMessageId,
        DateTimeOffset at)
    {
        var values = new List<EntityPropertyValue>();
        var inferred = new List<PropertyDefinition>();
        var mapped = 0;
        var unmapped = 0;

        foreach (var property in incoming)
        {
            var definition = _source.FindPropertyDefinitionByKey(property.DefinitionKey);

            if (definition is null)
            {
                definition = new PropertyDefinition
                {
                    // Derived from the key, not random.
                    //
                    // Two segments in one BOD carrying the same unknown property used
                    // to mint two definitions with the same DefinitionKey, which is
                    // uniquely indexed. A deterministic id makes the duplicate collapse
                    // into the same row instead of colliding on insert.
                    Id = StableDefinitionId(property.DefinitionKey),
                    DefinitionKey = property.DefinitionKey,
                    Origin = DefinitionOrigin.Inferred,
                    Name = property.Name ?? property.DefinitionKey,
                    DataType = property.DataType,
                    UnitOfMeasure = property.UnitOfMeasure,
                    CodeListId = property.CodeListId,
                    ReceivedFrom = fromParticipant,
                    ReceivedAt = at
                };

                inferred.Add(definition);
            }

            var sanctioned = effectiveSet.Find(definition.Id);
            var isMapped = definition.Origin != DefinitionOrigin.Inferred && sanctioned is not null;

            if (isMapped) mapped++; else unmapped++;

            values.Add(new EntityPropertyValue
            {
                EntityType = entityType,
                EntityKey = entityKey,
                DefinitionId = definition.Id,
                ViaClassId = sanctioned?.ContributedByClassId,
                NumericValue = property.NumericValue,
                CharacterValue = property.CharacterValue,
                DateTimeValue = property.DateTimeValue,
                BooleanValue = property.BooleanValue,
                UnitOfMeasure = property.UnitOfMeasure,
                CodeValue = property.CodeValue,
                CodeListId = property.CodeListId,
                Mapped = isMapped,
                Orphaned = false,
                SourceMessageId = sourceMessageId,
                ValidFrom = at
            });
        }

        return new PropertyIngestionResult(values, inferred, mapped, unmapped);
    }

    /// <summary>
    /// Same key, same id, in every participant and across every message.
    /// </summary>
    private static Guid StableDefinitionId(string definitionKey) =>
        new(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"inferred:{definitionKey}")));

    /// <summary>
    /// Reclassification does not delete property values. Anything no longer
    /// sanctioned by the new effective set is flagged orphaned and surfaced
    /// alongside unmapped properties (spec §6.5.7).
    /// </summary>
    public static int MarkOrphaned(
        IEnumerable<EntityPropertyValue> existing,
        EffectivePropertySet newEffectiveSet)
    {
        var count = 0;

        foreach (var value in existing)
        {
            var stillSanctioned = newEffectiveSet.Sanctions(value.DefinitionId);

            if (!stillSanctioned && !value.Orphaned)
            {
                value.Orphaned = true;
                value.ViaClassId = null;
                count++;
            }
            else if (stillSanctioned && value.Orphaned)
            {
                // Re-sanctioned, e.g. after a definition arrived from the RDL.
                value.Orphaned = false;
                value.ViaClassId = newEffectiveSet.Find(value.DefinitionId)?.ContributedByClassId;
            }
        }

        return count;
    }
}
