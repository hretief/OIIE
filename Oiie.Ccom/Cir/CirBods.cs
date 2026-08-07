using System.Xml.Linq;
using NodaTime;
using NodaTime.Text;
using Oiie.Ccom.Xml;

namespace Oiie.Ccom.Cir;

/// <summary>
/// Builds and reads the ws-CIR Annex A BODs.
///
/// These are hand-built with XElement rather than going through
/// <see cref="Oagis.CcomBod{TVerb, TNoun}"/>, because their DataArea has a
/// different shape: a verb followed by a single command element, not a verb
/// followed by a plural noun wrapper. Forcing them through the generic base would
/// mean overriding away most of what it does.
///
/// Note the root element namespace is the ws-CIR namespace, not CCOM.
/// </summary>
public static class CirBods
{
    private static readonly XNamespace Cir = Namespaces.Cir;
    private static readonly XNamespace Oagis = Namespaces.Oagis;

    /// <summary>
    /// ProcessRegistry: register entries, asking the registry to assign identities.
    ///
    /// CreateCIRID = true means "give these a shared identity if they do not have
    /// one". False would mean the caller expects them to exist already, which is a
    /// different request.
    /// </summary>
    public static XDocument ProcessRegistry(
        Registry registry,
        string senderLogicalId,
        string correlationId,
        string? senderReferenceId = null,
        bool createCirid = true,
        Instant? creationDateTime = null)
    {
        var root = new XElement(Cir + "ProcessRegistry",
            new XAttribute(XNamespace.Xmlns + "cir", Namespaces.Cir),
            new XAttribute(XNamespace.Xmlns + "oa", Namespaces.Oagis),
            new XAttribute("releaseID", "1.2.1"),
            ApplicationArea(senderLogicalId, correlationId, senderReferenceId, creationDateTime),
            new XElement(Cir + "DataArea",
                // acknowledgeCode="Always" is what asks for an AcknowledgeRegistry
                // back. Without it the Process verb is fire-and-forget: the provider
                // may apply the change perfectly well and send nothing, which is
                // indistinguishable from a provider that never received it.
                new XElement(Oagis + "Process",
                    new XAttribute("acknowledgeCode", "Always")),
                new XElement(Cir + "CreateRegistry",
                    registry.ToElement(Cir),
                    new XElement(Cir + "CreateCIRID", createCirid ? "true" : "false"))));

        return new XDocument(root);
    }

    /// <summary>
    /// ProcessEquivalentEntries: attach new entries to identities that already
    /// exist, rather than creating fresh ones.
    ///
    /// The difference from ProcessRegistry matters. Registering LOC-000001 on its
    /// own produces a second identity for the same pump — the duplicate the registry
    /// is supposed to prevent. Asserting equivalence produces one identity with two
    /// names, which is the point.
    /// </summary>
    public static XDocument ProcessEquivalentEntries(
        IReadOnlyList<EquivalentEntry> entries,
        string senderLogicalId,
        string correlationId,
        string? senderReferenceId = null,
        Instant? creationDateTime = null)
    {
        var payload = new XElement(Cir + "CreateEquivalentEntries");

        foreach (var entry in entries)
        {
            payload.Add(entry.ToElement(Cir));
        }

        var root = new XElement(Cir + "ProcessEquivalentEntries",
            new XAttribute(XNamespace.Xmlns + "cir", Namespaces.Cir),
            new XAttribute(XNamespace.Xmlns + "oa", Namespaces.Oagis),
            new XAttribute("releaseID", "1.2.1"),
            ApplicationArea(senderLogicalId, correlationId, senderReferenceId, creationDateTime),
            new XElement(Cir + "DataArea",
                new XElement(Oagis + "Process",
                    new XAttribute("acknowledgeCode", "Always")),
                payload));

        return new XDocument(root);
    }

    /// <summary>
    /// GetRegistry: ask what the registry knows.
    ///
    /// An empty filter list matches everything, so callers resolving one identifier
    /// should always supply an EntryFilter — otherwise the response is the whole
    /// registry.
    /// </summary>
    public static XDocument GetRegistry(
        IReadOnlyList<Filter> filters,
        string senderLogicalId,
        string correlationId,
        Instant? creationDateTime = null)
    {
        var payload = new XElement(Cir + "GetRegistry");

        foreach (var filter in filters)
        {
            payload.Add(filter.ToElement(Cir));
        }

        var root = new XElement(Cir + "GetRegistry",
            new XAttribute(XNamespace.Xmlns + "cir", Namespaces.Cir),
            new XAttribute(XNamespace.Xmlns + "oa", Namespaces.Oagis),
            new XAttribute("releaseID", "1.2.1"),
            ApplicationArea(senderLogicalId, correlationId, null, creationDateTime),
            new XElement(Cir + "DataArea",
                new XElement(Oagis + "Get"),
                payload));

        return new XDocument(root);
    }

