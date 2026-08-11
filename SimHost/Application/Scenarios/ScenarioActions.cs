using System.Globalization;
using SimHost.Application.Cir;
using SimHost.Application.Participants;
using SimHost.Domain.Mms;
using SimHost.Personalities.Eng;
using SimHost.Personalities.Mms;
using SimHost.Personalities.RegLocation;

namespace SimHost.Application.Scenarios;

/// <summary>
/// What an action reads to do its work.
///
/// Actions take the item rather than a pre-bound argument object because the
/// scenario model keeps arguments as a verbatim map: the shape of an action's
/// arguments is the action's own business, and centralising it here would mean
/// touching this type every time the vocabulary grows.
/// </summary>
public class ScenarioActionContext(
    ScenarioItem item,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? stepOutputs = null)
{
    public ScenarioItem Item { get; } = item;

    /// <summary>
    /// What earlier steps produced, keyed by their <c>id</c> then by field name.
    ///
    /// Exists because a value the scenario cannot know in advance is still a value a
    /// later step may need to name. The greenfield case is the whole reason: uc02
    /// asks the identity service to allocate a code, so the file cannot write that
    /// code down without asserting the thing it is supposed to be testing.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> StepOutputs { get; }
        = stepOutputs ?? new Dictionary<string, IReadOnlyDictionary<string, string?>>(
            StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>at</c> key. Actions that need a participant demand it explicitly.</summary>
    public string? At => Item.At;

    public string RequireParticipant() => Item.At
        ?? throw new ScenarioActionException($"{Item.Describe()}: requires 'at' to name a participant.");

    public string RequireString(string key) => GetString(key)
        ?? throw new ScenarioActionException($"{Item.Describe()}: missing required argument '{key}'.");

    public string? GetString(string key) =>
        Item.Args.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>
    /// Reads a named field from an earlier step's result.
    ///
    /// Both the step and the field are reported by name when either is missing,
    /// because the alternative — a null flowing on into the action — surfaces later as
    /// "no tag '' to relate from", which names neither the mistake nor its location.
    /// </summary>
    public string RequireFromStep(string stepId, string field)
    {
        if (!StepOutputs.TryGetValue(stepId, out var output))
        {
            var known = StepOutputs.Count == 0
                ? "no earlier step recorded a result"
                : $"known steps: {string.Join(", ", StepOutputs.Keys)}";

            throw new ScenarioActionException(
                $"{Item.Describe()}: no earlier step with id '{stepId}' ({known}).");
        }

        if (!output.TryGetValue(field, out var value) || string.IsNullOrEmpty(value))
        {
            throw new ScenarioActionException(
                $"{Item.Describe()}: step '{stepId}' produced no '{field}' " +
                $"(it produced: {string.Join(", ", output.Keys)}).");
        }

        return value;
    }

    public decimal? GetDecimal(string key)
    {
        var text = GetString(key);

        if (text is null)
        {
            return null;
        }

        return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ScenarioActionException(
                $"{Item.Describe()}: argument '{key}' is '{text}', which is not a number.");
    }
}

/// <summary>
/// A scenario argument was missing or unusable.
///
/// Distinguished from any other failure so the runner can attribute it to the
/// scenario file rather than to the participant under test — an author's typo and
/// a genuine interoperability defect should not read the same in a run report.
/// </summary>
public sealed class ScenarioActionException(string message) : Exception(message);

/// <summary>
/// Outcome of one action, in the form the runner persists.
///
/// <paramref name="Payload"/> is serialised into <c>ScenarioStepRun.ResultJson</c>, so
/// later assertions and the run view can see what the action actually produced rather
/// than only whether it threw.
/// </summary>
public sealed record ScenarioActionResult(string Summary, object? Payload = null);

public interface IScenarioAction
{
    /// <summary>The name as written in a scenario file, e.g. create_tag.</summary>
    string Name { get; }

    Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct);
}

/// <summary>
/// The action vocabulary, resolved by name.
///
/// Every action here wraps behaviour that already exists as a service — the same
/// calls the admin endpoints make. The engine drives the participants the way an
/// operator would, so a scenario cannot pass by exercising a path no real caller
/// takes.
/// </summary>
public sealed class ScenarioActionRegistry
{
    private readonly Dictionary<string, IScenarioAction> actions;

