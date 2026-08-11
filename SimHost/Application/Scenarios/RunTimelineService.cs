using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Sandbox;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Scenarios;

/// <summary>
/// One action from the scenario file with the assertions that followed it.
///
/// Assertions are nested rather than listed alongside, because a finding only means
/// something in the context of the step that provoked it: "MMS never received" is a
/// different defect after approve_stewardship than after create_tag.
/// </summary>
/// <param name="Bods">
/// The BODs this step put on the wire, so the payload can be read from the step that
/// caused it. Empty for steps that only touched local state — most actions never
/// publish, and an empty list says so more honestly than a dead link would.
/// </param>
public sealed record TimelineStep(
    int Ordinal,
    string? StepId,
    string ParticipantId,
    string Action,
    string? ArgsJson,
    string? ResultJson,
    FindingSeverity Outcome,
    string? Error,
    IReadOnlyList<AssertionResult> Assertions,
    IReadOnlyList<FlowMessage> Bods)
{
    /// <summary>Worst outcome among the step and its assertions, for the status dot.</summary>
    public FindingSeverity Severity =>
        Outcome == FindingSeverity.Fail || Assertions.Any(a => a.Severity == FindingSeverity.Fail)
            ? FindingSeverity.Fail
            : Assertions.Any(a => a.Severity == FindingSeverity.Concern)
                ? FindingSeverity.Concern
                : FindingSeverity.Pass;
}

