using System.Globalization;
using SimHost.Application.Cir;
using SimHost.Application.Participants;
using SimHost.Personalities.Eng;
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
public class ScenarioActionContext(ScenarioItem item)
{
    public ScenarioItem Item { get; } = item;

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

/// <summary>Adds or edits an ENG tag. Upsert semantics come from <see cref="EngService"/>.</summary>
public sealed class CreateTagAction(EngService eng) : IScenarioAction
{
    public string Name => "create_tag";

    public async Task<ScenarioActionResult> ExecuteAsync(ScenarioActionContext context, CancellationToken ct)
    {
        var tag = await eng.AddTagAsync(
            context.RequireString("tagNumber"),
            context.GetString("serviceDescription"),
            context.GetString("unitNumber"),
            context.GetString("classKey"),
            context.GetDecimal("rangeMinimum"),
            context.GetDecimal("rangeMaximum"),
            context.GetString("controlAction"),
            ct);

        return new ScenarioActionResult(
            $"Tag {tag.TagNumber} is {tag.Maturity}.",
            new { tag.Id, tag.TagNumber, Maturity = tag.Maturity.ToString() });
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

        return new ScenarioActionResult(
            $"Approved {result.Approved}, rejected {result.Rejected}.", result);
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
