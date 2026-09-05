namespace SimHost.Application.Topology;

/// <summary>
/// The loaded topology, queryable by scenario or participant.
///
/// Registered as a singleton alongside ParticipantRegistry. Exists so callers
/// ask questions rather than re-scan the list: "what does REG-LOCATION
/// subscribe to" is asked by the provisioner, the engines and the UI, and three
/// hand-rolled LINQ expressions over the same shape will eventually disagree
/// about case sensitivity.
/// </summary>
public sealed class TopologyRegistry(IReadOnlyList<TopologyConfig> scenarios)
{
    /// <summary>All scenarios, in journey order.</summary>
    public IReadOnlyList<TopologyConfig> Scenarios { get; } = scenarios;

    /// <summary>One scenario by id, or null when it is not declared.</summary>
    public TopologyConfig? Find(string scenarioId) =>
        Scenarios.FirstOrDefault(s =>
            string.Equals(s.ScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every channel across every scenario.</summary>
    public IEnumerable<ChannelTopology> AllChannels() =>
        Scenarios.SelectMany(s => s.Channels);

    /// <summary>
    /// Channels a participant publishes to. Spans scenarios, because a
    /// participant's role changes between them: REG-LOCATION subscribes in SC01
    /// and publishes in SC02.
    /// </summary>
    public IEnumerable<ChannelTopology> PublishedBy(string participantId) =>
        AllChannels().Where(c =>
            string.Equals(c.Publisher, participantId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Channels a participant subscribes to.</summary>
    public IEnumerable<ChannelTopology> SubscribedBy(string participantId) =>
        AllChannels().Where(c => c.Subscribers.Any(s =>
            string.Equals(s, participantId, StringComparison.OrdinalIgnoreCase)));
}
