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
    /// On by default, for the same reason as EngEngine: this shipped off so a
    /// fresh deployment would not reach out against configuration nobody had
    /// filled in, but a disabled engine reports success while reconciling
    /// nothing, and that silence proved more expensive to diagnose than a
    /// connection error that names itself.
    ///
    /// This gates the reconciling sweep. The webhook stays live regardless,
    /// because a notification that arrives and is silently dropped is worse than
    /// one that is refused.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Root of the sandbox API, e.g. https://host, from which this engine reads
    /// the channel topology. Optional: unset, the engine uses the channels
    /// derived from its own settings, exactly as it did before topology existed.
    /// </summary>
    public string? SandboxBaseUrl { get; set; }

    /// <summary>
    /// This engine's participant id, used to find its own rows in the topology.
    /// Matches the personality pack directory name.
    /// </summary>
    public string ParticipantId { get; set; } = "reg-location";

    /// <summary>
    /// The scenario whose topology this engine subscribes under. REG-LOCATION
    /// receives the design release, which is SC01. Its own publications on
    /// approval belong to SC02, and are looked up separately.
    /// </summary>
    public string ScenarioId { get; set; } = "sc01";

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
    /// Optional. Pins every outbound publication to one iTwin's channel; left
    /// empty, each approved tag is published onto the channel of the site its
    /// scope names.
    ///
    /// No longer required. The site is a property of the tag -- tag names scope,
    /// scope carries the site GUID -- so deriving it is both more accurate and
    /// immune to a registry reset, which is what made a configured value here
    /// stop the outbound leg dead once it went stale.
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
    /// should not silence the other. On by default: this is the leg that
    /// receives ENG's proposals, so off means the scenario stops at the bus.
    /// </summary>
    public bool IngestEnabled { get; set; } = true;

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

    /// <summary>
    /// The scope inbound proposals are registered in when the sender named no
    /// registration site.
    ///
    /// A fallback only. A segment that carries a RegistrationSite is filed in the
    /// scope REG-LOCATION holds for that site, because the sender knows which
    /// plant a location belongs to and this setting does not: one engine serves
    /// every iModel under every twin it subscribes to, so a configured scope
    /// would gather several plants' locations into one and no later correction
    /// could tell them apart again.
    /// </summary>
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
    /// Maps the common RDL class key onto a REG-LOCATION class_id.
    ///
    /// Per DR-030 SegmentType carries a governed RDL key rather than the
    /// sender's own vocabulary, so these keys are RDL codes, not ENG EC class
    /// names. That is what lets one map serve every publisher instead of one
    /// map per publisher.
    ///
    /// Still configuration rather than a join against class_objects.code, and
    /// deliberately so for now: REG-LOCATION holds only a subset of the
    /// library, and the gap between "the key is unknown here" and "the key does
    /// not exist" is the thing graceful degradation is built on. Making it a
    /// lookup would collapse that distinction into a missing row. The register
    /// notes this can become an ordinary lookup once RDL resolution is live.
    ///
    /// Keyed case-insensitively; the key's casing is the library's business.
    /// </summary>
    public Dictionary<string, int> InboundClassMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // Identity mapping, and worth writing down rather than assuming: both
        // sides took the name from the same source, so the correspondence looks
        // free. It is not -- it is a decision that these two happen to coincide,
        // and recording it keeps the mapping step visible for the day a key is
        // renamed on one side only.
        ["rdl:Streetlight"] = 1703,

        // Retained so a publisher that has not yet moved to RDL keys still
        // lands somewhere sensible instead of falling through to the fallback.
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
    ///
    /// On by default. Its channel is enterprise-level, so unlike the segment
    /// leg it can run before any iTwin exists.
    /// </summary>
    public bool SitesIngestEnabled { get; set; } = true;

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

    /// <summary>
    /// The TRN a site's Item is created under. See SiteNamespaceId.
    ///
    /// Zero, because that is the one transaction row bootstrap.sql guarantees --
    /// EIS requires a starting transaction and creates only trn_id 0. items.trn_id
    /// carries a foreign key to it, so any other default is a write that fails on
    /// referential integrity rather than a value that means something.
    /// </summary>
    public int SiteTrnId { get; set; } = 0;

    /// <summary>
    /// The value written to items.item_type for a site type.
    ///
    /// A constant rather than the iTwin's type string. The type string is the
    /// item's identity -- it goes in code and description -- whereas item_type
    /// classifies what kind of item the row is, and every site type is the same
    /// kind. Putting 'Highway' here would conflate the two and leave nothing
    /// distinguishing a site type from a piece of equipment.
    ///
    /// A single character, because items.item_type is CHAR(1) following the EIS
    /// convention -- 'U' is the unit/site kind. A longer word is not a nicer
    /// label here, it is a write that the database rejects outright.
    /// </summary>
    public string SiteItemType { get; set; } = "U";

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

    // ---- Class cross-references in CIR (the inbound leg) -------------------

    /// <summary>
    /// Check, and if necessary create, the cross-reference between the governed
    /// RDL class and this registry's own class_id.
    ///
    /// The mirror of ENG's outbound registration. ENG records that its
    /// <c>ENG.Streetlight</c> means the RDL class whose GUID is X; this records
    /// that REG-LOCATION's <c>class_id 1703</c> means the same X. Together they
    /// make CIR the place the correspondence lives, so a participant can ask
    /// what a class means to somebody else rather than keeping a private map.
    ///
    /// On by default for the prototype. In a provisioned production system
    /// both sides are pre-loaded out-of-band and this finds nothing to do.
    /// </summary>
    public bool RegisterClassIdentityInCir { get; set; } = true;

    /// <summary>
    /// The CIR registry holding class cross-references. Defaults to the
    /// enterprise, matching the registries this engine already writes.
    /// </summary>
    public string? ClassRegistryId { get; set; }

    /// <summary>
    /// The CIR category holding class cross-references. Must match what ENG
    /// writes: the whole point is that both participants' entries sit in one
    /// category, so a reader can see the correspondence between them.
    /// </summary>
    public string ClassCategoryId { get; set; } = "RDL-CLASS";

    /// <summary>
    /// How long a checked class cross-reference is kept before CIR is asked
    /// again. Reference data changes on a governance timescale, and an ingest
    /// drain handles many segments of few classes.
    /// </summary>
    public TimeSpan ClassCacheDuration { get; set; } = TimeSpan.FromMinutes(30);

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
