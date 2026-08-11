using SimHost.Application.Scenarios;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Guards step references.
///
/// The feature exists so a scenario can name a value it could not know when it was
/// written — an allocated code, a minted identity. The risk it carries is a silent
/// null: an unresolved reference that flows on into an action surfaces much later as
/// "no tag '' to relate from", which names neither the mistake nor where it was made.
/// These tests fix the resolution and, more importantly, the diagnostics.
/// </summary>
public class ScenarioStepReferenceTests
{
    private static ScenarioActionContext Context(
        Dictionary<string, object?> args,
        Dictionary<string, IReadOnlyDictionary<string, string?>>? outputs = null)
    {
        var item = new ScenarioItem
        {
            Ordinal = 1,
            At = "eng",
            Action = "relate_tags",
            Args = args
        };

        return new ScenarioActionContext(item, outputs);
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string?>> Allocated =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["allocate-valve"] = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["tagNumber"] = "P-001"
            }
        };

    [Fact]
    public void AStepResultIsReadableByName()
    {
        var context = Context([], Allocated);

        Assert.Equal("P-001", context.RequireFromStep("allocate-valve", "tagNumber"));
    }

    /// <summary>
    /// Payload fields are written in the casing of a C# property but referenced in the
    /// casing of a YAML argument, and the two do not match.
    /// </summary>
    [Fact]
    public void AFieldIsFoundRegardlessOfCasing()
    {
        var context = Context([], Allocated);

        Assert.Equal("P-001", context.RequireFromStep("allocate-valve", "TagNumber"));
    }

    [Fact]
    public void AnUnknownStepNamesTheStepsThatDoExist()
    {
        var context = Context([], Allocated);

        var error = Assert.Throws<ScenarioActionException>(
            () => context.RequireFromStep("allocate-pump", "tagNumber"));

        Assert.Contains("allocate-pump", error.Message, StringComparison.Ordinal);
        Assert.Contains("allocate-valve", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownFieldNamesTheFieldsThatDoExist()
    {
        var context = Context([], Allocated);

        var error = Assert.Throws<ScenarioActionException>(
            () => context.RequireFromStep("allocate-valve", "federationId"));

        Assert.Contains("federationId", error.Message, StringComparison.Ordinal);
        Assert.Contains("tagNumber", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reference made before the step it names has run is the ordering mistake this
    /// is most likely to catch, and it must not read as "the step produced nothing".
    /// </summary>
    [Fact]
    public void AReferenceWithNoStepsAtAllSaysSo()
    {
        var context = Context([]);

        var error = Assert.Throws<ScenarioActionException>(
            () => context.RequireFromStep("allocate-valve", "tagNumber"));

        Assert.Contains("no earlier step recorded a result", error.Message, StringComparison.Ordinal);
    }
}
