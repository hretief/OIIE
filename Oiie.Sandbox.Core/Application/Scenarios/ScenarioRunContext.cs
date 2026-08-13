namespace SimHost.Application.Scenarios;

/// <summary>
/// Holds the run currently executing, so records written anywhere in the process can
/// be attributed to it.
///
/// Deliberately a singleton rather than an <c>AsyncLocal</c>. The obvious design is an
/// ambient value flowing with the async context, but the two components that create
/// message records — <c>InboxPump</c> and <c>OutboxDispatcher</c> — are hosted services
/// running their own loops, started long before any run begins. An <c>AsyncLocal</c> set
/// by the runner would never reach them, and the failure would be silent: every
/// <c>ScenarioRunId</c> would stay null, and assertions scoped to a run would return
/// nothing rather than error. A wrong answer that looks like a legitimate empty result
/// is worse than an exception.
///
/// The cost of a singleton is that exactly one run may be in flight per process, which
/// <see cref="BeginAsync"/> enforces rather than assumes. Parallel CI runs are addressed
/// in the spec by run-scoped namespacing (§10) — separate schemas and channel prefixes,
/// and therefore separate processes — not by concurrency inside one host.
/// </summary>
public sealed class ScenarioRunContext
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// A single reference so the run id and scenario id are published together. Two
    /// separate fields could be read half-updated by the inbox pump, attributing a
    /// message to a run id with the wrong scenario name beside it in the log.
    /// </summary>
    private volatile ActiveRun? _active;

    /// <summary>
    /// The run in flight, or null. Read by the inbox pump when archiving an inbound
    /// message and by services creating outbox items.
    /// </summary>
    public Guid? CurrentRunId => _active?.RunId;

    public string? CurrentScenarioId => _active?.ScenarioId;

    /// <summary>
    /// Claims the context for a run. Waits rather than throwing when another run holds
    /// it, so a queued run starts late instead of failing — but the wait is bounded, and
    /// a caller that cannot claim the context within it gets a clear error rather than a
    /// deadlock.
    /// </summary>
    public async Task<IAsyncDisposable> BeginAsync(
        Guid runId,
        string scenarioId,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(timeout, ct))
        {
            throw new InvalidOperationException(
                $"Scenario '{CurrentScenarioId}' is still running. Only one scenario may run " +
                "at a time in a single host, because messages on the bus cannot be attributed " +
                "to one of two concurrent runs.");
        }

        _active = new ActiveRun(runId, scenarioId);

        return new Scope(this);
    }

    private void End()
    {
        _active = null;
        _gate.Release();
    }

    private sealed record ActiveRun(Guid RunId, string ScenarioId);

    private sealed class Scope(ScenarioRunContext context) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            context.End();
            return ValueTask.CompletedTask;
        }
    }
}
