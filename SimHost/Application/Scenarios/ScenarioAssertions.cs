using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimHost.Application.Cir;
using SimHost.Application.Participants;
using SimHost.Domain.Common;
using SimHost.Domain.Sandbox;
using SimHost.Infrastructure.Sql;

namespace SimHost.Application.Scenarios;

/// <summary>
/// The verdict of one assertion, before it is persisted as an
/// <see cref="AssertionResult"/>.
///
/// Not a boolean, and not an exception. <see cref="Observed"/> and
/// <see cref="Suggests"/> exist because a failure is handed to whoever owns the
/// component named by <see cref="Owner"/>, and "it did not work" is not something
/// an owner can act on. The PowerShell suite this replaces reported every failure
/// in exactly this shape (testing/test-sandbox.ps1).
/// </summary>
public sealed record ScenarioAssertionOutcome(
    FindingSeverity Severity,
    FindingOwner Owner,
    string Observed,
    string? Suggests = null)
{
    public static ScenarioAssertionOutcome Pass(string observed) =>
        new(FindingSeverity.Pass, FindingOwner.Sandbox, observed);

    public static ScenarioAssertionOutcome Fail(
        FindingOwner owner, string observed, string? suggests = null) =>
        new(FindingSeverity.Fail, owner, observed, suggests);

    public static ScenarioAssertionOutcome Concern(
        FindingOwner owner, string observed, string? suggests = null) =>
        new(FindingSeverity.Concern, owner, observed, suggests);
}

/// <summary>What an assertion reads. Arguments come from the item's verbatim map.</summary>
public sealed class ScenarioAssertionContext(ScenarioItem item, Guid runId)
    : ScenarioActionContext(item)
{
    /// <summary>
    /// Scopes queries to this run.
    ///
    /// Every message query filters on it, because the sandbox database is not reset
    /// between runs by default: an assertion that merely finds "a Sync Segments on the
    /// Eng channel" would pass on yesterday's message and report a working bus while
    /// nothing at all was delivered today.
    /// </summary>
    public Guid RunId { get; } = runId;
}

public interface IScenarioAssertion
{
    /// <summary>The name as written in a scenario file, e.g. message_received.</summary>
    string Name { get; }

    Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct);
}

public sealed class ScenarioAssertionRegistry
{
    private readonly Dictionary<string, IScenarioAssertion> assertions;

