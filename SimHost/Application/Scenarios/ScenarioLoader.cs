using YamlDotNet.RepresentationModel;

namespace SimHost.Application.Scenarios;

/// <summary>
/// Raised when a scenario file cannot be loaded. Carries every problem found rather
/// than only the first, so a malformed file is corrected in one pass instead of one
/// error per edit-run cycle.
/// </summary>
public sealed class ScenarioLoadException : Exception
{
    public ScenarioLoadException(string path, IReadOnlyList<string> errors)
        : base($"{System.IO.Path.GetFileName(path)} has {errors.Count} problem(s):{Environment.NewLine}" +
               string.Join(Environment.NewLine, errors.Select(e => "  " + e)))
    {
        Path = path;
        Errors = errors;
    }

    public string Path { get; }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Reads scenario YAML into <see cref="ScenarioDefinition"/> (spec §11.1).
///
/// Uses the representation model rather than object deserialisation for two reasons:
/// an item's shape is known only by which key is present, and every node carries a
/// source line — so a mistake is reported as "uc01-handover.yaml line 27" rather than
/// as a deserialisation error naming a C# property the author never saw.
/// </summary>
public sealed class ScenarioLoader
{
    private static readonly HashSet<string> StepKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "at", "action", "assert", "args", "within"
    };

    public IReadOnlyList<ScenarioDefinition> LoadAll(string scenariosRoot)
    {
        if (!Directory.Exists(scenariosRoot))
        {
            throw new DirectoryNotFoundException($"Scenarios directory not found: {scenariosRoot}");
        }

        var definitions = Directory
            .EnumerateFiles(scenariosRoot, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(Load)
            .ToList();

        var duplicates = definitions
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate scenario ids: {string.Join(", ", duplicates)}");
        }

        return definitions;
    }

    public ScenarioDefinition Load(string path)
    {
        var errors = new List<string>();

        var yaml = new YamlStream();
        using (var reader = new StreamReader(path))
        {
            yaml.Load(reader);
        }

        if (yaml.Documents.Count == 0 ||
            yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new ScenarioLoadException(path, ["the file is empty or is not a YAML mapping"]);
        }

        var id = Scalar(root, "id")
                 ?? Path.GetFileNameWithoutExtension(path);

        var name = Scalar(root, "name");

        var participants = Sequence(root, "participants")
            .Select(node => (node as YamlScalarNode)?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        var setup = ReadSetup(root, errors);
        var items = ReadItems(root, errors);

        if (items.Count == 0)
        {
            errors.Add("no steps: the scenario would pass without doing anything");
        }

        if (errors.Count > 0)
        {
            throw new ScenarioLoadException(path, errors);
        }

        return new ScenarioDefinition
        {
            Id = id,
            Name = name,
            Participants = participants,
            Setup = setup,
            Items = items
        };
    }

    /// <summary>
    /// Checks names against what the engine can actually run.
    ///
    /// Separate from <see cref="Load"/> because the registries are composed after the
    /// files are read. Worth doing at all because an unknown action is otherwise
    /// discovered mid-run, after a reset has already destroyed the state that would
    /// let the run be retried cheaply.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        ScenarioDefinition definition,
        IReadOnlySet<string> knownParticipants,
        IReadOnlySet<string> knownActions,
        IReadOnlySet<string> knownAssertions)
    {
        var errors = new List<string>();

        foreach (var participant in definition.Participants)
        {
            if (!knownParticipants.Contains(participant))
            {
                errors.Add($"participants: '{participant}' is not a known participant");
            }
        }

        foreach (var item in definition.Items)
        {
            var where = $"line {item.Line}";

            if (item.At is { } at && !knownParticipants.Contains(at))
            {
                errors.Add($"{where}: at '{at}' is not a known participant");
            }

            if (item.IsAssertion)
            {
                if (!knownAssertions.Contains(item.Assert!))
                {
                    errors.Add($"{where}: assert '{item.Assert}' is not in the assertion vocabulary");
                }
            }
            else if (item.Action is null)
            {
                errors.Add($"{where}: item has neither an action nor an assert");
            }
            else if (!knownActions.Contains(item.Action))
            {
                errors.Add($"{where}: action '{item.Action}' is not registered");
            }
        }

        foreach (var channel in definition.Setup.Channels)
        {
            foreach (var subscriber in channel.Subscribers)
            {
                if (!knownParticipants.Contains(subscriber))
                {
                    errors.Add(
                        $"setup.channels {channel.Uri}: subscriber '{subscriber}' is not a known participant");
                }
            }
        }

        return errors;
    }

