using System.Xml.Linq;
using System.Xml.Serialization;
using NodaTime;
using NodaTime.Text;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Types;

/// <summary>
/// Everything that can appear as a BOD noun. The Attribute and AttributeSetForEntity
/// members are the CCOM-native carrier for the Sandbox property model — a fixed
/// spine plus an open, class-governed attribute set.
/// </summary>
public abstract class Entity : CcomBase, INoun
{
    protected Entity()
    {
    }

    protected Entity(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
        InfoSource = GetChild(nameof(InfoSource), e => new InfoSource(e));
        Created = element.Child(nameof(Created)).SafeInstant();
        Attribute = GetChildren(nameof(Attribute), e => new Attribute(e));
        AttributeSetForEntity = GetChildren(nameof(AttributeSetForEntity), e => new AttributeSetForEntity(e));

        // CCOM supersedes the attribute constructs with property constructs, and the
        // current schema carries both. The Sandbox writes attributes deliberately
        // (spec §6.5) but must not silently drop property-shaped content sent by a
        // more modern participant, so it is read into the same types and flagged
        // XmlIgnore — receive-tolerant, send-conservative, enforced by the model
        // rather than by discipline.
        Property = GetChildren(nameof(Property), e => new Attribute(e));
        PropertySetForEntity = GetChildren(nameof(PropertySetForEntity), e => new AttributeSetForEntity(e));
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    /// <summary>The participant's own key. Pairs with InfoSource to form the CIR identity.</summary>
    [XmlElement(Order = 1)]
    public string? IDInInfoSource { get; set; }

    [XmlElement(Order = 2)]
    public InfoSource? InfoSource { get; set; }

    [XmlIgnore]
    public Instant? Created { get; set; }

    [XmlElement("Created", Order = 3)]
    public string? CreatedText
    {
        get => Created is { } value ? InstantPattern.ExtendedIso.Format(value) : null;
        set => Created = XNodeExtensions.ParseInstant(value);
    }

    /// <summary>Loose properties not governed by an attribute set.</summary>
    [XmlElement("Attribute", Order = 20)]
    public List<Attribute> Attribute { get; set; } = [];

    /// <summary>
    /// Class-governed property groups. AttributeSet.Type is the class; its
    /// SetAttribute members are the property values that class sanctions.
    /// </summary>
    [XmlElement("AttributeSetForEntity", Order = 21)]
    public List<AttributeSetForEntity> AttributeSetForEntity { get; set; } = [];

    /// <summary>
    /// Property-shaped content received from another participant. Never serialised:
    /// see the note in the constructor.
    /// </summary>
    [XmlIgnore]
    public List<Attribute> Property { get; set; } = [];

    /// <summary>Property-set classification received from another participant. Never serialised.</summary>
    [XmlIgnore]
    public List<AttributeSetForEntity> PropertySetForEntity { get; set; } = [];

    /// <summary>Attributes and properties together, for ingestion.</summary>
    public IEnumerable<Attribute> AllLooseValues => Attribute.Concat(Property);

    /// <summary>Attribute sets and property sets together, for ingestion.</summary>
    public IEnumerable<AttributeSetForEntity> AllValueSets =>
        AttributeSetForEntity.Concat(PropertySetForEntity);

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
    public bool ShouldSerializeAttribute() => Attribute.Count > 0;
    public bool ShouldSerializeAttributeSetForEntity() => AttributeSetForEntity.Count > 0;
    public bool ShouldSerializeCreatedText() => Created.HasValue;
}

/// <summary>The definition of a property — name, meaning, and reference-data identity.</summary>
public class AttributeType : CcomBase
{
    public AttributeType()
    {
    }

    public AttributeType(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
        InfoSource = GetChild(nameof(InfoSource), e => new InfoSource(e));
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? IDInInfoSource { get; set; }

    [XmlElement(Order = 2)]
    public InfoSource? InfoSource { get; set; }

    [XmlElement(Order = 3)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 4)]
    public string? FullName { get; set; }

    [XmlElement(Order = 5)]
    public string? Description { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>A property value on an entity.</summary>
public class Attribute : CcomBase
{
    public Attribute()
    {
    }

    public Attribute(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
        Type = GetChild(nameof(Type), e => new AttributeType(e));
        ValueContent = ValueContent.Parse(element.Child(nameof(ValueContent)));
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 2)]
    public string? FullName { get; set; }

    [XmlElement(Order = 3)]
    public string? Description { get; set; }

    [XmlElement(Order = 4)]
    public AttributeType? Type { get; set; }

    [XmlElement(Order = 5)]
    public ValueContent? ValueContent { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;

    /// <summary>
    /// An empty ValueContent element is a schema error, and a TextContent with a
    /// null Text serialises to exactly that.
    /// </summary>
    public bool ShouldSerializeValueContent() => ValueContent switch
    {
        null => false,
        TextContent { Text: null } => false,
        _ => true
    };

    public override string ToString() => $"{ShortName ?? Type?.ShortName}: {ValueContent?.AsDisplayText()}";
}

/// <summary>
/// The definition of a class — a named group of properties that entities can be
/// classified against. This is the CCOM carrier for a reference-data class.
/// </summary>
public class AttributeSetType : CcomBase
{
    public AttributeSetType()
    {
    }

    public AttributeSetType(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
        InfoSource = GetChild(nameof(InfoSource), e => new InfoSource(e));
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? IDInInfoSource { get; set; }

    [XmlElement(Order = 2)]
    public InfoSource? InfoSource { get; set; }

    [XmlElement(Order = 3)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 4)]
    public string? FullName { get; set; }

    [XmlElement(Order = 5)]
    public string? Description { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>A class instance: the class, plus the property values it sanctions.</summary>
public class AttributeSet : CcomBase
{
    public AttributeSet()
    {
    }

    public AttributeSet(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        Type = GetChild(nameof(Type), e => new AttributeSetType(e))
               ?? GetChild("PropertySetType", e => new AttributeSetType(e));

        var members = GetChildren(nameof(SetAttribute), e => new Attribute(e));
        SetAttribute = members.Count > 0
            ? members
            : GetChildren("SetProperty", e => new Attribute(e));
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 2)]
    public AttributeSetType? Type { get; set; }

    [XmlElement("SetAttribute", Order = 3)]
    public List<Attribute> SetAttribute { get; set; } = [];

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>Binds an attribute set to an entity — the classification assignment.</summary>
public class AttributeSetForEntity : CcomBase
{
    public AttributeSetForEntity()
    {
    }

    public AttributeSetForEntity(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        AttributeSet = GetChild(nameof(AttributeSet), e => new AttributeSet(e))
                       ?? GetChild("PropertySet", e => new AttributeSet(e));
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public AttributeSet? AttributeSet { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}