    public ScenarioAssertionRegistry(IEnumerable<IScenarioAssertion> assertions)
    {
        this.assertions = assertions.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> Names =>
        assertions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IScenarioAssertion Get(string name) => assertions.TryGetValue(name, out var assertion)
        ? assertion
        : throw new ScenarioActionException($"Unknown assertion '{name}'.");
}

/// <summary>
/// Polls until a condition holds or the budget runs out, and reports how long it took.
///
/// Assertions wait rather than sleeping a fixed interval because the interesting
/// number is when the condition became true, not whether it was true after an
/// arbitrary pause. A message that arrives at 44s of a 45s budget passes, but is one
/// dispatcher hiccup from failing, and the recorded wait is what makes that visible
/// before it becomes an intermittent CI failure.
/// </summary>
public static class ScenarioWait
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static async Task<(bool Settled, int WaitedSeconds)> UntilAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan? within,
        CancellationToken ct)
    {
        var budget = within ?? TimeSpan.Zero;
        var started = DateTimeOffset.UtcNow;

        while (true)
        {
            if (await condition(ct))
            {
                return (true, Elapsed(started));
            }

            if (DateTimeOffset.UtcNow - started >= budget)
            {
                return (false, Elapsed(started));
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    private static int Elapsed(DateTimeOffset started) =>
        (int)Math.Round((DateTimeOffset.UtcNow - started).TotalSeconds);
}

/// <summary>
/// A message matching channel/verb/noun/topic arrived at a participant.
///
/// Failure is attributed to ISBM rather than to the Sandbox: the publisher's outbox
/// row is checked first, so if the message was posted and did not arrive, the gap is
/// on the bus. When it was never posted, the finding says so and points at the
/// Sandbox instead — the two produce identical symptoms from the receiver's side, and
/// telling them apart is the whole reason this assertion looks at both.
/// </summary>
public sealed class MessageReceivedAssertion(IParticipantDbContextFactory factory)
    : IScenarioAssertion
{
    public string Name => "message_received";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var criteria = MessageCriteria.From(context);

        var (settled, waited) = await ScenarioWait.UntilAsync(
            async token => await CountAsync(participantId, criteria, context.RunId, token) > 0,
            context.Item.Within,
            ct);

        if (settled)
        {
            return ScenarioAssertionOutcome.Pass(
                $"{participantId} received {criteria.Describe()} after {waited}s.");
        }

        var arrived = await AnyAsync(participantId, criteria, context.RunId, ct);

        return arrived
            ? ScenarioAssertionOutcome.Fail(
                FindingOwner.Isbm,
                $"{participantId} received no {criteria.Describe()} within {waited}s, " +
                "though other messages arrived in this run.",
                "The subscription is live, so the publication was either filtered out by " +
                "channel or topic, or never posted. Compare the publisher's outbox row.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Isbm,
                $"{participantId} received nothing at all within {waited}s.",
                "No message reached this participant in the whole run. Check the " +
                "subscription opened before the first publication, and that the session " +
                "survived.");
    }

    private async Task<int> CountAsync(
        string participantId, MessageCriteria criteria, Guid runId, CancellationToken ct)
    {
        await using var db = factory.Create(participantId);

        return await criteria.Apply(Inbound(db, runId)).CountAsync(ct);
    }

    private async Task<bool> AnyAsync(
        string participantId, MessageCriteria criteria, Guid runId, CancellationToken ct)
    {
        await using var db = factory.Create(participantId);

        return await Inbound(db, runId).AnyAsync(ct);
    }

    internal static IQueryable<MessageRecord> Inbound(ParticipantDbContext db, Guid runId) =>
        db.Set<MessageRecord>()
            .Where(m => m.Direction == MessageDirection.Inbound && m.ScenarioRunId == runId);
}

/// <summary>
/// Negative form, for filter and topic tests.
///
/// Waits out the full budget rather than returning as soon as it finds nothing:
/// "no message has arrived yet" is not the same claim as "no message arrives", and
/// only the second one is worth asserting.
/// </summary>
public sealed class MessageNotReceivedAssertion(IParticipantDbContextFactory factory)
    : IScenarioAssertion
{
    public string Name => "message_not_received";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var criteria = MessageCriteria.From(context);
        var budget = context.Item.Within ?? TimeSpan.Zero;

        var (arrived, waited) = await ScenarioWait.UntilAsync(
            async token =>
            {
                await using var db = factory.Create(participantId);
                return await criteria.Apply(
                    MessageReceivedAssertion.Inbound(db, context.RunId)).AnyAsync(token);
            },
            budget,
            ct);

        return arrived
            ? ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} received {criteria.Describe()} after {waited}s, " +
                "when it should not have.",
                "The filter under test is not excluding this message. Check the channel " +
                "binding and topic on both the publisher and the subscriber.")
            : ScenarioAssertionOutcome.Pass(
                $"{participantId} received no {criteria.Describe()} across {waited}s.");
    }
}

/// <summary>
/// The archived message passed XSD validation, not merely well-formedness.
///
/// A schema-invalid BOD that is nonetheless processed is the most expensive kind of
/// interoperability defect, because it works between two implementations that share
/// the same misreading and fails against every third party.
/// </summary>
public sealed class BodValidAssertion(IParticipantDbContextFactory factory) : IScenarioAssertion
{
    public string Name => "bod_valid";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();

        await using var db = factory.Create(participantId);

        var message = await MessageReceivedAssertion.Inbound(db, context.RunId)
            .OrderByDescending(m => m.OccurredAt)
            .FirstOrDefaultAsync(ct);

