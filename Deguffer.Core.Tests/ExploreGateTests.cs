using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Safety;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>When the Explore page may scan, relaunch elevated, or act on what is picked.</summary>
public sealed class ExploreGateTests
{
    private static readonly FakeVolumeInventory Volumes = new FakeVolumeInventory()
        .With(@"C:\")
        .With(@"G:\", features: VolumeFeatures.RemoteStorage);

    private static readonly ExploreTarget Scannable = new(@"C:\", null);

    /// <summary>
    /// §7.1: what is picked is acted on only while nothing else is under way, and only while the map
    /// shows the drive as it is now. A step of a replay is a picture of what the drive held.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void ASelectionActsOnlyOnTheDriveAsItIsNowWithNothingUnderWay(bool busy, bool replaying, bool mayAct)
    {
        Assert.Equal(mayAct, new ExploreGate(busy, replaying).SelectionMayAct);
    }

    /// <summary>
    /// A scan waits for the work under way, and never starts on a target the page refuses or on
    /// nothing at all. A replay on the map does not stop one: a scan replaces the picture.
    /// </summary>
    [Fact]
    public void AScanWaitsForTheWorkUnderWayAndATargetThePageWillScan()
    {
        Assert.True(new ExploreGate(Busy: false, Replaying: true).MayScan(Scannable, Volumes));
        Assert.False(new ExploreGate(Busy: true, Replaying: false).MayScan(Scannable, Volumes));
        Assert.False(new ExploreGate(Busy: false, Replaying: false).MayScan(new ExploreTarget(@"G:\", null), Volumes));
        Assert.False(new ExploreGate(Busy: false, Replaying: false).MayScan(new ExploreTarget(null, null), Volumes));
    }

    /// <summary>
    /// Elevating ends this process, so it waits for an action running on any page as well as for what
    /// a scan waits for. A scan does not wait for that action.
    /// </summary>
    [Fact]
    public void ElevatingWaitsForAnActionRunningOnAnyPage()
    {
        var running = new RunningActions();
        var idle = new ExploreGate(Busy: false, Replaying: false);

        Assert.True(idle.MayElevate(Scannable, Volumes, running));
        Assert.False(new ExploreGate(Busy: true, Replaying: false).MayElevate(Scannable, Volumes, running));
        Assert.False(idle.MayElevate(new ExploreTarget(@"G:\", null), Volumes, running));

        using (running.Begin(RunningAction.StorageClean))
        {
            Assert.False(idle.MayElevate(Scannable, Volumes, running));
            Assert.True(idle.MayScan(Scannable, Volumes));
        }

        Assert.True(idle.MayElevate(Scannable, Volumes, running));
    }
}
