namespace EngEngine.Application;

/// <summary>
/// Everything the engine needs to know about the two systems it stands between.
///
/// Bound from the "EngEngine" configuration section. The poll schedule is not
/// here: a TimerTrigger binding expression is resolved by WebJobs as a literal
/// setting name before the double underscore is folded into a section, so the
/// schedule has to live at the root as a flat EngEnginePollSchedule setting.
/// </summary>
public sealed class EngEngineOptions
{
    /// <summary>
    /// Off by default. An engine that starts polling the moment it is deployed,
    /// against configuration nobody has filled in yet, produces a stream of
    /// connection errors that look like a fault rather than an absence.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Root of the sandbox API, e.g. https://host, from which this engine reads
    /// the channel topology.
    ///
    /// Optional. Unset, the engine publishes to the channels derived below,
    /// which is what it did before topology existed. Set, the topology answers
    /// instead — so a new subscriber or a moved channel is a configuration
    /// change at the sandbox rather than a settings edit on every engine that
    /// touches it.
    /// </summary>
    public string? SandboxBaseUrl { get; set; }

    /// <summary>
    /// This engine's participant id, used to find its own rows in the topology.
    /// Matches the personality pack directory name.
    /// </summary>
    public string ParticipantId { get; set; } = "eng";

    /// <summary>
    /// The scenario whose topology this engine publishes under. ENG originates
    /// the design release, which is SC01.
    /// </summary>
    public string ScenarioId { get; set; } = "sc01";

    // ---- Upstream: ENG -----------------------------------------------------

    /// <summary>Root of the ENG function app, e.g. https://host/api.</summary>
    public string? EngBaseUrl { get; set; }

    /// <summary>Function key for ENG, sent as x-functions-key.</summary>
    public string? EngApiKey { get; set; }

    /// <summary>
    /// Optional. Restricts the drain to a single iModel; left empty, the engine
    /// publishes every iModel ENG holds, each onto its own iTwin's channel.
    ///
    /// A poll filter, and nothing more. It selects which markers this engine
    /// reads; it does not describe what gets published. Provenance
    /// (InfoSource.UUID) comes from the marker itself, because an iModel id is
    /// data ENG generates rather than a deployment choice, and a reset
    /// regenerates it. Reusing this value as data made a stale setting able to
    /// mis-stamp a publication instead of merely failing the lookup.
    ///
    /// It is no longer required for the same reason: a setting naming
    /// provider-generated data goes stale at the next reset, and an engine that
    /// depended on it went quiet until someone noticed and edited it. Set it
    /// only to pin a deployment to one model deliberately -- a focused test --
    /// and expect to revisit it after any reset.
    /// </summary>
    public Guid IModelId { get; set; }

    // ---- Downstream: ISBM --------------------------------------------------

    /// <summary>
    /// The owner-operator organisation, and the first segment of every channel
    /// URI. Stable: it is the one part of the convention that outlives projects.
    /// </summary>
    public string Enterprise { get; set; } = "acme";

    /// <summary>
    /// The integration function this engine serves, and the third segment of the
    /// channel URI. ENG carries design, so "engineering".
    ///
    /// Not the participant name. An early convention put the sender in the URI,
    /// which meant a channel could not be read without knowing who happened to
    /// write to it; the domain says what the channel is *for*, which is what a
    /// subscriber actually selects on.
    /// </summary>
    public string Domain { get; set; } = "engineering";

    /// <summary>
    /// Overrides the derived channel URI.
    ///
    /// Present as an escape hatch for a broker whose channels were provisioned
    /// before this convention, and deliberately empty by default: a value here
    /// silently defeats the convention, so it has to be an explicit choice
    /// rather than something inherited from a sample settings file.
    /// </summary>
    public string? ChannelUriOverride { get; set; }

    /// <summary>
    /// Topics the publication is posted under. Subscribers filter on these, so
    /// changing them silently strands whoever was listening for the old one.
    ///
    /// Form is oiie:{scenario}/ccom:{bod}. The scenario is part of the topic
    /// because the same BOD means different things at different points in a
    /// journey: SyncSegments from ENG is a design proposal, whereas SyncSegments
    /// from REG-LOCATION is an approved location, and a subscriber that wanted
    /// only one of those could not otherwise tell them apart.
    /// </summary>
    public string[] Topics { get; set; } = ["oiie:sc01/ccom:SyncSegments"];