        if (message is null)
        {
            return ScenarioAssertionOutcome.Fail(
                FindingOwner.Isbm,
                $"{participantId} has no archived inbound message in this run to validate.",
                "Assert message_received before bod_valid, so a delivery failure is not " +
                "reported as a schema failure.");
        }

        var status = message.ValidationStatus;

        if (string.Equals(status, nameof(Oiie.Ccom.BodValidationStatus.Valid),
            StringComparison.OrdinalIgnoreCase))
        {
            return ScenarioAssertionOutcome.Pass(
                $"{message.Verb}{message.Noun} {message.BodId} validated against the schemas.");
        }

        if (string.Equals(status, nameof(Oiie.Ccom.BodValidationStatus.NotValidated),
            StringComparison.OrdinalIgnoreCase))
        {
            return ScenarioAssertionOutcome.Concern(
                FindingOwner.Sandbox,
                $"{message.Verb}{message.Noun} {message.BodId} was not validated.",
                "The packaged schemas were unavailable, so this run proves nothing about " +
                "schema conformance either way.");
        }

        return ScenarioAssertionOutcome.Fail(
            FindingOwner.Sandbox,
            $"{message.Verb}{message.Noun} {message.BodId} is {status}: {message.ValidationDetail}",
            "The BOD was built by this host, so the defect is in the builder or the " +
            "mapping, not on the wire.");
    }
}

/// <summary>Participant domain state holds a matching row.</summary>
public sealed class StoreContainsAssertion(
    IParticipantDbContextFactory factory) : IScenarioAssertion
{
    public string Name => "store_contains";

    public Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct) =>
        StoreQuery.EvaluateAsync(factory, context, expected: true, ct);
}

/// <summary>Negative form — the row is absent, or was removed.</summary>
public sealed class StoreNotContainsAssertion(
    IParticipantDbContextFactory factory) : IScenarioAssertion
{
    public string Name => "store_not_contains";

    public Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct) =>
        StoreQuery.EvaluateAsync(factory, context, expected: false, ct);
}

/// <summary>
/// A set of source/id pairs share one CIRID.
///
/// This is the assertion uc01 exists for. ENG's TIC-106, REG-LOCATION's LOC-000412
/// and MMS's 234443 are the same physical thing under three identifiers, and the
/// handover has only succeeded if the registry says so.
/// </summary>
public sealed class CirEquivalentAssertion(
    CirClient cir, ParticipantRegistry registry) : IScenarioAssertion
{
    public string Name => "cir_equivalent";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var entries = CirEntryReference.ReadAll(context).ToList();

        if (entries.Count < 2)
        {
            throw new ScenarioActionException(
                $"{context.Item.Describe()}: cir_equivalent needs at least two entries.");
        }

        // Resolution is performed from one participant's point of view, because that
        // is how a participant actually asks: it holds a foreign identifier and wants
        // the local one. Asking as an omniscient observer would prove something no
        // real caller can rely on.
        var asker = registry.Get(context.At ?? entries[0].ParticipantId);

        var resolved = new List<(CirEntryReference Entry, Guid? Cirid)>();
        var waited = 0;

        foreach (var entry in entries)
        {
            var sourceId = registry.Get(entry.ParticipantId).Config.SourceId;

            var (_, elapsed) = await ScenarioWait.UntilAsync(
                async token =>
                {
                    var result = await cir.ResolveAsync(asker, sourceId, entry.Id, token);
                    return result.Cirid is not null;
                },
                context.Item.Within,
                ct);

            waited += elapsed;

            var final = await cir.ResolveAsync(asker, sourceId, entry.Id, ct);
            resolved.Add((entry, final.Cirid));
        }

        var unresolved = resolved.Where(r => r.Cirid is null).ToList();

        if (unresolved.Count > 0)
        {
            return ScenarioAssertionOutcome.Fail(
                FindingOwner.Cir,
                $"After {waited}s these identifiers do not resolve: " +
                string.Join(", ", unresolved.Select(u => u.Entry.Describe())),
                "Either the owning participant never registered them, or the registry " +
                "accepted the registration without storing it. Compare the CirExchange " +
                "rows for the registering participants.");
        }

        var distinct = resolved.Select(r => r.Cirid!.Value).Distinct().ToList();

        return distinct.Count == 1
            ? ScenarioAssertionOutcome.Pass(
                $"{entries.Count} identifiers share CIRID {distinct[0]}.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Cir,
                "The identifiers resolve, but to " + distinct.Count + " different CIRIDs: " +
                string.Join(", ", resolved.Select(r => $"{r.Entry.Describe()} = {r.Cirid}")),
                "Each entry registered successfully but no equivalence was asserted " +
                "between them, so the registry holds three unrelated things rather than " +
                "one thing with three names.");
    }
}

