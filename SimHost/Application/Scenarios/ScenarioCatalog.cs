using SimHost.Application.Participants;

namespace SimHost.Application.Scenarios;

/// <summary>
/// The scenario files on disk, loaded and checked against what the engine can run.
///
/// Reads from disk on every call rather than caching. Scenario authoring is an edit
/// loop — write a file, run it, read the findings, correct it — and a cache would mean
/// every correction needed a restart to take effect. The files are small and the read
/// is dwarfed by the run itself.
/// </summary>
public sealed class ScenarioCatalog(
    ScenarioLoader loader,
    ScenarioActionRegistry actions,
    ScenarioAssertionRegistry assertions,
    ParticipantRegistry participants,
    IWebHostEnvironment environment,
    IConfiguration configuration)
{
    public string Root => configuration["Sandbox:ScenariosPath"]
        ?? Path.Combine(environment.ContentRootPath, "Scenarios");

    public IReadOnlyList<ScenarioDefinition> LoadAll() => loader.LoadAll(Root);

    /// <summary>
    /// Loads one scenario by id and refuses to return it unless the engine can run
    /// every action and assertion it names.
    ///
    /// Validation happens here, before a run starts, because a scenario with
    /// <c>setup.reset</c> destroys the state of the previous run on its first step: an
    /// unknown action discovered halfway through has already cost the evidence that
    /// would explain the failure.
    /// </summary>
    public ScenarioDefinition Require(string scenarioId)
    {
        var definition = LoadAll().FirstOrDefault(d =>
            string.Equals(d.Id, scenarioId, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException(
                $"No scenario '{scenarioId}' in {Root}.");

        var errors = Validate(definition);

        if (errors.Count > 0)
        {
            throw new ScenarioLoadException($"{definition.Id}.yaml", errors);
        }

        return definition;
    }

    public IReadOnlyList<string> Validate(ScenarioDefinition definition) =>
        ScenarioLoader.Validate(
            definition,
            participants.All.Select(p => p.ParticipantId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            actions.Names,
            assertions.Names);
}
