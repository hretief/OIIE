using System.Globalization;

namespace SimHost.Application.Scenarios;

/// <summary>
/// A scenario file from <c>SimHost/Scenarios</c> (spec §11.1).
///
/// Configuration is files in git rather than rows in a table, so a run is
/// reproducible from a commit hash — the same reasoning as personality packs.
/// </summary>
public sealed class ScenarioDefinition
{
    /// <summary>File stem and identity, e.g. uc01-handover.</summary>
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>Participants the scenario touches. Validated against the registry at load.</summary>
    public IReadOnlyList<string> Participants { get; init; } = [];

    public ScenarioSetup Setup { get; init; } = new();

    /// <summary>
    /// Steps and assertions in file order, in one list.
    ///
    /// The spec interleaves them under a single <c>steps:</c> key, and that ordering is
    /// meaningful — an assertion belongs to the step above it. Splitting them into two
    /// collections would lose the association that makes a failure readable.
    /// </summary>
    public IReadOnlyList<ScenarioItem> Items { get; init; } = [];
}

public sealed class ScenarioSetup
{
    /// <summary>Run the full reset (spec §10) before the first step.</summary>
    public bool Reset { get; init; }

    /// <summary>
    /// Channels the scenario requires, with their subscribers.
    ///
    /// Declared rather than assumed because a subscription only receives what is
    /// published after it opens: if the engine does not confirm the subscriber is
    /// listening before the first publication, the run fails as "nothing arrived"
    /// and the cause is invisible.
    /// </summary>
    public IReadOnlyList<ScenarioChannel> Channels { get; init; } = [];
}

public sealed class ScenarioChannel
{
    public string Uri { get; init; } = string.Empty;

    /// <summary>Publication or Request, matching the ISBM channel type.</summary>
    public string Type { get; init; } = "Publication";

    public IReadOnlyList<string> Subscribers { get; init; } = [];
}

/// <summary>
/// One entry from the scenario's item list — either an action to perform or an
/// assertion to evaluate, distinguished by <see cref="IsAssertion"/>.
///
/// Modelled as one type rather than a discriminated hierarchy because YAML gives no
/// type tag: the shape is known only by which key is present, <c>action</c> or
/// <c>assert</c>. Resolving that at load keeps the ambiguity in one place.
/// </summary>
public sealed class ScenarioItem
{
    /// <summary>1-based position in the file, assigned at load. Used to order results.</summary>
    public int Ordinal { get; init; }

    /// <summary>Author-supplied id, e.g. s1. Absent on most assertions.</summary>
    public string? StepId { get; init; }

    /// <summary>Participant this runs against — the <c>at</c> key.</summary>
    public string? At { get; init; }

    /// <summary>Action name, when this item is a step.</summary>
    public string? Action { get; init; }

    /// <summary>Assertion name, when this item is an assertion.</summary>
    public string? Assert { get; init; }

    public bool IsAssertion => Assert is not null;

    /// <summary>
    /// How long a timed assertion may wait before failing — the <c>within</c> key.
    ///
    /// Null means evaluate once, immediately. That distinction matters: a
    /// <c>store_contains</c> with no wait asserts the state as it stands, while the
    /// same assertion with a wait tolerates the dispatcher not having caught up.
    /// </summary>
    public TimeSpan? Within { get; init; }

    /// <summary>
    /// Everything else on the item, verbatim. Both an action's <c>args</c> map and an
    /// assertion's own keys (<c>channel</c>, <c>verb</c>, <c>noun</c>, <c>entity</c>,
    /// <c>where</c>, <c>entries</c>) land here, so handlers read their own arguments
    /// and the model does not need a property per assertion in the vocabulary.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Args { get; init; }
        = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Source line, for error messages that point at the file.</summary>
    public int Line { get; init; }

    public string Describe() => IsAssertion
        ? $"assert {Assert}{(At is null ? "" : $" at {At}")}"
        : $"{Action}{(At is null ? "" : $" at {At}")}";
}

/// <summary>
/// Parses the spec's duration form — <c>30s</c>, <c>2m</c>, <c>500ms</c>.
///
/// A bare number is rejected rather than assumed to be seconds. <c>within: 30</c>
/// meaning thirty seconds in one file and thirty milliseconds in a reader's head is
/// the kind of ambiguity that produces an intermittent test nobody trusts.
/// </summary>
public static class ScenarioDuration
{
    public static TimeSpan Parse(string value)
    {
        if (TryParse(value, out var result))
        {
            return result;
        }

        throw new FormatException(
            $"Cannot parse duration '{value}'. Expected a number with a unit, " +
            "such as 500ms, 30s or 2m.");
    }

    public static bool TryParse(string? value, out TimeSpan result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        var (suffix, factor) = text.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
            ? ("ms", 1d)
            : text.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                ? ("s", 1000d)
                : text.EndsWith("m", StringComparison.OrdinalIgnoreCase)
                    ? ("m", 60_000d)
                    : (string.Empty, 0d);

        if (suffix.Length == 0)
        {
            return false;
        }

        var number = text[..^suffix.Length].Trim();

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0)
        {
            return false;
        }

        result = TimeSpan.FromMilliseconds(parsed * factor);
        return true;
    }
}
