using System.Xml.Linq;
using System.Xml.Serialization;
using NodaTime;
using NodaTime.Text;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Types;

/// <summary>Reference-data type of a segment — the functional location class.</summary>
public class SegmentType : CcomBase
{
    public SegmentType()
    {
    }

    public SegmentType(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
        InfoSource = GetChild(nameof(InfoSource), e => new InfoSource(e));
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
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

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

public class AssetType : CcomBase
{
    public AssetType()
    {
    }

    public AssetType(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
        InfoSource = GetChild(nameof(InfoSource), e => new InfoSource(e));
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
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

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>Functional location. The REG-LOCATION noun.</summary>
public class Segment : Entity
{
    public Segment()
    {
    }

    public Segment(XElement element) : base(element)
    {
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
        Type = GetChild(nameof(Type), e => new SegmentType(e));
        IsGroup = element.Child(nameof(IsGroup)).SafeBoolean();
        ParentComponent = GetChildren(nameof(ParentComponent), e => new SegmentComponent(e));
        ChildComponent = GetChildren(nameof(ChildComponent), e => new SegmentComponent(e));
    }

    [XmlElement(Order = 4)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 5)]
    public string? FullName { get; set; }

    [XmlElement(Order = 6)]
    public string? Description { get; set; }

    [XmlElement(Order = 7)]
    public SegmentType? Type { get; set; }

    [XmlIgnore]
    public bool? IsGroup { get; set; }

    [XmlElement("IsGroup", Order = 8)]
    public string? IsGroupText
    {
        get => IsGroup?.ToString().ToLowerInvariant();
        set => IsGroup = bool.TryParse(value, out var v) ? v : null;
    }

    [XmlElement("ParentComponent", Order = 9)]
    public List<SegmentComponent> ParentComponent { get; set; } = [];

    [XmlElement("ChildComponent", Order = 10)]
    public List<SegmentComponent> ChildComponent { get; set; } = [];

    public bool ShouldSerializeIsGroupText() => IsGroup.HasValue;
    public bool ShouldSerializeParentComponent() => ParentComponent.Count > 0;
    public bool ShouldSerializeChildComponent() => ChildComponent.Count > 0;

    public override string ToString() => $"{ShortName} ({IDInInfoSource})";
}

/// <summary>Parent/child link between segments — the breakdown structure edge.</summary>
public class SegmentComponent : CcomBase
{
    public SegmentComponent()
    {
    }

    public SegmentComponent(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        ParentSegment = GetChild(nameof(ParentSegment), e => new Segment(e));
        ChildSegment = GetChild(nameof(ChildSegment), e => new Segment(e));
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public Segment? ParentSegment { get; set; }

    [XmlElement(Order = 2)]
    public Segment? ChildSegment { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>Serialised physical asset. The REG-ASSET noun.</summary>
public class Asset : Entity
{
    public Asset()
    {
    }

    public Asset(XElement element) : base(element)
    {
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
        Type = GetChild(nameof(Type), e => new AssetType(e));
        SerialNumber = element.Child(nameof(SerialNumber)).SafeValue();
        Model = GetChild(nameof(Model), e => new Model(e));
    }

    [XmlElement(Order = 4)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 5)]
    public string? FullName { get; set; }

    [XmlElement(Order = 6)]
    public string? Description { get; set; }

    [XmlElement(Order = 7)]
    public AssetType? Type { get; set; }

    // Model before SerialNumber. The CCOM sequence runs Type, RegistrationSite,
    // Manufacturer, Model, ... , SerialNumber, so the obvious pairing of the two
    // identifying fields is the wrong order and the schema rejects it.
    [XmlElement(Order = 8)]
    public Model? Model { get; set; }

    [XmlElement(Order = 9)]
    public string? SerialNumber { get; set; }

    public override string ToString() => $"{ShortName} ({IDInInfoSource})";
}

/// <summary>OEM product model. The REG-PRODUCT noun.</summary>
public class Model : Entity
{
    public Model()
    {
    }

    public Model(XElement element) : base(element)
    {
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
        ModelNumber = element.Child("PartNumber").SafeValue();
    }

    [XmlElement(Order = 4)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 5)]
    public string? FullName { get; set; }

    [XmlElement(Order = 6)]
    public string? Description { get; set; }

    /// <summary>
    /// The manufacturer's designation for the model, carried as CCOM's
    /// <c>PartNumber</c>. CCOM has no <c>ModelNumber</c> element: the number a
    /// nameplate calls the model is the number the manufacturer orders it by.
    /// </summary>
    [XmlElement("PartNumber", Order = 7)]
    public string? ModelNumber { get; set; }

    public override string ToString() => $"{ShortName} ({ModelNumber})";
}

/// <summary>
/// Association of an asset to a functional location at a point in time. Install and
/// remove events are the payload of scenarios 4, 5 and 33.
/// </summary>
public class AssetSegmentEvent : Entity
{
    public AssetSegmentEvent()
    {
    }

    public AssetSegmentEvent(XElement element) : base(element)
    {
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        Description = element.Child(nameof(Description)).SafeValue();
        Asset = GetChild(nameof(Asset), e => new Asset(e));
        Segment = GetChild(nameof(Segment), e => new Segment(e));
        EventDateTime = element.Child("Start").SafeInstant();
        Type = GetChild(nameof(Type), e => new EventType(e));
    }

    [XmlElement(Order = 4)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 5)]
    public string? Description { get; set; }

    [XmlElement(Order = 6)]
    public EventType? Type { get; set; }

    // Start and End belong to TimestampedEvent, so they precede AssetSegmentEvent's
    // own Asset and Segment in the schema sequence.
    [XmlIgnore]
    public Instant? EventDateTime { get; set; }

    /// <summary>
    /// The instant the installation or removal occurred, carried as CCOM's
    /// <c>Start</c>. There is no <c>EventDateTime</c> element in CCOM: a
    /// TimestampedEvent expresses a point in time as a Start with no End, and the
    /// same pair expresses a duration when both are present. Naming the property
    /// for its meaning while serialising it under the schema's name keeps the
    /// distinction from leaking into every caller.
    /// </summary>
    [XmlElement("Start", Order = 7)]
    public string? EventDateTimeText
    {
        get => EventDateTime is { } value ? InstantPattern.ExtendedIso.Format(value) : null;
        set => EventDateTime = XNodeExtensions.ParseInstant(value);
    }

    public bool ShouldSerializeEventDateTimeText() => EventDateTime.HasValue;

    [XmlElement(Order = 8)]
    public Asset? Asset { get; set; }

    [XmlElement(Order = 9)]
    public Segment? Segment { get; set; }
}

/// <summary>Reference-data type of an event — Install, Remove, and so on.</summary>
public class EventType : CcomBase
{
    public EventType()
    {
    }

    public EventType(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
        InfoSource = GetChild(nameof(InfoSource), e => new InfoSource(e));
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
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

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}
