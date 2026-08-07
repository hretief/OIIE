using System.Xml.Linq;
using System.Xml.Serialization;
using NodaTime;
using NodaTime.Text;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Oagis;

public class Sender
{
    public Sender()
    {
    }

    public Sender(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        LogicalID = element.Child(nameof(LogicalID)).SafeValue();
        ComponentID = element.Child(nameof(ComponentID)).SafeValue();
        TaskID = element.Child(nameof(TaskID)).SafeValue();

        /// Carries the release container reference — named version, work package,
        /// ECN — so a receiver can answer "which release produced this".
        ReferenceID = element.Child(nameof(ReferenceID)).SafeValue();

        AuthorizationID = element.Child(nameof(AuthorizationID)).SafeValue();
        ConfirmationCode = element.Child(nameof(ConfirmationCode)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public string? LogicalID { get; set; }

    [XmlElement(Order = 1)]
    public string? ComponentID { get; set; }

    [XmlElement(Order = 2)]
    public string? TaskID { get; set; }

    [XmlElement(Order = 3)]
    public string? ReferenceID { get; set; }

    [XmlElement(Order = 4)]
    public string? AuthorizationID { get; set; }

    [XmlElement(Order = 5)]
    public string? ConfirmationCode { get; set; }

    public bool ShouldSerializeTaskID() => !string.IsNullOrWhiteSpace(TaskID);
    public bool ShouldSerializeReferenceID() => !string.IsNullOrWhiteSpace(ReferenceID);
    public bool ShouldSerializeAuthorizationID() => !string.IsNullOrWhiteSpace(AuthorizationID);
    public bool ShouldSerializeConfirmationCode() => !string.IsNullOrWhiteSpace(ConfirmationCode);
}

public class ApplicationArea
{
    public ApplicationArea()
    {
    }

    public ApplicationArea(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        Sender = new Sender(element.Child(nameof(Sender)));
        CreationDateTime = element.Child(nameof(CreationDateTime)).SafeInstant();
        BODID = element.Child(nameof(BODID)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Sender? Sender { get; set; } = new();

    [XmlIgnore]
    public Instant? CreationDateTime { get; set; }

    /// <summary>
    /// Serialisation surface for <see cref="CreationDateTime"/>. XmlSerializer has
    /// no NodaTime support, so the round trip goes through ISO-8601 text.
    /// </summary>
    [XmlElement("CreationDateTime", Order = 1)]
    public string? CreationDateTimeText
    {
        get => CreationDateTime is { } value ? InstantPattern.ExtendedIso.Format(value) : null;
        set => CreationDateTime = value is null ? null : XNodeExtensions.ParseInstant(value);
    }

    /// <summary>
    /// Carries the Sandbox correlation id, so one Application Insights query can
    /// reconstruct a multi-hop exchange across every participant and both providers.
    /// </summary>
    [XmlElement(Order = 2)]
    public string? BODID { get; set; }
}

public class OriginalApplicationArea
{
    public OriginalApplicationArea()
    {
    }

    public OriginalApplicationArea(XElement? element)
    {
        if (element is null)
        {
            return;
        }

        Sender = new Sender(element.Child(nameof(Sender)));
        BODID = element.Child(nameof(BODID)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Sender? Sender { get; set; }

    [XmlElement(Order = 1)]
    public string? BODID { get; set; }
}