/// <summary>
/// One BOD moving between two participants, as reconstructed from both ends.
/// </summary>
/// <param name="From">Publisher, or null when only the receiving end was recorded.</param>
/// <param name="To">Receiver, or null when the message was posted but nothing consumed it.</param>
/// <param name="MessageId">
/// Identifies the record this row was built from, so the transformation view can be
/// addressed. Paired with <paramref name="RecordedBy"/> because MessageId is only
/// unique within the participant that wrote it: sender and receiver each assign their
/// own id to the same BOD.
/// </param>
/// <param name="RecordedBy">The participant whose store this record was read from.</param>
public sealed record FlowMessage(
    string? From,
    string? To,
    string ChannelUri,
    string Verb,
    string Noun,
    string BodId,
    string ValidationStatus,
    ProcessingStatus ProcessingStatus,
    DateTimeOffset OccurredAt,
    Guid MessageId,
    string RecordedBy,
    string CorrelationId)
{
    public bool IsValid =>
        string.Equals(ValidationStatus, "Valid", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A message nothing received. Worth showing rather than filtering: a publication
    /// with no subscriber looks identical to a successful send from the sender's side.
    /// </summary>
    public bool IsUndelivered => To is null;
}

/// <summary>Everything the run-detail page needs, gathered once.</summary>
public sealed record RunTimeline(
    ScenarioRun Run,
    IReadOnlyList<TimelineStep> Steps,
    IReadOnlyList<FlowMessage> Messages);

/// <summary>
/// Assembles one run's steps, assertions and message flow for display.
///
/// The message flow is reconstructed by pairing each participant's outbound record
/// with the inbound record of whoever received it, matched on BodId. It is not read
/// from a single ledger, because no such ledger exists — each participant records only
/// what it saw, which is the point: the sandbox simulates independent systems, and a
/// central log of who sent what to whom would be a fiction none of them could produce.
///
/// Pairing on BodId rather than on channel and timestamp because a channel carries
/// many messages and clocks across participants are not guaranteed to agree.
/// </summary>
public sealed class RunTimelineService(
    ISandboxDbContextFactory sandbox,
    IParticipantDbContextFactory participants,
    ParticipantRegistry registry,
    ILogger<RunTimelineService> logger)
{
    public async Task<RunTimeline?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        await using var db = sandbox.Create();

        var run = await db.ScenarioRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

        if (run is null)
        {
            return null;
        }

        var steps = await db.ScenarioSteps
            .AsNoTracking()
            .Where(s => s.ScenarioRunId == runId)
            .OrderBy(s => s.Ordinal)
            .ToListAsync(ct);

        var assertions = await db.Assertions
            .AsNoTracking()
            .Where(a => a.ScenarioRunId == runId)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        // Assertions carry the ordinal of the step they followed, so grouping by ordinal
        // reunites them without needing a foreign key the engine does not write.
        var byOrdinal = assertions
            .GroupBy(a => a.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AssertionResult>)[.. g]);

        var flow = await ReadFlowAsync(runId, ct);

        var timeline = steps
            .Select(s => new TimelineStep(
                s.Ordinal, s.StepId, s.ParticipantId, s.Action,
                s.ArgsJson, s.ResultJson, s.Outcome, s.Error,
                byOrdinal.TryGetValue(s.Ordinal, out var found) ? found : [],
                BodsFor(s, flow)))
            .ToList();

        // Assertions evaluated before any step ran — the subscription precondition —
        // have no step to hang off and would otherwise vanish from the view entirely.
        if (byOrdinal.TryGetValue(0, out var preconditions))
        {
            timeline.Insert(0, new TimelineStep(
                0, null, string.Empty, "preconditions", null, null,
                FindingSeverity.Pass, null, preconditions, []));
        }

        return new RunTimeline(run, timeline, flow);
    }

    /// <summary>
    /// The BODs a step put on the wire.
    ///
    /// Correlated on the id the action itself reported where it reported one, because
    /// that is the only exact answer: the publisher stamps the same correlation id on
    /// every message it emits for that call. Where an action reports nothing, the step's
    /// own execution window against the participant it ran at is used instead — coarser,
    /// since a concurrent background republish could fall inside it, but the alternative
    /// is showing no BOD at all for the steps that publish most.
    /// </summary>
    private static IReadOnlyList<FlowMessage> BodsFor(
        ScenarioStepRun step, IReadOnlyList<FlowMessage> flow)
    {
        if (CorrelationIdOf(step.ResultJson) is { } correlationId)
        {
            var matched = flow
                .Where(f => f.CorrelationId == correlationId)
                .ToList();

            if (matched.Count > 0)
            {
                return matched;
            }
        }

        if (string.IsNullOrEmpty(step.ParticipantId))
        {
            return [];
        }

        var finished = step.FinishedUtc ?? DateTimeOffset.UtcNow;

        return
        [
            .. flow.Where(f =>
                f.From == step.ParticipantId
                && f.OccurredAt >= step.StartedUtc
                && f.OccurredAt <= finished)
        ];
    }

    /// <summary>
    /// Pulls a correlation id out of an action's result, whatever it chose to call it.
    ///
    /// Searches one level into nested objects because the runner wraps every action
    /// result as <c>{ Summary, Payload }</c>, and the id an action reports lives on its
    /// own payload rather than on that envelope. Absence is normal: most actions never
    /// publish and so have none to report.
    /// </summary>
    private static string? CorrelationIdOf(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);

            return Find(document.RootElement, depth: 2);
        }
        catch (JsonException)
        {
            // A result that is not an object is simply one with no correlation id to find.
            return null;
        }

        static string? Find(JsonElement element, int depth)
        {
            if (element.ValueKind != JsonValueKind.Object || depth == 0)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("correlationId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                if (Find(property.Value, depth - 1) is { } nested)
                {
                    return nested;
                }
            }

            return null;
        }
    }

    private async Task<IReadOnlyList<FlowMessage>> ReadFlowAsync(
        Guid runId, CancellationToken ct)
    {
        var outbound = new List<(string Participant, MessageRecord Message)>();
        var inbound = new List<(string Participant, MessageRecord Message)>();

        foreach (var participant in registry.All)
        {
            try
            {
                await using var db = participants.Create(participant.ParticipantId);

                var messages = await db.Set<MessageRecord>()
                    .AsNoTracking()
                    .Where(m => m.ScenarioRunId == runId)
                    .ToListAsync(ct);

                foreach (var message in messages)
                {
                    var target = message.Direction == MessageDirection.Outbound
                        ? outbound
                        : inbound;

                    target.Add((participant.ParticipantId, message));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex, "Flow skipped {ParticipantId}: its messages could not be read.",
                    participant.ParticipantId);
            }
        }

        var flow = new List<FlowMessage>();

        foreach (var (sender, message) in outbound)
        {
            // One publication can be received by several subscribers, so this fans out
            // to one arrow per receiver rather than collapsing to a single line that
            // would hide a subscriber that missed it.
            var receivers = inbound
                .Where(i => i.Message.BodId == message.BodId)
                .ToList();

            if (receivers.Count == 0)
            {
                flow.Add(Build(sender, null, message, sender));
                continue;
            }

            foreach (var (receiver, received) in receivers)
            {
                // Validation and processing status are taken from the receiving end:
                // the sender considers a message fine by definition, and the interesting
                // failure is always what the receiver made of it.
                flow.Add(Build(sender, receiver, received, receiver));
            }
        }

        // Inbound with no matching outbound means something arrived from outside this
        // run — a leftover on the channel, or a real external publisher. Either way it
        // belongs on the diagram, because it is genuinely part of what the run saw.
        foreach (var (receiver, message) in inbound)
        {
            if (!outbound.Any(o => o.Message.BodId == message.BodId))
            {
                flow.Add(Build(null, receiver, message, receiver));
            }
        }

        return [.. flow.OrderBy(f => f.OccurredAt)];
    }

    private static FlowMessage Build(
        string? from, string? to, MessageRecord message, string recordedBy) =>
        new(from, to, message.ChannelUri, message.Verb, message.Noun, message.BodId,
            message.ValidationStatus, message.ProcessingStatus, message.OccurredAt,
            message.MessageId, recordedBy, message.CorrelationId);
}
