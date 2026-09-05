namespace SimHost.Application.Topology;

/// <summary>
/// The channel topology for one scenario: who publishes, who subscribes, on
/// which channel, under which topics.
///
/// Centralised because the same channel URI was previously stated in three
/// places — the publisher's settings, the subscriber's settings, and the
/// scenario file that asserted on it. Three copies of a string that must match
/// exactly, with no check that they do. A subscriber left on a stale URI does
/// not fail: it waits on a channel nobody writes to, indefinitely and silently.
///
/// Files rather than tables, so topology is reproducible from a commit hash,
/// and read at runtime rather than embedded, so adding a participant is a
/// configuration change and a provisioning run — never a rebuild.
/// </summary>
public sealed class TopologyConfig
{
    /// <summary>
    /// Scenario key, matching the scenario segment of the topics below
    /// (oiie:{scenario}/ccom:{bod}) and the scenario file of the same name.
    /// </summary>
    public string ScenarioId { get; set; } = string.Empty;

    /// <summary>Human-readable name, for the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Ordinal within the use case. Establishes the order scenarios are
    /// presented in, and nothing more — it does not gate anything. Running SC02
    /// before SC01 is not blocked; it finds an empty queue and has nothing to
    /// approve, which is a clearer signal than a refusal would be.
    /// </summary>
    public int Scenario { get; set; }

    /// <summary>The use case this scenario belongs to.</summary>
    public string UseCase { get; set; } = string.Empty;

    /// <summary>How to tell the scenario has finished. See <see cref="CompletionRule"/>.</summary>
    public CompletionRule? Completion { get; set; }

    /// <summary>Channels this scenario runs on.</summary>
    public List<ChannelTopology> Channels { get; set; } = [];
}

/// <summary>
/// Names the participant-side evidence that a scenario completed.
///
/// Completion is observed, not recorded. There is no run log to consult and no
/// status field to advance: the scenario is done when the data is visible at
/// the receiving participant. A record of the run could disagree with the
/// participants — reporting success for data that was later reset — and the
/// participants are the ones telling the truth.
/// </summary>
public sealed class CompletionRule
{
    /// <summary>The participant to inspect.</summary>
    public string Participant { get; set; } = string.Empty;

    /// <summary>
    /// The collection at that participant whose non-emptiness proves arrival.
    /// </summary>
    public string Contains { get; set; } = string.Empty;

    /// <summary>What this signifies, shown to an operator.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// One channel, with the participants at either end of it.
/// </summary>
public sealed class ChannelTopology
{
    /// <summary>
    /// Channel URI, possibly containing {enterprise} and {iTwinId} placeholders.
    /// Resolve through <see cref="Resolve"/> rather than reading directly.
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>ISBM channel type: Publication or Request.</summary>
    public string Type { get; set; } = "Publication";

    /// <summary>The participant that posts to this channel.</summary>
    public string Publisher { get; set; } = string.Empty;

    /// <summary>
    /// Participants that read this channel.
    ///
    /// May name participants that are declared but not yet working. That is
    /// deliberate: an absent subscriber and a subscriber that received nothing
    /// are different findings, and only the second is a test result.
    /// </summary>
    public List<string> Subscribers { get; set; } = [];

    /// <summary>
    /// Topics the publication is posted under, in the form
    /// oiie:{scenario}/ccom:{bod}. Subscribers filter on these, so changing one
    /// strands whoever was listening for the old value.
    /// </summary>
    public List<string> Topics { get; set; } = [];

    /// <summary>What travels on this channel, shown to an operator.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>True when this channel is per-iTwin rather than enterprise-wide.</summary>
    public bool IsPerITwin => Uri.Contains("{iTwinId}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Substitutes the placeholders. The federation id is formatted "D" to match
    /// the engines' interpolation of the same convention; any other format
    /// produces a URI that differs from the one being published to, which the
    /// broker accepts without complaint.
    /// </summary>
    public string Resolve(string enterprise, Guid? iTwinFederationId = null)
    {
        var uri = Uri.Replace("{enterprise}", enterprise, StringComparison.OrdinalIgnoreCase);

        if (iTwinFederationId is { } id)
        {
            uri = uri.Replace("{iTwinId}", id.ToString("D"), StringComparison.OrdinalIgnoreCase);
        }

        return uri;
    }
}