    /// <summary>
    /// Topics a SyncSites publication is posted under.
    ///
    /// Separate from <see cref="Topics"/> because it travels on a different
    /// channel to a different audience. A subscriber to the enterprise sites
    /// channel wants to know that a site exists; it has no interest in the
    /// segments that later fill it.
    /// </summary>
    public string[] SitesTopics { get; set; } = ["oiie/ccom:SyncSites"];

    /// <summary>
    /// How far back the watermark is rewound on each pass.
    ///
    /// ENG documents modifiedSince as inclusive and expects readers to overlap
    /// rather than resume exactly where they stopped: a row committed during the
    /// previous query but stamped just before it would otherwise never be seen.
    /// The published-marker set is what makes the resulting re-reads harmless.
    /// </summary>
    public TimeSpan PollOverlap { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Ceiling on markers published in one drain, so a first run against a
    /// long-lived iModel does not try to publish its whole history at once.
    /// </summary>
    public int MaxMarkersPerPoll { get; set; } = 10;

    // ---- Engine state ------------------------------------------------------

    /// <summary>Container holding the engine's watermark document.</summary>
    public string StateContainer { get; set; } = "engengine";

    /// <summary>Blob holding the watermark and the published-marker set.</summary>
    public string StateBlob { get; set; } = "eng-state.json";

    /// <summary>
    /// The logical identifier ENG publishes under, used to build the CCOM
    /// InfoSource. This is what a receiver sees as the origin of the data.
    /// </summary>
    public string SourceId { get; set; } = "ENG";

    /// <summary>The OAGIS ApplicationArea sender LogicalID.</summary>
    public string LogicalId { get; set; } = "ENG";

    /// <summary>
    /// Maps ENG's EC class onto the common RDL key published in SegmentType.
    ///
    /// Per DR-030 the wire carries a governed RDL key owned by neither sender
    /// nor receiver, and each participant translates at its own edge. This is
    /// ENG's edge. Publishing the EC class verbatim, as this builder used to,
    /// made every consumer keep a private map of ENG's vocabulary -- the cost
    /// of which only became visible when MMS became the second consumer.
    ///
    /// Configuration rather than a lookup because the correspondence between
    /// an EC class and an RDL class is a modelling decision somebody has to
    /// make and record, not something derivable from either schema.
    ///
    /// Keyed case-insensitively; the EC class's casing is iModel-side business.
    /// Keys are fully qualified names as ENG reports them -- "ENG.Streetlight",
    /// schema and class joined by a dot -- not the display label
    /// ("Lighting | Streetlight"), which is presentation and not identity.
    /// </summary>
    public Dictionary<string, string> OutboundRdlClassMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENG.Streetlight"] = "rdl:LightingUnit"
    };

    /// <summary>
    /// The RDL key for an EC class, or null when the class is unmapped.
    ///
    /// Null rather than a fallback, and the asymmetry with REG-LOCATION's
    /// inbound fallback is deliberate. A receiver that cannot map an inbound
    /// key still has the segment and can bind it at a parent; a publisher that
    /// invents a key puts a false statement on the bus that every consumer
    /// will then record as fact. The caller decides what to do with the gap.
    /// </summary>
    public string? ResolveRdlClassKey(string? ecClassName) =>
        !string.IsNullOrWhiteSpace(ecClassName)
            && OutboundRdlClassMap.TryGetValue(ecClassName, out var key)
                ? key
                : null;

    /// <summary>
    /// The publication channel for an iTwin, per the OIIE channel naming
    /// convention:
    ///
    ///     /{enterprise}/{itwin-federation-id}/{domain}/{type}
    ///
    /// The federation id rather than a name, because names change: a corridor
    /// renamed or a project re-scoped would otherwise break every subscription
    /// pointing at it. The UUID is assigned once and never moves, so the URI
    /// survives every reorganisation the human-readable name does not. The
    /// readable name lives in the channel's description field instead.
    ///
    /// Derived here rather than configured so the convention is expressed once.
    /// A URI pasted into settings is a copy that cannot be corrected centrally,
    /// and the settings file is exactly where a convention quietly rots.
    /// </summary>
    public string ChannelUriFor(Guid iTwinFederationId) =>
        !string.IsNullOrWhiteSpace(ChannelUriOverride)
            ? ChannelUriOverride
            : $"/{Enterprise}/{iTwinFederationId:D}/{Domain}/publication";

    /// <summary>
    /// Overrides the derived sites channel URI. Empty by default, for the same
    /// reason as <see cref="ChannelUriOverride"/>.
    /// </summary>
    public string? SitesChannelUriOverride { get; set; }

