using System.Xml.Serialization;

namespace Oiie.Ccom.Oagis.Verbs;

public class ResponseCriteria
{
    [XmlElement(Order = 0)]
    public ResponseExpression? ResponseExpression { get; set; }

    [XmlElement(Order = 1)]
    public ChangeStatus? ChangeStatus { get; set; }
}

public class ChangeStatus
{
    [XmlElement(Order = 0)]
    public string? Code { get; set; }

    [XmlElement(Order = 1)]
    public string? Description { get; set; }

    [XmlElement(Order = 2)]
    public string? ReasonCode { get; set; }

    [XmlElement("Reason", Order = 3)]
    public List<string> Reason { get; set; } = [];
}
