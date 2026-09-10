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
    /// Off by default.
    ///
    /// Validation posts requests at a channel someone has to have provisioned,
    /// and an engine that starts doing that on deployment produces errors that
    /// look like a fault rather than an absence -- the same reasoning that
    /// keeps <see cref="EngEngineOptions.Enabled"/> off.
    /// </summary>
    public bool Enabled { get; set; }

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
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(20);

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
