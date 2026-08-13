using SimHost.Domain.Sandbox;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Scenarios;

/// <summary>
/// Starts a scenario run in the background and returns its id straight away.
///
/// The UI needs the id before the run finishes, because a run takes a minute or more
/// and the whole point of the detail page is watching it happen. Awaiting the runner
/// would leave the browser on a dead button for the duration and then arrive at an
/// already-finished view, which is the one thing live progress is meant to avoid.
///
/// The work is queued on the host's task pool rather than tied to the Blazor circuit:
/// a user who closes the tab should not cancel a run that CI or a colleague may be
/// watching, and <see cref="ScenarioRunContext"/> already serialises concurrent runs.
/// </summary>
public sealed class ScenarioLauncher(
    ScenarioCatalog catalog,
    ScenarioRunner runner,
    ISandboxDbContextFactory sandbox,
    SandboxResetService resets,
    ILogger<ScenarioLauncher> logger)
{
    /// <summary>
    /// Creates the run row, schedules execution, and returns the id to navigate to.
    /// </summary>
    /// <param name="baseUrl">
    /// This deployment's own address, used to honour <c>setup.reset</c>. The reset runs
    /// over HTTP against the admin endpoint that defines what a reset means.
    /// </param>
    /// <exception cref="ScenarioLoadException">The scenario file has errors.</exception>
    /// <exception cref="InvalidOperationException">A declared reset could not be run.</exception>
    public async Task<Guid> StartAsync(
        string scenarioId, string baseUrl, CancellationToken ct = default)
    {
        // Validated on this thread so an unusable scenario surfaces as an error on the
        // button rather than as a run that appears and immediately aborts.
        var definition = catalog.Require(scenarioId);

        // Before the run row exists, because a reset purges run history and would
        // otherwise delete the row this run is about to write into.
        if (await resets.ApplyAsync(definition, baseUrl, ct) is { Succeeded: false } failed)
        {
            throw new InvalidOperationException(
                $"{definition.Id} requires a reset, which failed: {failed.Summary} {failed.Detail}");
        }

        var run = new ScenarioRun
        {
            ScenarioId = definition.Id,
            Title = definition.Name,
            Mode = ScenarioRunMode.Demo
        };

        await using (var db = sandbox.Create())
        {
            db.ScenarioRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(
                    definition, ScenarioRunMode.Demo, seed: 0,
                    existingRunId: run.Id, ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                // RunAsync records its own failures; this catches only a failure to start
                // at all, which would otherwise be an unobserved task exception.
                logger.LogError(ex, "Background run {RunId} of {ScenarioId} failed to start.",
                    run.Id, definition.Id);
            }
        }, CancellationToken.None);

        return run.Id;
    }
}
