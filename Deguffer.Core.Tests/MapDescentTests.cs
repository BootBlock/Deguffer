using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A change of folder on the map: the camera flies from the outer picture filling the screen into the
/// inner folder's shape until its own picture fills it, or back out, and the inner picture comes in
/// over the outer one on the same frame, or goes.
/// </summary>
public sealed class MapDescentTests
{
    private const double Precision = 1e-9;

    [Fact]
    public void TheFlightRunsFromTheFoldersShapeToTheWholeScreen()
    {
        var shape = new MapFrame(0.6, 0.1, 0.3, 0.2);
        var descent = new MapDescent(shape, Travels: true);

        AssertClose(shape, descent.Inner(0));
        AssertClose(MapFrame.Whole, descent.Outer(0));
        Assert.Equal(0, descent.Opacity(0));

        // Exactly, not nearly: the map's camera takes the inner picture over from here, and a rounding
        // error would move it as it did.
        Assert.Equal(MapFrame.Whole, descent.Inner(1));
        Assert.Equal(MapFrame.Whole, descent.Outer(0));
        AssertClose(shape.Carried(MapFrame.Whole), descent.Outer(1));
        Assert.Equal(1, descent.Opacity(1));
    }

    /// <summary>
    /// The camera zooms about the one point of the screen that the folder's shape and the whole screen
    /// agree on, on each axis, so nothing slides sideways on the way: a straight line between the two
    /// frames would drift the outer picture across the screen as it grew.
    /// </summary>
    [Theory]
    [InlineData(0.6, 0.1, 0.3, 0.2)]
    [InlineData(0, 0.5, 0.25, 0.5)]
    [InlineData(0.9, 0.9, 0.01, 0.05)]
    public void ThePointTheTwoEndsAgreeOnStaysStill(double x, double y, double width, double height)
    {
        var descent = new MapDescent(new MapFrame(x, y, width, height), Travels: true);
        var stillX = x / (1 - width);
        var stillY = y / (1 - height);

        for (var step = 0; step <= 20; step++)
        {
            var outer = descent.Outer(step / 20.0);

            Assert.Equal(stillX, outer.X + (stillX * outer.Width), Precision);
            Assert.Equal(stillY, outer.Y + (stillY * outer.Height), Precision);
        }
    }

    /// <summary>
    /// The zoom goes by equal ratios rather than equal steps, so a flight many folders deep spends as
    /// long on each doubling: halfway through, the outer picture is magnified by the square root of
    /// what it is at the end, not by half of it.
    /// </summary>
    [Fact]
    public void TheZoomMovesByEqualRatios()
    {
        var descent = new MapDescent(new MapFrame(0.4, 0.45, 0.01, 0.04), Travels: true);
        var end = descent.Outer(1);
        var halfway = descent.Outer(0.5);

        Assert.Equal(Math.Sqrt(end.Width), halfway.Width, Precision);
        Assert.Equal(Math.Sqrt(end.Height), halfway.Height, Precision);
    }

    /// <summary>
    /// The outer picture covers the whole screen at every step, so nothing bare shows round the folder
    /// on the way in or out. Including a shape a zoom had magnified past the screen's edges, which is
    /// flown to from the part of it on the screen: flown to whole, the outer picture would shrink away
    /// from the edges.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0.5, 0.5)]
    [InlineData(0.7, 0.8, 0.3, 0.2)]
    [InlineData(0.3, 0.4, 0.01, 0.02)]
    [InlineData(-0.5, 0.2, 1.2, 1.5)]
    public void TheOuterPictureCoversTheScreenThroughout(double x, double y, double width, double height)
    {
        var descent = new MapDescent(new MapFrame(x, y, width, height), Travels: true);

        for (var step = 0; step <= 20; step++)
        {
            var screen = descent.Outer(step / 20.0);

            Assert.True(screen.X <= Precision, $"the left edge was bare at step {step}: {screen}");
            Assert.True(screen.Y <= Precision, $"the top edge was bare at step {step}: {screen}");
            Assert.True(screen.X + screen.Width >= 1 - Precision, $"the right edge was bare at step {step}: {screen}");
            Assert.True(screen.Y + screen.Height >= 1 - Precision, $"the bottom edge was bare at step {step}: {screen}");
        }
    }

    /// <summary>
    /// A spring turned round near either end can carry the progress a hair past it. The pictures stay
    /// at that end rather than the outer one shrinking off the screen's edges.
    /// </summary>
    [Fact]
    public void AProgressPastEitherEndStaysAtThatEnd()
    {
        var shape = new MapFrame(0.2, 0.3, 0.4, 0.5);
        var descent = new MapDescent(shape, Travels: true);

        AssertClose(MapFrame.Whole, descent.Outer(-0.05));
        AssertClose(MapFrame.Whole, descent.Inner(1.05));
        Assert.Equal(0, descent.Opacity(-0.05));
        Assert.Equal(1, descent.Opacity(1.05));
    }

    [Fact]
    public void TheInnerPictureComesInWithoutEverFadingBack()
    {
        var descent = new MapDescent(new MapFrame(0.2, 0.2, 0.2, 0.2), Travels: true);
        var previous = 0.0;

        for (var step = 0; step <= 25; step++)
        {
            var opacity = descent.Opacity(step / 25.0);

            Assert.InRange(opacity, previous, 1);
            previous = opacity;
        }
    }

    /// <summary>
    /// With animation effects off nothing grows or stretches: both pictures fill the screen where they
    /// are throughout, and the inner one fades in over the outer, or out of it.
    /// </summary>
    [Fact]
    public void WithMotionOffThePicturesStayInPlace()
    {
        var descent = new MapDescent(new MapFrame(0.6, 0.1, 0.3, 0.2), Travels: false);

        for (var step = 0; step <= 20; step++)
        {
            Assert.Equal(MapFrame.Whole, descent.Inner(step / 20.0));
            Assert.Equal(MapFrame.Whole, descent.Outer(step / 20.0));
        }

        Assert.Equal(0.5, descent.Opacity(0.5), Precision);
    }

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
