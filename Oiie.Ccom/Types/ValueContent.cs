using System.Globalization;
using System.Xml.Linq;
using System.Xml.Serialization;
using NodaTime;
using NodaTime.Text;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Types;

/// <summary>
/// CCOM's typed value carrier. The concrete child element is the discriminator on
/// the wire — there is no xsi:type — so reading dispatches on child element name
/// and writing strips the xsi:type XmlSerializer would otherwise emit
/// (see CcomBod.CleanUpDocument).
///
/// This hierarchy is the wire form of the Sandbox property model: NumberContent
/// and MeasureContent map to EntityProperty.NumericValue, TextContent to
/// CharacterValue, and so on.
/// </summary>
[XmlInclude(typeof(TextContent))]
[XmlInclude(typeof(NumberContent))]
[XmlInclude(typeof(MeasureContent))]
[XmlInclude(typeof(BooleanContent))]
[XmlInclude(typeof(UUIDContent))]
[XmlInclude(typeof(UTCDateTimeContent))]
[XmlInclude(typeof(EnumerationItemContent))]
[XmlInclude(typeof(UriContent))]
[XmlInclude(typeof(PercentageContent))]
[XmlInclude(typeof(ProbabilityContent))]
[XmlInclude(typeof(CoordinateContent))]
public abstract class ValueContent
{
    private static readonly Dictionary<string, Func<XElement, ValueContent>> Factories =
        new(StringComparer.Ordinal)
        {
            ["Text"] = e => new TextContent(e),
            ["Number"] = e => new NumberContent(e),
            ["Measure"] = e => new MeasureContent(e),
            ["Boolean"] = e => new BooleanContent(e),
            ["UUID"] = e => new UUIDContent(e),
            ["UTCDateTime"] = e => new UTCDateTimeContent(e),
            ["EnumerationItem"] = e => new EnumerationItemContent(e),
            ["URI"] = e => new UriContent(e),
            ["Percentage"] = e => new PercentageContent(e),
            ["Probability"] = e => new ProbabilityContent(e),
            ["Coordinate"] = e => new CoordinateContent(e)
        };

    /// <summary>
    /// Dispatches on the first recognised child element. Returns null for an
    /// unrecognised shape rather than throwing — an unmapped value is retained
    /// and flagged, never discarded.
    /// </summary>
    public static ValueContent? Parse(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        foreach (var (name, factory) in Factories)
        {
            if (element.Child(name) is { } child)
            {
                return factory(child);
            }
        }

        return null;
    }

    public abstract string? AsDisplayText();
}

public class TextContent : ValueContent
{
    public TextContent()
    {
    }

    public TextContent(XElement element) => Text = element.SafeValue();

    [XmlElement(Order = 0)]
    public string? Text { get; set; }

    public override string? AsDisplayText() => Text;
}

public class NumberContent : ValueContent
{
    public NumberContent()
    {
    }

    public NumberContent(XElement element) => Number = element.SafeDecimal();

    [XmlIgnore]
    public decimal? Number { get; set; }

    [XmlElement("Number", Order = 0)]
    public string? NumberText
    {
        get => Number?.ToString(CultureInfo.InvariantCulture);
        set => Number = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    public override string? AsDisplayText() => NumberText;
}

/// <summary>
/// Unit of measure. A named reference-data item in CCOM rather than a bare code,
/// so a receiver can resolve it the same way it resolves any other definition.
/// </summary>
public class UnitOfMeasure : CcomBase
{
    public UnitOfMeasure()
    {
    }

