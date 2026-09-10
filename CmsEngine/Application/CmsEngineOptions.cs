namespace CmsEngine.Application;

/// <summary>
/// Configuration for the CMS integration engine.
///
/// Narrower than RegLocationEngineOptions because this engine has one leg. CMS
/// publishes nothing and proposes nothing: it is told what sites exist and
/// records them. There is no outbound channel, no sweep schedule and no
/// approval gate, so none of that appears here.
/// </summary>
public sealed class CmsEngineOptions
{
    // ---- Upstream: CMS ----------------------------------------------------

    /// <summary>
    /// Where CmsProvider's REST API lives.
    ///
    /// Empty disables the ingest leg rather than failing it. The engine and the
    /// provider are separate hosts and either may be started first; treating a
    /// missing CMS as a configuration error would make ordinary local startup
    /// look broken.
    /// </summary>
    public string? CmsBaseUrl { get; set; }

    /// <summary>The Functions key for CmsProvider, if it requires one.</summary>
    public string? CmsApiKey { get; set; }

    // ---- Identity ---------------------------------------------------------

    /// <summary>
    /// The enterprise this engine belongs to, used to name the sites channel
    /// and as the owner of CIR entries.
    /// </summary>
    public string Enterprise { get; set; } = "acme";

    /// <summary>
    /// How CMS identifies itself in CIR. Its own key space, distinct from
    /// REG-LOCATION's: both may hold the same site, and the registry is what
    /// relates their two identifiers.
    /// </summary>
    public string SourceId { get; set; } = "CMS";

    /// <summary>How CMS identifies itself on the bus.</summary>
    public string LogicalId { get; set; } = "CMS";

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
    /// the channel with until after it has been processed. The literal
    /// 'enterprise' sits in that position instead.
    /// </summary>
    public string? SitesChannelUriOverride { get; set; }

    /// <summary>Topics subscribed to on the enterprise sites channel.</summary>
    public string[] SitesTopics { get; set; } = ["oiie/ccom:SyncSites"];

    /// <summary>
    /// The channel SyncSites is consumed from:
    ///
    ///     /{enterprise}/enterprise/sites/publication
    ///
    /// The same channel REG-LOCATION reads. Each subscriber opens its own
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
    /// Whether a site written to CMS is also registered in CIR.
    ///
    /// Registration is what lets another system resolve CMS's site identifier
    /// without knowing CMS's key space. Without it the site exists but is
    /// unreachable by anything that did not already hold its SiteID.
    /// </summary>
    public bool RegisterInCir { get; set; } = true;

    /// <summary>Where CIR lives. Empty leaves sites written but unregistered.</summary>
    public string? CirBaseUrl { get; set; }

    /// <summary>The Functions key for CIR, if it requires one.</summary>
    public string? CirApiKey { get; set; }
}
