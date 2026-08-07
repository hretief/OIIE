using System.Xml.Linq;
using System.Xml.Serialization;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Types;

/// <summary>
/// The system that registered an entity. Maps onto the ws-CIR Entry.SourceID —
/// the pairing of InfoSource with IDInInfoSource is the CCOM equivalent of the
/// registry's SourceID plus IDInSource.
/// </summary>
public class InfoSource : CcomBase
{
    public InfoSource()
    {
    }

    public InfoSource(XElement element) : base(element)
    {
        UUID = element.Child(nameof(UUID)).SafeGuid();
        ShortName = element.Child(nameof(ShortName)).SafeValue();
        FullName = element.Child(nameof(FullName)).SafeValue();
        URL = element.Child(nameof(URL)).SafeValue();
    }

    [XmlElement(Order = 0)]
    public Guid UUID { get; set; }

    [XmlElement(Order = 1)]
    public string? ShortName { get; set; }

    [XmlElement(Order = 2)]
    public string? FullName { get; set; }

    [XmlElement(Order = 3)]
    public string? URL { get; set; }

    public bool ShouldSerializeUUID() => UUID != Guid.Empty;
}