    public UnitOfMeasure(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        ShortName = element.Child(nameof(ShortName)).SafeValue() ?? element.SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? IDInInfoSource { get; set; }

    [XmlElement(Order = 2)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 3)]
    public string? FullName { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>Numeric quantity with its unit.</summary>
public class Measure : CcomBase
{
    public Measure()
    {
    }

    public Measure(XElement element) : base(element)
    {
        Value = element.Child(nameof(Value)).SafeDecimal() ?? element.SafeDecimal();
        UnitOfMeasure = GetChild(nameof(UnitOfMeasure), e => new UnitOfMeasure(e));
    }

    [XmlIgnore]
    public decimal? Value { get; set; }

    [XmlElement("Value", Order = 0)]
    public string? ValueText
    {
        get => Value?.ToString(CultureInfo.InvariantCulture);
        set => Value = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    [XmlElement(Order = 1)]
    public UnitOfMeasure? UnitOfMeasure { get; set; }
}

/// <summary>
/// The common case for engineering properties.
///
/// Note the shape: every ValueContent subtype must expose exactly one property whose
/// element name matches its discriminator key, because the concrete child element is
/// the only discriminator left once xsi:type has been stripped.
/// </summary>
public class MeasureContent : ValueContent
{
    public MeasureContent()
    {
    }

    public MeasureContent(XElement element) => Measure = new Measure(element);

    [XmlElement(Order = 0)]
    public Measure? Measure { get; set; }

    [XmlIgnore]
    public decimal? Value
    {
        get => Measure?.Value;
        set => (Measure ??= new Measure()).Value = value;
    }

    [XmlIgnore]
    public string? UnitOfMeasure
    {
        get => Measure?.UnitOfMeasure?.ShortName;
        set => ((Measure ??= new Measure()).UnitOfMeasure ??= new UnitOfMeasure()).ShortName = value;
    }

    public override string? AsDisplayText() => UnitOfMeasure is null
        ? Measure?.ValueText
        : $"{Measure?.ValueText} {UnitOfMeasure}";
}

public class BooleanContent : ValueContent
{
    public BooleanContent()
    {
    }

    public BooleanContent(XElement element) => Boolean = element.SafeBoolean();

    [XmlIgnore]
    public bool? Boolean { get; set; }

    [XmlElement("Boolean", Order = 0)]
    public string? BooleanText
    {
        get => Boolean?.ToString().ToLowerInvariant();
        set => Boolean = bool.TryParse(value, out var v) ? v : null;
    }

    public override string? AsDisplayText() => BooleanText;
}

public class UUIDContent : ValueContent
{
    public UUIDContent()
    {
    }

    public UUIDContent(XElement element) => UUID = element.SafeGuid();

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    public override string? AsDisplayText() => UUID == Guid.Empty ? null : UUID.ToString();
}

public class UTCDateTimeContent : ValueContent
{
    public UTCDateTimeContent()
    {
    }

    public UTCDateTimeContent(XElement element) => UTCDateTime = element.SafeInstant();

    [XmlIgnore]
    public Instant? UTCDateTime { get; set; }

    [XmlElement("UTCDateTime", Order = 0)]
    public string? UTCDateTimeText
    {
        get => UTCDateTime is { } value ? InstantPattern.ExtendedIso.Format(value) : null;
        set => UTCDateTime = XNodeExtensions.ParseInstant(value);
    }

    public override string? AsDisplayText() => UTCDateTimeText;
}

/// <summary>A member of a controlled list.</summary>
public class EnumerationItem : CcomBase
{
    public EnumerationItem()
    {
    }

    public EnumerationItem(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        IDInInfoSource = element.Child(nameof(IDInInfoSource)).SafeValue();
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? IDInInfoSource { get; set; }

    [XmlElement(Order = 2)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 3)]
    public string? FullName { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}

/// <summary>
/// A value drawn from a controlled list — the wire form of a code-list constrained
/// property in the Sandbox classification model.
/// </summary>
public class EnumerationItemContent : ValueContent
{
    public EnumerationItemContent()
    {
    }

    public EnumerationItemContent(XElement element) => EnumerationItem = new EnumerationItem(element);

    [XmlElement(Order = 0)]
    public EnumerationItem? EnumerationItem { get; set; }

    [XmlIgnore]
    public string? ShortName
    {
        get => EnumerationItem?.ShortName;
        set => (EnumerationItem ??= new EnumerationItem()).ShortName = value;
    }

    [XmlIgnore]
    public string? FullName
    {
        get => EnumerationItem?.FullName;
        set => (EnumerationItem ??= new EnumerationItem()).FullName = value;
    }

    public override string? AsDisplayText() => FullName ?? ShortName;
}

public class UriContent : ValueContent
{
    public UriContent()
    {
    }

    public UriContent(XElement element)
    {
        URI = new UriValue
        {
            Value = element.SafeValue(),
            ResourceName = element.SafeAttributeValue("resourceName")
        };
    }

    [XmlElement("URI", Order = 0)]
    public UriValue? URI { get; set; }

    public override string? AsDisplayText() => URI?.Value;
}

public class UriValue
{
    [XmlAttribute("resourceName")]
    public string? ResourceName { get; set; }

    [XmlText]
    public string? Value { get; set; }
}

/// <summary>Percentage. A bare decimal, distinct from Number by intent.</summary>
public class PercentageContent : ValueContent
{
    public PercentageContent()
    {
    }

    public PercentageContent(XElement element) => Percentage = element.SafeDecimal();

    [XmlIgnore]
    public decimal? Percentage { get; set; }

    [XmlElement("Percentage", Order = 0)]
    public string? PercentageText
    {
        get => Percentage?.ToString(CultureInfo.InvariantCulture);
        set => Percentage = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    public override string? AsDisplayText() => PercentageText;
}

public class ProbabilityContent : ValueContent
{
    public ProbabilityContent()
    {
    }

    public ProbabilityContent(XElement element) => Probability = element.SafeDecimal();

    [XmlIgnore]
    public decimal? Probability { get; set; }

    [XmlElement("Probability", Order = 0)]
    public string? ProbabilityText
    {
        get => Probability?.ToString(CultureInfo.InvariantCulture);
        set => Probability = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    public override string? AsDisplayText() => ProbabilityText;
}

public class Coordinate : CcomBase
{
    public Coordinate()
    {
    }

    public Coordinate(XElement element) : base(element)
    {
        X = element.Child(nameof(X)).SafeDecimal();
        Y = element.Child(nameof(Y)).SafeDecimal();
        Z = element.Child(nameof(Z)).SafeDecimal();
    }

    [XmlIgnore] public decimal? X { get; set; }
    [XmlIgnore] public decimal? Y { get; set; }
    [XmlIgnore] public decimal? Z { get; set; }

    [XmlElement("X", Order = 0)]
    public string? XText
    {
        get => X?.ToString(CultureInfo.InvariantCulture);
        set => X = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [XmlElement("Y", Order = 1)]
    public string? YText
    {
        get => Y?.ToString(CultureInfo.InvariantCulture);
        set => Y = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    [XmlElement("Z", Order = 2)]
    public string? ZText
    {
        get => Z?.ToString(CultureInfo.InvariantCulture);
        set => Z = decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

public class CoordinateContent : ValueContent
{
    public CoordinateContent()
    {
    }

    public CoordinateContent(XElement element) => Coordinate = new Coordinate(element);

    [XmlElement(Order = 0)]
    public Coordinate? Coordinate { get; set; }

    public override string? AsDisplayText() =>
        Coordinate is null ? null : $"({Coordinate.XText}, {Coordinate.YText}, {Coordinate.ZText})";
}
