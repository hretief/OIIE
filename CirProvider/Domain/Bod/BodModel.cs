namespace CirProvider.Domain.Bod;

/// <summary>OAGIS verbs used by ws-CIR. Annex A defines no Sync verb.</summary>
public enum BodVerb
{
    Process,
    Acknowledge,
    Change,
    Respond,
    Get,
    Show,
    Cancel
}

/// <summary>
/// Which OAGIS verb elements are acceptable in a request DataArea for a given
/// catalogue verb.
///
/// GetRegistry.xsd and GetEquivalentEntries.xsd both declare
/// &lt;xs:element ref="oa:Process"/&gt; even though the Annex A catalogue lists
/// their verb as Get. That looks like a defect in the published schemas, so both
/// are accepted on input; a schema-validating client will send oa:Process.
/// </summary>
public static class BodVerbElements
{
    public static IReadOnlyList<string> Accepted(BodVerb verb) => verb switch
    {
        BodVerb.Get => ["Get", "Process"],
        _ => [verb.ToString()]
    };
}

/// <summary>
/// OAGIS ProcessType.acknowledgeCode and ChangeType.responseCode, both typed as
/// ResponseCodeContentType. Annex A requires a server to support all of them, so
/// they decide whether a response BOD is produced at all.
///
/// CodeLists.xsd defines ResponseCodeEnumerationType as Always | OnError | Never,
/// but ResponseCodeContentType is a union of that enumeration with
/// xsd:normalizedString — arbitrary values are legal and must not be rejected.
/// Unrecognised values fall back to Always.
/// </summary>
public enum ConfirmationCode
{
    /// <summary>Always return the response BOD.</summary>
    Always,

    /// <summary>Never return a response BOD, even on fault.</summary>
    Never,

    /// <summary>Return the response BOD only when the operation faulted.</summary>
    OnError
}

/// <summary>OAGIS ApplicationArea. §Annex A requires at least these three members.</summary>
public sealed record ApplicationArea
{
    public string? SenderLogicalId { get; init; }
    public DateTimeOffset CreationDateTime { get; init; } = DateTimeOffset.UtcNow;
    public string BodId { get; init; } = Guid.NewGuid().ToString();
}

/// <summary>
/// One entry in the Annex A BOD catalogue.
///
/// Acknowledge and Respond BODs have no noun element. Their DataArea is the verb
/// followed by fault elements, each named for the fault and repeatable, in the
/// order the schema declares — AcknowledgeRegistry.xsd, for example, allows
/// CreateRegistryFault, CreateCategoryFault, DuplicateEntryFault and
/// DuplicatePropertyFault, which is exactly §3.1.1's fault set. The catalogue's
/// "CreateRegistry faults" names the operation whose faults these are, not a
/// wrapper element.
/// </summary>
public sealed record BodDefinition(
    string BodName,
    BodVerb Verb,
    string Noun,
    string? ResponseBod,
    IReadOnlyList<CirFaultCode>? DeclaredFaults = null)
{
    public bool ExpectsResponse => ResponseBod is not null;

    /// <summary>True when the DataArea carries fault elements instead of a noun.</summary>
    public bool CarriesFaults => Verb is BodVerb.Acknowledge or BodVerb.Respond;

    /// <summary>Schema declaration order, which an xsd:sequence makes significant.</summary>
    public IReadOnlyList<CirFaultCode> FaultOrder => DeclaredFaults ?? [];
}