    public ScenarioActionRegistry(IEnumerable<IScenarioAction> actions)
    {
        this.actions = actions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Names =>
        actions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IScenarioAction Get(string name) => actions.TryGetValue(name, out var action)
        ? action
        : throw new ScenarioActionException($"Unknown action '{name}'.");
}

/// <summary>
/// Adds or edits an ENG tag. Upsert semantics come from <see cref="EngService"/>.
///
/// Give <c>tagNumber</c> to author a specific tag, or <c>codePrefix</c> to let the
/// identity service allocate the next one in a series — the greenfield case, where
/// the scenario cannot know the code in advance because nothing has issued it yet.
/// </summary>
public sealed class CreateTagAction(EngService eng) : IScenarioAction
{
    public string Name => "create_tag";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var tag = await eng.AddTagAsync(
            context.GetString("tagNumber"),
            context.GetString("serviceDescription"),
            context.GetString("unitNumber"),
            context.GetString("classKey"),
            context.GetDecimal("rangeMinimum"),
            context.GetDecimal("rangeMaximum"),
            context.GetString("controlAction"),
            context.GetString("codePrefix"),
            ct);

        return new ScenarioActionResult(
            $"Tag {tag.TagNumber} is {tag.Maturity}.",
            new
            {
                tag.Id,
                tag.TagNumber,
                FederationId = tag.FederationId.ToString(),
                Maturity = tag.Maturity.ToString()
            });
    }
}

/// <summary>
/// Asserts a design relationship between two existing ENG tags.
///
/// Direction is explicit: <c>from</c> is the source and <c>to</c> the sink, so
/// "BBFQ0032 supplies P-101" is authored as from BBFQ0032 to P-101. The reverse
/// reading is not authored at all — it comes from the relationship type.
///
/// Each end is named either literally (<c>from</c>) or by the step that produced it
/// (<c>fromStep</c>). The second form exists for the greenfield case, where the code
/// was allocated during the run and writing it into the file would defeat the point
/// of allocating it.
/// </summary>
public sealed class RelateTagsAction(EngService eng) : IScenarioAction
{
    public string Name => "relate_tags";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var from = EndPoint(context, "from");
        var to = EndPoint(context, "to");

        var relationship = await eng.RelateTagsAsync(
            from,
            to,
            context.RequireString("type"),
            (int?)context.GetDecimal("order"),
            ct);

        return new ScenarioActionResult(
            $"{from} {context.RequireString("type")} {to}.",
            new
            {
                relationship.Id,
                FederationId = relationship.FederationId.ToString(),
                relationship.TypeKey,
                From = from,
                To = to
            });
    }

    /// <summary>
    /// Resolves one end from whichever form the author used, refusing both and
    /// neither. Silently preferring one would make a scenario that names an end twice
    /// read as though both were honoured.
    /// </summary>
    private static string EndPoint(ScenarioActionContext context, string key)
    {
        var literal = context.GetString(key);
        var step = context.GetString($"{key}Step");

        return (literal, step) switch
        {
            (not null, not null) => throw new ScenarioActionException(
                $"{context.Item.Describe()}: '{key}' and '{key}Step' are both set; use one."),
            (not null, null) => literal,
            (null, not null) => context.RequireFromStep(step, "tagNumber"),
            _ => throw new ScenarioActionException(
                $"{context.Item.Describe()}: missing required argument '{key}' or '{key}Step'.")
        };
    }
}

/// <summary>
/// ENG's release event.
///
/// A failed validation gate is returned as a result, not thrown: the gate refusing
/// to release is a legitimate outcome the scenario may be asserting on, and the
/// findings are the evidence.
/// </summary>
public sealed class PromoteNamedVersionAction(
    EngService eng, ParticipantRegistry registry) : IScenarioAction
{
    public string Name => "promote_named_version";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var publisher = ScenarioChannels.RequirePublisher(registry, EngService.ParticipantId);

        var result = await eng.PromoteAsync(
            context.RequireString("name"),
            publisher.ChannelUri,
            publisher.Topics.FirstOrDefault(),
            ct);

        var summary = result.Released
            ? $"Released '{result.Name}' with {result.TagCount} tag(s)."
            : $"Promotion of '{result.Name}' was refused: {string.Join("; ", result.Findings)}";

        return new ScenarioActionResult(summary, result);
    }
}

