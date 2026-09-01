namespace MmsEngine.Application;

/// <summary>
/// Configuration for the MMS integration engine.
///
/// Narrower than RegLocationEngineOptions because this engine has one leg. MMS
/// publishes nothing and proposes nothing about sites: it is told what plants
/// exist and records them so work can later be raised against them. There is no
/// outbound channel, no sweep schedule and no approval gate, so none of that
/// appears here.
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

    /// <summary>Whether the engine consumes SyncSites from ENG.</summary>
    public bool SitesIngestEnabled { get; set; }

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
}
