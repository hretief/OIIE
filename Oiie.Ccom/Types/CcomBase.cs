using System.Collections.Concurrent;
using System.Xml.Linq;
using System.Xml.Serialization;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Types;

/// <summary>
/// Read-side base. Each concrete type parses its own element in its constructor;
/// nothing here goes through XmlSerializer.
/// </summary>
public abstract class CcomBase
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, Type>> DerivedTypeCache = new();

    private readonly XElement? _element;

    protected CcomBase()
    {
    }

    protected CcomBase(XElement element)
    {
        _element = element ?? throw new ArgumentNullException(nameof(element));
        XsiType = element.XsiType();
    }

    /// <summary>
    /// Preserved from the wire so polymorphic nouns can be resolved.
    ///
    /// XmlIgnore is required, not cosmetic: XmlSerializer generates reader IL
    /// alongside writer IL even for serialise-only use, and a property with a
    /// non-public setter is a hard error there. Read-only properties are skipped
    /// silently, which is why the other read-side members are tolerated.
    /// </summary>
    [XmlIgnore]
    public string? XsiType { get; protected set; }

    /// <summary>The element this instance was parsed from, if any.</summary>
    [XmlIgnore]
    public XElement? SourceElement => _element;

    protected T? GetChild<T>(string name, Func<XElement, T> factory) where T : CcomBase =>
        _element.Child(name) is { } child ? factory(child) : null;

    protected List<T> GetChildren<T>(string name, Func<XElement, T> factory) where T : CcomBase =>
        _element.Children(name).Select(factory).ToList();

    /// <summary>
    /// Resolves a concrete subtype from the element's xsi:type. Used where CCOM
    /// models a hierarchy on the wire — Event and ActualEvent in particular.
    /// </summary>
    public static T? GetDerivedInstance<T>(XElement element) where T : CcomBase
    {
        ArgumentNullException.ThrowIfNull(element);

        var xsiType = element.XsiType();
        if (string.IsNullOrWhiteSpace(xsiType))
        {
            return null;
        }

        var map = DerivedTypeCache.GetOrAdd(typeof(T), baseType =>
            baseType.Assembly.GetTypes()
                .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract)
                .ToDictionary(t => t.Name, StringComparer.Ordinal));

        return map.TryGetValue(xsiType, out var type)
            ? Activator.CreateInstance(type, element) as T
            : null;
    }
}
