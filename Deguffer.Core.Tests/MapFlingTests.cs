using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// How fast a mouse was dragging the picture as it let go, which is the speed the picture carries on
/// at. A hand that stopped first must fling nothing, or a picture placed with care drifts off.
/// </summary>
public sealed class MapFlingTests
{
    private static readonly TimeSpan Report = TimeSpan.FromMilliseconds(8);

    [Fact]
    public void AHandMovingSteadilyFlingsAtItsOwnSpeed()
    {
        var fling = new MapFling();
        var at = Drag(fling, from: TimeSpan.Zero, steps: 30, dx: 4, dy: -2);

        var (x, y) = fling.Release(at);

        // Four pixels every eight milliseconds is five hundred a second.
        Assert.Equal(500, x, 1e-6);
        Assert.Equal(-250, y, 1e-6);
    }

    [Fact]
    public void AHandThatStoppedBeforeLettingGoFlingsNothing()
    {
        var fling = new MapFling();
        var at = Drag(fling, from: TimeSpan.Zero, steps: 30, dx: 4, dy: 0);

        Assert.Equal((0, 0), fling.Release(at + MapFling.Rest + Report));
    }

    /// <summary>
    /// Only the end of the drag counts. A slow drag that ended in a flick flings at the flick's speed,
    /// and averaged over the whole drag it would hardly move.
    /// </summary>
    [Fact]
    public void OnlyTheLastMomentOfTheDragSetsTheSpeed()
    {
        var fling = new MapFling();
        var at = Drag(fling, from: TimeSpan.Zero, steps: 100, dx: 0.5, dy: 0);
        at = Drag(fling, from: at, steps: 20, dx: 10, dy: 0, startX: 50);

        var (x, _) = fling.Release(at);

        Assert.Equal(1250, x, 1e-6);
    }

    [Fact]
    public void APressWithNoMoveFlingsNothing()
    {
        var fling = new MapFling();

        fling.Track(10, 10, TimeSpan.FromSeconds(1));

        Assert.Equal((0, 0), fling.Release(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ANewPressForgetsTheLastDrag()
    {
        var fling = new MapFling();
        var at = Drag(fling, from: TimeSpan.Zero, steps: 30, dx: 4, dy: 0);

        fling.Clear();
        fling.Track(0, 0, at);

        Assert.Equal((0, 0), fling.Release(at));
    }

    /// <summary>A hand moving by (<paramref name="dx"/>, <paramref name="dy"/>) at each report from <paramref name="startX"/>. Says when it last reported.</summary>
    private static TimeSpan Drag(MapFling fling, TimeSpan from, int steps, double dx, double dy, double startX = 0)
    {
        var at = from;
        var x = startX;
        var y = 0.0;

        for (var step = 0; step < steps; step++)
        {
            at += Report;
            x += dx;
            y += dy;
            fling.Track(x, y, at);
        }

        return at;
    }
}
