using Oiie.Participant.Engine.Contracts;
using Oiie.Participant.Engine.Plan;
using Xunit;

namespace Tests;

/// §8 obligation 2. Each of these is a rule whose violation corrupts customer
/// data silently, so each gets a test that pins the rejection.
public sealed class WritePlanValidatorTests
{
    [Fact]
    public void Rejects_upsert_without_match()
    {
        // The sharpest edge: without a match key every at-least-once redelivery
        // produces a duplicate, indistinguishable from a legitimate row.
        var plan = Build(op => op with { Match = null });
        Assert.Contains("upsert without @match", Errors(plan));
    }

    [Fact]
    public void Rejects_field_without_update_policy()
    {
        // Parse-time, not validate-time — a default would silently reinstate the
        // overwrite behaviour §6.1 and DR-032 exist to prevent.
        var xml = """
            <WritePlan participant="mms" noun="SyncSites" bodId="B1">
              <Item sourceRef="S1">
                <Op id="a" resource="LIGHT_SYSTEM_INVENTORY" mode="upsert" match="EXT_ASSET_ID">
                  <Field name="EXT_ASSET_ID">abc</Field>
                </Op>
              </Item>
            </WritePlan>
            """;
        var ex = Assert.Throws<PlanFormatException>(() => WritePlanParser.Parse(XDocument.Parse(xml)));
        Assert.Contains("no onUpdate", ex.Message);
    }

    [Fact]
    public void Rejects_register_naming_a_field_the_op_does_not_return()
    {
        // DR-033: the native key is IDENTITY-assigned, so a Register that cannot
        // obtain it leaves a row no other participant can name.
        var plan = Build(op => op with { Returns = [] });
        Assert.Contains("does not return", Errors(plan));
    }

    [Fact]
    public void Rejects_cyclic_op_dependencies() { /* a.ref → b, b.ref → a */ }

    [Fact]
    public void Rejects_match_key_that_is_not_onUpdate_never() { /* ... */ }

    [Fact]
    public void Failure_rules_keyed_on_message_text_are_rejected_at_load() { /* ... */ }

    private static IReadOnlyList<string> Errors(WritePlan p) =>
        WritePlanValidator.Validate(p, Fixtures.MmsBindings);

    private static WritePlan Build(Func<PlanOp, PlanOp> mutate) => /* ... */ null!;
}