/// <summary>
/// Publishes ENG's design relationships.
///
/// Separate from promote_named_version because the edges can only be stored by a
/// receiver that already holds both ends: a registry stewarding incoming segments
/// has them only once approved, so this runs after that approval rather than
/// alongside the release that proposed them.
/// </summary>
public sealed class PublishRelationshipsAction(
    EngService eng, ParticipantRegistry registry) : IScenarioAction
{
    public string Name => "publish_relationships";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var publisher = ScenarioChannels.RequirePublisher(registry, EngService.ParticipantId);

        var result = await eng.PublishRelationshipsAsync(
            publisher.ChannelUri,
            publisher.Topics.FirstOrDefault(),
            ct);

        return new ScenarioActionResult(
            result.EdgeCount > 0
                ? $"Published {result.EdgeCount} relationship(s)."
                : result.Detail ?? "Nothing to publish.",
            result);
    }
}

/// <summary>REG-LOCATION's release event: approve the stewardship queue and republish.</summary>
public sealed class ApproveStewardshipAction(
    RegLocationService service, ParticipantRegistry registry) : IScenarioAction
{
    public string Name => "approve_stewardship";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var publisher = ScenarioChannels.RequirePublisher(registry, RegLocationService.ParticipantId);

        var result = await service.ApproveAllAsync(
            publisher.ChannelUri,
            publisher.Topics.FirstOrDefault(),
            context.GetString("decidedBy") ?? "steward",
            ct);

        // An empty queue is a failure, not a no-op. The step exists to release something,
        // so approving nothing means the proposal had not arrived yet — and letting that
        // pass defers the error to a later step where the cause is no longer visible.
        if (result.Approved == 0 && result.Rejected == 0)
        {
            throw new ScenarioActionException(
                "The stewardship queue at REG-LOCATION was empty, so nothing was released. "
                + "Wait for the proposal to arrive before approving it.");
        }

        return new ScenarioActionResult(
            $"Approved {result.Approved}, rejected {result.Rejected}.", result);
    }
}

/// <summary>Registers a serialised asset MMS itself originates.</summary>
public sealed class RegisterEquipmentAction(MmsWorkOrderService service) : IScenarioAction
{
    public string Name => "register_equipment";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var equipment = await service.RegisterEquipmentAsync(
            context.RequireString("equipmentNumber"),
            context.GetString("designation"),
            context.GetString("serialNumber"),
            context.GetString("modelNumber"),
            ct);

        return new ScenarioActionResult(
            $"Equipment {equipment.EquipmentNumber} registered.",
            new
            {
                equipment.EquipmentNumber,
                FederationId = equipment.FederationId.ToString(),
                equipment.SerialNumber
            });
    }
}

/// <summary>Raises a maintenance work order to install or remove a serialised asset.</summary>
public sealed class RaiseWorkOrderAction(MmsWorkOrderService service) : IScenarioAction
{
    public string Name => "raise_work_order";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var kindText = context.RequireString("eventKind");

        if (!Enum.TryParse<AssetEventKind>(kindText, ignoreCase: true, out var kind))
        {
            throw new ScenarioActionException(
                $"{context.Item.Describe()}: 'eventKind' is '{kindText}'; expected Install or Removal.");
        }

        var order = await service.RaiseAsync(
            context.RequireString("orderNumber"),
            kind,
            context.RequireString("equipmentNumber"),
            context.RequireString("functionalLocation"),
            context.GetString("description"),
            ct);

        return new ScenarioActionResult(
            $"Work order {order.OrderNumber} raised to {order.EventKind} {order.EquipmentNumber}.",
            new { order.OrderNumber, EventKind = order.EventKind.ToString(), order.State });
    }
}

/// <summary>
/// Records that the technician did the physical work.
///
/// <c>occurredAt</c> may be given to model a work order entered days after the
/// event, which is the ordinary case on a paper-then-keyboard workflow and the
/// reason Scenario 11 carries an explicit event timestamp at all.
/// </summary>
public sealed class CompleteWorkOrderAction(MmsWorkOrderService service) : IScenarioAction
{
    public string Name => "complete_work_order";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var occurredText = context.GetString("occurredAt");

