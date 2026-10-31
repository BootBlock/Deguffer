using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// One of the map's moves under way at a time: its end is reported once, whether it ran its course or
/// was finished early, and the late report of a move finished early never ends the move started since.
/// </summary>
public sealed class MapMoveRunTests
{
    [Fact]
    public void AMoveIsUnderWayFromItsStartUntilItEnds()
    {
        var moves = new MapMoveRun();

        Assert.False(moves.IsMoving);

        var run = moves.Start();

        Assert.True(moves.IsMoving);
        Assert.True(moves.Completes(run));
        Assert.True(moves.Finish());
        Assert.False(moves.IsMoving);
    }

    [Fact]
    public void AMoveEndsOnce()
    {
        var moves = new MapMoveRun();
        var run = moves.Start();

        Assert.True(moves.Finish());
        Assert.False(moves.Finish());
        Assert.False(moves.Completes(run));
    }

    [Fact]
    public void NothingUnderWayHasNothingToEnd() => Assert.False(new MapMoveRun().Finish());

    /// <summary>
    /// The race the guard is for: a move finished early by the next one, whose animations report
    /// themselves over while the next is still travelling.
    /// </summary>
    [Fact]
    public void TheLateEndOfAMoveFinishedEarlyLeavesTheNextMoving()
    {
        var moves = new MapMoveRun();
        var first = moves.Start();

        moves.Finish();

        var second = moves.Start();

        Assert.False(moves.Completes(first));
        Assert.True(moves.IsMoving);
        Assert.True(moves.Completes(second));
    }

    [Fact]
    public void AMoveUnderWayIsFinishedBeforeTheNextStarts()
    {
        var moves = new MapMoveRun();

        moves.Start();

        Assert.Throws<InvalidOperationException>(() => moves.Start());
    }
}
