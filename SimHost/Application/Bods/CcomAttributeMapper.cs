using CcomAttribute = Oiie.Ccom.Types.Attribute;
using Oiie.Ccom.Types;
using SimHost.Application.Classification;
using SimHost.Domain.Common;

namespace SimHost.Application.Bods;

/// <summary>
/// Maps between the Sandbox classification model (§6.5) and CCOM's attribute model.
///
/// Named for the construct it targets, because CCOM supersedes attributes with
/// properties and both remain in the current schema. Attributes are used
/// deliberately: the Sandbox exists to demonstrate ecosystem behaviour, not to be a
/// CCOM conformance reference, and the published document available for
/// verification uses attributes. Migrating later means rewriting this file and
/// adding types to Oiie.Ccom — nothing in the domain model, resolver, narrowing
/// rules or EF schema mentions CCOM at all.
///
/// Reading accepts both shapes (see Entity.AllLooseValues); writing emits only
/// attributes.
///
/// The correspondence is close enough to be structural rather than invented:
///
///   ClassDefinition        ↔ AttributeSetType
///   PropertyDefinition     ↔ AttributeType
///   EntityClassification   ↔ AttributeSetForEntity
///   EntityPropertyValue    ↔ Attribute / SetAttribute
///   typed value columns    ↔ ValueContent subtypes
///
/// This means class-governed properties have a conformant wire form and need no
/// local extension. Reference-data identity travels as InfoSource + IDInInfoSource
/// on the type, which is what lets a receiver resolve a definition it holds — or
/// discover that it does not.
/// </summary>
public sealed class CcomAttributeMapper
{
    private readonly IClassificationSource _source;

    public CcomAttributeMapper(IClassificationSource source)
    {
        _source = source;
    }

    // --- Write side ---------------------------------------------------------

    /// <summary>
    /// Groups values by the class that sanctioned them, so each contributing class
    /// becomes one AttributeSet. Values with no sanctioning class — locally defined
    /// or retained from another participant — go into the loose Attribute list.
    /// </summary>
    public void Apply(
        Entity entity,
        IReadOnlyList<EntityPropertyValue> values,
        EffectivePropertySet effectiveSet,
        string rdlSourceName)
    {
        foreach (var group in values.Where(v => v.ViaClassId is not null).GroupBy(v => v.ViaClassId!.Value))
        {
            var classDefinition = _source.GetClass(group.Key);
            if (classDefinition is null)
            {
                continue;
            }

            entity.AttributeSetForEntity.Add(new AttributeSetForEntity
            {
                AttributeSet = new AttributeSet
                {
                    ShortName = classDefinition.Name,
                    Type = new AttributeSetType
                    {
                        IDInInfoSource = classDefinition.ClassKey,
                        InfoSource = new InfoSource { ShortName = classDefinition.RdlSourceId ?? rdlSourceName },
                        ShortName = classDefinition.Name,
                        Description = classDefinition.Description
                    },
                    SetAttribute = group.Select(v => ToAttribute(v, effectiveSet, rdlSourceName)).ToList()
                }
            });
        }

        foreach (var value in values.Where(v => v.ViaClassId is null))
        {
            entity.Attribute.Add(ToAttribute(value, effectiveSet, rdlSourceName));
        }
    }

    private CcomAttribute ToAttribute(
        EntityPropertyValue value, EffectivePropertySet effectiveSet, string rdlSourceName)
    {
        var definition = _source.GetPropertyDefinition(value.DefinitionId);
        var effective = effectiveSet.Find(value.DefinitionId);

        return new CcomAttribute
        {
            ShortName = definition?.Name,
            Type = new AttributeType
            {
                IDInInfoSource = definition?.DefinitionKey,
                InfoSource = new InfoSource
                {
                    ShortName = definition?.RdlSourceId ?? rdlSourceName
                },
                ShortName = definition?.Name,
                Description = definition?.Description
            },
            ValueContent = ToValueContent(value, definition, effective)
        };
    }

    private static ValueContent? ToValueContent(
        EntityPropertyValue value, PropertyDefinition? definition, EffectiveProperty? effective)
    {
        var unit = value.UnitOfMeasure ?? effective?.Constraint.DefaultUom ?? definition?.UnitOfMeasure;

        if (value.NumericValue is { } number)
        {
            return unit is null
                ? new NumberContent { Number = number }
                : new MeasureContent { Value = number, UnitOfMeasure = unit };
        }

        if (value.CodeValue is not null)
        {
            return new EnumerationItemContent { ShortName = value.CodeValue };
        }

        if (value.BooleanValue is { } flag)
        {
            return new BooleanContent { Boolean = flag };
        }

        if (value.DateTimeValue is { } timestamp)
        {
            return new UTCDateTimeContent
            {
                UTCDateTime = NodaTime.Instant.FromDateTimeOffset(timestamp)
            };
        }

        return value.CharacterValue is null ? null : new TextContent { Text = value.CharacterValue };
    }

    // --- Read side ----------------------------------------------------------

    /// <summary>
    /// Flattens an entity's attribute sets and loose attributes into the incoming
    /// property shape the ingestor consumes. The class keys encountered are returned
    /// separately so the caller can bind them — including binding to a known
    /// ancestor when the leaf class is unfamiliar.
    /// </summary>
    public (List<IncomingProperty> Properties, List<string> ClassKeys) Extract(Entity entity)
    {
        var properties = new List<IncomingProperty>();
        var classKeys = new List<string>();

        foreach (var setForEntity in entity.AllValueSets)
        {
            var set = setForEntity.AttributeSet;
            if (set is null)
            {
                continue;
            }

            if (set.Type?.IDInInfoSource is { } classKey)
            {
                classKeys.Add(classKey);
            }

            properties.AddRange(set.SetAttribute.Select(ToIncoming));
        }

        properties.AddRange(entity.AllLooseValues.Select(ToIncoming));

        return (properties, classKeys);
    }

    private static IncomingProperty ToIncoming(CcomAttribute attribute)
    {
        var key = attribute.Type?.IDInInfoSource
            ?? attribute.Type?.ShortName
            ?? attribute.ShortName
            ?? "unknown";

        var name = attribute.ShortName ?? attribute.Type?.ShortName;

        return attribute.ValueContent switch
        {
            MeasureContent measure => new IncomingProperty(
                key, name, PropertyDataType.Numeric,
                NumericValue: measure.Value, UnitOfMeasure: measure.UnitOfMeasure),

            NumberContent number => new IncomingProperty(
                key, name, PropertyDataType.Numeric, NumericValue: number.Number),

            BooleanContent boolean => new IncomingProperty(
                key, name, PropertyDataType.Boolean, BooleanValue: boolean.Boolean),

            UTCDateTimeContent timestamp => new IncomingProperty(
                key, name, PropertyDataType.DateTime,
                DateTimeValue: timestamp.UTCDateTime?.ToDateTimeOffset()),

            EnumerationItemContent enumeration => new IncomingProperty(
                key, name, PropertyDataType.Character,
                CharacterValue: enumeration.AsDisplayText(),
                CodeValue: enumeration.ShortName),

            { } other => new IncomingProperty(
                key, name, PropertyDataType.Character, CharacterValue: other.AsDisplayText()),

            null => new IncomingProperty(key, name, PropertyDataType.Character)
        };
    }
}
