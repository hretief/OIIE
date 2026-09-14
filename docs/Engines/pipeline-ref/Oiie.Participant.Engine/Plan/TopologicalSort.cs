using Oiie.Participant.Engine.Contracts;

namespace Oiie.Participant.Engine.Plan;

/// §4.3 rule 2 — document order is NOT execution order. A hand-ordered list
/// breaks silently the day someone inserts a row above its dependency.
internal static class TopologicalSort
{
    public static IReadOnlyList<PlanOp> Order(IReadOnlyList<PlanOp> ops)
    {
        var byId = ops.ToDictionary(o => o.Id);
        var state = ops.ToDictionary(o => o.Id, _ => 0);   // 0 new, 1 visiting, 2 done
        var ordered = new List<PlanOp>(ops.Count);

        void Visit(PlanOp op)
        {
            switch (state[op.Id])
            {
                case 2: return;
                case 1: throw new PlanFormatException($"cyclic dependency at op '{op.Id}'");
            }

            state[op.Id] = 1;
            foreach (var dep in Dependencies(op))
                if (byId.TryGetValue(dep, out var target))
                    Visit(target);
            state[op.Id] = 2;
            ordered.Add(op);
        }

        foreach (var op in ops) Visit(op);
        return ordered;
    }

    public static bool HasCycle(IReadOnlyList<PlanOp> ops)
    {
        try { Order(ops); return false; }
        catch (PlanFormatException) { return true; }
    }

    /// Only OpRef creates an ordering constraint. ContextRef ("$ownerId") is
    /// already resolved by E4 before any op runs, so it never orders anything.
    private static IEnumerable<string> Dependencies(PlanOp op) =>
        op.Fields.Where(f => f.OpRef is not null)
                 .Select(f => WritePlanValidator.SplitRef(f.OpRef!).Op);
}