public static class BodCatalogue
{
    private static readonly BodDefinition[] All =
    [
        // Request BODs
        new("ProcessRegistry",          BodVerb.Process, "CreateRegistry",           "AcknowledgeRegistry"),
        new("ProcessEquivalentEntries", BodVerb.Process, "CreateEquivalentEntries",  "AcknowledgeEquivalentEntries"),
        new("ChangeRegistry",           BodVerb.Change,  "UpdateRegistry",           "RespondRegistry"),
        new("GetRegistry",              BodVerb.Get,     "GetRegistry",              "ShowRegistry"),
        new("GetEquivalentEntries",     BodVerb.Get,     "GetEquivalentEntries",     "ShowEquivalentEntries"),
        new("GetEntriesByCIRID",        BodVerb.Get,     "GetEntriesByCIRID",        "ShowEntriesByCIRID"),

        // No response: the underlying operation returns nothing and raises nothing.
        new("ChangeEntryCIRID",         BodVerb.Change,  "UpdateEntryCIRID",         null),

        // No response, for consistency with the OAGIS model.
        new("CancelRegistry",           BodVerb.Cancel,  "DeleteRegistry",           null),
        new("CancelCategory",           BodVerb.Cancel,  "DeleteCategory",           null),
        new("CancelEntries",            BodVerb.Cancel,  "DeleteEntries",            null),
        new("CancelProperties",         BodVerb.Cancel,  "DeleteProperties",         null),

        // Response BODs. Fault lists are the xsd:sequence order taken verbatim
        // from AcknowledgeRegistry.xsd, AcknowledgeEquivalentEntries.xsd and
        // RespondRegistry.xsd. The order is not the same across the three and is
        // not alphabetical, so it has to be copied rather than derived.
        new("AcknowledgeRegistry", BodVerb.Acknowledge, "CreateRegistry", null,
        [
            CirFaultCode.CreateRegistryFault,
            CirFaultCode.CreateCategoryFault,
            CirFaultCode.DuplicateEntryFault,
            CirFaultCode.DuplicatePropertyFault
        ]),

        // Note the declaration order: EntryNotFoundFault comes first, and
        // DuplicatePropertyFault is not declared for this BOD at all.
        new("AcknowledgeEquivalentEntries", BodVerb.Acknowledge, "CreateEquivalentEntries", null,
        [
            CirFaultCode.EntryNotFoundFault,
            CirFaultCode.RegistryNotFoundFault,
            CirFaultCode.CategoryNotFoundFault,
            CirFaultCode.DuplicateEntryFault
        ]),

        new("RespondRegistry", BodVerb.Respond, "UpdateRegistry", null,
        [
            CirFaultCode.RegistryNotFoundFault,
            CirFaultCode.CategoryNotFoundFault,
            CirFaultCode.EntryNotFoundFault,
            CirFaultCode.PropertyNotFoundFault
        ]),
        new("ShowRegistry",                 BodVerb.Show,        "GetRegistryResponse",         null),
        new("ShowEquivalentEntries",        BodVerb.Show,        "GetEquivalentEntriesResponse", null),
        new("ShowEntriesByCIRID",           BodVerb.Show,        "GetEntriesByCIRIDResponse",   null)
    ];

    public static BodDefinition? Find(string bodName) =>
        All.FirstOrDefault(b => string.Equals(b.BodName, bodName, StringComparison.Ordinal));

    public static IReadOnlyList<BodDefinition> RequestBods =>
        All.Where(b => b.Verb is BodVerb.Process or BodVerb.Change or BodVerb.Get or BodVerb.Cancel).ToList();
}

/// <summary>A parsed inbound BOD. The noun payload stays as XML until dispatch.</summary>
public sealed record BodRequest
{
    public required BodDefinition Definition { get; init; }
    public required ApplicationArea ApplicationArea { get; init; }

    /// <summary>Present on Process and Change BODs only.</summary>
    public ConfirmationCode Confirmation { get; init; } = ConfirmationCode.Always;

    /// <summary>The noun element from the DataArea, unparsed.</summary>
    public required System.Xml.Linq.XElement Noun { get; init; }
}

/// <summary>
/// The outcome of dispatching a BOD. <see cref="ResponseBodName"/> is null when
/// the catalogue defines no response, or when the confirmation code suppressed it.
/// </summary>
public sealed record BodResult
{
    public string? ResponseBodName { get; init; }
    public ApplicationArea? OriginalApplicationArea { get; init; }
    public IReadOnlyList<CirFault> Faults { get; init; } = [];

    /// <summary>Populated for Show BODs.</summary>
    public IReadOnlyList<Registry> Registries { get; init; } = [];

    public bool HasFaults => Faults.Count > 0;
}
