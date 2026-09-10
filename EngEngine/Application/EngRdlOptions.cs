namespace EngEngine.Application;

/// <summary>
/// What ENG needs in order to ask RDL what it holds.
///
/// Separate from <see cref="EngEngineOptions"/>, which describes publishing.
/// This describes a question ENG asks about its own configuration, on a
/// different channel, in the opposite direction, and a deployment may
/// reasonably want one without the other.
///
/// Mirrors RdlEngine's RdlIsbmOptions on purpose: the two halves of one
/// conversation should be recognisable as such, and a matched pair of
/// deployments should need no configuration at all to find each other.
/// </summary>
public sealed class EngRdlOptions
{
    /// <summary>
    /// On by default.
    ///
    /// This shipped off so a fresh deployment would not post at a channel
    /// nobody had provisioned. It now defaults on because the RDL round trip
    /// stopped being merely advisory: registering a class cross-reference in
    /// CIR depends on it, since RDL is the only place the governed class GUID
    /// can come from. Off, the registration silently never happens.
    ///
    /// Failure remains non-fatal. An unreachable RDL leaves the map unverified
    /// and the mapping unregistered; it does not stop a drain.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Channel RDL reads GetTaxonomySet requests from.</summary>
    public string RequestChannelUri { get; set; } = "/OIIE/RDL/Request";

    /// <summary>
    /// Topics the request is posted under. RDL filters on these, so a value
    /// here that RDL does not listen for is silent non-delivery.
    ///
    /// Deliberately empty by default: configuration binding ADDS to an existing
    /// collection rather than replacing it, so a non-empty initialiser plus an
    /// EngRdl__Topics__0 setting yields a duplicated list.
    /// <see cref="EffectiveTopics"/> supplies the fallback instead.
    /// </summary>
    public List<string> Topics { get; set; } = [];

    /// <summary>Matches RdlIsbmOptions.DefaultTopic; the two must agree.</summary>
    public const string DefaultTopic = "OIIE:S35:V1.0/CCOM:GetTaxonomySet:R1.0";

    /// <summary>Configured topics, de-duplicated, falling back to the default.</summary>
    public IReadOnlyList<string> EffectiveTopics =>
        Topics.Count == 0 ? [DefaultTopic] : Topics.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// How long to wait for RDL's answer before giving up on this pass.
    ///
    /// Bounded because validation is advisory: a drain must not sit waiting on
    /// a provider that may not be running, when the markers it is holding are
    /// publishable either way.
    ///
    /// Must exceed RDL's drain interval, not merely its response time. RDL
    /// reads the request channel on a timer, so a request posted just after a
    /// tick waits nearly a full interval before anybody looks at it. At 20s
    /// against a 60s timer this timed out roughly four times in five and
    /// succeeded only when a drain happened to land just before a tick --
    /// which reads as an intermittent provider fault rather than the timing
    /// mismatch it is. 90s covers a full interval plus the fetch.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Gap between response polls while waiting.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a successfully fetched library is reused before being asked
    /// for again. The RDL library changes on a human timescale -- someone adds
    /// a class -- so re-fetching it every drain would be a request per poll
    /// interval to learn nothing.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a failed attempt is remembered before retrying.
    ///
    /// Much shorter than <see cref="CacheDuration"/>, because a failure is
    /// usually a provider that is not up yet rather than a settled answer --
    /// but not zero, or every drain would pay <see cref="ResponseTimeout"/>
    /// for as long as RDL stayed down.
    /// </summary>
    public TimeSpan FailureCacheDuration { get; set; } = TimeSpan.FromMinutes(5);
}
