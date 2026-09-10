using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;
using Oiie.Ccom.Types;

namespace Oiie.Ccom.Extensions;

/// <summary>
/// Builds the GetTaxonomySet request BOD — the RDL class-list request named by
/// OIIE Scenarios 34 and 35.
///
/// Hand-built with XElement rather than through
/// <see cref="Oagis.CcomBod{TVerb, TNoun}"/>, for the same reason as
/// <see cref="Cir.CirBods"/>: the DataArea carries a verb followed by a
/// selector, not a verb followed by a plural noun wrapper.
///
/// The root element sits in the Sandbox extension namespace, NOT in CCOM.
/// MIMOSA names this BOD in the scenarios but has published no schema for it,
/// so this is a reconstruction. Publishing it into the CCOM namespace would
/// make it indistinguishable on the wire from a future official BOD of the same
/// name. See schemas/sandbox/GetTaxonomySet.xsd and
/// docs/taxonomyset-bod-findings.md.
///
/// The root element sits in the Sandbox extension namespace, NOT in CCOM.
/// MIMOSA names these BODs in the scenarios but has published no schema for
/// them, so they are reconstructions. Publishing them into the CCOM namespace
/// would make them indistinguishable on the wire from future official BODs of
/// the same name. See schemas/sandbox/ and docs/taxonomyset-bod-findings.md.
///
/// The classes travel inside the response as a Taxonomy connection network
/// (TaxonomySet -> Taxonomy -> Connection -> From/To), which is CCOM's own
/// model for "the parent-child relationship between two base types within a
/// taxonomy". That preserves the RDL hierarchy; a flat list would not.
/// </summary>
public static class TaxonomySetBods
{
    private static readonly XNamespace Ext = Namespaces.SandboxExtensions;
    private static readonly XNamespace Oagis = Namespaces.Oagis;
    private static readonly XNamespace Ccom = Namespaces.Ccom;
    private static readonly XNamespace Xsi = Namespaces.XmlSchemaInstance;

    /// <summary>
    /// Source id for RDL-governed reference data, so a class code resolves to
    /// the same UUID everywhere it appears.
    /// </summary>
    public const string RdlSourceId = "MIMOSA-RDL";

    /// <summary>
    /// GetTaxonomySet: ask a reference-data provider which taxonomy sets it holds.
    ///
    /// An empty selector list means "everything you have", which is what a
    /// participant validating its mapping table wants on first contact. Supply a
    /// selector to name one set, or to request only what changed since a known
    /// instant.
    /// </summary>
    public static XDocument GetTaxonomySet(
        IReadOnlyList<TaxonomySetSelector> selectors,
        string senderLogicalId,
        string correlationId,
        Instant? creationDateTime = null)
    {
        ArgumentNullException.ThrowIfNull(selectors);

        // oa:Get extends RequestVerbType, which requires at least one
        // oa:Expression -- an empty verb element is invalid. The expression is
        // the XPath telling the provider where the selectors live, the same
        // idiom SyncBodBase uses for ActionExpression.
        var dataArea = new XElement(Ext + "DataArea",
            new XElement(Oagis + "Get",
                new XElement(Oagis + "Expression",
                    new XAttribute("expressionLanguage", "Xpath"),
                    "/GetTaxonomySet/DataArea/TaxonomySet")));

        foreach (var selector in selectors)
        {
            dataArea.Add(selector.ToElement(Ext));
        }

        var root = new XElement(Ext + "GetTaxonomySet",
            new XAttribute(XNamespace.Xmlns + "ext", Namespaces.SandboxExtensions),
            new XAttribute(XNamespace.Xmlns + "oa", Namespaces.Oagis),
            new XAttribute("releaseID", "1.2.1"),
            ApplicationArea(senderLogicalId, correlationId, creationDateTime),
            dataArea);

        return new XDocument(root);
    }

