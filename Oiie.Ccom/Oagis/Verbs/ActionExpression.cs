using System.Xml.Linq;
using System.Xml.Serialization;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Oagis.Verbs;

/// <summary>
/// XPath into the document's own DataArea, telling the receiver which nodes the
/// verb applies to. Generated automatically by <see cref="SyncBodBase{TNoun}"/>.
/// </summary>
public class ActionExpression
{
    public ActionExpression()
    {
    }

    public ActionExpression(XElement element)
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
