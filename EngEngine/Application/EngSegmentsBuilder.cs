using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;

namespace EngEngine.Application;

/// <summary>
/// Turns the contents of an ENG marker into the BOD that carries it.
///
/// The vocabulary follows the SimHost ENG personality, which is the reference for
/// how this sandbox says "here are some functional locations". What differs is the
/// source: this builds from ENG elements read over HTTP rather than from a local
/// tag table, so the mapping is thinner. ENG stores an element, not a datasheet --
/// there is no service description, range or control action to carry, because ENG
/// does not have them to give.
///
/// Nothing ENG-internal appears in the output. NamedVersionId, ChangesetId and
/// ChangesetIndex all stop here. The marker travels as its name, which is what an
/// engineer chose and what a person reading the message downstream can act on.
///
/// The one ENG-internal value that does travel is the element's own ECInstanceId,
/// and it travels as IDInInfoSource against the element's FederationGuid. That
/// pairing is the whole point of publishing: it tells a receiver "the thing you
/// know as this federation id is the thing ENG knows as that row", which is what
/// lets ENG be asked about it again later. It is a registration of ENG's identity
/// under the federated one, not a key anybody downstream is expected to resolve
/// on its own.
///
/// ENG's identity is a composite, so it takes three fields to state it:
///
///   iModelId     -> InfoSource.UUID
///   ECInstanceId -> Segment.IDInInfoSource
///   CodeValue    -> Segment.ShortName
///
/// All three are needed. ECInstanceId is unique only within one iModel, and
/// CodeValue is not unique across iModels, so a receiver that holds only part of
/// the key holds something that resolves to more than one element or to none.
/// Together with Segment.UUID -- the FederationGuid, which is the CIRID -- these
/// are what let a downstream system register ENG's key against the federated
/// identity and navigate back to the exact element.
///
/// Separately from that key, the owning iTwin travels as Segment.RegistrationSite:
///
///   iTwinId -> RegistrationSite.UUID
///
/// It is not part of ENG's key and does not help resolve an element. It says which
/// plant the element belongs to, which is what a receiver scopes by. REG-LOCATION
/// has no concept of an iModel and takes segments from every model under a twin
/// equally, so the twin is the only part of this it can file against.
/// </summary>
public sealed class EngSegmentsBuilder(IOptions<EngEngineOptions> options)
{
    private readonly EngEngineOptions _options = options.Value;

    /// <summary>
    /// Builds the publication for one marker.
    ///
    /// Replace rather than Add: receivers upsert on the sender's identifier, so
    /// Replace states the real contract and makes republication after a failed
    /// drain idempotent instead of duplicating rows.
    /// </summary>
    public XElement Build(
        EngNamedVersion marker,
        IReadOnlyList<EngElement> elements,
        Guid iTwinId,
        string correlationId)
    {
        var bod = new SyncSegments(ActionCodes.Replace);

        bod.ApplicationArea.BODID = correlationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = _options.LogicalId,
            ComponentID = "EngEngine",

            // The marker name, so a receiver can answer "which handover produced
            // this" in the same words the engineer used. Deliberately not the
            // changeset: that identifies the same act in terms only ENG can read.
            ReferenceID = marker.Name
        };

        // The iModel, not the notion "ENG". ENG's native key is a composite --
        // iModelId, ECInstanceId, CodeValue -- and no single field identifies an
        // element without all three: an iTwin may hold a Mechanical and a
        // Structural iModel, and the same CodeValue can exist in both.
        //
        // The three parts travel as InfoSource.UUID, IDInInfoSource and ShortName
        // respectively. This one used to be a hash of the literal "ENG", which
        // named the kind of system rather than the instance, so a receiver holding
        // element 44732 could not say which iModel to open it in. Every element in
        // one publication comes from one iModel, so it belongs on the shared
        // InfoSource rather than repeated on each segment.
        //
        // Taken from the marker rather than from configuration. Provenance is a
        // statement about the thing being published, so it has to come from the
        // payload: the configured id is a poll filter, and the two are only equal
        // by convention. When they disagreed the engine polled one model and
        // stamped another -- which, unlike a stale id that fails the lookup
        // outright, publishes successfully and is wrong.
        var infoSource = new InfoSource
        {
            UUID = marker.IModelId,
            ShortName = _options.SourceId
        };

