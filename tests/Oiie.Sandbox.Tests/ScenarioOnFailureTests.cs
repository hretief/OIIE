using SimHost.Application.Scenarios;
using SimHost.Domain.Sandbox;
using Xunit;

namespace SimHost.Tests;

/// <summary>
/// Guards the <c>on_failure</c> key.
///
/// The key exists so a scenario can record an observation that is genuinely optional
/// in a correct run without that observation failing the run. The risk it introduces
/// is silence: a key that quietly suppressed assertions would hide real regressions.
/// These tests fix the two properties that keep it honest — it must default to
/// failing, and it must only ever be spelled in ways the loader recognises.
/// </summary>
public class ScenarioOnFailureTests
{
    private static ScenarioDefinition Load(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"scn-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);

        try
        {
            return new ScenarioLoader().Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private const string Assertion = """
        id: t
        participants:
          - eng
        steps:
          - at: eng
            assert: message_received
            channel: /X
            verb: Sync
            noun: Segments
        """;

    [Fact]
    public void AnAssertionFailsByDefault()
    {
        var item = Assert.Single(Load(Assertion).Items);

        Assert.Equal(FindingSeverity.Fail, item.OnFailure);
    }

    [Fact]
    public void ConcernIsCarriedThrough()
    {
        var item = Assert.Single(Load(Assertion + "\n    on_failure: concern\n").Items);

        Assert.Equal(FindingSeverity.Concern, item.OnFailure);
    }

    /// <summary>
    /// The key is grammar, not an argument. Were it to fall through to the argument
    /// map it would reach assertions as an unrecognised criterion.
    /// </summary>
    [Fact]
    public void ConcernIsNotPassedOnAsAnArgument()
    {
        var item = Assert.Single(Load(Assertion + "\n    on_failure: concern\n").Items);

        Assert.False(item.Args.ContainsKey("on_failure"));
    }

    /// <summary>
    /// A misspelling must not silently read as "not fail". Downgrading on anything
    /// other than the one accepted word would turn a typo into a suppressed assertion.
    /// </summary>
    [Fact]
    public void AnUnknownSeverityIsRejected()
    {
        var error = Assert.Throws<ScenarioLoadException>(
            () => Load(Assertion + "\n    on_failure: ignore\n"));

        Assert.Contains("on_failure", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OnFailureIsRejectedOnAnAction()
    {
        var yaml = """
            id: t
            participants:
              - eng
            steps:
              - at: eng
                action: create_tag
                on_failure: concern
                args:
                  tagNumber: P-101
            """;

        var error = Assert.Throws<ScenarioLoadException>(() => Load(yaml));

        Assert.Contains("on_failure", error.Message, StringComparison.Ordinal);
    }
}
