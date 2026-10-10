using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A rectangle in fractions of what it is a part of: the arithmetic a change of folder and a zoom to
/// a shape are built from.
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


    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
