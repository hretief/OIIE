namespace SimHost.Domain.Common;

/// <summary>
/// A class entities are classified against, carrying a property set. Tags and
/// assets do not have fixed schemas — a centrifugal pump carries flow rate, head
/// and NPSH; a gate valve carries body rating and seat material. Neither set is
/// enumerable in advance (spec §6.5).
/// </summary>
public class ClassDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>RDL URI or local key.</summary>
    public string ClassKey { get; set; } = string.Empty;

    public DefinitionOrigin Origin { get; set; }
    public string? RdlSourceId { get; set; }

    public string Version { get; set; } = "1";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Segment | Asset | Model | Material.</summary>
    public string AppliesTo { get; set; } = string.Empty;

    public ClassKind Kind { get; set; } = ClassKind.Taxonomy;

    public Guid? ParentClassId { get; set; }

    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }

    /// <summary>Participant it arrived from, if it came over the bus.</summary>
    public string? ReceivedFrom { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
}

public class PropertyDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string DefinitionKey { get; set; } = string.Empty;

    public DefinitionOrigin Origin { get; set; }
    public string? RdlSourceId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    public PropertyDataType DataType { get; set; }

    public string? UnitOfMeasure { get; set; }
    public string? UomListId { get; set; }
    public string? CodeListId { get; set; }

    public string? ReceivedFrom { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
}

/// <summary>
/// Attachment of a property definition to a class, with constraints. A subclass
/// may narrow an inherited entry — tighten a range, promote Optional to Required,
/// restrict a code list, fix a unit — but never widen, contradict, or remove
/// (spec §6.5.4).
/// </summary>
public class ClassProperty
{
    public long Id { get; set; }

    public Guid ClassId { get; set; }
    public Guid DefinitionId { get; set; }

    public PropertyRequirement Requirement { get; set; } = PropertyRequirement.Optional;
    public int? MaxCardinality { get; set; }

    public string? DefaultUom { get; set; }
    public string? CodeListId { get; set; }

    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }

    public string? DisplayGroup { get; set; }
    public int? DisplayOrder { get; set; }
}

public class EntityClassification
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;

    public Guid ClassId { get; set; }

    /// <summary>Exactly one primary taxonomy class per entity.</summary>
    public bool IsPrimary { get; set; }

    public string AssignedBy { get; set; } = "system";
    public Guid? SourceMessageId { get; set; }

    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidTo { get; set; }
}

/// <summary>
/// A property value on an entity. Typed value columns mirror CCOM's own
/// numeric/character/blob split and keep the attribute BODs (mim_5021-5029)
/// mappable without inventing a serialisation.
/// </summary>
public class EntityPropertyValue
{
    public long Id { get; set; }

    public string EntityType { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;

    public Guid DefinitionId { get; set; }

    /// <summary>Which class in the chain sanctioned this value.</summary>
    public Guid? ViaClassId { get; set; }

    public decimal? NumericValue { get; set; }
    public string? CharacterValue { get; set; }
    public DateTimeOffset? DateTimeValue { get; set; }
    public bool? BooleanValue { get; set; }
    public string? BlobRef { get; set; }

    /// <summary>As supplied; may differ from the definition's unit.</summary>
    public string? UnitOfMeasure { get; set; }

    public string? CodeValue { get; set; }
    public string? CodeListId { get; set; }

    /// <summary>False = retained but not understood locally. Never discarded.</summary>
    public bool Mapped { get; set; } = true;

    /// <summary>No longer sanctioned after reclassification. Flagged, not deleted.</summary>
    public bool Orphaned { get; set; }

    public Guid? SourceMessageId { get; set; }

    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ValidTo { get; set; }
}
