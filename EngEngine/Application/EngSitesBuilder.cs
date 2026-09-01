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
///   iTwinId  -> Site.UUID       the federation id, and the CIRID downstream
///   type     -> Site.Type.UUID  minted once per type string and reused
///
/// Site.UUID is passed through untouched. It is the platform's own federation
/// identifier, the receiver will register REG-LOCATION's Scope and Serial
/// against it, and deriving anything here would break the correspondence the
/// whole workflow rests on.
///
/// Site.Type.UUID is different: the platform gives a type as a bare string with
/// no identifier attached, so somebody has to mint one. ENG does, once, and
/// remembers it -- see GetOrCreateITwinTypeUuidAsync. Minting per publication
/// would give the second Highway project a different 'Highway' from the first,
/// and REG-LOCATION would hold two item rows nothing could tell apart.
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
    public XElement Build(EngITwin twin, Guid siteTypeUuid, string correlationId)
    {
        var bod = new SyncSites(ActionCodes.Replace);

        bod.ApplicationArea.BODID = correlationId;
        bod.ApplicationArea.Sender = new Sender
        {
            LogicalID = _options.LogicalId,
            ComponentID = "EngEngine",

            // The twin's own code, so a receiver reading the envelope can say
            // which site this concerns without parsing the noun.
            ReferenceID = twin.Code
        };

        bod.With(BuildSite(twin, siteTypeUuid));

        return bod.CreateDocument().Root
            ?? throw new InvalidOperationException("SyncSites serialised to an empty document.");
    }

    /// <summary>
    /// Whether a twin can be published at all.
    ///
    /// It needs a type. Everything else the site carries is naming, and naming
    /// can be thin -- a site with no description is still a site. The type is
    /// not naming: REG-LOCATION creates an Item from it, and the Serial that
    /// represents this iTwin hangs off that Item. A twin published without one
    /// would ask the receiver to invent a classification, and an invented
    /// classification is one every later twin has to be reconciled against.
    /// </summary>
    public static bool IsPublishable(EngITwin twin) =>
        !string.IsNullOrWhiteSpace(twin.TwinType);

    /// <summary>
    /// The type string a site's identity is minted from.
    ///
    /// Exposed so the caller can resolve the UUID before building, since that
    /// resolution is a database write and does not belong inside a mapper.
    /// </summary>
    public static string TypeNameOf(EngITwin twin) =>
        twin.TwinType ?? throw new InvalidOperationException(
            $"iTwin {twin.ITwinId} has no type and cannot be published.");

    private Site BuildSite(EngITwin twin, Guid siteTypeUuid)
    {
        // The platform's displayName is the human-readable name; Code is what
        // the provider stored, which for a twin registered from a platform
        // payload is the project number. Falling back keeps a sparsely
        // registered twin publishable under something a person can read.
        var displayName = FirstNonBlank(twin.DisplayName, twin.Code);

        return new Site
        {
            // The federation id, unaltered. This becomes the CIRID, and the
            // Scope, Item and Serial the receiver creates all hang off it.
            UUID = twin.ITwinId,

            // ENG's own handle on the twin, registered against the federation
            // id above, so a receiver can come back and ask about it.
            IDInInfoSource = twin.ITwinId.ToString(),

            InfoSource = new InfoSource
            {
                UUID = CcomUuid.ForInfoSource(_options.SourceId),
                ShortName = _options.SourceId
            },

            ShortName = displayName,

            // Name and number together, per the spec's Step 3 mapping. The
            // number alone is not readable and the name alone is not unique --
            // two corridors may both be called Main Street.
            FullName = Join(displayName, twin.Number),

            Description = twin.Description,

            Type = new SegmentType
            {
                // Minted by ENG and reused, not derived from the type string:
                // deriving it would make the identity a function of the
                // spelling, so correcting a type name would silently
                // reclassify every site already published under it.
                UUID = siteTypeUuid,

                IDInInfoSource = twin.TwinType,

                // The type is ENG's vocabulary rather than an RDL key, so it is
                // sourced as ENG. Claiming a reference library would tell a
                // receiver it can look 'Highway' up somewhere that has never
                // heard of it.
                InfoSource = new InfoSource
                {
                    UUID = CcomUuid.ForInfoSource(_options.SourceId),
                    ShortName = _options.SourceId
                },

                ShortName = twin.TwinType,
                FullName = Join(twin.TwinType, twin.SubClass)
            }
        };
    }

    // An em dash, matching the spec: 'US Route 202 — HWYUSR202'. When the
    // second part is absent the separator goes with it, rather than leaving a
    // dangling dash in a name a person reads.
    private static string? Join(string? first, string? second) =>
        string.IsNullOrWhiteSpace(second) ? first
        : string.IsNullOrWhiteSpace(first) ? second
        : $"{first} — {second}";

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
