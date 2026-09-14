using Oiie.Participant.Engine.Contracts;
using Oiie.Participant.Engine.Failures;
using Oiie.Participant.Engine.Resolution;

namespace Oiie.Participant.Engine.Plan;

/// E5/E6. Contains no participant name and no CCOM noun (§1).
public sealed class PlanExecutor(
    IResourceWriter writer,
    ResourceBindings bindings,
    ICirRegistrar cir,
    IFailureClassifier failures,
    ILogger<PlanExecutor> log)
{
    public async Task<PersistOutcome> ExecuteAsync(
        WritePlan plan, TransformContext context, CancellationToken ct)
    {
        var errors = WritePlanValidator.Validate(plan, bindings);
        if (errors.Count > 0)
            // A malformed plan is a defect in an artifact, not a runtime condition.
            // Replay will not help, so it is terminal.
            return new PersistOutcome.Rejected(string.Join("; ", errors));

        var results = new List<PersistedItem>(plan.Items.Count);

        foreach (var item in plan.Items)
        {
            try
            {
                results.Add(await ExecuteItemAsync(item, context, ct));
            }
            catch (ItemSkipped skip)
            {
                // §4.3 rule 3 — one unresolvable segment does not fail the message.
                log.LogInformation("skipping {Item}: {Reason}", item.SourceRef, skip.Reason);
                results.Add(new PersistedItem(
                    item.SourceRef, new Dictionary<string, string>(),
                    ItemDisposition.Skipped, skip.Reason));
            }
            catch (Exception ex)
            {
                var verdict = failures.Classify(ex);
                // Transient fails the WHOLE message: replay is per-message, and a
                // partially applied plan is safe to re-run precisely because every
                // op is an upsert on a declared key (§4.3 rule 4).
                return verdict.Transient
                    ? new PersistOutcome.Failed(true, verdict.Reason)
                    : new PersistOutcome.Failed(false, verdict.Reason);
            }
        }

        return new PersistOutcome.Applied(results);
    }

    private async Task<PersistedItem> ExecuteItemAsync(
        PlanItem item, TransformContext context, CancellationToken ct)
    {
        var returned = new Dictionary<string, string>();   // "op.field" → value

        foreach (var op in TopologicalSort.Order(item.Ops))
        {
            var resource = Deref(op.Resource, context, returned);
            var binding  = bindings.Get(resource);

            if (op.Mode is OpMode.Delete)
            {
                var key = ValueOf(op.Fields.Single(f => f.Name == op.Match), context, returned);
                await writer.DeleteAsync(resource, op.Match!, key, ct);
                continue;
            }

            var matchValue = ValueOf(op.Fields.Single(f => f.Name == op.Match), context, returned);
            var existing   = binding.Read is null
                ? null
                : await writer.ReadAsync(resource, op.Match!, matchValue, ct);

            if (op.Mode is OpMode.Lookup)
            {
                if (existing is null) throw new ItemSkipped($"lookup '{resource}' found no {matchValue}");
                foreach (var r in op.Returns)
                    returned[$"{op.Id}.{r.As}"] = existing[r.Name]
                        ?? throw new ItemSkipped($"'{resource}' returned no {r.Name}");
                continue;
            }

            var payload = BuildPayload(op, context, returned, isInsert: existing is null);
            var state   = await writer.WriteAsync(resource, payload, ct);

            foreach (var r in op.Returns)
                returned[$"{op.Id}.{r.As}"] = state[r.Name]
                    ?? throw new InvalidOperationException(
                        $"'{resource}' did not return '{r.Name}'; the plan cannot register it.");
        }

        foreach (var reg in item.Registrations)
        {
            // DR-033 — the native key goes back into CIR. Until it does, the
            // participant holds a row no one else can name. adopt-existing lets
            // the new entry take the existing CIRID rather than displace it.
            await cir.RegisterAsync(
                cirId:      Deref(reg.CirId, context, returned),
                category:   reg.Category,
                idInSource: returned[reg.IdInSourceOpRef],
                merge:      reg.Merge,
                ct);
        }

        return new PersistedItem(item.SourceRef, returned, ItemDisposition.Written);
    }

    /// §4.2 — this is where the update policy actually bites.
    private static Dictionary<string, string> BuildPayload(
        PlanOp op, TransformContext context,
        IReadOnlyDictionary<string, string> returned, bool isInsert)
    {
        var payload = new Dictionary<string, string>();

        foreach (var f in op.Fields)
        {
            var include = f.OnUpdate switch
            {
                UpdatePolicy.Set         => true,
                UpdatePolicy.Never       => isInsert || f.Name == op.Match,
                UpdatePolicy.SetIfAbsent => isInsert,
                _ => throw new ArgumentOutOfRangeException()
            };

            if (include) payload[f.Name] = ValueOf(f, context, returned);
        }

        // NOTE the absence of any "write null for unmapped columns" branch.
        // A field the transform omitted is never sent. DR-032: a nullable column
        // encoding the receiving system's own organisation is that system's to
        // populate, and writing null would erase what MMS users chose.
        return payload;
    }

    private static string ValueOf(
        PlanField f, TransformContext context, IReadOnlyDictionary<string, string> returned)
    {
        if (f.Literal is not null) return f.Literal;

        if (f.ContextRef is { } key)
        {
            if (context.Unresolved.Contains(key))
                throw new ItemSkipped($"'{key}' did not resolve");
            return context.Resolved[key];
        }

        return returned[f.OpRef!];
    }

    private static string Deref(
        string token, TransformContext context, IReadOnlyDictionary<string, string> returned)
    {
        if (!token.StartsWith('$')) return token;
        var key = token[1..];
        if (context.Unresolved.Contains(key))
            throw new ItemSkipped($"'{key}' did not resolve");
        return context.Resolved[key];
    }
}

internal sealed class ItemSkipped(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
