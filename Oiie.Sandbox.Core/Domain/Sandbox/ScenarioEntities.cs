namespace SimHost.Domain.Sandbox;

/// <summary>
/// Which component a finding belongs to.
///
/// Carried on every result because "the registration timed out" is true of the
/// Sandbox, ISBM and CIR simultaneously and tells none of their owners anything.
/// The PowerShell suite this engine replaces attributed every failure this way,
/// and a plain pass/fail result would discard it (testing/test-sandbox.ps1).
/// </summary>
public enum FindingOwner { Sandbox, Isbm, Cir, Environment }

/// <summary>
/// A failing assertion stops the run; a concern is recorded and the run continues.
///
/// Kept distinct because conflating them costs information in both directions: a
/// red run that is really an environment note gets ignored, and a genuine defect
/// hidden behind one gets missed.
/// </summary>
public enum FindingSeverity { Pass, Concern, Fail }

public enum ScenarioRunState { Running, Passed, Failed, Aborted }

public enum ScenarioRunMode { Ci, Demo }

/// <summary>
/// One execution of one scenario file (spec §11).
///
/// Persisted rather than held in memory so a run survives an App Service recycle
/// and the run-history view (§11.5) has something to read. The id is stamped onto
/// <see cref="SimHost.Domain.Common.MessageRecord.ScenarioRunId"/> and
/// <see cref="SimHost.Domain.Common.OutboxItem.ScenarioRunId"/>, which is what lets
/// an assertion scope its query to this run rather than to whatever the previous
/// run left behind.
/// </summary>
public class ScenarioRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>File stem, e.g. sc01-design-release.</summary>
    public string ScenarioId { get; set; } = string.Empty;

    public string? Title { get; set; }

    public ScenarioRunMode Mode { get; set; } = ScenarioRunMode.Ci;
    public ScenarioRunState State { get; set; } = ScenarioRunState.Running;

    /// <summary>
    /// Seed for the deterministic RNG in CI mode (§11.3). Recorded so a failing run
    /// can be reproduced exactly rather than approximately.
    /// </summary>
    public int Seed { get; set; }

    public int Passed { get; set; }
    public int Concerns { get; set; }
    public int Failed { get; set; }

    /// <summary>Set when the run stopped early, e.g. the bus was not delivering.</summary>
    public string? AbortReason { get; set; }

    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
}

/// <summary>
/// One <c>action</c> item from the scenario file. Assertions hang off the step that
/// preceded them, so a failure reads as "approve_stewardship, then MMS never received"
/// rather than as an orphaned assertion.
/// </summary>
public class ScenarioStepRun
{
    public long Id { get; set; }

    public Guid ScenarioRunId { get; set; }

    /// <summary>Ordinal within the scenario, 1-based. Assertions share the ordinal of their step.</summary>
    public int Ordinal { get; set; }

    /// <summary>Author-supplied step id from the YAML, e.g. s3.</summary>
    public string? StepId { get; set; }

    /// <summary>Participant the action was invoked against.</summary>
    public string ParticipantId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    /// <summary>JSON, verbatim from the scenario file's args map.</summary>
    public string? ArgsJson { get; set; }

    public FindingSeverity Outcome { get; set; } = FindingSeverity.Pass;

    /// <summary>JSON returned by the action handler, kept for assertions that read it.</summary>
    public string? ResultJson { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
}

/// <summary>
/// One evaluated assertion (§11.2).
///
/// <see cref="Observed"/> and <see cref="Suggests"/> are the reason this is not a
/// boolean. A failure is meant to be handed to whoever owns the component as a
/// description of what was seen from outside, not as "it does not work".
/// </summary>
public class AssertionResult
{
    public long Id { get; set; }

    public Guid ScenarioRunId { get; set; }

    /// <summary>Ordinal of the step this assertion followed.</summary>
    public int Ordinal { get; set; }

    /// <summary>Assertion name from the vocabulary, e.g. message_received.</summary>
    public string Assertion { get; set; } = string.Empty;

    /// <summary>Participant the assertion was evaluated against, where applicable.</summary>
    public string? ParticipantId { get; set; }

    /// <summary>JSON, the assertion's arguments verbatim from the scenario file.</summary>
    public string? ArgsJson { get; set; }

    public FindingSeverity Severity { get; set; } = FindingSeverity.Pass;

    public FindingOwner Owner { get; set; } = FindingOwner.Sandbox;

    /// <summary>What was actually seen. Required on anything that is not a pass.</summary>
    public string? Observed { get; set; }

    /// <summary>What that most likely means, for the owner named above.</summary>
    public string? Suggests { get; set; }

    /// <summary>
    /// Seconds spent waiting before the assertion settled. Recorded because a timed
    /// assertion that passes at 44s of a 45s budget is one dispatcher hiccup away
    /// from failing, and a bare pass hides that.
    /// </summary>
    public int WaitedSeconds { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