    /// <summary>
    /// The channel SyncSites publishes onto:
    ///
    ///     /{enterprise}/enterprise/sites/publication
    ///
    /// The literal 'enterprise' sits where a federation id sits on every other
    /// channel, and that is the whole point: this message is what creates an
    /// iTwin context, so it cannot be addressed to one. A per-iTwin channel for
    /// SyncSites would have to be provisioned by the message it carries.
    ///
    /// It is the only flow where the enterprise level is structurally required
    /// rather than merely convenient, which is why it is a distinct member and
    /// not a Domain the caller passes in.
    /// </summary>
    public string SitesChannelUri =>
        !string.IsNullOrWhiteSpace(SitesChannelUriOverride)
            ? SitesChannelUriOverride
            : $"/{Enterprise}/enterprise/sites/publication";

    // ---- Per-iTwin channel provisioning ------------------------------------

    /// <summary>
    /// The domains ENG provisions a per-iTwin publication channel for when a
    /// twin is created.
    /// </summary>
    /// <remarks>
    /// ENG provisions more than its own "engineering" domain, which looks like
    /// overreach and is not. ENG creates elements, which are its own; but it
    /// also creates Sites, and a Site is infrastructure for every participant.
    /// The channels named for a site's federation id are part of that
    /// infrastructure, so they come into being with the site rather than with
    /// the first participant that happens to need one. REG-LOCATION still
    /// publishes on "operations" and ENG never writes to it -- provisioning is
    /// not ownership of the traffic.
    ///
    /// The alternative is what this replaces: each consumer creating its own
    /// channel on first ingest, which means the channel does not exist until
    /// someone has already needed it, and a publication sent a moment too early
    /// has nowhere to land.
    ///
    /// A list rather than two members because the set grows -- CMS will want an
    /// alerting domain on the same site -- and adding one should be a settings
    /// change, not another method here.
    /// </remarks>
    public string[] ITwinChannelDomains { get; set; } = ["engineering", "operations"];

    /// <summary>
    /// Whether ENG provisions the per-iTwin channels at all.
    ///
    /// Separable from <see cref="RegisterInCir"/> deliberately: provisioning
    /// channels and registering identity fail for different reasons, and an
    /// environment where the broker is managed externally should be able to
    /// turn off the former without losing the latter.
    /// </summary>
    public bool ProvisionITwinChannels { get; set; } = true;

    /// <summary>
    /// The per-iTwin channel URI for a given domain, per the same convention
    /// <see cref="ChannelUriFor(Guid)"/> follows.
    ///
    /// Takes the domain explicitly because this is used to build channels for
    /// other participants to read, where <see cref="Domain"/> -- ENG's own --
    /// would be the wrong answer for all but one of them.
    /// </summary>
    public string ChannelUriFor(Guid iTwinFederationId, string domain) =>
        $"/{Enterprise}/{iTwinFederationId:D}/{domain}/publication";

    // ---- Downstream: CIR ---------------------------------------------------

    /// <summary>Root of the CIR function app, e.g. https://host/api.</summary>
    public string? CirBaseUrl { get; set; }

    /// <summary>Function key for CIR, sent as x-functions-key.</summary>
    public string? CirApiKey { get; set; }

    /// <summary>
    /// Whether ENG registers its twins in CIR as well as publishing them.
    /// </summary>
    /// <remarks>
    /// ENG registers because it is the source of the identity, not because it
    /// consumes one. The federation id it puts in the Cirid is the id every
    /// other participant will correlate on, and it is ENG's row that says what
    /// that GUID means upstream; a consumer registering it instead is recording
    /// a fact it inferred rather than one it holds.
    ///
    /// Defaulted on but honoured everywhere, so an environment without a CIR
    /// still publishes: registering an identity and announcing a twin are
    /// different acts, and the second should not be blocked by the first.
    /// </remarks>
    public bool RegisterInCir { get; set; } = true;

    /// <summary>
    /// The CIR category ENG's twins are registered under.
    ///
    /// Distinct from REG-LOCATION's ITWIN-SITE: the two describe the same twin
    /// from different systems, each keyed by its own primary key, which is
    /// precisely the correlation CIR exists to hold. Collapsing them into one
    /// category would discard the distinction that makes the entry useful.
    /// </summary>
    public string CirITwinCategory { get; set; } = "ITWIN";
}