/// <summary>An entry exists in the registry for a given source and id.</summary>
public sealed class CirRegisteredAssertion(
    CirClient cir, ParticipantRegistry registry) : IScenarioAssertion
{
    public string Name => "cir_registered";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var asker = registry.Get(participantId);

        var sourceId = context.GetString("source") is { } source
            ? registry.Get(source).Config.SourceId
            : asker.Config.SourceId;

        var id = context.RequireString("id");

        Guid? cirid = null;

        var (settled, waited) = await ScenarioWait.UntilAsync(
            async token =>
            {
                var result = await cir.ResolveAsync(asker, sourceId, id, token);
                cirid = result.Cirid;
                return cirid is not null;
            },
            context.Item.Within,
            ct);

        return settled
            ? ScenarioAssertionOutcome.Pass($"{sourceId}:{id} is registered as {cirid}.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Cir,
                $"{sourceId}:{id} did not resolve within {waited}s.",
                "The registration either never reached the registry or was not stored. " +
                "The participant's CirExchange rows hold what was sent and what came back.");
    }
}

/// <summary>
/// A participant's identity map holds a live binding for a foreign identifier.
///
/// Distinct from <c>cir_registered</c>: the registry may be perfectly correct while
/// the participant has not resolved it, and only the local binding makes the foreign
/// identifier usable in the participant's own screens.
/// </summary>
public sealed class IdentityResolvedAssertion(
    IParticipantDbContextFactory factory) : IScenarioAssertion
{
    public string Name => "identity_resolved";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var sourceId = context.RequireString("sourceId");
        var idInSource = context.RequireString("idInSource");

        IdentityMapEntry? entry = null;

        var (settled, waited) = await ScenarioWait.UntilAsync(
            async token =>
            {
                await using var db = factory.Create(participantId);

                entry = await db.Set<IdentityMapEntry>()
                    .Where(e => e.ForeignSourceId == sourceId && e.ForeignIdInSource == idInSource)
                    .OrderByDescending(e => e.ResolvedAt)
                    .FirstOrDefaultAsync(token);

                return entry is not null && entry.IsLive(DateTimeOffset.UtcNow);
            },
            context.Item.Within,
            ct);

        if (settled)
        {
            return ScenarioAssertionOutcome.Pass(
                $"{participantId} holds a live binding for {sourceId}:{idInSource} " +
                $"({entry!.Cirid}) after {waited}s.");
        }

        return entry is null
            ? ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} has no identity map entry for {sourceId}:{idInSource} " +
                $"after {waited}s.",
                "The participant never asked the registry. Check that the inbound handler " +
                "attempts resolution for foreign identifiers it does not recognise.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} holds a binding for {sourceId}:{idInSource} that is " +
                (entry.Invalidated
                    ? $"invalidated: {entry.InvalidatedReason}"
                    : $"stale since {entry.StaleAfter:u}."),
                "The binding exists but is not usable, so the participant should have " +
                "re-resolved it.");
    }
}

/// <summary>Publication intent reached an expected state — recorded, held, or posted.</summary>
public sealed class OutboxStateAssertion(IParticipantDbContextFactory factory) : IScenarioAssertion
{
    public string Name => "outbox_state";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var stateText = context.RequireString("state");

