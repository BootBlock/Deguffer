using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the pointer's light lights, and so what is shaded again as it moves: everything it lit before
/// and everything it lights now, and nothing else, so a moving light leaves no trace behind it and
/// costs no more than its own reach.
/// </summary>
public sealed class PointerLightTests
{
    /// <summary>The light reaches the same distance on screen at any display scale.</summary>
    [Fact]
    public void TheReachFollowsTheDisplayScale()
    {
        Assert.Equal(PointerLight.ReachInDips, PointerLight.At(0, 0, 1).Reach);
        Assert.Equal(PointerLight.ReachInDips * 1.5f, PointerLight.At(0, 0, 1.5).Reach);
    }

    /// <summary>
    /// The light lights a square round its point, wide enough to hold every pixel within its reach, and
    /// cut to the canvas.
    /// </summary>
    [Fact]
    public void ItLightsTheSquareWithinItsReachOnTheCanvas()
    {
        Assert.Equal(new CanvasRegion(90, 190, 22, 22), new PointerLight(100.5f, 200.5f, 10).Lit(1000, 1000));
        Assert.Equal(new CanvasRegion(0, 0, 16, 16), new PointerLight(5, 5, 10).Lit(1000, 1000));
        Assert.Equal(new CanvasRegion(990, 0, 10, 16), new PointerLight(1000, 5, 10).Lit(1000, 1000));
    }

    /// <summary>A light whose reach misses the canvas lights none of it.</summary>
    [Fact]
    public void ALightOffTheCanvasLightsNothing() =>
        Assert.Null(new PointerLight(-50, 500, 10).Lit(1000, 1000));

    /// <summary>
    /// A light that moves is shaded again over everything it lit and everything it now lights, so what
    /// it left goes back to the picture without it.
    /// </summary>
    [Fact]
    public void AMoveShadesWhereItWasAndWhereItIs()
    {
        var before = new PointerLight(100, 100, 10);
        var after = new PointerLight(130, 105, 10);

        Assert.Equal(
            new CanvasRegion(90, 90, 51, 26),
            PointerLight.Between(before, after, 1000, 1000));
    }

    /// <summary>A light that comes or goes is shaded over its own reach alone.</summary>
    [Fact]
    public void ALightThatComesOrGoesShadesItsOwnReach()
    {
        var light = new PointerLight(100, 100, 10);

        Assert.Equal(light.Lit(1000, 1000), PointerLight.Between(null, light, 1000, 1000));
        Assert.Equal(light.Lit(1000, 1000), PointerLight.Between(light, null, 1000, 1000));
        Assert.Null(PointerLight.Between(null, null, 1000, 1000));
    }
}
