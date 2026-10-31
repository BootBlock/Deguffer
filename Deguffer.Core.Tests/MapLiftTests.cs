using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The part of a canvas the mask of a lifted shape covers: the shape's whole pixels and one more round
/// them, and never past the canvas, however far a zoomed shape's outline runs beyond it.
/// </summary>
public sealed class MapLiftTests
{
    private const int Width = 1176;

    private const int Height = 1064;

    /// <summary>A shape inside the canvas is covered to its whole pixels, with one more on each side for its smoothed edge.</summary>
    [Fact]
    public void AShapeIsCoveredToItsWholePixelsAndOneMore()
    {
        var area = MapLift.Area([Rectangle(1, 100.5f, 40.25f, 300.5f, 90.75f)], Width, Height);

        Assert.Equal(new CanvasRegion(99, 39, 203, 53), area);
    }

    /// <summary>
    /// The outline of a folder in a zoomed drawing runs far past the canvas on every side, and the mask
    /// is the canvas and no more: a mask the size of the outline would be memory nothing shows.
    /// </summary>
    [Fact]
    public void AShapeRunningPastTheCanvasIsHeldToIt()
    {
        var area = MapLift.Area([Rectangle(1, -50_000, -3e9f, 80_000, 3e9f)], Width, Height);

        Assert.Equal(new CanvasRegion(0, 0, Width, Height), area);
    }

    /// <summary>Every outline lifted counts, the shape under the pointer and the one the keyboard is on alike.</summary>
    [Fact]
    public void EveryOutlineIsCovered()
    {
        var area = MapLift.Area([Rectangle(1, 10, 10, 20, 20), Rectangle(2, 500, 600, 510, 620)], Width, Height);

        Assert.Equal(new CanvasRegion(9, 9, 502, 612), area);
    }

    /// <summary>Nothing is covered by no outline, nor by a shape wholly off the canvas, so nothing is lifted.</summary>
    [Fact]
    public void NothingIsCoveredByNoShapeOrOneOffTheCanvas()
    {
        Assert.Null(MapLift.Area([], Width, Height));
        Assert.Null(MapLift.Area([Rectangle(1, Width + 10, 10, Width + 90, 50)], Width, Height));
        Assert.Null(MapLift.Area([Rectangle(1, 10, -90, 50, -10)], Width, Height));
    }

    private static ExploreOutline Rectangle(int node, float left, float top, float right, float bottom) =>
        new(node, [new(left, top), new(right, top), new(right, bottom), new(left, bottom)]);
}