        if (!Enum.TryParse<OutboxState>(stateText, ignoreCase: true, out var expected))
        {
            throw new ScenarioActionException(
                $"{context.Item.Describe()}: '{stateText}' is not an outbox state. " +
                $"Expected one of {string.Join(", ", Enum.GetNames<OutboxState>())}.");
        }

        var verb = context.GetString("verb");
        var noun = context.GetString("noun");
        var count = context.GetDecimal("count") is { } value ? (int)value : 1;

        var matched = 0;

        var (settled, waited) = await ScenarioWait.UntilAsync(
            async token =>
            {
                await using var db = factory.Create(participantId);

                var query = db.Set<OutboxItem>()
                    .Where(o => o.ScenarioRunId == context.RunId && o.State == expected);

                if (verb is not null) query = query.Where(o => o.Verb == verb);
                if (noun is not null) query = query.Where(o => o.Noun == noun);

                matched = await query.CountAsync(token);
                return matched >= count;
            },
            context.Item.Within,
            ct);

        return settled
            ? ScenarioAssertionOutcome.Pass(
                $"{participantId} has {matched} outbox item(s) in {expected} after {waited}s.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} has {matched} outbox item(s) in {expected} after {waited}s, " +
                $"expected at least {count}.",
                expected == OutboxState.Posted
                    ? "Intent was recorded but never posted, which points at the dispatcher " +
                      "rather than at the release action."
                    : "The release action did not record the publication intent it should have.");
    }
}

/// <summary>An item is queued for a human decision.</summary>
public sealed class PendingWorkAssertion(IParticipantDbContextFactory factory) : IScenarioAssertion
{
    public string Name => "pending_work";

    public async Task<ScenarioAssertionOutcome> EvaluateAsync(
        ScenarioAssertionContext context, CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var count = context.GetDecimal("count") is { } value ? (int)value : 1;

        var matched = 0;

        var (settled, waited) = await ScenarioWait.UntilAsync(
            async token =>
            {
                await using var db = factory.Create(participantId);
                matched = await db.Set<PendingWorkItem>().CountAsync(token);
                return matched >= count;
            },
            context.Item.Within,
            ct);

        return settled
            ? ScenarioAssertionOutcome.Pass(
                $"{participantId} has {matched} pending work item(s) after {waited}s.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} has {matched} pending work item(s) after {waited}s, " +
                $"expected at least {count}.",
                "The inbound message either did not arrive or was applied without raising " +
                "the decision it should have.");
    }
}

/// <summary>Channel, topic, verb and noun as written on a message assertion.</summary>
internal sealed record MessageCriteria(
    string? ChannelUri, string? Topic, string? Verb, string? Noun)
{
    public static MessageCriteria From(ScenarioAssertionContext context) => new(
        context.GetString("channel"),
        context.GetString("topic"),
        context.GetString("verb"),
        context.GetString("noun"));

    public IQueryable<MessageRecord> Apply(IQueryable<MessageRecord> query)
    {
        // Channel is matched by suffix. Scenario files name the logical channel
        // (/Enterprise/Site/Eng) while the provider prefixes it per environment
        // (/OIIE-SANDBOX/Enterprise/Site/Eng), and an exact match would make every
        // scenario file environment-specific.
        if (ChannelUri is not null) query = query.Where(m => m.ChannelUri.EndsWith(ChannelUri));
        if (Topic is not null) query = query.Where(m => m.Topic == Topic);
        if (Verb is not null) query = query.Where(m => m.Verb == Verb);
        if (Noun is not null) query = query.Where(m => m.Noun == Noun);

        return query;
    }

    public string Describe()
    {
        var parts = new List<string>();

        if (Verb is not null || Noun is not null) parts.Add($"{Verb}{Noun}".Trim());
        if (ChannelUri is not null) parts.Add($"on {ChannelUri}");
        if (Topic is not null) parts.Add($"topic {Topic}");

        return parts.Count == 0 ? "any message" : string.Join(" ", parts);
    }
}

