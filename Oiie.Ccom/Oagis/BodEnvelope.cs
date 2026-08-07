using System.Xml;
using System.Xml.Linq;
using NodaTime;
using Oiie.Ccom.Types;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Oagis;

/// <summary>
/// Read-side counterpart to <see cref="CcomBod{TVerb, TNoun}"/>.
///
/// Parses any BOD without knowing its type in advance, which is what an inbox
/// needs: a participant receives whatever arrives on its channels, and only then
/// decides whether it has a handler.
///
/// Nouns are located by following the verb's ActionExpression or ResponseExpression
/// XPath into the document, rather than by assuming a DataArea shape. That is how
/// OAGIS intends a receiver to find the affected nodes, and it means a BOD whose
/// noun wrapper is named unexpectedly still parses.
/// </summary>
public sealed class BodEnvelope
{
    private BodEnvelope()
    {
    }

    public string RootName { get; private set; } = string.Empty;

    public string Verb { get; private set; } = string.Empty;

    public string Noun { get; private set; } = string.Empty;

    public ApplicationArea? ApplicationArea { get; private set; }

    /// <summary>Correlation id, carried in BODID.</summary>
    public string? BodId => ApplicationArea?.BODID;

    public Instant? CreationDateTime => ApplicationArea?.CreationDateTime;

    public string? SenderLogicalId => ApplicationArea?.Sender?.LogicalID;

    /// <summary>Release container reference — named version, work package, ECN.</summary>
    public string? SenderReferenceId => ApplicationArea?.Sender?.ReferenceID;

    /// <summary>Add, Change, Replace, Delete — from the verb's ActionExpression.</summary>
    public string? ActionCode { get; private set; }

    /// <summary>BODID of the document being responded to, for Acknowledge and Respond.</summary>
    public string? OriginalBodId { get; private set; }

    public XDocument Document { get; private set; } = new();

    /// <summary>Noun elements as they appeared, before typing.</summary>
    public IReadOnlyList<XElement> NounElements { get; private set; } = [];

    public static BodEnvelope Parse(string xml)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new BodFormatException("Document is not well-formed XML.", ex);
        }

        return Parse(document);
    }

    public static BodEnvelope Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.Root
            ?? throw new BodFormatException("Document has no root element.");

        var envelope = new BodEnvelope
        {
            Document = document,
            RootName = root.Name.LocalName
        };

        if (root.Child("ApplicationArea") is not { } applicationArea)
        {
            throw new BodFormatException("Could not find ApplicationArea element.");
        }

        envelope.ApplicationArea = new ApplicationArea(applicationArea);

        if (root.Child("DataArea") is not { } dataArea)
        {
            throw new BodFormatException("Could not find DataArea element.");
        }

        // The verb is the first element child of the DataArea. Taking the first
        // XElement rather than the first node matters — real documents carry
        // whitespace text nodes there.
        var verbElement = dataArea.Elements().FirstOrDefault()
            ?? throw new BodFormatException("Could not find a verb element in the DataArea.");

        envelope.Verb = verbElement.Name.LocalName;
        envelope.Noun = envelope.RootName.StartsWith(envelope.Verb, StringComparison.Ordinal)
            ? envelope.RootName[envelope.Verb.Length..]
            : envelope.RootName;

        envelope.OriginalBodId = dataArea.Child("Acknowledge/OriginalApplicationArea/BODID").SafeValue()
            ?? dataArea.Child("OriginalApplicationArea/BODID").SafeValue();

        var expression = verbElement.Child("ActionCriteria/ActionExpression")
            ?? verbElement.Child("ResponseCriteria/ResponseExpression");

        envelope.ActionCode = expression.SafeAttributeValue("actionCode");

        envelope.NounElements = ResolveNounElements(document, dataArea, expression);

        return envelope;
    }

    /// <summary>
    /// Follows the expression XPath where present, and falls back to walking the
    /// DataArea for a plural wrapper. Published BODs are not consistent about
    /// supplying an expression, so both routes are needed.
    /// </summary>
    private static List<XElement> ResolveNounElements(
        XDocument document, XElement dataArea, XElement? expression)
    {
        var xpath = expression.SafeValue()?.Trim();

        if (!string.IsNullOrWhiteSpace(xpath))
        {
            var located = ResolveLocalNamePath(document, xpath);
            if (located is not null)
            {
                return located.Elements().ToList();
            }
        }

        // Fall back to the first element after the verb — the plural wrapper.
        var wrapper = dataArea.Elements().Skip(1).FirstOrDefault();
        return wrapper?.Elements().ToList() ?? [];
    }

    /// <summary>
    /// Resolves a simple slash path by local name only.
    ///
    /// XPathSelectElement is not used deliberately: BOD expressions are written
    /// without namespace prefixes while the documents themselves are namespaced,
    /// so a real XPath evaluation matches nothing.
    /// </summary>
    private static XElement? ResolveLocalNamePath(XDocument document, string xpath)
    {
        var segments = xpath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var root = document.Root;
        if (root is null || !string.Equals(root.Name.LocalName, segments[0], StringComparison.Ordinal))
        {
            return null;
        }

        var current = root;
        foreach (var segment in segments.Skip(1))
        {
            current = current.Elements()
                .FirstOrDefault(e => string.Equals(e.Name.LocalName, segment, StringComparison.Ordinal));

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>
    /// Types the noun elements. Unrecognised nouns yield nothing rather than
    /// throwing — a participant is expected to receive BODs it has no handler for,
    /// and the message is still archived and visible in the wire view.
    /// </summary>
    public List<T> NounsAs<T>(Func<XElement, T> factory) where T : Entity =>
        NounElements.Select(factory).ToList();

    public bool Is(string verb, string noun) =>
        string.Equals(Verb, verb, StringComparison.Ordinal) &&
        string.Equals(Noun, noun, StringComparison.Ordinal);
}

public sealed class BodFormatException : Exception
{
    public BodFormatException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