    /// <summary>
    /// ShowTaxonomySet: the answer to a GetTaxonomySet, carrying the classes the
    /// provider holds.
    ///
    /// <paramref name="originalBodId"/> is the BODID of the request being
    /// answered, echoed in oa:Show/OriginalApplicationArea. It is the only link
    /// between request and response, so it is required rather than optional.
    ///
    /// <paramref name="originalCreationDateTime"/> is the request's own
    /// CreationDateTime. OAGIS makes it mandatory inside an ApplicationArea, so
    /// the echo cannot carry the BODID alone. A responder that has the request
    /// in hand should always pass it; the fallback exists only so the document
    /// stays valid, and a fabricated timestamp there is a poorer echo than the
    /// real one.
    /// </summary>
    public static XDocument ShowTaxonomySet(
        string setShortName,
        IReadOnlyList<RdlClass> classes,
        string senderLogicalId,
        string correlationId,
        string originalBodId,
        string? version = null,
        Instant? asOf = null,
        Instant? creationDateTime = null,
        Instant? originalCreationDateTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setShortName);
        ArgumentNullException.ThrowIfNull(classes);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalBodId);

        var snapshot = asOf ?? SystemClock.Instance.GetCurrentInstant();

        // Element order below follows the schema's sequence exactly:
        // Entity's UUID, then the Nameable group, then Taxonomy, then our
        // Version and AsOf extension. Reordering these silently breaks
        // validation, since the content model is a sequence and not an all.
        var taxonomySet = new XElement(Ext + "TaxonomySet",
            new XElement(Ccom + "UUID",
                CcomUuid.ForReferenceData(RdlSourceId, setShortName).ToString()),
            new XElement(Ccom + "ShortName", setShortName),
            Taxonomy(setShortName, classes));

        if (version is { Length: > 0 })
        {
            taxonomySet.Add(new XElement(Ext + "Version", version));
        }

        taxonomySet.Add(new XElement(Ext + "AsOf",
            InstantPattern.ExtendedIso.Format(snapshot)));

        var root = new XElement(Ext + "ShowTaxonomySet",
            new XAttribute(XNamespace.Xmlns + "ext", Namespaces.SandboxExtensions),
            new XAttribute(XNamespace.Xmlns + "ccom", Namespaces.Ccom),
            new XAttribute(XNamespace.Xmlns + "oa", Namespaces.Oagis),
            new XAttribute(XNamespace.Xmlns + "xsi", Namespaces.XmlSchemaInstance),
            new XAttribute("releaseID", "1.2.1"),
            ApplicationArea(senderLogicalId, correlationId, creationDateTime),
            new XElement(Ext + "DataArea",
                new XElement(Oagis + "Show",
                    new XElement(Oagis + "OriginalApplicationArea",
                        // CreationDateTime precedes BODID and is mandatory:
                        // ApplicationAreaType is a sequence, so both the
                        // presence and the order are enforced.
                        new XElement(Oagis + "CreationDateTime",
                            InstantPattern.ExtendedIso.Format(
                                originalCreationDateTime ?? snapshot)),
                        new XElement(Oagis + "BODID", originalBodId))),
                new XElement(Ext + "TaxonomySets", taxonomySet)));

        return new XDocument(root);
    }

    /// <summary>
    /// The taxonomy holding one Connection per class.
    ///
    /// A class with a parent becomes From=parent, To=child. A root class
    /// becomes From=itself with To omitted, which is precisely what CCOM's
    /// annotation on TypeConnection prescribes for a childless root. Without
    /// that rule a root class would have no way to appear at all.
    /// </summary>
    private static XElement Taxonomy(string setShortName, IReadOnlyList<RdlClass> classes)
    {
        var byCode = classes
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .ToDictionary(c => c.Code, StringComparer.Ordinal);

        var taxonomy = new XElement(Ccom + "Taxonomy",
            new XElement(Ccom + "UUID",
                CcomUuid.ForReferenceData(RdlSourceId, $"{setShortName}#taxonomy").ToString()),
            new XElement(Ccom + "ShortName", setShortName));

        foreach (var rdlClass in classes)
        {
            var connection = new XElement(Ccom + "Connection",
                new XElement(Ccom + "UUID",
                    CcomUuid.ForReferenceData(RdlSourceId, $"{rdlClass.Code}#connection").ToString()));

            if (rdlClass.ParentCode is { Length: > 0 }
                && byCode.TryGetValue(rdlClass.ParentCode, out var parent))
            {
                connection.Add(BaseType("From", parent));
                connection.Add(BaseType("To", rdlClass));
            }
            else
            {
                // Root, or a parent the provider did not include in this
                // snapshot. Either way the class itself must still appear, so
                // it is emitted as a root rather than dropped.
                connection.Add(BaseType("From", rdlClass));
            }

            taxonomy.Add(connection);
        }

        return taxonomy;
    }

    /// <summary>
    /// One class, as a ccom:BaseType element.
    ///
    /// BaseType is abstract, so xsi:type naming a concrete derivation is
    /// mandatory -- without it the document does not validate. Note that
    /// CcomBod.CleanUpDocument strips xsi:type as a serialiser artefact; here
    /// it is load-bearing, which is one reason these BODs are hand-built.
    ///
    /// The UUID is the one the library holds. Only when the class arrives
    /// without one is a value derived from the code, and that is a fallback
    /// rather than the design: a derived identity is reproducible but not
    /// authoritative, and it silently changes meaning if the derivation ever
    /// does. Preferring the stored value makes the response a report of what
    /// the library knows instead of a claim this serialiser invents.
    /// </summary>
    private static XElement BaseType(string elementName, RdlClass rdlClass)
    {
        var uuid = rdlClass.Uuid ?? CcomUuid.ForReferenceData(RdlSourceId, rdlClass.Code);

        var element = new XElement(Ccom + elementName,
            new XAttribute(Xsi + "type", $"ccom:{rdlClass.ConcreteType}"),
            new XElement(Ccom + "UUID", uuid.ToString()),
            new XElement(Ccom + "IDInInfoSource", rdlClass.Code),
            new XElement(Ccom + "ShortName", rdlClass.Code));

        if (rdlClass.Name is { Length: > 0 })
        {
            element.Add(new XElement(Ccom + "FullName", rdlClass.Name));
        }

        if (rdlClass.Description is { Length: > 0 })
        {
            element.Add(new XElement(Ccom + "Description", rdlClass.Description));
        }

        return element;
    }

    /// <summary>
    /// Flattens a ShowTaxonomySet back into the class list a consumer can check
    /// its mapping table against.
    ///
    /// Walks the TypeConnection network: a Connection with both From and To is
    /// a parent-child edge, and one with only From is a root. A class named as
    /// a parent but never carried in its own connection is still returned, as a
    /// root -- dropping it would hide a class the provider clearly holds.
    ///
    /// Returns an empty list for a document that is not a ShowTaxonomySet,
    /// rather than throwing: the caller is validating, and "no classes" is the
    /// answer that fails closed.
    /// </summary>
    public static IReadOnlyList<RdlClass> ParseShowTaxonomySet(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.Root;
        if (root is null || root.Name != Ext + "ShowTaxonomySet")
        {
            return [];
        }

        var connections = root
            .Elements(Ext + "DataArea")
            .Elements(Ext + "TaxonomySets")
            .Elements(Ext + "TaxonomySet")
            .Elements(Ccom + "Taxonomy")
            .Elements(Ccom + "Connection");

        // Insertion-ordered so the reconstructed list keeps the order the
        // provider sent, which is what makes a test assertion readable.
        var result = new Dictionary<string, RdlClass>(StringComparer.Ordinal);

        foreach (var connection in connections)
        {
            var from = ReadBaseType(connection.Element(Ccom + "From"));
            var to = ReadBaseType(connection.Element(Ccom + "To"));

            if (from is not null && !result.ContainsKey(from.Code))
            {
                result.Add(from.Code, from);
            }

            if (to is null) continue;

            // The edge lives on the child, so the parent code is applied here
            // rather than when From was read.
            result[to.Code] = to with { ParentCode = from?.Code };
        }

        return [.. result.Values];
    }

    private static RdlClass? ReadBaseType(XElement? element)
    {
        if (element is null) return null;

        var code = (string?)element.Element(Ccom + "ShortName")
            ?? (string?)element.Element(Ccom + "IDInInfoSource");

        if (string.IsNullOrWhiteSpace(code)) return null;

        // xsi:type arrives prefixed (ccom:SegmentType). Only the local part is
        // meaningful to a consumer, and the prefix is not guaranteed to be
        // "ccom" on a document written by someone else.
        var xsiType = (string?)element.Attribute(Xsi + "type");
        var concreteType = xsiType is { Length: > 0 }
            ? xsiType[(xsiType.IndexOf(':') + 1)..]
            : "SegmentType";

        return new RdlClass
        {
            Code = code,
            Name = (string?)element.Element(Ccom + "FullName"),
            Description = (string?)element.Element(Ccom + "Description"),
            // Carried through so a consumer sees the identity the provider
            // actually published rather than re-deriving one locally and
            // reaching a different answer. Parsed leniently: a malformed UUID
            // is not worth discarding an otherwise usable class over.
            Uuid = Guid.TryParse((string?)element.Element(Ccom + "UUID"), out var uuid)
                ? uuid
                : null,
            ConcreteType = concreteType
        };
    }

    /// <summary>
    /// Reads a GetTaxonomySet, returning the selectors it carries and the BODID
    /// to correlate the answer with.
    ///
    /// An empty selector list means "everything you hold" — that is the
    /// documented meaning of a request with no selector, not an error.
    ///
    /// Returns null for a document that is not a GetTaxonomySet, so a responder
    /// can distinguish "not mine" from "asks for nothing" and leave someone
    /// else's message alone.
    /// </summary>
    public static TaxonomySetRequest? ParseGetTaxonomySet(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.Root;
        if (root is null || root.Name != Ext + "GetTaxonomySet")
        {
            return null;
        }

        var dataArea = root.Element(Ext + "DataArea");

        var selectors = dataArea
            ?.Elements(Ext + "TaxonomySet")
            .Select(e => new TaxonomySetSelector
            {
                Uuid = (string?)e.Element(Ext + "UUID"),
                ShortName = (string?)e.Element(Ext + "ShortName"),
                FullName = (string?)e.Element(Ext + "FullName"),
                Version = (string?)e.Element(Ext + "Version")
            })
            .ToList() ?? [];

        var applicationArea = root.Element(Oagis + "ApplicationArea");

        return new TaxonomySetRequest
        {
            BodId = (string?)applicationArea?.Element(Oagis + "BODID") ?? string.Empty,
            CreationDateTime = ParseInstant((string?)applicationArea?.Element(Oagis + "CreationDateTime")),
            Selectors = selectors
        };
    }

    private static Instant? ParseInstant(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parsed = InstantPattern.ExtendedIso.Parse(text);
        return parsed.Success ? parsed.Value : null;
    }

    private static XElement ApplicationArea(
        string senderLogicalId, string correlationId, Instant? creationDateTime)
    {
        var timestamp = creationDateTime ?? SystemClock.Instance.GetCurrentInstant();

        return new XElement(Oagis + "ApplicationArea",
            new XElement(Oagis + "Sender",
                new XElement(Oagis + "LogicalID", senderLogicalId),
                new XElement(Oagis + "ComponentID", "SimHost")),
            new XElement(Oagis + "CreationDateTime",
                InstantPattern.ExtendedIso.Format(timestamp)),
            new XElement(Oagis + "BODID", correlationId));
    }
}

