namespace CirProvider.Domain;

/// <summary>ws-CIR §2.3 — container for a set of Categories.</summary>
public sealed record Registry
{
    public required string Id { get; init; }
    public IReadOnlyList<LocalizedText> Description { get; init; } = [];
    public IReadOnlyList<Category> Categories { get; init; } = [];
}

/// <summary>ws-CIR §2.4 — container for Entries. Unique on (Id, SourceId) within a Registry.</summary>
public sealed record Category
{
    public required string Id { get; init; }
    public required string SourceId { get; init; }
    public IReadOnlyList<LocalizedText> Description { get; init; } = [];
    public IReadOnlyList<Entry> Entries { get; init; } = [];
}

/// <summary>ws-CIR §2.5 — a registered object. Unique on (IdInSource, SourceId) within a Category.</summary>
public sealed record Entry
{
    public required string IdInSource { get; init; }
    public required string SourceId { get; init; }

    /// <summary>Correlation UUID. Entries sharing a CIRID are equivalent objects.</summary>
    public Guid? Cirid { get; init; }

    public string? SourceOwnerId { get; init; }
    public string? Name { get; init; }
    public LocalizedText? Description { get; init; }
    public bool? Inactive { get; init; }
    public IReadOnlyList<Property> Properties { get; init; } = [];
}

/// <summary>ws-CIR §2.6 — a linking attribute of an Entry. Unique Id per Entry.</summary>
public sealed record Property
{
    public required string Id { get; init; }
    public string? DataType { get; init; }
    public IReadOnlyList<PropertyValue> PropertyValue { get; init; } = [];
}

/// <summary>ws-CIR §2.7 — key/value/unit triple.</summary>
public sealed record PropertyValue
{
    public string? Key { get; init; }
    public required string Value { get; init; }
    public string? UnitOfMeasure { get; init; }
}

/// <summary>UN/CEFACT TextType — text plus optional language/locale.</summary>
public sealed record LocalizedText
{
    public required string Value { get; init; }
    public string? LanguageId { get; init; }
}

// ---------------------------------------------------------------------------
// Identifiers
// ---------------------------------------------------------------------------

public record EntryIdentifier
{
    public required string RegistryId { get; init; }
    public required string CategoryId { get; init; }
    public required string CategorySourceId { get; init; }
    public required string EntryIdInSource { get; init; }
    public required string EntrySourceId { get; init; }
}

public sealed record PropertyIdentifier : EntryIdentifier
{
    public required string PropertyId { get; init; }
}

public sealed record CategoryIdentifier
{
    public required string RegistryId { get; init; }
    public required string CategoryId { get; init; }
    public required string CategorySourceId { get; init; }
}

// ---------------------------------------------------------------------------
// Filters — ws-CIR §3.2.1
// ---------------------------------------------------------------------------

/// <summary>
/// Filter types within one Filter AND together; multiple Filters of the same
/// type OR together. A null member means "unfiltered" (logical TRUE).
/// Values may contain the ws-CIR §4 wildcard subset and are implicitly anchored.
/// </summary>
public sealed record CirFilter
{
    public RegistryFilter? RegistryFilter { get; init; }
    public CategoryFilter? CategoryFilter { get; init; }
    public EntryFilter? EntryFilter { get; init; }
    public PropertyFilter? PropertyFilter { get; init; }
}

public sealed record RegistryFilter
{
    public string? Id { get; init; }
    public string? Description { get; init; }
}

public sealed record CategoryFilter
{
    public string? Id { get; init; }
    public string? SourceId { get; init; }
    public string? Description { get; init; }
}

public sealed record EntryFilter
{
    public string? IdInSource { get; init; }
    public string? SourceId { get; init; }
    public string? SourceOwnerId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public Guid? Cirid { get; init; }
    public bool? Inactive { get; init; }
}

public sealed record PropertyFilter
{
    public string? Id { get; init; }
    public string? Key { get; init; }
    public string? Value { get; init; }
}

// ---------------------------------------------------------------------------
// Request shapes
// ---------------------------------------------------------------------------

public sealed record CreateRegistryRequest
{
    public required IReadOnlyList<Registry> Registry { get; init; }
    public bool CreateCirid { get; init; }
}

public sealed record UpdateRegistryRequest
{
    public required IReadOnlyList<Registry> Registry { get; init; }
}

/// <summary>ws-CIR §3.1.2 — links a new Entry to an existing equivalent Entry.</summary>
public sealed record EquivalentEntryRequest
{
    public required string ExistingIdInSource { get; init; }
    public required string ExistingSourceId { get; init; }
    public required string RegistryId { get; init; }
    public required string CategoryId { get; init; }
    public required string CategorySourceId { get; init; }
    public required Entry Entry { get; init; }
}

public sealed record UpdateEntryCiridRequest
{
    public required IReadOnlyList<Guid> OldCirid { get; init; }
    public required Guid NewCirid { get; init; }
}

public sealed record GetRegistryRequest
{
    public IReadOnlyList<CirFilter> Filter { get; init; } = [];
}

public sealed record GetEquivalentEntriesRequest
{
    public required IReadOnlyList<EntryIdentifier> EntryIdentifier { get; init; }
    public IReadOnlyList<string> TargetSourceId { get; init; } = [];
}