/// <summary>One <c>{ source, id }</c> pair from a CIR assertion's entry list.</summary>
internal sealed record CirEntryReference(string ParticipantId, string Id)
{
    public static IEnumerable<CirEntryReference> ReadAll(ScenarioAssertionContext context)
    {
        if (!context.Item.Args.TryGetValue("entries", out var raw) ||
            raw is not IEnumerable<object?> entries)
        {
            throw new ScenarioActionException(
                $"{context.Item.Describe()}: expected an 'entries' list of source/id pairs.");
        }

        foreach (var entry in entries)
        {
            if (entry is not IDictionary<string, object?> map ||
                !map.TryGetValue("source", out var source) ||
                !map.TryGetValue("id", out var id) ||
                source is null || id is null)
            {
                throw new ScenarioActionException(
                    $"{context.Item.Describe()}: every entry needs a source and an id.");
            }

            yield return new CirEntryReference(
                Convert.ToString(source, CultureInfo.InvariantCulture)!,
                Convert.ToString(id, CultureInfo.InvariantCulture)!);
        }
    }

    public string Describe() => $"{ParticipantId}:{Id}";
}

internal static class StoreQuery
{
    /// <summary>
    /// Counts rows of a named entity, optionally filtered by a SQL predicate.
    ///
    /// The entity name is resolved through the EF model rather than interpolated, so a
    /// scenario file cannot name a table that does not exist and cannot reach outside
    /// the participant's schema. The <c>where</c> clause is passed through as written:
    /// scenario files are code in git reviewed like any other, and inventing a
    /// restricted expression language would buy no safety a schema-qualified,
    /// least-privileged connection does not already provide.
    /// </summary>
    public static async Task<ScenarioAssertionOutcome> EvaluateAsync(
        IParticipantDbContextFactory factory,
        ScenarioAssertionContext context,
        bool expected,
        CancellationToken ct)
    {
        var participantId = context.RequireParticipant();
        var entity = context.RequireString("entity");
        var where = context.GetString("where");

        var matched = 0;

        var (settled, waited) = await ScenarioWait.UntilAsync(
            async token =>
            {
                await using var db = factory.Create(participantId);

                matched = await CountAsync(db, context.Item, entity, where, token);
                return expected ? matched > 0 : matched == 0;
            },
            context.Item.Within,
            ct);

        var predicate = where is null ? entity : $"{entity} where {where}";

        if (settled)
        {
            return ScenarioAssertionOutcome.Pass(expected
                ? $"{participantId} holds {matched} {predicate} after {waited}s."
                : $"{participantId} holds no {predicate} across {waited}s.");
        }

        return expected
            ? ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} holds no {predicate} after {waited}s.",
                "The message may have arrived and been rejected rather than applied. " +
                "The participant's Message rows carry the processing status and detail.")
            : ScenarioAssertionOutcome.Fail(
                FindingOwner.Sandbox,
                $"{participantId} holds {matched} {predicate} after {waited}s, expected none.",
                "The row exists when it should not, so either the filter under test did " +
                "not apply or a previous run's state was not reset.");
    }

    private static async Task<int> CountAsync(
        ParticipantDbContext db,
        ScenarioItem item,
        string entity,
        string? where,
        CancellationToken ct)
    {
        var type = db.Model.GetEntityTypes()
            .FirstOrDefault(t => string.Equals(
                t.ClrType.Name, entity, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            throw new ScenarioActionException(
                $"{item.Describe()}: '{entity}' is not an entity this participant stores.");
        }

        var table = type.GetTableName()
            ?? throw new ScenarioActionException(
                $"{item.Describe()}: '{entity}' is not mapped to a table.");

        var schema = type.GetSchema() ?? db.Schema;

        // Aliased Value because SqlQueryRaw<int> binds by column name.
        var sql = $"SELECT COUNT(*) AS Value FROM [{schema}].[{table}]" +
            (string.IsNullOrWhiteSpace(where) ? string.Empty : $" WHERE {where}");

        return await db.Database.SqlQueryRaw<int>(sql).SingleAsync(ct);
    }
}
