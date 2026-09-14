using System.Xml.Linq;
using Oiie.Participant.Engine.Contracts;
using Xunit;

namespace Tests;

/// §8 obligation 1 — the regression suite the domain expert's edits run against.
/// No broker, no database, no HTTP. This is only possible because Apply is pure.
public sealed class GoldenFileTests
{
    [Theory]
    [InlineData("syncsites-single")]
    [InlineData("syncsites-republished")]
    [InlineData("syncsegments-streetlight")]
    [InlineData("syncsegments-unmapped-class")]
    public void Transform_matches_golden_plan(string fixture)
    {
        var (bod, context, expected) = Fixtures.Load(fixture);
        var transform = Fixtures.TransformFor(fixture);

        var actual = transform.Apply(bod, context);

        Assert.Equal(Canonical(expected), Canonical(actual));
    }

    /// §8 obligation 5. Whatever the add plan wrote, the delete plan removes,
    /// keyed the same way. Checkable by diffing two artifacts instead of reading
    /// two code paths — an asymmetry here is a leak, not a style difference.
    [Fact]
    public void Delete_plan_keys_match_add_plan_keys()
    {
        var add    = Fixtures.TransformFor("syncsites-single").Apply(Fixtures.Bod("syncsites-single"), Fixtures.EmptyContext);
        var delete = Fixtures.TransformFor("syncsites-delete").Apply(Fixtures.Bod("syncsites-delete"), Fixtures.EmptyContext);

        var addKeys = add.Items.SelectMany(i => i.Ops).Select(o => (o.Resource, o.Match));
        var delKeys = delete.Items.SelectMany(i => i.Ops).Select(o => (o.Resource, o.Match));

        Assert.Equal(addKeys.OrderBy(k => k.Resource), delKeys.OrderBy(k => k.Resource));
    }

    private static string Canonical(WritePlan p) => /* stable serialisation */ p.ToString();
}
