using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimHost.Application.Topology;

/// <summary>
/// Loads scenario topology files from the Topology directory.
///
/// Mirrors <see cref="Participants.PersonalityLoader"/> deliberately: same
/// deserialiser settings, same enumerate-and-validate shape. Two loaders that
/// behave differently over the same kind of file is a trap for whoever adds the
/// third one.
///
/// Every *.yaml in the directory is loaded, so a new scenario is a new file and
/// nothing else — no registration list to update, which is the point.
/// </summary>
public static class TopologyLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<TopologyConfig> LoadAll(string topologyRoot)
    {
        // Absence is tolerated where a missing personalities directory is not.
        // A deployment with no topology files still routes messages, because the
        // engines keep their configured defaults as a fallback; failing startup
        // here would take down a host over configuration it can run without.
        if (!Directory.Exists(topologyRoot))
        {
            return [];
        }

        var configs = new List<TopologyConfig>();

        foreach (var file in Directory.EnumerateFiles(
                     topologyRoot, "*.yaml", SearchOption.AllDirectories))
        {
            configs.Add(Load(file));
        }

        var duplicates = configs
            .GroupBy(c => c.ScenarioId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate scenario ids in topology: {string.Join(", ", duplicates)}");
        }

        // Ordered so the UI and any listing present scenarios in journey order
        // without each caller having to know that ordinal is the sort key.
        return [.. configs.OrderBy(c => c.Scenario)];
    }

    public static TopologyConfig Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var config = Deserializer.Deserialize<TopologyConfig>(yaml)
            ?? throw new InvalidOperationException($"Empty topology file: {path}");

        if (string.IsNullOrWhiteSpace(config.ScenarioId))
        {
            throw new InvalidOperationException($"scenarioId is required: {path}");
        }

        // A channel with no publisher cannot be provisioned or reasoned about,
        // and the failure it causes surfaces far from here — as a subscriber
        // that never receives anything. Rejecting it at load keeps the
        // diagnosis at the file that caused it.
        foreach (var channel in config.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel.Uri))
            {
                throw new InvalidOperationException(
                    $"Channel uri is required in {path} (scenario {config.ScenarioId})");
            }

            if (string.IsNullOrWhiteSpace(channel.Publisher))
            {
                throw new InvalidOperationException(
                    $"Channel {channel.Uri} has no publisher in {path}");
            }
        }

        return config;
    }
}
