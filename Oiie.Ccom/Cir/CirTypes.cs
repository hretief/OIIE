using System.Xml.Linq;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Cir;

/// <summary>
/// The registry hierarchy: Registry contains Categories, a Category contains
/// Entries, an Entry carries Properties.
///
/// The identity that matters is <see cref="Entry.SourceID"/> plus
/// <see cref="Entry.IDInSource"/> — which system said it, and what that system
/// calls it. <see cref="Entry.CIRID"/> is what the registry assigns to say two of
/// those pairs denote the same physical thing.
/// </summary>
public sealed class Registry
{
    public Registry()
    {
    }

    public Registry(XElement element)
    {
        ID = element.Child(nameof(ID)).SafeValue() ?? string.Empty;
        Description = element.Children(nameof(Description)).Select(e => e.Value).ToList();
        Category = element.Children(nameof(Category)).Select(e => new Category(e)).ToList();
    }

    public string ID { get; set; } = string.Empty;

    public List<string> Description { get; set; } = [];

    public List<Category> Category { get; set; } = [];

    public XElement ToElement(XNamespace ns) => new(ns + nameof(Registry),
        new XElement(ns + nameof(ID), ID),
        Description.Select(d => new XElement(ns + nameof(Description), d)),
        Category.Select(c => c.ToElement(ns)));
}

/// <summary>
/// A kind of thing being registered — Segment, Asset, Model.
///
/// CategorySourceID names the authority that defined the category, so two
/// organisations can both have a "Segment" category without collision.
/// </summary>
public sealed class Category
{
    public Category()
    {
    }

    public Category(XElement element)
    {
        ID = element.Child(nameof(ID)).SafeValue() ?? string.Empty;
        CategorySourceID = element.Child(nameof(CategorySourceID)).SafeValue() ?? string.Empty;
        Description = element.Children(nameof(Description)).Select(e => e.Value).ToList();
        Entry = element.Children(nameof(Entry)).Select(e => new Entry(e)).ToList();
    }

    public string ID { get; set; } = string.Empty;

    public string CategorySourceID { get; set; } = string.Empty;

    public List<string> Description { get; set; } = [];

    public List<Entry> Entry { get; set; } = [];

    public XElement ToElement(XNamespace ns) => new(ns + nameof(Category),
        new XElement(ns + nameof(ID), ID),
        new XElement(ns + nameof(CategorySourceID), CategorySourceID),
        Description.Select(d => new XElement(ns + nameof(Description), d)),
        Entry.Select(e => e.ToElement(ns)));
}

public sealed class Entry
{
    public Entry()
    {
    }

    public Entry(XElement element)
    {
        IDInSource = element.Child(nameof(IDInSource)).SafeValue() ?? string.Empty;
        SourceID = element.Child(nameof(SourceID)).SafeValue() ?? string.Empty;
        CIRID = element.Child(nameof(CIRID)).SafeNullableGuid();
        SourceOwnerID = element.Child(nameof(SourceOwnerID)).SafeValue();
        Name = element.Child(nameof(Name)).SafeValue();
        Description = element.Children(nameof(Description)).Select(e => e.Value).ToList();
        Inactive = element.Child(nameof(Inactive)).SafeBoolean();
        Property = element.Children(nameof(Property)).Select(e => new Property(e)).ToList();
    }

    /// <summary>What the owning system calls it, e.g. TIC-106.</summary>
    public string IDInSource { get; set; } = string.Empty;

    /// <summary>Which system says so, e.g. ENG.</summary>
    public string SourceID { get; set; } = string.Empty;

    /// <summary>
    /// The shared identity. Left null when registering: the registry assigns it,
    /// and supplying one would be asserting an equivalence rather than requesting
    /// registration.
    /// </summary>
    public Guid? CIRID { get; set; }

    public string? SourceOwnerID { get; set; }

    public string? Name { get; set; }

    public List<string> Description { get; set; } = [];

    public bool? Inactive { get; set; }

    /// <summary>
    /// Discriminating values used to identify equivalent entries. Deliberately few:
    /// the ws-CIR Property set is a linking aid, not a property master. The full
    /// attribute set stays with the participant and travels in CCOM BODs.
    /// </summary>
    public List<Property> Property { get; set; } = [];

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(Entry),
            new XElement(ns + nameof(IDInSource), IDInSource),
            new XElement(ns + nameof(SourceID), SourceID));

        if (CIRID is { } cirid) element.Add(new XElement(ns + nameof(CIRID), cirid.ToString()));
        if (SourceOwnerID is { Length: > 0 }) element.Add(new XElement(ns + nameof(SourceOwnerID), SourceOwnerID));
        if (Name is { Length: > 0 }) element.Add(new XElement(ns + nameof(Name), Name));

        foreach (var description in Description)
        {
            element.Add(new XElement(ns + nameof(Description), description));
        }

        if (Inactive is { } inactive)
        {
            element.Add(new XElement(ns + nameof(Inactive), inactive ? "true" : "false"));
        }

        foreach (var property in Property)
        {
            element.Add(property.ToElement(ns));
        }

        return element;
    }
}

public sealed class Property
{
    public Property()
    {
    }

    public Property(XElement element)
    {
        ID = element.Child(nameof(ID)).SafeValue() ?? string.Empty;
        DataType = element.Child(nameof(DataType)).SafeValue();
        PropertyValue = element.Children(nameof(PropertyValue))
            .Select(e => new PropertyValue(e)).ToList();
    }

    public string ID { get; set; } = string.Empty;

    public List<PropertyValue> PropertyValue { get; set; } = [];

    public string? DataType { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(Property),
            new XElement(ns + nameof(ID), ID));

        foreach (var value in PropertyValue)
        {
            element.Add(value.ToElement(ns));
        }

