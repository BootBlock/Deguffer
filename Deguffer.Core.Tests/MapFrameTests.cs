using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A rectangle in fractions of what it is a part of: the arithmetic a folder opening and a zoom to a
/// shape are built from.
/// </summary>
public sealed class MapFrameTests
{
    private const double Precision = 1e-9;

    [Fact]
    public void AFrameIsCutToTheBoundsItIsAskedToStayInside()
    {
        AssertClose(new MapFrame(0, 0.25, 0.5, 0.75), new MapFrame(-0.5, 0.25, 1, 1).Clipped(MapFrame.Whole));
        AssertClose(new MapFrame(0.2, 0.3, 0.1, 0.1), new MapFrame(0.2, 0.3, 0.1, 0.1).Clipped(MapFrame.Whole));
    }

    [Fact]
    public void AFrameOutsideTheBoundsIsCutToNothing()
    {
        var clipped = new MapFrame(1.5, 0.2, 0.3, 0.3).Clipped(MapFrame.Whole);

        Assert.Equal(0, clipped.Width);
    }

    [Fact]
    public void AFrameMovesEachEdgeInAStraightLine()
    {
        var from = new MapFrame(0.2, 0.4, 0.1, 0.3);

        AssertClose(from, MapFrame.Between(from, MapFrame.Whole, 0));
        AssertClose(new MapFrame(0.1, 0.2, 0.55, 0.65), MapFrame.Between(from, MapFrame.Whole, 0.5));
        AssertClose(MapFrame.Whole, MapFrame.Between(from, MapFrame.Whole, 1));
    }

    /// <summary>
    /// Carrying the whole so that one part of it lands on another frame puts that part exactly there,
    /// and every other point of the whole goes with it in proportion.
    /// </summary>
    [Fact]
    public void CarryingAPartOntoAFramePutsItExactlyThere()
    {
        var part = new MapFrame(0.6, 0.2, 0.2, 0.4);
        var onto = new MapFrame(0.1, 0.05, 0.7, 0.9);

        var whole = part.Carried(onto);

        Assert.Equal(onto.X, whole.X + (part.X * whole.Width), Precision);
        Assert.Equal(onto.Y, whole.Y + (part.Y * whole.Height), Precision);
        Assert.Equal(onto.Width, part.Width * whole.Width, Precision);
        Assert.Equal(onto.Height, part.Height * whole.Height, Precision);
        AssertClose(MapFrame.Whole, part.Carried(part));
    }

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
