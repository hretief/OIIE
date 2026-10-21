namespace MmsEngine.Application;

/// <summary>
/// Configuration for the MMS integration engine.
///
/// MMS consumes sites and approved segments. It has no approval gate and
/// publishes no BODs; its native identifiers are written back into CIR.
/// </summary>
public sealed class MmsEngineOptions
{
    // ---- Upstream: MMS ----------------------------------------------------

    /// <summary>
    /// Where MmsProvider's REST API lives.
    ///
    /// Empty disables the ingest leg rather than failing it. The engine and the
    /// provider are separate hosts and either may be started first; treating a
    /// missing MMS as a configuration error would make ordinary local startup
    /// look broken.
    /// </summary>
    public string? MmsBaseUrl { get; set; }

    /// <summary>The Functions key for MmsProvider, if it requires one.</summary>
    public string? MmsApiKey { get; set; }

    // ---- Identity ---------------------------------------------------------

    /// <summary>
    /// The enterprise this engine belongs to, used to name the sites channel
    /// and as the owner of CIR entries.
    /// </summary>
    public string Enterprise { get; set; } = "acme";

    /// <summary>
    /// How MMS identifies itself in CIR. Its own key space, distinct from CMS's
    /// and REG-LOCATION's: all three may hold the same site, and the registry is
    /// what relates their identifiers.
    /// </summary>
    public string SourceId { get; set; } = "MMS";

    /// <summary>How MMS identifies itself on the bus.</summary>
    public string LogicalId { get; set; } = "MMS";

    // ---- Upstream: the sites leg ------------------------------------------

    /// <summary>
    /// Whether the engine consumes SyncSites from ENG. On by default, matching
    /// what the deploy script has always applied.
    /// </summary>
    public bool SitesIngestEnabled { get; set; } = true;

    /// <summary>
    /// The enterprise channel SyncSites arrives on, matching what EngEngine
    /// publishes to.
    ///
    /// Not derived from an iTwin id, and it cannot be: this is the message that
    /// brings an iTwin context into being, so there is no federation id to name
    /// the channel with until after it has been processed.
    /// </summary>
    public string? SitesChannelUriOverride { get; set; }

    /// <summary>Topics subscribed to on the enterprise sites channel.</summary>
    public string[] SitesTopics { get; set; } = ["oiie/ccom:SyncSites"];

    /// <summary>
    /// The channel SyncSites is consumed from:
    ///
    ///     /{enterprise}/enterprise/sites/publication
    ///
    /// The same channel CMS and REG-LOCATION read. Each subscriber opens its own
    /// session, so the broker delivers a copy to each rather than one consumer
    /// taking the message from the others.
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

    /// <summary>Consumes approved SC02 locations. Opt in after CIR and subscriptions are ready.</summary>
    public bool SegmentsIngestEnabled { get; set; }

    /// <summary>The approved-segment topic, distinct from ENG's design proposal topic.</summary>
    public string[] SegmentsTopics { get; set; } = ["oiie:sc02/ccom:SyncSegments"];

    /// <summary>Approved publications must identify this logical sender.</summary>
    public string ApprovedLogicalId { get; set; } = "REG-LOCATION";

    /// <summary>Native source of the approved location's CIR anchor.</summary>
    public string ApprovedSourceId { get; set; } = "REG-LOCATION";

    /// <summary>Optional single-site restriction for focused ingestion.</summary>
    public Guid ITwinFederationId { get; set; }

    public string SegmentsChannelUriFor(Guid site) =>
        $"/{Enterprise}/{site:D}/operations/publication";

    // ---- Downstream: CIR --------------------------------------------------

    /// <summary>
    /// Whether a site written to MMS is also registered in CIR.
    ///
    /// Registration is what lets another system resolve MMS's site identifier
    /// without knowing MMS's key space. Without it the site exists but is
    /// unreachable by anything that did not already hold its SiteID.
    /// </summary>
    public bool RegisterInCir { get; set; } = true;

    /// <summary>Where CIR lives. Empty leaves sites written but unregistered.</summary>
    public string? CirBaseUrl { get; set; }

    /// <summary>The Functions key for CIR, if it requires one.</summary>
    public string? CirApiKey { get; set; }

    // ---- Class dispatch ---------------------------------------------------

    /// <summary>
    /// Maps the common RDL class key onto the MMS table that receives it.
    ///
    /// MMS has no UAV schema: its table names are its classes, so per DR-030 an
    /// inbound class is a dispatch rather than a column value. Naming
    /// LIGHT_UNIT_INVENTORY selects a table, its columns, its primary key and
    /// its foreign keys.
    ///
    /// Table names, not an enum, because this is a map between two vocabularies
    /// and the MMS side of it is genuinely the physical schema. An enum would
    /// add a third vocabulary that neither the bus nor the database uses.
    /// </summary>
    public Dictionary<string, string> InboundRdlTableMap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rdl:Streetlight"] = "LIGHT_UNIT_INVENTORY"
    };

    /// <summary>
    /// The MMS table for an RDL key, or null when the key is unmapped.
    ///
    /// No fallback here, unlike REG-LOCATION's inbound class. REG-LOCATION can
    /// bind an unknown class at a parent because its classes are data in a
    /// column; MMS would have to pick a table, and writing a light unit into an
    /// arbitrary one is not degraded behaviour, it is corruption. An unmapped
    /// key means the caller must decline the segment.
    /// </summary>
    public string? ResolveTargetTable(string? rdlClassKey) =>
        !string.IsNullOrWhiteSpace(rdlClassKey)
            && InboundRdlTableMap.TryGetValue(rdlClassKey, out var table)
                ? table
                : null;
}
