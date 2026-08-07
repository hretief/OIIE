using System.Collections.Concurrent;
using System.Reflection;
using System.Xml.Linq;
using System.Xml.Serialization;
using NodaTime;
using Oiie.Ccom.Oagis.Verbs;
using Oiie.Ccom.Types;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Oagis;

/// <summary>
/// Base for <b>writing</b> BODs. For reading, use <see cref="BodEnvelope"/>.
///
/// The asymmetry is deliberate. Writing benefits from typed objects and
/// XmlSerializer; reading does not, because deserialising untrusted XML into
/// typed objects is a security-scan finding and hand-parsing tolerates the
/// namespace defects present in published packages.
///
/// One generic type covers every verb/noun combination. Element names — root,
/// verb, and the plural entity wrapper — are supplied through XmlAttributeOverrides
/// at serialisation time rather than by attributing forty near-identical classes.
/// </summary>
public abstract class CcomBod<TVerb, TNoun>
    where TVerb : Verb
    where TNoun : INoun
{
    /// <summary>
    /// Keyed on the concrete BOD type, not on the closed generic.
    ///
    /// A static field on the open generic would be shared by every concrete class
    /// closing over the same verb and noun, while CreateSerializer reads
    /// GetType().Name for the root element — so the first class to initialise
    /// would silently define the root element name for all of them.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, XmlSerializer> Serializers = new();

    /// <summary>
    /// OAGIS release the BOD is based on. CCOM BODs carry 1.0; the ws-CIR BOD set
    /// requires 1.2.1, so ws-CIR documents override DefaultReleaseId.
    /// </summary>
    [XmlAttribute("releaseID")]
    public string ReleaseId { get; set; }

    /// <summary>Absent on CCOM BODs unless explicitly set.</summary>
    [XmlAttribute("versionID")]
    public string? VersionId { get; set; }

    protected CcomBod() => ReleaseId = DefaultReleaseId;

    protected virtual string DefaultReleaseId => "1.0";

    public bool ShouldSerializeVersionId() => !string.IsNullOrWhiteSpace(VersionId);

    [XmlElement(Namespace = Namespaces.Oagis)]
    public ApplicationArea ApplicationArea { get; set; } = new()
    {
        BODID = Guid.NewGuid().ToString(),
        CreationDateTime = SystemClock.Instance.GetCurrentInstant()
    };

    [XmlElement]
    public DataArea<TVerb, TNoun>? DataArea { get; set; }

    /// <summary>Root element namespace. CCOM by default; ws-CIR BODs override.</summary>
    public virtual string RootNamespace => Namespaces.Ccom;

    /// <summary>Root element name. Convention: the concrete class name.</summary>
    public virtual string RootNodeName => GetType().Name;

    /// <summary>Plural wrapper inside DataArea. Convention: noun name + "s".</summary>
    public virtual string DataAreaNodeName => $"{typeof(TNoun).Name}s";

    /// <summary>Verb element name inside DataArea. Convention: the verb type name.</summary>
    public virtual string DataAreaVerbName => typeof(TVerb).Name;

    [XmlIgnore]
    public Dictionary<string, string> CustomNamespaces { get; } = [];

    protected abstract DataArea<TVerb, TNoun> CreateDataArea();

    public XDocument CreateDocument()
    {
        var serializer = Serializers.GetOrAdd(GetType(), _ => CreateSerializer());

        var document = new XDocument();
        using (var writer = document.CreateWriter())
        {
            var namespaces = new XmlSerializerNamespaces();
            // Published CCOM documents put the CCOM namespace on the default
            // prefix and switch the default inside OAGIS subtrees.
            namespaces.Add(string.Empty, Namespaces.Ccom);
            namespaces.Add("oa", Namespaces.Oagis);
            namespaces.Add("xsi", Namespaces.XmlSchemaInstance);

            foreach (var pair in CustomNamespaces)
            {
                namespaces.Add(pair.Key, pair.Value);
            }

            serializer.Serialize(writer, this, namespaces);
        }

        CleanUpDocument(document);
        return document;
    }

    public string ToXmlString() => CreateDocument().ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// Removes serialiser artefacts that CCOM rejects.
    ///
    /// XmlSerializer emits xsi:type on polymorphic ValueContent, which the CCOM
    /// schema does not permit — the concrete child element already carries the
    /// discriminator. It also emits xsi:nil for null values, which is invalid for
    /// UTCDateTime. Both are cheaper to strip afterwards than to prevent.
    /// </summary>
    protected virtual void CleanUpDocument(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var xsiType = XName.Get("type", Namespaces.XmlSchemaInstance);
        var xsiNil = XName.Get("nil", Namespaces.XmlSchemaInstance);

        foreach (var element in document.Descendants()
                     .Where(d => d.Name.LocalName == nameof(ValueContent))
                     .ToList())
        {
            element.Attributes(xsiType).Remove();
            element.Descendants().Attributes(xsiNil).Remove();
        }
    }

    private XmlSerializer CreateSerializer()
    {
        var overrides = new XmlAttributeOverrides();

        overrides.Add(GetType(), new XmlAttributes
        {
            XmlRoot = new XmlRootAttribute { ElementName = RootNodeName, Namespace = RootNamespace },
            XmlType = new XmlTypeAttribute { AnonymousType = true, Namespace = RootNamespace }
        });

        var verbAttributes = new XmlAttributes();
        verbAttributes.XmlElements.Add(new XmlElementAttribute
        {
            ElementName = DataAreaVerbName,
            Namespace = Namespaces.Oagis
        });

        var entityAttributes = new XmlAttributes();
        if (string.IsNullOrWhiteSpace(DataAreaNodeName))
        {
            entityAttributes.XmlElements.Add(new XmlElementAttribute { ElementName = typeof(TNoun).Name });
        }
        else
        {
            entityAttributes.XmlArray = new XmlArrayAttribute { ElementName = DataAreaNodeName };
            entityAttributes.XmlArrayItems.Add(new XmlArrayItemAttribute { ElementName = typeof(TNoun).Name });
        }

        overrides.Add(typeof(DataArea<TVerb, TNoun>), nameof(DataArea<TVerb, TNoun>.Verb), verbAttributes);
        overrides.Add(typeof(DataArea<TVerb, TNoun>), nameof(DataArea<TVerb, TNoun>.Entities), entityAttributes);

        // Derived types must declare the CCOM namespace explicitly or XmlSerializer
        // emits them unqualified.
        var ccomRoot = new XmlAttributes
        {
            XmlRoot = new XmlRootAttribute { Namespace = Namespaces.Ccom }
        };

        foreach (var derived in DerivedTypesOf<ValueContent>().Concat(DerivedTypesOf<Entity>()))
        {
            if (overrides[derived] is null)
            {
                overrides.Add(derived, ccomRoot);
            }
        }

        return new XmlSerializer(GetType(), overrides);
    }

    private static IEnumerable<Type> DerivedTypesOf<T>()
    {
        var baseType = typeof(T);
        return baseType.Assembly.GetTypes().Where(t => t != baseType && baseType.IsAssignableFrom(t));
    }
}