/// <summary>
/// An inbound GetTaxonomySet: who asked, when, and for what.
/// </summary>
public sealed record TaxonomySetRequest
{
    /// <summary>
    /// The requester's BODID, echoed back in the response. Empty when the
    /// request omitted it — OAGIS makes BODID optional, so a responder has to
    /// cope rather than assume.
    /// </summary>
    public required string BodId { get; init; }

    /// <summary>The request's CreationDateTime, echoed in OriginalApplicationArea.</summary>
    public Instant? CreationDateTime { get; init; }

    /// <summary>Empty means "everything you hold".</summary>
    public required IReadOnlyList<TaxonomySetSelector> Selectors { get; init; }
}

/// <summary>
/// Identification-only selector: it names WHICH taxonomy set is wanted, never
/// what the set should contain. Every part is optional, so a default instance
/// selects everything the provider holds.
/// </summary>
public sealed record TaxonomySetSelector
{
    public string? Uuid { get; init; }

    public string? ShortName { get; init; }

    public string? FullName { get; init; }

    /// <summary>Version of the reference data set, per Scenarios 34 and 35.</summary>
    public string? Version { get; init; }

    /// <summary>
    /// Incremental request: return only what changed at or after this instant.
    /// Omit it to ask for a full snapshot.
    /// </summary>
    public Instant? ChangedSince { get; init; }

