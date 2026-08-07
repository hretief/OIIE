using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimHost.Application.Participants;

/// <summary>
/// Loads personality.yaml files from the Personalities directory. Configuration
/// is files in git rather than tables, so a scenario is reproducible from a
/// commit hash — which is what makes CI results mean anything (spec §6.7).
/// </summary>
public static class PersonalityLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static IReadOnlyList<PersonalityConfig> LoadAll(string personalitiesRoot)
    {
        if (!Directory.Exists(personalitiesRoot))
        {
            throw new DirectoryNotFoundException(
                $"Personalities directory not found: {personalitiesRoot}");
        }

        var configs = new List<PersonalityConfig>();

        foreach (var file in Directory.EnumerateFiles(
                     personalitiesRoot, "personality.yaml", SearchOption.AllDirectories))
        {
            configs.Add(Load(file));
        }

        var duplicates = configs
            .GroupBy(c => c.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate participant ids: {string.Join(", ", duplicates)}");
        }

        return configs;
    }

    public static PersonalityConfig Load(string path)
    {
        var yaml = File.ReadAllText(path);
        var config = Deserializer.Deserialize<PersonalityConfig>(yaml)
            ?? throw new InvalidOperationException($"Empty personality file: {path}");

        if (string.IsNullOrWhiteSpace(config.ParticipantId))
        {
            throw new InvalidOperationException($"participantId is required: {path}");
        }

        return config;
    }
}
