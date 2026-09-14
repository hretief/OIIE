using Oiie.Participant.Engine.Contracts;

namespace Oiie.Participant.Engine.Plan;

/// §4.3 — structural rules, enforced before a single HTTP call is made.
/// Every rule here exists because breaking it corrupts customer data silently.
public static class WritePlanValidator
{
    public static IReadOnlyList<string> Validate(WritePlan plan, ResourceBindings bindings)
    {
        var errors = new List<string>();

        foreach (var item in plan.Items)
        {
            var opIds = item.Ops.Select(o => o.Id).ToList();
            if (opIds.Count != opIds.Distinct().Count())
                errors.Add($"{item.SourceRef}: duplicate op id");

            foreach (var op in item.Ops)
            {
                // Rule 1. The sharpest edge in the whole design (§6.1.1).
                // An unconditional insert turns every at-least-once redelivery
                // into a duplicate row, indistinguishable from a legitimate one.
                if (op.Mode is OpMode.Upsert && string.IsNullOrEmpty(op.Match))
                    errors.Add($"{item.SourceRef}/{op.Id}: upsert without @match");

                if (op.Match is { } m && op.Fields.All(f => f.Name != m))
                    errors.Add($"{item.SourceRef}/{op.Id}: match key '{m}' is not among the fields");

                if (op.Fields.Any(f => f.Name == op.Match && f.OnUpdate != UpdatePolicy.Never))
                    errors.Add($"{item.SourceRef}/{op.Id}: match key must be onUpdate=\"never\"");

                // A literal resource must exist in bindings; a "$ref" resource is
                // checked at execution, once the resolution has a value.
                if (!op.Resource.StartsWith('$') && !bindings.Knows(op.Resource))
                    errors.Add($"{item.SourceRef}/{op.Id}: no binding for resource '{op.Resource}'");

                foreach (var f in op.Fields.Where(f => f.OpRef is not null))
                {
                    var (refOp, refField) = SplitRef(f.OpRef!);
                    var target = item.Ops.FirstOrDefault(o => o.Id == refOp);
                    if (target is null)
                        errors.Add($"{item.SourceRef}/{op.Id}: ref to unknown op '{refOp}'");
                    else if (target.Returns.All(r => r.As != refField))
                        errors.Add($"{item.SourceRef}/{op.Id}: op '{refOp}' does not return '{refField}'");
                }
            }

            foreach (var reg in item.Registrations)
            {
                var (refOp, refField) = SplitRef(reg.IdInSourceOpRef);
                var target = item.Ops.FirstOrDefault(o => o.Id == refOp);
                if (target is null)
                    errors.Add($"{item.SourceRef}: <Register> refs unknown op '{refOp}'");
                else if (target.Returns.All(r => r.As != refField))
                    // DR-033: MMS assigns LIGHT_UNIT_ID by IDENTITY, so it is known
                    // only from the response. A Register that cannot get it leaves a
                    // row no other participant can name.
                    errors.Add($"{item.SourceRef}: op '{refOp}' does not return '{refField}'");
            }

            // Rule 2. Cycles must be caught here, not discovered at runtime.
            if (TopologicalSort.HasCycle(item.Ops))
                errors.Add($"{item.SourceRef}: cyclic dependency between ops");
        }

        return errors;
    }

    internal static (string Op, string Field) SplitRef(string opRef)
    {
        var i = opRef.IndexOf('.');
        return i < 0 ? (opRef, "") : (opRef[..i], opRef[(i + 1)..]);
    }
}