        // The iTwin that owns the iModel, as the site every segment was
        // registered against. This answers a different question from InfoSource
        // above: InfoSource says which model produced the element, the site says
        // which plant it belongs to. A receiver needs both -- REG-LOCATION scopes
        // by twin and has no concept of an iModel, while ENG cannot resolve an
        // element without the model.
        //
        // The twin id is used directly rather than derived, matching the site
        // SyncSites registers: minting a second identity for the same plant is
        // precisely what stops a receiver recognising it as one thing.
        //
        // Null when the twin is unknown, which leaves the segment unscoped rather
        // than scoped to something invented.
        var registrationSite = iTwinId == Guid.Empty
            ? null
            : new Site
            {
                UUID = iTwinId,

                // The twin's record id in ENG: enough for a receiver to come back
                // and ask about it. Its own InfoSource, not the iModel's -- a twin
                // is not an element of the model it contains, and reusing that one
                // would say the two ids are keys within the same source.
                IDInInfoSource = iTwinId.ToString(),
                InfoSource = new InfoSource
                {
                    UUID = CcomUuid.ForInfoSource(_options.SourceId),
                    ShortName = _options.SourceId
                }
            };

        foreach (var element in elements)
        {
            bod.With(BuildSegment(element, infoSource, registrationSite));
        }

        // The root element rather than the document: ISBM carries message content
        // as an element, and handing over a document would mean the XML declaration
        // travelled inside a payload that is about to be embedded in another one.
        return bod.CreateDocument().Root
            ?? throw new InvalidOperationException("SyncSegments serialised to an empty document.");
    }

    /// <summary>
    /// Whether an element can be published at all.
    ///
    /// It cannot without a FederationGuid. ENG does not mint one -- federating is
    /// a decision someone takes about an element, not a property it acquires by
    /// being saved -- and an element that has never been federated has no identity
    /// any other system has agreed to.
    ///
    /// An earlier version derived a UUID from the iModel and code when the guid
    /// was absent. That was wrong in a way that would have been expensive to find:
    /// the derived id is stable, so it looks correct for as long as nobody
    /// federates the element -- and the day someone does, the same pump arrives
    /// downstream under a second identity with no way to tell it was ever one
    /// thing. Publishing nothing is recoverable; publishing a fabricated identity
    /// is not.
    /// </summary>
    public static bool IsPublishable(EngElement element) =>
        element.FederationGuid is not null;

    private Segment BuildSegment(EngElement element, InfoSource infoSource, Site? registrationSite)
    {
        var federationId = element.FederationGuid
            ?? throw new InvalidOperationException(
                $"Element {element.ECInstanceId} has no FederationGuid and cannot be published.");

        var segment = new Segment
        {
            // The federated identity, unaltered. This is the value the receiver
            // and ENG have in common; everything else in the segment is
            // description hanging off it.
            UUID = federationId,

            // ENG's own identification of the element, registered against the
            // federation id above. A receiver holding the pair can come back to
            // ENG and ask about this element by the name ENG uses for it.
            IDInInfoSource = element.ECInstanceId.ToString(),

            InfoSource = infoSource,
            RegistrationSite = registrationSite,
            ShortName = element.CodeValue,
            FullName = element.UserLabel ?? element.DisplayName,
            Description = element.DisplayName ?? element.UserLabel
        };

        if (element.FullyQualifiedECClassName is { Length: > 0 } className)
        {
            // The EC class is ENG's own vocabulary, not an RDL key, so it is sourced
            // as ENG rather than MIMOSA-RDL. Claiming otherwise would tell a receiver
            // it can look the class up in a library that has never heard of it.
            //
            // Its own InfoSource, not the segment's: that one now identifies the
            // iModel, and a class is not an element of the iModel the way a segment
            // is. Reusing it would say ECInstanceId and class name are two
            // identifiers within the same source, and a receiver reconstructing the
            // composite key from the pair would build a key that resolves to nothing.
            segment.Type = new SegmentType
            {
                UUID = CcomUuid.ForReferenceData(_options.SourceId, className),
                IDInInfoSource = className,
                InfoSource = new InfoSource
                {
                    UUID = CcomUuid.ForInfoSource(_options.SourceId),
                    ShortName = _options.SourceId
                },
                ShortName = className.Split(':').Last()
            };
        }

        return segment;
    }
}