    private static XElement ApplicationArea(
        string senderLogicalId, string correlationId, string? referenceId, Instant? creationDateTime)
    {
        var sender = new XElement(Oagis + "Sender",
            new XElement(Oagis + "LogicalID", senderLogicalId),
            new XElement(Oagis + "ComponentID", "SimHost"));

        if (referenceId is { Length: > 0 })
        {
            sender.Add(new XElement(Oagis + "ReferenceID", referenceId));
        }

        var timestamp = creationDateTime ?? SystemClock.Instance.GetCurrentInstant();

        return new XElement(Oagis + "ApplicationArea",
            sender,
            new XElement(Oagis + "CreationDateTime", InstantPattern.ExtendedIso.Format(timestamp)),
            new XElement(Oagis + "BODID", correlationId));
    }
}

/// <summary>
/// An AcknowledgeRegistry fault. The BOD carries four distinct kinds, and the
/// element name is the only thing distinguishing them, so it is preserved.
/// </summary>
public sealed record CirFault(string Kind, string? Detail);

/// <summary>
/// Reads AcknowledgeRegistry and ShowRegistry responses.
///
/// Acknowledgement is not success. AcknowledgeRegistry carries zero or more faults
/// per registry, category, entry and property, and a response with faults still
/// arrives as a well-formed acknowledgement — treating any response as confirmation
/// would silently discard exactly the information the round trip exists to obtain.
/// </summary>
public sealed record CirResponse(
    string Verb,
    string? BodId,
    IReadOnlyList<Registry> Registries,
    IReadOnlyList<CirFault> Faults)
{
    /// <summary>
    /// The response as it arrived. Kept because a fault whose structure we did not
    /// anticipate leaves the parsed view empty, and an empty fault message is
    /// indistinguishable from no fault at all.
    /// </summary>
    public string? RawXml { get; init; }

    public bool HasFaults => Faults.Count > 0;

    /// <summary>
    /// Every child element of a fault, flattened. Fault shapes vary and guessing at
    /// Message or Description alone loses whatever the provider actually said.
    /// </summary>
    private static string DescribeFault(System.Xml.Linq.XElement fault)
    {
        var parts = fault.Descendants()
            .Where(e => !e.HasElements && !string.IsNullOrWhiteSpace(e.Value))
            .Select(e => $"{e.Name.LocalName}={e.Value.Trim()}")
            .ToList();

        if (parts.Count > 0) return string.Join("; ", parts);

        var text = fault.Value.Trim();
        return string.IsNullOrWhiteSpace(text)
            ? $"(no detail; element was {fault.Name})"
            : text;
    }

    /// <summary>
    /// Anything whose name ends in Fault is treated as one.
    ///
    /// A fixed list silently ignores fault types it does not know about, and a fault
    /// that is not reported is worse than one that is reported clumsily — the caller
    /// sees a successful acknowledgement and carries on.
    /// </summary>
    private static bool IsFault(string localName) =>
        localName.EndsWith("Fault", StringComparison.Ordinal);

    public static CirResponse Parse(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = document.Root
            ?? throw new InvalidOperationException("CIR response has no root element.");

        var dataArea = root.Child("DataArea");

        var verb = dataArea?.Elements().FirstOrDefault()?.Name.LocalName ?? root.Name.LocalName;
        var bodId = root.Child("ApplicationArea/BODID").SafeValue();

        var registries = dataArea?
            .Descendants()
            .Where(e => e.Name.LocalName == "Registry" && e.Parent?.Name.LocalName != "Category")
            .Select(e => new Registry(e))
            .ToList() ?? [];

        // Descendants, not children: faults may sit inside the command element
        // rather than beside it, and a fault found nowhere is reported as success.
        var faults = dataArea?
            .Descendants()
            .Where(e => IsFault(e.Name.LocalName))
            .Select(e => new CirFault(e.Name.LocalName, DescribeFault(e)))
            .ToList() ?? [];

        return new CirResponse(verb, bodId, registries, faults)
        {
            RawXml = root.ToString(SaveOptions.DisableFormatting)
        };
    }

    /// <summary>All entries across every registry and category, flattened.</summary>
    public IEnumerable<Entry> AllEntries =>
        Registries.SelectMany(r => r.Category).SelectMany(c => c.Entry);
}
