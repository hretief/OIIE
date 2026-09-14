using Xunit;

namespace Tests;

/// §8 obligations 3 and 6. Both paths are unreachable from the happy path by
/// construction, so the absence of failures in normal running says nothing about
/// whether either works.
public sealed class ReplayTests
{
    [Fact]
    public async Task Executing_the_same_plan_twice_changes_nothing()
    {
        var provider = Fixtures.LiveProvider();
        var plan     = Fixtures.SegmentPlan();

        var first  = await Fixtures.Executor(provider).ExecuteAsync(plan, Fixtures.SegmentContext, default);
        var before = await provider.SnapshotAsync();

        var second = await Fixtures.Executor(provider).ExecuteAsync(plan, Fixtures.SegmentContext, default);
        var after  = await provider.SnapshotAsync();

        Assert.IsType<PersistOutcome.Applied>(first);
        Assert.IsType<PersistOutcome.Applied>(second);
        Assert.Equal(before.RowCount, after.RowCount);
        Assert.Equal(before.NativeKeys, after.NativeKeys);   // no new IDENTITY values
        Assert.Equal(before.CirEntries, after.CirEntries);   // adopt-existing held
    }

    [Fact]
    public async Task Unreachable_provider_leaves_the_message_on_the_channel()
    {
        var outcome = await Fixtures.Executor(Fixtures.UnreachableProvider())
                                    .ExecuteAsync(Fixtures.SegmentPlan(), Fixtures.SegmentContext, default);

        var failed = Assert.IsType<PersistOutcome.Failed>(outcome);
        Assert.True(failed.Transient);   // NOT Rejected — this is the LTP-4 assertion
    }

    [Fact]
    public async Task Unresolvable_class_skips_the_item_without_failing_the_message()
    {
        // DR-030: no fallback table. The item is skipped and reported, never guessed.
        var outcome = await Fixtures.Executor(Fixtures.LiveProvider())
                                    .ExecuteAsync(Fixtures.SegmentPlan(), Fixtures.UnresolvedClassContext, default);

        var applied = Assert.IsType<PersistOutcome.Applied>(outcome);
        Assert.All(applied.Items, i => Assert.Equal(ItemDisposition.Skipped, i.Disposition));
    }
}
