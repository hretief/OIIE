using System.Xml.Linq;
using System.Xml.Serialization;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Oagis.Verbs;

public class ResponseExpression
{
    public ResponseExpression()
    {
    }

    public ResponseExpression(XElement element)
    {
        ActionCode = element.SafeAttributeValue("actionCode");
        ExpressionLanguage = element.SafeAttributeValue("expressionLanguage");
        Value = element.SafeValue();
    }

    [XmlAttribute("actionCode")]
    public string? ActionCode { get; set; }

    [XmlAttribute("expressionLanguage")]
    public string? ExpressionLanguage { get; set; } = "Xpath";

    [XmlText]
    public string? Value { get; set; }
}
