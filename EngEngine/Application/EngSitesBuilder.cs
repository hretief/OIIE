using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Oiie.Ccom.Bods;
using Oiie.Ccom.Oagis;
using Oiie.Ccom.Types;

namespace EngEngine.Application;

/// <summary>
/// Turns an iTwin into the BOD that announces it.
///
/// SyncSites is the flow that creates a context rather than filling one. Every
/// other publication this engine makes assumes a site already exists to hold
/// it: SyncSegments needs a Scope for its functional locations, and the ISBM
/// channel it publishes onto is named after the iTwin. This is the message that
/// brings both into being, which is why it is the one flow that travels on the
/// enterprise channel instead of a per-iTwin one.
///
/// The mapping is small because an iTwin is small. What matters is not the
/// number of fields but which of them are identities:
///
///   iTwinId      -> Site.UUID       the federation id, and the CIRID downstream
///   iTwinTypeId  -> Site.Type.UUID  the stored identity of the boundary
///
/// Site.UUID is passed through untouched. It is the platform's own federation
/// identifier, the receiver will register REG-LOCATION's Scope and Serial
/// against it, and deriving anything here would break the correspondence the
/// whole workflow rests on.
///
/// Site.Type.UUID is different: the platform gives a type as a bare string with
/// no identifier attached, so ENG keeps the identity in dbo.iTwinType and the
/// twin references it. Both the id and the name are read from that row -- not
/// from the twin's own free-text Type column, which holds a second copy of the
/// name that nothing keeps in step -- so the two halves of the published Type
/// cannot disagree.
///
/// Storing it rather than deriving it from the string is what makes the
/// identity survive a rename: correcting a boundary's spelling leaves the UUID
/// alone, so nothing already published to REG-LOCATION reclassifies.
/// </summary>
public sealed class EngSitesBuilder(IOptions<EngEngineOptions> options)
{
    private readonly EngEngineOptions _options = options.Value;

    /// <summary>
    /// Builds the publication for one iTwin.
    ///
    /// Replace rather than Add, matching <see cref="EngSegmentsBuilder"/>: the
    /// receiver upserts on Site.UUID, so a redelivered or replayed site updates
    /// the scope it created the first time instead of making a second one. The
    /// spec's idempotency rule is stated in the verb.
    /// </summary>
    public XElement Build(EngITwin twin, string correlationId)
    {
        var bod = new SyncSites(ActionCodes.Replace);

        bod.ApplicationArea.BODID = correlationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = _options.LogicalId,
            ComponentID = "EngEngine",

            // Names the flow this envelope belongs to, per the sample.
            TaskID = "Site Creation",

            // The twin's own handle, so a receiver reading the envelope can say
            // which site this concerns without parsing the noun.
            ReferenceID = twin.Handle
        };

        bod.With(BuildSite(twin));

        return bod.CreateDocument().Root
            ?? throw new InvalidOperationException("SyncSites serialised to an empty document.");
    }

    /// <summary>
    /// Builds the publication announcing that an iTwin no longer exists.
    /// </summary>
    /// <remarks>
    /// Still a Sync BOD, with actionCode Delete on the ActionExpression, rather
    /// than a DeleteSites root. That looks like the weaker choice and is not.
    /// OAGIS derives the noun by stripping the verb from the root name, so a
    /// root of SyncSites carrying a Delete verb parses to the noun 'SyncSites'
    /// and matches no handler; the receiver would skip it as an unrecognised BOD
    /// and report success having done nothing. Keeping the Sync/Sites pair
    /// intact and varying the action code is both what the action code is for
    /// and the only form the envelope parser can actually read.
    ///
    /// Carries the UUID alone. Everything else on a Site is naming, and naming
    /// the receiver already holds -- it keys on the UUID, so any other value
    /// here could only agree or be wrong. Sending the twin's last known name
    /// with its deletion invites a receiver to match on it and delete by name.
    /// </remarks>
    public XElement BuildDelete(Guid iTwinId, string correlationId)
    {
        var bod = new SyncSites(ActionCodes.Delete);

        bod.ApplicationArea.BODID = correlationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = _options.LogicalId,
            ComponentID = "EngEngine",
            TaskID = "Site Deletion",
            ReferenceID = iTwinId.ToString()
        };

        bod.With(new Site
        {
            UUID = iTwinId,
            IDInInfoSource = iTwinId.ToString()
        });

        return bod.CreateDocument().Root
            ?? throw new InvalidOperationException("SyncSites serialised to an empty document.");
    }

    /// <summary>
    /// Whether a twin can be published at all.
    ///
    /// It needs a boundary. Everything else the site carries is naming, and
    /// naming can be thin -- a site with no description is still a site. The
    /// type is not naming: REG-LOCATION creates an Item from it, and the Serial
    /// that represents this iTwin hangs off that Item. A twin published without
    /// one would ask the receiver to invent a classification, and an invented
    /// classification is one every later twin has to be reconciled against.
    ///
    /// The id is what is checked rather than the name, because the id is what
    /// Site.Type.UUID carries. A twin with a name but no id would serialise a
    /// site type with an empty identity.
    /// </summary>
    public static bool IsPublishable(EngITwin twin) =>
        twin.ITwinTypeId is not null;

    private Site BuildSite(EngITwin twin)
    {
        return new Site
        {
            // The federation id, unaltered. This becomes the CIRID, and the
            // Scope, Item and Serial the receiver creates all hang off it.
            UUID = twin.ITwinId,

            // The same id again, per the sample: ENG identifies the twin to
            // itself by its federation id, so there is no second key to give.
            IDInInfoSource = twin.ITwinId.ToString(),

            // The engineering number, per the sample. Falls back through the
            // handle so a twin registered without a number still names itself
            // rather than going out blank -- receivers key their scope code on
            // this.
            ShortName = FirstNonBlank(twin.Number, twin.Handle),

            Description = twin.Description,

            Type = new SegmentType
            {
                // The stored identity of the boundary, not derived from its
                // name: renaming a boundary must not reclassify the sites
                // already published under it.
                UUID = twin.ITwinTypeId!.Value,

                // The id rather than the name, per the sample. This is how a
                // receiver refers the boundary back to ENG, and the id is the
                // half of the row that does not move.
                IDInInfoSource = twin.ITwinTypeId!.Value.ToString(),

                // The name from the type row, not the twin's free-text Type
                // column. Both carry it, but only the type row is unique and
                // only it moves on a rename, so sourcing the name here keeps it
                // agreeing with the UUID above.
                ShortName = twin.ITwinTypeNumber
            }
        };
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
