namespace RegLocationEngine.Application;

/// <summary>
/// Everything the engine needs to know about the systems it stands between.
///
/// Bound from the "RegLocationEngine" configuration section. The sweep schedule
/// is not here: a TimerTrigger binding expression is resolved by WebJobs as a
/// literal setting name before the double underscore is folded into a section,
/// so the schedule has to live at the root as a flat
/// RegLocationEngineSweepSchedule setting.
/// </summary>
public sealed class RegLocationEngineOptions
{
    /// <summary>
    /// Off by default, for the same reason as EngEngine: an engine that starts
    /// reaching out the moment it is deployed, against configuration nobody has
    /// filled in yet, produces a stream of connection errors that look like a
    /// fault rather than an absence.
    ///
    /// This gates the reconciling sweep. The webhook stays live regardless,
    /// because a notification that arrives and is silently dropped is worse than
    /// one that is refused.
    /// </summary>
    public bool Enabled { get; set; }

    // ---- Upstream: REG-LOCATION -------------------------------------------

    /// <summary>Root of the REG-LOCATION function app, e.g. https://host/api.</summary>
    public string? RegLocationBaseUrl { get; set; }

    /// <summary>Function key for REG-LOCATION, sent as x-functions-key.</summary>
    public string? RegLocationApiKey { get; set; }

    /// <summary>
    /// The scope whose approvals this engine carries.
    ///
    /// Null means all of them, which is the right default for a single-site
    /// demo and the wrong one for a broker serving several sites at once: an
    /// unscoped engine would publish another site's locations onto this channel.
    /// </summary>
    public int? ScopeId { get; set; }

    // ---- Downstream: ISBM --------------------------------------------------

    /// <summary>
    /// The owner-operator organisation, and the first segment of every channel
    /// URI. Stable: it is the one part of the convention that outlives projects.
    /// </summary>
    public string Enterprise { get; set; } = "acme";

    /// <summary>
    /// The integration function this engine serves, and the third segment of the
    /// channel URI. REG-LOCATION carries approved functional locations, which is
    /// operations rather than engineering: the same BOD on the same iTwin is a
    /// different channel depending on whether a steward has accepted it.
    /// </summary>
    public string Domain { get; set; } = "operations";

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
    /// Topics the publication is posted under.
    ///
    /// Form is oiie:{scenario}/ccom:{bod}. The scenario is in the topic because
    /// the same BOD means different things at different points in a journey:
    /// SyncSegments from ENG is a design proposal, whereas SyncSegments from
    /// REG-LOCATION is an approved location, and a subscriber wanting only one of
    /// those could not otherwise tell them apart.
    /// </summary>
    public string[] Topics { get; set; } = ["oiie:sc01/ccom:SyncSegments"];

    /// <summary>
    /// The iTwin the channel URI is built from. Required before anything can be
    /// published, because the federation id is the channel's identity.
    /// </summary>
    public Guid IModelId { get; set; }

    // ---- Downstream: CIR ---------------------------------------------------

    /// <summary>Root of the CIR function app, e.g. https://host/api.</summary>
    public string? CirBaseUrl { get; set; }

    /// <summary>Function key for CIR, sent as x-functions-key.</summary>
    public string? CirApiKey { get; set; }

    /// <summary>
    /// Whether an approved tag is registered in CIR as well as published.
    ///
    /// Separable from publication on purpose: registering an identity and
    /// announcing a change are different acts, and a deployment without a CIR
    /// yet should still be able to publish.
    /// </summary>
    public bool RegisterInCir { get; set; } = true;

    // ---- Engine state ------------------------------------------------------

    /// <summary>Container holding the engine's state document.</summary>
    public string StateContainer { get; set; } = "reglocationengine";

    /// <summary>Blob holding the set of tags already published.</summary>
    public string StateBlob { get; set; } = "reglocation-state.json";

    /// <summary>
    /// Ceiling on tags handled in one reconciling sweep, so a first run against
    /// an established registry does not try to publish all of it at once.
    /// </summary>
    public int MaxTagsPerSweep { get; set; } = 50;

    /// <summary>
    /// The logical identifier REG-LOCATION publishes under, used to build the
    /// CCOM InfoSource. This is what a receiver sees as the origin of the data,
    /// and it is REG-LOCATION rather than ENG even though the content began
    /// there: the approved location is this registry's assertion, not the
    /// design tool's.
    /// </summary>
    public string SourceId { get; set; } = "REG-LOCATION";

    /// <summary>The OAGIS ApplicationArea sender LogicalID.</summary>
    public string LogicalId { get; set; } = "REG-LOCATION";

    /// <summary>
    /// The publication channel for an iTwin, per the OIIE channel naming
    /// convention:
    ///
    ///     /{enterprise}/{itwin-federation-id}/{domain}/{type}
    ///
    /// The federation id rather than a name, because names change: a corridor
    /// renamed or a project re-scoped would otherwise break every subscription
    /// pointing at it. Derived here rather than configured so the convention is
    /// expressed once; a URI pasted into settings is a copy that cannot be
    /// corrected centrally.
    /// </summary>
    public string ChannelUriFor(Guid iTwinFederationId) =>
        !string.IsNullOrWhiteSpace(ChannelUriOverride)
            ? ChannelUriOverride
            : $"/{Enterprise}/{iTwinFederationId:D}/{Domain}/publication";
}