    internal XElement ToElement(XNamespace ns)
    {
        var element = new XElement(ns + "TaxonomySet");

        // Order matters: the schema declares a sequence, not an all group.
        if (Uuid is { Length: > 0 })
        {
            element.Add(new XElement(ns + "UUID", Uuid));
        }

        if (ShortName is { Length: > 0 })
        {
            element.Add(new XElement(ns + "ShortName", ShortName));
        }

        if (FullName is { Length: > 0 })
        {
            element.Add(new XElement(ns + "FullName", FullName));
        }

        if (Version is { Length: > 0 })
        {
            element.Add(new XElement(ns + "Version", Version));
        }

        if (ChangedSince is { } changedSince)
        {
            element.Add(new XElement(ns + "ChangedSince",
                InstantPattern.ExtendedIso.Format(changedSince)));
        }

        return element;
    }
}

/// <summary>
/// One RDL class as the sandbox uses it: a code, a readable name, and the code
/// of the class it specialises.
///
/// This is the flattened form. On the wire the same information travels as a
/// TypeConnection network, but a consumer validating its own mapping table
/// wants a list it can look codes up in, not a graph it has to walk.
/// </summary>
public sealed record RdlClass
{
    /// <summary>The identifier callers classify against, e.g. rdl:LightingUnit.</summary>
    public required string Code { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Code of the class this one specialises. Null for a root.</summary>
    public string? ParentCode { get; init; }

    /// <summary>
    /// The class's identity as the library holds it.
    ///
    /// Set this when the value came from storage. It is what a consumer keys
    /// its local copy of the vocabulary on, so it must be the same value on
    /// every response and from every provider serving the same library.
    ///
    /// Null means the source had no identity to give, and the builder falls
    /// back to deriving one from the code. That fallback keeps a document
    /// well-formed but is strictly worse: it is only stable for as long as the
    /// derivation is, and two systems agree only by both happening to run this
    /// code. A library that stores its guids should never reach it.
    /// </summary>
    public Guid? Uuid { get; init; }

    /// <summary>
    /// The concrete ccom:BaseType derivation this class is expressed as.
    /// Defaults to SegmentType, which is what a functional-location or
    /// equipment class is in CCOM terms. It is settable because a provider
    /// classifying against, say, AssetType must be able to say so -- the
    /// abstract BaseType gives no default of its own.
    /// </summary>
    public string ConcreteType { get; init; } = "SegmentType";
}
