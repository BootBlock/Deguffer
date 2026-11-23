using Deguffer.Core.Exploring;
using Deguffer.Testing;

namespace Deguffer.Benchmark.Tests;

/// <summary>
/// The snapshots an Explore run assembles are counted and timed where each is assembled, so the
/// report says what they cost the run rather than how long they waited for a thread.
/// </summary>
public sealed class TimedSnapshotsTests
{
    [Fact]
    public async Task EachAssemblingIsCountedAndItsTimeAdded()
    {
        var clock = new ManualTimeProvider();
        var snapshots = new TimedSnapshots(clock);

        await snapshots.Assembly.Start(() => clock.Advance(TimeSpan.FromMilliseconds(5)));
        await snapshots.Assembly.Start(() => clock.Advance(TimeSpan.FromMilliseconds(7)));

        Assert.Equal(new SnapshotTimes(2, TimeSpan.FromMilliseconds(12)), snapshots.Times);
    }

    [Fact]
    public void NothingAssembledCostsNothing() =>
        Assert.Equal(new SnapshotTimes(0, TimeSpan.Zero), new TimedSnapshots(new ManualTimeProvider()).Times);

    /// <summary>Ordered as a scan with no other view asks for its snapshots, so the run times what Explore draws first.</summary>
    [Fact]
    public void SnapshotsAreOrderedByName() =>
        Assert.Equal(ExploreChildOrder.ByName, new TimedSnapshots(new ManualTimeProvider()).Assembly.Order());
}
