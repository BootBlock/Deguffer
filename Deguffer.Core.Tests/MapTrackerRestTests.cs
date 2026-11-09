using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// When the map's tracker is at rest, which is when its bounds stop following its scale. Bounds that
/// follow the scale of a tracker at rest have the compositor work them out again at every frame for as
/// long as the process runs; bounds pinned while it moves leave a glide or a coast unheld at an edge.
/// </summary>
public sealed class MapTrackerRestTests
{
    [Fact]
    public void ATrackerNothingHasMovedIsAtRest() => Assert.True(new MapTrackerRest().AtRest);

    [Fact]
    public void AGlideIsAtRestOnceItsOwnRestIsReported()
    {
        var rest = new MapTrackerRest();

        rest.Launched(4);

        Assert.False(rest.AtRest);
        Assert.True(rest.Rested(4));
        Assert.True(rest.AtRest);
    }

    /// <summary>A rest on its way when the map set the tracker moving again is the rest of what that move replaced.</summary>
    [Fact]
    public void ARestFromBeforeTheLatestMoveLeavesItMoving()
    {
        var rest = new MapTrackerRest();

        rest.Launched(4);
        rest.Launched(7);

        Assert.False(rest.Rested(4), "the rest of a replaced glide was taken as the rest of the glide that replaced it");
        Assert.False(rest.AtRest);
        Assert.True(rest.Rested(7));
    }

    /// <summary>A jump asked during a glide stops it, and the tracker reports its rest as the jump's.</summary>
    [Fact]
    public void ARestOfARequestAfterTheMoveIsAtRest()
    {
        var rest = new MapTrackerRest();

        rest.Launched(4);

        Assert.True(rest.Rested(5));
        Assert.True(rest.AtRest);
    }

    [Fact]
    public void AHandMovesTheTrackerUntilItsRest()
    {
        var rest = new MapTrackerRest();

        rest.Taken();

        Assert.False(rest.AtRest);
        Assert.True(rest.Rested(0));
    }

    /// <summary>The map's rest from before the hand took the tracker reports nothing of the hand.</summary>
    [Fact]
    public void AHandTakingAGlideIsAtRestOnlyAtTheHandsRest()
    {
        var rest = new MapTrackerRest();

        rest.Taken();
        rest.Launched(3);

        Assert.False(rest.Rested(0), "the hand's rest was taken as the rest of a glide asked after it");
        Assert.True(rest.Rested(3));
    }

    /// <summary>
    /// A glide asked while a hand holds the tracker is refused and never starts, so the hand's rest is
    /// the last one to come. Waiting for the glide's would follow the scale for as long as the process
    /// runs.
    /// </summary>
    [Fact]
    public void AMoveTheTrackerRefusedWaitsForTheHandsRest()
    {
        var rest = new MapTrackerRest();

        rest.Taken();
        rest.Launched(3);
        rest.Refused(3);

        Assert.False(rest.AtRest);
        Assert.True(rest.Rested(0));
    }

    [Fact]
    public void ARefusalOfAnOlderRequestChangesNothing()
    {
        var rest = new MapTrackerRest();

        rest.Launched(3);
        rest.Refused(2);

        Assert.False(rest.Rested(0), "a refusal of a request the tracker was not moving for was taken as a hand");
        Assert.True(rest.Rested(3));
    }
}
