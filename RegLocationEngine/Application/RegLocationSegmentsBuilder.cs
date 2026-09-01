using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;
using RegLocationEngine.Infrastructure.RegLocation;

namespace RegLocationEngine.Application;

/// <summary>
/// Turns approved REG-LOCATION tags into the BOD that carries them.
///
/// The same BOD as EngEngine emits, and deliberately so: a functional location is
/// a Segment whether a design tool proposed it or a steward accepted it. What
/// differs is who is asserting it and on which channel. ENG's SyncSegments says
/// "this is what we drew"; this one says "this is what we have accepted into the
/// registry", and a subscriber tells them apart by the InfoSource and the
/// channel, not by the message type.
///
/// REG-LOCATION's identity is stated the same way ENG's is:
///
///   scope        -> InfoSource.UUID
///   tag_id       -> Segment.IDInInfoSource
///   code         -> Segment.ShortName
///
/// A tag_id is unique only within one registry instance and a code only within
/// an item and revision, so neither alone lets a receiver navigate back. Together
/// with Segment.UUID -- the registry GUID, which is the CIRID -- they let a
/// downstream system register REG-LOCATION's key against the federated identity.
///
/// Nothing registry-internal beyond that travels: item_id, class_id and the
/// object row's flags stop here. They are how this registry stores a tag, not
/// what a tag is.
/// </summary>
public sealed class RegLocationSegmentsBuilder(IOptions<RegLocationEngineOptions> options)
{
    private readonly RegLocationEngineOptions _options = options.Value;

    /// <summary>
    /// Builds the publication for a batch of approved tags.
    ///
    /// Replace rather than Add, for the same reason as ENG: receivers upsert on
    /// the sender's identifier, so Replace states the real contract and makes
    /// republication after a failed send idempotent instead of duplicating rows.
    /// It matters more here than there -- a tag republished by the reconciling
    /// sweep is the expected case, not the exceptional one.
    /// </summary>
    public XElement Build(
        IReadOnlyList<RegTagDetail> tags,
        string decidedBy,
        string correlationId)
    {
        var bod = new SyncSegments(ActionCodes.Replace);

        bod.ApplicationArea.BODID = correlationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = _options.LogicalId,
            ComponentID = "RegLocationEngine",

            // Who approved, so a receiver can answer "on whose authority is this
            // in the registry". That is the question this publication exists to
            // let someone ask; the ENG equivalent carries the marker name because
            // there the answer is "which handover", and here it is "which
            // steward".
            ReferenceID = decidedBy
        };

        foreach (var detail in tags)
        {
            bod.With(BuildSegment(detail));
        }

        // The root element rather than the document: ISBM carries message content
        // as an element, and handing over a document would mean the XML
        // declaration travelled inside a payload about to be embedded in another.
        return bod.CreateDocument().Root
            ?? throw new InvalidOperationException("SyncSegments serialised to an empty document.");
    }

    /// <summary>
    /// Whether a tag can be published at all.
    ///
    /// It cannot without a registry GUID, and it cannot before it is approved.
    ///
    /// The GUID rule is ENG's rule for the same reason: an identity nobody has
    /// agreed to is not federated, and deriving one from the code would be worse
    /// than publishing nothing -- it looks correct until the day a real GUID
    /// appears, and then the same location arrives downstream under a second
    /// identity with no way to tell it was ever one thing.
    ///
    /// The state rule is the gate itself. A proposed tag reaching the channel
    /// would mean the steward's decision had been bypassed, which is precisely
    /// the failure this engine exists to prevent.
    /// </summary>
    public static bool IsPublishable(RegTagDetail detail) =>
        detail.Object.Guid is not null
        && string.Equals(detail.Tag.State, "Approved", StringComparison.OrdinalIgnoreCase);

    private Segment BuildSegment(RegTagDetail detail)
    {
        var federationId = detail.Object.Guid
            ?? throw new InvalidOperationException(
                $"Tag {detail.Tag.TagId} has no registry GUID and cannot be published.");

        var segment = new Segment
        {
            // The federated identity, unaltered. This is the value the receiver
            // and REG-LOCATION have in common; everything else hangs off it.
            //
            // Note that it survives revision: the registry mints one GUID per
            // functional location, not per revision, so revision 2 of TIC-101
            // replaces revision 1 downstream rather than arriving beside it.
            UUID = federationId,

            // REG-LOCATION's own identification, registered against the
            // federation id above, so a receiver holding the pair can come back
            // and ask about this tag by the name the registry uses for it.
            IDInInfoSource = detail.Tag.TagId.ToString(),

            // The scope, not the notion "REG-LOCATION". A broker may front more
            // than one registry, and a receiver holding tag 4102 needs to know
            // which one to open it in. Derived from the scope rather than
            // configured so it cannot drift from the tag it describes.
            InfoSource = new InfoSource
            {
                UUID = CcomUuid.ForInfoSource($"{_options.SourceId}:{detail.Object.ScopeId}"),
                ShortName = _options.SourceId
            },

            ShortName = detail.Tag.Code,
            FullName = detail.Tag.Name,
            Description = detail.Tag.Name
        };

        return segment;
    }
}
