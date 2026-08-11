using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oiie.Isbm.Client;
using SimHost.Domain.Sandbox;
using SimHost.Infrastructure.Isbm;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Scenarios;

/// <summary>Summary of a finished run, for the caller that asked for it.</summary>
public sealed record ScenarioRunSummary(
    Guid RunId,
    string ScenarioId,
    ScenarioRunState State,
    int Passed,
    int Concerns,
    int Failed,
    string? AbortReason,
    IReadOnlyList<AssertionResult> Findings);

/// <summary>
/// Executes one scenario file, in order, recording every step and assertion.
///
/// The engine is a faithful port of what testing/test-sandbox.ps1 does imperatively,
/// with one behavioural difference that matters: a failing assertion does not stop the
/// run. The PowerShell suite stops because a shell script has nowhere to put a partial
/// result; here the whole point of the orchestration tables is that a run reports every
/// finding at once. Stopping at the first failure means each CI cycle reveals exactly
/// one problem, and a handover that breaks in three places takes three days to
/// diagnose.
///
/// A step that <em>throws</em> is different, and does abort: subsequent steps assume the
/// earlier ones happened, so continuing past a failed action produces cascading
/// failures that say nothing about their own subject.
/// </summary>
public sealed class ScenarioRunner(
    ScenarioActionRegistry actions,
    ScenarioAssertionRegistry assertions,
    ScenarioRunContext runContext,
    ISandboxDbContextFactory sandbox,
    ILogger<ScenarioRunner> logger,
    IIsbmSessionStoreAccessor? sessions = null)
{
    /// <summary>
    /// How long a queued run waits for the one in flight. Generous because the wait is
    /// legitimate — a CI job triggered while a demo is running should queue rather than
    /// be rejected — but bounded, so a run that leaked the context fails visibly.
    /// </summary>
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <param name="existingRunId">
    /// An already-persisted run to execute into, used when the caller needed the id
    /// before the run started — the UI navigates to the detail page immediately and
    /// watches rows appear. Null creates the row here, which is the ordinary path.
    /// </param>
    public async Task<ScenarioRunSummary> RunAsync(
        ScenarioDefinition scenario,
        ScenarioRunMode mode = ScenarioRunMode.Ci,
        int seed = 0,
        Guid? existingRunId = null,
        CancellationToken ct = default)
    {
        await using var db = sandbox.Create();

        var run = existingRunId is { } id
            ? await EntityFrameworkQueryableExtensions.FirstAsync(db.ScenarioRuns, r => r.Id == id, ct)
            : new ScenarioRun
            {
                ScenarioId = scenario.Id,
                Title = scenario.Name,
                Mode = mode,
                Seed = seed
            };

        if (existingRunId is null)
        {
            db.ScenarioRuns.Add(run);
            await db.SaveChangesAsync(ct);
        }

        await using var scope = await runContext.BeginAsync(run.Id, scenario.Id, ClaimTimeout, ct);

        var findings = new List<AssertionResult>();

        try
        {
            var precondition = await VerifySubscriptionsAsync(scenario, run.Id, ct);

            if (precondition is not null)
            {
                findings.Add(precondition);
                db.Assertions.Add(precondition);

                if (precondition.Severity == FindingSeverity.Fail)
                {
                    // Aborting rather than proceeding. Every message assertion below
                    // would fail for the same reason, burying the one finding that
                    // explains them all under a dozen that do not.
                    return await FinishAsync(
                        db, run, findings, ScenarioRunState.Aborted,
                        precondition.Observed, ct);
                }
            }

            var ordinal = 0;

            var stepOutputs = new Dictionary<string, IReadOnlyDictionary<string, string?>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in scenario.Items)
            {
                ct.ThrowIfCancellationRequested();

                if (item.IsAssertion)
                {
                    findings.Add(await EvaluateAsync(db, run, item, ordinal, ct));
                    continue;
                }

                ordinal = item.Ordinal;

                var step = await ExecuteAsync(db, run, item, stepOutputs, ct);

                if (step.Outcome == FindingSeverity.Fail)
                {
                    return await FinishAsync(
                        db, run, findings, ScenarioRunState.Aborted,
                        $"Step {item.Describe()} failed: {step.Error}", ct);
                }
            }

            var state = findings.Any(f => f.Severity == FindingSeverity.Fail)
                ? ScenarioRunState.Failed
                : ScenarioRunState.Passed;

            return await FinishAsync(db, run, findings, state, null, ct);
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(
                db, run, findings, ScenarioRunState.Aborted, "The run was cancelled.", ct: default);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scenario {ScenarioId} run {RunId} failed.", scenario.Id, run.Id);

            return await FinishAsync(
                db, run, findings, ScenarioRunState.Aborted, ex.Message, ct: default);
        }
    }

    /// <summary>
    /// Confirms every declared subscriber is listening before the first publication.
    ///
    /// This is the single most valuable check in the engine, because a subscription
    /// opened late produces a run in which nothing arrives and every assertion blames
    /// the wrong component. A subscription only receives what is published after it
    /// opens, so verifying afterwards proves nothing.
    /// </summary>
    private async Task<AssertionResult?> VerifySubscriptionsAsync(
        ScenarioDefinition scenario, Guid runId, CancellationToken ct)
    {
        var required = scenario.Setup.Channels
            .Where(c => string.Equals(c.Type, "Publication", StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => c.Subscribers.Select(s => (Subscriber: s, c.Uri)))
            .ToList();

        if (required.Count == 0)
        {
            return null;
        }

        if (sessions is null)
        {
            // A concern, not a failure. The scenario may still be meaningful against a
            // host with no bus configured, and the message assertions will report the
            // absence themselves — with their own evidence, which is better than this
            // check guessing on their behalf.
            return new AssertionResult
            {
                ScenarioRunId = runId,
                Ordinal = 0,
                Assertion = "subscriptions_open",
                Severity = FindingSeverity.Concern,
                Owner = FindingOwner.Environment,
                Observed = "No ISBM provider is configured, so subscriptions could not be " +
                    "confirmed before the first publication.",
                Suggests = "Any message assertion in this run will fail for want of a bus " +
                    "rather than for want of a working participant."
            };
        }

        var missing = new List<string>();

        foreach (var (subscriber, uri) in required)
        {
            try
            {
                var open = await sessions.For(subscriber).ListAsync(ct);

                var listening = open.Any(s =>
                    s.Kind == IsbmSessionKind.Subscription &&
                    s.ChannelUri.EndsWith(uri, StringComparison.OrdinalIgnoreCase));

                if (!listening)
                {
                    missing.Add($"{subscriber} on {uri}");
                }
            }
            catch (Exception ex)
            {
                missing.Add($"{subscriber} on {uri} ({ex.Message})");
            }
        }

        return missing.Count == 0
            ? new AssertionResult
            {
                ScenarioRunId = runId,
                Ordinal = 0,
                Assertion = "subscriptions_open",
                Severity = FindingSeverity.Pass,
                Observed = $"All {required.Count} declared subscription(s) are open."
            }
            : new AssertionResult
            {
                ScenarioRunId = runId,
                Ordinal = 0,
                Assertion = "subscriptions_open",
                Severity = FindingSeverity.Fail,
                Owner = FindingOwner.Isbm,
                Observed = "These declared subscriptions are not open: " +
                    string.Join(", ", missing),
                Suggests = "A subscription only receives what is published after it opens, " +
                    "so running now would report every message as undelivered. Check the " +
                    "channels exist and the subscriber's session survived the last reset."
            };
    }

    private async Task<ScenarioStepRun> ExecuteAsync(
        SandboxDbContext db,
        ScenarioRun run,
        ScenarioItem item,
        Dictionary<string, IReadOnlyDictionary<string, string?>> stepOutputs,
        CancellationToken ct)
    {
        var step = new ScenarioStepRun
        {
            ScenarioRunId = run.Id,
            Ordinal = item.Ordinal,
            StepId = item.StepId,
            ParticipantId = item.At ?? string.Empty,
            Action = item.Action!,
            ArgsJson = Serialize(item.Args)
        };

        db.ScenarioSteps.Add(step);
        await db.SaveChangesAsync(ct);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await actions.Get(item.Action!)
                .ExecuteAsync(new ScenarioActionContext(item, stepOutputs), ct);

            step.ResultJson = Serialize(new { result.Summary, result.Payload });
            step.Outcome = FindingSeverity.Pass;

            if (item.StepId is { } stepId && result.Payload is { } payload)
            {
                stepOutputs[stepId] = Flatten(payload);
            }

            logger.LogInformation(
                "Scenario {ScenarioId} step {Ordinal} {Action}: {Summary} ({Elapsed}ms)",
                run.ScenarioId, item.Ordinal, item.Action, result.Summary,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            step.Outcome = FindingSeverity.Fail;
            step.Error = ex.Message;

            logger.LogError(ex,
                "Scenario {ScenarioId} step {Ordinal} {Action} threw.",
                run.ScenarioId, item.Ordinal, item.Action);
        }

        step.FinishedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return step;
    }

    private async Task<AssertionResult> EvaluateAsync(
        SandboxDbContext db, ScenarioRun run, ScenarioItem item, int ordinal, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;

        var result = new AssertionResult
        {
            ScenarioRunId = run.Id,
            Ordinal = ordinal,
            Assertion = item.Assert!,
            ParticipantId = item.At,
            ArgsJson = Serialize(item.Args)
        };

        try
        {
            var outcome = await assertions.Get(item.Assert!)
                .EvaluateAsync(new ScenarioAssertionContext(item, run.Id), ct);

            result.Severity = outcome.Severity;
            result.Owner = outcome.Owner;
            result.Observed = outcome.Observed;
            result.Suggests = outcome.Suggests;

            // An assertion the author marked optional still runs and still reports what
            // it saw; only the verdict softens. Suppressing the evaluation instead would
            // hide the observation that makes an intermittent condition diagnosable.
            if (result.Severity == FindingSeverity.Fail
                && item.OnFailure == FindingSeverity.Concern)
            {
                result.Severity = FindingSeverity.Concern;
            }
        }
        catch (ScenarioActionException ex)
        {
            // An unusable argument is the scenario author's error, not the
            // participant's, and attributing it to the participant would send someone
            // looking for a defect in working code.
            result.Severity = FindingSeverity.Fail;
            result.Owner = FindingOwner.Sandbox;
            result.Observed = $"The scenario file is wrong at line {item.Line}: {ex.Message}";
        }
        catch (Exception ex)
        {
            result.Severity = FindingSeverity.Fail;
            result.Owner = FindingOwner.Environment;
            result.Observed = $"The assertion could not be evaluated: {ex.Message}";
            result.Suggests = "This is a fault in the engine or its connections, so the " +
                "assertion proves nothing about the participants either way.";
        }

        result.WaitedSeconds = (int)Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds);

        db.Assertions.Add(result);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Scenario {ScenarioId} assert {Assertion}: {Severity} - {Observed}",
            run.ScenarioId, result.Assertion, result.Severity, result.Observed);

        return result;
    }

    private static async Task<ScenarioRunSummary> FinishAsync(
        SandboxDbContext db,
        ScenarioRun run,
        List<AssertionResult> findings,
        ScenarioRunState state,
        string? abortReason,
        CancellationToken ct)
    {
        run.Passed = findings.Count(f => f.Severity == FindingSeverity.Pass);
        run.Concerns = findings.Count(f => f.Severity == FindingSeverity.Concern);
        run.Failed = findings.Count(f => f.Severity == FindingSeverity.Fail);
        run.State = state;
        run.AbortReason = abortReason;
        run.FinishedUtc = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return new ScenarioRunSummary(
            run.Id, run.ScenarioId, run.State,
            run.Passed, run.Concerns, run.Failed, run.AbortReason,
            findings);
    }

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, Json);

    /// <summary>
    /// Reduces an action's payload to the scalar fields a later step can name.
    ///
    /// Round-tripping through JSON rather than reflecting over the anonymous type
    /// keeps this honest: a later step can refer to exactly what the run record shows,
    /// so what a scenario can read and what an operator can see never diverge. Nested
    /// objects are skipped rather than flattened, because a path syntax would be a
    /// second way to express something no action currently produces.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> Flatten(object payload)
    {
        var flattened = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, Json));

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return flattened;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            flattened[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                    property.Value.ToString(),
                _ => null
            };
        }

        return flattened;
    }
}
