namespace Oiie.Ccom.Oagis;

/// <summary>
/// OAGIS DataArea. Element names for both Verb and Entities are supplied at
/// serialisation time via XmlAttributeOverrides, which is what lets one generic
/// type serve every verb/noun combination.
/// </summary>
public class DataArea<TVerb, TNoun>
{
    public TVerb? Verb { get; set; }

    public List<TNoun> Entities { get; set; } = [];
}