    private static ScenarioSetup ReadSetup(YamlMappingNode root, List<string> errors)
    {
        if (!TryGet(root, "setup", out var node) || node is not YamlMappingNode setup)
        {
            return new ScenarioSetup();
        }

        var reset = false;
        if (Scalar(setup, "reset") is { } resetText && !bool.TryParse(resetText, out reset))
        {
            errors.Add($"setup.reset: '{resetText}' is not true or false");
        }

        var channels = new List<ScenarioChannel>();

        foreach (var entry in Sequence(setup, "channels"))
        {
            if (entry is not YamlMappingNode channel)
            {
                errors.Add($"line {entry.Start.Line}: setup.channels entry is not a mapping");
                continue;
            }

            var uri = Scalar(channel, "uri");

            if (string.IsNullOrWhiteSpace(uri))
            {
                errors.Add($"line {entry.Start.Line}: setup.channels entry has no uri");
                continue;
            }

            channels.Add(new ScenarioChannel
            {
                Uri = uri,
                Type = Scalar(channel, "type") ?? "Publication",
                Subscribers = Sequence(channel, "subscribers")
                    .OfType<YamlScalarNode>()
                    .Select(s => s.Value)
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .ToList()
            });
        }

        return new ScenarioSetup { Reset = reset, Channels = channels };
    }

    private static List<ScenarioItem> ReadItems(YamlMappingNode root, List<string> errors)
    {
        var items = new List<ScenarioItem>();
        var ordinal = 0;

        foreach (var entry in Sequence(root, "steps"))
        {
            if (entry is not YamlMappingNode item)
            {
                errors.Add($"line {entry.Start.Line}: steps entry is not a mapping");
                continue;
            }

            var line = entry.Start.Line;
            var action = Scalar(item, "action");
            var assert = Scalar(item, "assert");

            if (action is null && assert is null)
            {
                errors.Add($"line {line}: item has neither an action nor an assert");
                continue;
            }

            if (action is not null && assert is not null)
            {
                errors.Add($"line {line}: item has both an action and an assert");
                continue;
            }

            TimeSpan? within = null;

            if (Scalar(item, "within") is { } withinText)
            {
                if (ScenarioDuration.TryParse(withinText, out var parsed))
                {
                    within = parsed;
                }
                else
                {
                    errors.Add(
                        $"line {line}: within '{withinText}' needs a unit, such as 30s or 2m");
                }
            }

            items.Add(new ScenarioItem
            {
                Ordinal = ++ordinal,
                Line = (int)line,
                StepId = Scalar(item, "id"),
                At = Scalar(item, "at"),
                Action = action,
                Assert = assert,
                Within = within,
                Args = ReadArgs(item)
            });
        }

        return items;
    }

    /// <summary>
    /// Collects an item's arguments from both places they can appear: an explicit
    /// <c>args</c> mapping, and any key that is not part of the item's own grammar.
    /// The spec writes assertion arguments inline (<c>channel:</c>, <c>noun:</c>) but
    /// action arguments nested under <c>args:</c>, and both forms have to work.
    /// </summary>
    private static Dictionary<string, object?> ReadArgs(YamlMappingNode item)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in item.Children)
        {
            if (key is not YamlScalarNode { Value: { } name } || StepKeys.Contains(name))
            {
                continue;
            }

            args[name] = Convert(value);
        }

        if (TryGet(item, "args", out var argsNode) && argsNode is YamlMappingNode mapping)
        {
            foreach (var (key, value) in mapping.Children)
            {
                if (key is YamlScalarNode { Value: { } name })
                {
                    args[name] = Convert(value);
                }
            }
        }

        return args;
    }

    /// <summary>
    /// Scalars stay strings. YAML's implicit typing would turn a tag number such as
    /// <c>101</c> into an integer and <c>N</c> into false, and these values are
    /// compared against database columns holding text.
    /// </summary>
    private static object? Convert(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value,
        YamlSequenceNode sequence => sequence.Children.Select(Convert).ToList(),
        YamlMappingNode mapping => mapping.Children
            .Where(c => c.Key is YamlScalarNode { Value: not null })
            .ToDictionary(
                c => ((YamlScalarNode)c.Key).Value!,
                c => Convert(c.Value),
                StringComparer.OrdinalIgnoreCase),
        _ => null
    };

    private static bool TryGet(YamlMappingNode node, string key, out YamlNode value)
    {
        foreach (var (candidate, child) in node.Children)
        {
            if (candidate is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                value = child;
                return true;
            }
        }

        value = null!;
        return false;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        TryGet(node, key, out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static IEnumerable<YamlNode> Sequence(YamlMappingNode node, string key) =>
        TryGet(node, key, out var value) && value is YamlSequenceNode sequence
            ? sequence.Children
            : [];
}