        var occurredAt = occurredText is { Length: > 0 }
            ? DateTimeOffset.TryParse(
                occurredText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : throw new ScenarioActionException(
                    $"{context.Item.Describe()}: 'occurredAt' is '{occurredText}', which is not a timestamp.")
            : DateTimeOffset.UtcNow;

        var order = await service.CompleteAsync(
            context.RequireString("orderNumber"),
            occurredAt,
            context.GetString("performedBy"),
            ct);

        return new ScenarioActionResult(
            $"Work order {order.OrderNumber} completed.",
            new { order.OrderNumber, order.State, OccurredAt = order.OccurredAt?.ToString("O") });
    }
}

/// <summary>
/// MMS's release event: sign off a completed work order and publish the
/// installation or removal to O&amp;M systems.
/// </summary>
public sealed class SignOffWorkOrderAction(
    MmsWorkOrderService service, ParticipantRegistry registry) : IScenarioAction
{
    public string Name => "sign_off_work_order";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var publisher = ScenarioChannels.RequirePublisher(registry, MmsService.ParticipantId);

        var result = await service.SignOffAsync(
            context.RequireString("orderNumber"),
            publisher.ChannelUri,
            publisher.Topics.FirstOrDefault(),
            context.GetString("signedOffBy") ?? "planner",
            ct);

        var summary = result.Published
            ? $"Signed off {result.OrderNumber}: {result.EventKind} of {result.EquipmentNumber}."
            : $"Sign-off of {result.OrderNumber} was refused: {string.Join("; ", result.Findings)}";

        return new ScenarioActionResult(summary, result);
    }
}

/// <summary>Registers a participant's own entries with the CIR.</summary>
public sealed class RegisterCirAction(CirRegistrationService service) : IScenarioAction
{
    public string Name => "register_cir";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var result = await service.SyncAsync(participantId, ct);

        var summary = result.Faults.Count > 0
            ? $"{participantId}: registration reported {result.Faults.Count} fault(s)."
            : $"{participantId}: registered {result.Registered}, asserted {result.EquivalencesAsserted}.";

        return new ScenarioActionResult(summary, result);
    }
}

/// <summary>
/// Resolves a foreign identifier through the CIR.
///
/// A miss is returned rather than thrown, because "the identifier did not resolve"
/// is precisely the condition uc01 exists to detect.
/// </summary>
public sealed class ResolveIdentityAction(
    CirClient cir, ParticipantRegistry registry) : IScenarioAction
{
    public string Name => "resolve_identity";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var participant = registry.Get(context.RequireParticipant());

        var sourceId = context.RequireString("sourceId");
        var idInSource = context.RequireString("idInSource");

        var result = await cir.ResolveAsync(participant, sourceId, idInSource, ct);

        var summary = result.Cirid is { } cirid
            ? $"{sourceId}:{idInSource} resolved to {cirid} ({result.Equivalents.Count} equivalent(s))."
            : $"{sourceId}:{idInSource} did not resolve. {result.Detail}".TrimEnd();

        return new ScenarioActionResult(summary, new
        {
            result.Cirid,
            result.FromCache,
            result.Detail,
            Equivalents = result.Equivalents.Select(e => new
            {
                e.SourceID,
                e.IDInSource,
                e.Name,
                e.CIRID
            }).ToArray()
        });
    }
}

internal static class ScenarioChannels
{
    /// <summary>
    /// The participant's publisher channel, or a failure that names the participant.
    ///
    /// A release action with nowhere to publish is a configuration fault, and saying
    /// so here keeps it from surfacing later as an assertion that simply never saw a
    /// message arrive.
    /// </summary>
    public static ChannelBinding RequirePublisher(ParticipantRegistry registry, string participantId) =>
        registry.Get(participantId).Config.Channels
            .FirstOrDefault(c => c.Role == ChannelRole.Publisher)
        ?? throw new ScenarioActionException(
            $"Participant '{participantId}' has no publisher channel configured.");
}
