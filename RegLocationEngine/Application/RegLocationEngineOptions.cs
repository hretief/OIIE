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
    ///
    /// An iTwin federation id, NOT an iModel id. An iModel is an ENG-internal
    /// container -- elements and named versions live in one, and REG-LOCATION
    /// sees neither. What it sees are CCOM Segments on a channel keyed by the
    /// iTwin, per /{enterprise}/{itwin-federation-id}/{domain}/{type}.
    /// </summary>
    public Guid ITwinFederationId { get; set; }

    /// <summary>
    /// Former name of <see cref="ITwinFederationId"/>, kept so existing
    /// RegLocationEngine__IModelId settings keep binding.
    ///
    /// The old name was wrong in a way that failed silently: supplying an actual
    /// iModel id yields a well-formed channel URI that nobody publishes to, so
    /// the engine polls forever and reports no error. Setting either name works;
    /// the new one wins if both are present.
    /// </summary>
    [Obsolete("Renamed to ITwinFederationId: the value is an iTwin federation id, not an iModel id.")]
    public Guid IModelId
    {
        get => ITwinFederationId;
        set
        {
            if (ITwinFederationId == Guid.Empty)
            {
                ITwinFederationId = value;
            }
        }
    }

    // ---- Upstream: the inbound leg from ENG --------------------------------

    /// <summary>
    /// Whether the engine consumes SyncSegments from ENG.
    ///
    /// Separate from <see cref="Enabled"/> because the two legs fail
    /// independently: a deployment may want to receive proposals long before it
    /// has a CIR to register approvals in, and a broker outage on one channel
    /// should not silence the other.
    /// </summary>
    public bool IngestEnabled { get; set; }

    /// <summary>
    /// The domain of the channel ENG publishes on.
    ///
    /// Deliberately not <see cref="Domain"/>. That one is this engine's own
    /// outbound channel, and the two are different by design: ENG publishes a
    /// design proposal on the engineering channel, and REG-LOCATION publishes an
    /// approved location on the operations channel. Subscribing on the outbound
    /// domain would mean listening to ourselves and never hearing ENG at all --
    /// a mistake that produces silence rather than an error.
    /// </summary>
    public string InboundDomain { get; set; } = "engineering";

    /// <summary>Overrides the derived inbound channel URI. See ChannelUriOverride.</summary>
    public string? InboundChannelUriOverride { get; set; }

    /// <summary>
    /// Topics subscribed to on the inbound channel. The same BOD as the
    /// outbound topic, because it is the same scenario seen from the other side.
    /// </summary>
    public string[] InboundTopics { get; set; } = ["oiie:sc01/ccom:SyncSegments"];

    /// <summary>
    /// The item every inbound proposal is filed under.
    ///
    /// Bootstrapped rather than derived: tags.item_id is NOT NULL and an item is
    /// the thing a tag is a revision of, but a CCOM Segment carries no item --
    /// that context arrives as a Site/SiteType in a different message this leg
    /// does not handle. Configured, so the emulation is honest about which part
    /// of the registry it is standing in for.
    /// </summary>
    public int InboundItemId { get; set; } = 1;

    /// <summary>The scope inbound proposals are registered in. Bootstrapped, as with the item.</summary>
    public int InboundScopeId { get; set; } = 1;

    /// <summary>
    /// The revision an inbound proposal enters at.
    ///
    /// Zero, and not the revision ENG happens to hold: a proposal has not been
    /// through a revision cycle in *this* registry, and inheriting a foreign
    /// system's numbering would assert a history REG-LOCATION cannot vouch for.
    /// </summary>
    public int InboundRevision { get; set; }

    /// <summary>
    /// Maps the sender's class name onto a REG-LOCATION class_id.
    ///
    /// This has to be configuration rather than a lookup, and the reason is
    /// worth stating: ENG names its EC class in SegmentType/IDInInfoSource
    /// (e.g. "Functional:FunctionalComponentElement"), while REG-LOCATION's
    /// class_objects table has no name column at all and its seeded classes have
    /// null GUIDs. The two sides currently share no value that could be joined
    /// on, so the correspondence between one system's vocabulary and the other's
    /// is a decision somebody has to make and record. Better here, where it can
    /// be read and changed, than hidden in a hash.
    ///
    /// Keyed case-insensitively; the class name's casing is the sender's business.
    /// </summary>
    public Dictionary<string, int> InboundClassMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Functional:FunctionalComponentElement"] = 1001,
        ["Functional:FunctionalElement"] = 1001
    };

    /// <summary>
    /// The class used when the sender's class is absent or unmapped.
    ///
    /// A fallback rather than a rejection: an unrecognised class is a gap in the
    /// mapping table, not a defect in the proposal, and dropping the segment
    /// would hide a location from the steward who is the one person able to
    /// notice the mapping is wrong. It is logged at warning so the gap is
    /// visible without being fatal.
    /// </summary>
    public int InboundFallbackClassId { get; set; } = 1001;

    // ---- Upstream: the sites leg ------------------------------------------

    /// <summary>
    /// Whether the engine consumes SyncSites from ENG.
    ///
    /// Its own switch, separate from <see cref="IngestEnabled"/>, because the
    /// two legs are not merely independent -- they are ordered. Sites must be
    /// ingested before segments have a scope to land in, so a deployment being
    /// bootstrapped may want this leg on while the segment leg is still off.
    /// </summary>
    public bool SitesIngestEnabled { get; set; }

    /// <summary>
    /// The enterprise channel SyncSites arrives on, matching what EngEngine
    /// publishes to.
    ///
    /// Not derived from an iTwin id like every other channel here, and it
    /// cannot be: this is the message that brings an iTwin context into being,
    /// so there is no federation id to name the channel with until after it has
    /// been processed. The literal 'enterprise' sits in that position instead.
    /// </summary>
    public string? SitesChannelUriOverride { get; set; }

    /// <summary>Topics subscribed to on the enterprise sites channel.</summary>
    public string[] SitesTopics { get; set; } = ["oiie/ccom:SyncSites"];

    /// <summary>
    /// The namespace a site's Item is created in.
    ///
    /// Configured because the BOD does not carry it and cannot: a namespace is
    /// how this registry partitions its own identifiers, and a sender has no
    /// business asserting one. The same applies to the unit and TRN below.
    /// </summary>
    public int SiteNamespaceId { get; set; } = 1;

    /// <summary>The unit a site's Item is created under. See SiteNamespaceId.</summary>
    public int SiteUnitId { get; set; } = 1;

    /// <summary>The TRN a site's Item is created under. See SiteNamespaceId.</summary>
    public int SiteTrnId { get; set; } = 1;

    /// <summary>
    /// The value written to items.item_type for a site type.
    ///
    /// A constant rather than the iTwin's type string. The type string is the
    /// item's identity -- it goes in code and description -- whereas item_type
    /// classifies what kind of item the row is, and every site type is the same
    /// kind. Putting 'Highway' here would conflate the two and leave nothing
    /// distinguishing a site type from a piece of equipment.
    /// </summary>
    public string SiteItemType { get; set; } = "Site";

    /// <summary>
    /// The channel SyncSites is consumed from:
    ///
    ///     /{enterprise}/enterprise/sites/publication
    /// </summary>
    public string SitesChannelUri =>
        !string.IsNullOrWhiteSpace(SitesChannelUriOverride)
            ? SitesChannelUriOverride
            : $"/{Enterprise}/enterprise/sites/publication";

    /// <summary>
    /// Ceiling on publications drained in one poll, so a backlog is worked
    /// through in bounded batches rather than in one unbounded run.
    /// </summary>
    public int MaxMessagesPerPoll { get; set; } = 25;

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

    /// <summary>
    /// The channel this engine subscribes to for proposals from ENG. Same
    /// convention, different domain -- see <see cref="InboundDomain"/>.
    /// </summary>
    public string InboundChannelUriFor(Guid iTwinFederationId) =>
        !string.IsNullOrWhiteSpace(InboundChannelUriOverride)
            ? InboundChannelUriOverride
            : $"/{Enterprise}/{iTwinFederationId:D}/{InboundDomain}/publication";

    /// <summary>
    /// The REG-LOCATION class for a sender's class name, falling back when it is
    /// absent or unmapped. The out flag lets the caller log the gap without
    /// having to repeat the lookup.
    /// </summary>
    public int ResolveClassId(string? senderClassName, out bool mapped)
    {
        if (!string.IsNullOrWhiteSpace(senderClassName)
            && InboundClassMap.TryGetValue(senderClassName, out var classId))
        {
            mapped = true;
            return classId;
        }

        mapped = false;
        return InboundFallbackClassId;
    }
}
