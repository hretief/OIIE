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

    // ---- Upstream: ENG -----------------------------------------------------

    /// <summary>Root of the ENG function app, e.g. https://host/api.</summary>
    public string? EngBaseUrl { get; set; }

    /// <summary>Function key for ENG, sent as x-functions-key.</summary>
    public string? EngApiKey { get; set; }

    /// <summary>
    /// The iModel whose markers are published. Required: an engine that polled
    /// every iModel would publish another project's design onto this channel.
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
}