        if (DataType is { Length: > 0 })
        {
            element.Add(new XElement(ns + nameof(DataType), DataType));
        }

        return element;
    }

    public static Property Simple(string id, string value, string? unitOfMeasure = null) => new()
    {
        ID = id,
        PropertyValue = [new PropertyValue { Value = value, UnitOfMeasure = unitOfMeasure }]
    };
}

public sealed class PropertyValue
{
    public PropertyValue()
    {
    }

    public PropertyValue(XElement element)
    {
        Key = element.Child(nameof(Key)).SafeValue();
        Value = element.Child(nameof(Value)).SafeValue() ?? string.Empty;
        UnitOfMeasure = element.Child(nameof(UnitOfMeasure)).SafeValue();
    }

    public string? Key { get; set; }

    public string Value { get; set; } = string.Empty;

    public string? UnitOfMeasure { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(PropertyValue));

        if (Key is { Length: > 0 }) element.Add(new XElement(ns + nameof(Key), Key));
        element.Add(new XElement(ns + nameof(Value), Value));
        if (UnitOfMeasure is { Length: > 0 }) element.Add(new XElement(ns + nameof(UnitOfMeasure), UnitOfMeasure));

        return element;
    }
}

// --- Filters ----------------------------------------------------------------

/// <summary>
/// Query criteria. Every member is optional at every level, so an empty filter
/// matches everything — worth knowing before issuing one against a populated
/// registry.
/// </summary>
public sealed class Filter
{
    public RegistryFilter? RegistryFilter { get; set; }

    public CategoryFilter? CategoryFilter { get; set; }

    public EntryFilter? EntryFilter { get; set; }

    public PropertyFilter? PropertyFilter { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(Filter));

        if (RegistryFilter is not null) element.Add(RegistryFilter.ToElement(ns));
        if (CategoryFilter is not null) element.Add(CategoryFilter.ToElement(ns));
        if (EntryFilter is not null) element.Add(EntryFilter.ToElement(ns));
        if (PropertyFilter is not null) element.Add(PropertyFilter.ToElement(ns));

        return element;
    }
}

public sealed class RegistryFilter
{
    public string? ID { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(RegistryFilter));
        if (ID is { Length: > 0 }) element.Add(new XElement(ns + nameof(ID), ID));
        return element;
    }
}

public sealed class CategoryFilter
{
    public string? ID { get; set; }

    public string? CategorySourceID { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(CategoryFilter));
        if (ID is { Length: > 0 }) element.Add(new XElement(ns + nameof(ID), ID));
        if (CategorySourceID is { Length: > 0 })
        {
            element.Add(new XElement(ns + nameof(CategorySourceID), CategorySourceID));
        }
        return element;
    }
}

public sealed class EntryFilter
{
    public string? IDInSource { get; set; }

    public string? SourceID { get; set; }

    public Guid? CIRID { get; set; }

    public string? SourceOwnerID { get; set; }

    public string? Name { get; set; }

    public bool? Inactive { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(EntryFilter));

        if (IDInSource is { Length: > 0 }) element.Add(new XElement(ns + nameof(IDInSource), IDInSource));
        if (SourceID is { Length: > 0 }) element.Add(new XElement(ns + nameof(SourceID), SourceID));
        if (CIRID is { } cirid) element.Add(new XElement(ns + nameof(CIRID), cirid.ToString()));
        if (SourceOwnerID is { Length: > 0 }) element.Add(new XElement(ns + nameof(SourceOwnerID), SourceOwnerID));
        if (Name is { Length: > 0 }) element.Add(new XElement(ns + nameof(Name), Name));
        if (Inactive is { } inactive) element.Add(new XElement(ns + nameof(Inactive), inactive ? "true" : "false"));

        return element;
    }
}

public sealed class PropertyFilter
{
    public string? ID { get; set; }

    public string? DataType { get; set; }

    public XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + nameof(PropertyFilter));
        if (ID is { Length: > 0 }) element.Add(new XElement(ns + nameof(ID), ID));
        if (DataType is { Length: > 0 }) element.Add(new XElement(ns + nameof(DataType), DataType));
        return element;
    }
}

/// <summary>
/// Asserts that a new entry denotes the same thing as one already registered.
///
/// This is the operation that actually links identities. Registration alone gives
/// each participant its own entry; only an equivalence assertion makes them one
/// physical thing, and only a participant that has seen both identifiers is in a
/// position to assert it. In the handover chain that is REG-LOCATION: it received
/// ENG:TIC-106 and issued LOC-000001, so it alone knows they are the same pump.
/// </summary>
public sealed class EquivalentEntry
{
    /// <summary>The identifier already in the registry, e.g. TIC-106.</summary>
    public string ExistingIDInSource { get; set; } = string.Empty;

    /// <summary>The system that owns it, e.g. ENG.</summary>
    public string ExistingSourceID { get; set; } = string.Empty;

    public string RegistryID { get; set; } = string.Empty;

    public string CategoryID { get; set; } = string.Empty;

    public string CategorySourceID { get; set; } = string.Empty;

    /// <summary>The new entry to attach to the existing entry's identity.</summary>
    public Entry Entry { get; set; } = new();

    public XElement ToElement(XNamespace ns) => new(ns + nameof(EquivalentEntry),
        new XElement(ns + nameof(ExistingIDInSource), ExistingIDInSource),
        new XElement(ns + nameof(ExistingSourceID), ExistingSourceID),
        new XElement(ns + nameof(RegistryID), RegistryID),
        new XElement(ns + nameof(CategoryID), CategoryID),
        new XElement(ns + nameof(CategorySourceID), CategorySourceID),
        Entry.ToElement(ns));
}
