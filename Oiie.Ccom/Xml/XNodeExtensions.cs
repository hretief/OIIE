using System.Globalization;
using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;

namespace Oiie.Ccom.Xml;

/// <summary>
/// Namespace-agnostic, null-tolerant navigation over parsed BODs.
///
/// Every read-side type parses its own element in its constructor using these,
/// rather than going through XmlSerializer. That is deliberate: deserialising
/// untrusted XML into typed objects is a security-scan finding (SCS0028), and
/// hand-parsing also tolerates the namespace inconsistencies that appear in
/// published packages.
/// </summary>
public static class XNodeExtensions
{
    /// <summary>
    /// Resolves a child by local name, ignoring namespace. Accepts a slash-separated
    /// path: Child("InfoSource/UUID").
    /// </summary>
    public static XElement? Child(this XNode? node, string path)
    {
        if (node is not XContainer container || string.IsNullOrEmpty(path))
        {
            return null;
        }

        XContainer? current = container;

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current?
                .Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, segment, StringComparison.Ordinal));

            if (current is null)
            {
                return null;
            }
        }

        return current as XElement;
    }

    /// <summary>
    /// All matching children of the final path segment. Returns an empty list rather
    /// than null so callers can enumerate unconditionally.
    /// </summary>
    public static IReadOnlyList<XElement> Children(this XNode? node, string path)
    {
        if (node is not XContainer container || string.IsNullOrEmpty(path))
        {
            return [];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        XContainer? current = container;

        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = current?
                .Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, segments[i], StringComparison.Ordinal));

            if (current is null)
            {
                return [];
            }
        }

        var last = segments[^1];
        return current?
            .Elements()
            .Where(e => string.Equals(e.Name.LocalName, last, StringComparison.Ordinal))
            .ToList() ?? [];
    }

    public static string? SafeValue(this XObject? node) => node switch
    {
        XElement element => element.Value,
        XAttribute attribute => attribute.Value,
        _ => null
    };

    public static string? SafeAttributeValue(this XObject? node, string name) =>
        node is XElement element
            ? element.Attributes()
                .FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.Ordinal))?
                .Value
            : null;

    public static Guid SafeGuid(this XObject? node) =>
        Guid.TryParse(node.SafeValue(), out var value) ? value : Guid.Empty;

    public static Guid? SafeNullableGuid(this XObject? node) =>
        Guid.TryParse(node.SafeValue(), out var value) ? value : null;

    public static bool? SafeBoolean(this XObject? node) =>
        bool.TryParse(node.SafeValue(), out var value) ? value : null;

    public static decimal? SafeDecimal(this XObject? node) =>
        decimal.TryParse(node.SafeValue(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static double? SafeDouble(this XObject? node) =>
        double.TryParse(node.SafeValue(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static int? SafeInteger(this XObject? node) =>
        int.TryParse(node.SafeValue(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public static Instant? SafeInstant(this XObject? node) => ParseInstant(node.SafeValue());

    public static Instant? ParseInstant(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = InstantPattern.ExtendedIso.Parse(raw);
        if (parsed.Success)
        {
            return parsed.Value;
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var fallback)
            ? Instant.FromDateTimeOffset(fallback)
            : null;
    }

    /// <summary>xsi:type on an element, with any namespace prefix stripped.</summary>
    public static string? XsiType(this XObject? node)
    {
        var raw = node is XElement element
            ? element.Attribute(XName.Get("type", Namespaces.XmlSchemaInstance))?.Value
            : null;

        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var colon = raw.IndexOf(':');
        return colon >= 0 ? raw[(colon + 1)..] : raw;
    }
}
