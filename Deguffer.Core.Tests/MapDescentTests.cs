using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A folder opening on the map: its shape grows from where it was until it fills the screen, the old
/// picture stretches with it, and the folder's own drawing comes in over it on the same frame.
/// </summary>
public sealed class MapDescentTests
{
    private const double Precision = 1e-9;

    private static readonly TimeSpan Start = TimeSpan.FromSeconds(5);

    [Fact]
    public void AFolderOpensFromWhereItsShapeWasToTheWholeScreen()
    {
        var shape = new MapFrame(0.6, 0.1, 0.3, 0.2);
        var descent = new MapDescent(shape, Start);

        AssertClose(shape, descent.Opened(Start));
        AssertClose(MapFrame.Whole, descent.Departing(Start));
        Assert.Equal(0, descent.Opacity(Start));
        Assert.False(descent.IsOverAt(Start + (MapGlide.Duration / 2)));

        Assert.True(descent.IsOverAt(Start + MapGlide.Duration));
        AssertClose(MapFrame.Whole, descent.Opened(Start + MapGlide.Duration));
        Assert.Equal(1, descent.Opacity(Start + MapGlide.Duration));
    }

    /// <summary>
    /// At every step the old picture's copy of the shape lies exactly under the new drawing, so the one
    /// turns into the other rather than sliding past it.
    /// </summary>
    [Fact]
    public void TheOldShapeLiesUnderTheNewDrawingAtEveryStep()
    {
        var shape = new MapFrame(0.15, 0.55, 0.25, 0.4);
        var descent = new MapDescent(shape, Start);

        for (var step = 0; step <= 20; step++)
        {
            var now = Start + (MapGlide.Duration * (step / 20.0));
            var screen = descent.Departing(now);
            var opened = descent.Opened(now);

            Assert.Equal(opened.X, screen.X + (shape.X * screen.Width), Precision);
            Assert.Equal(opened.Y, screen.Y + (shape.Y * screen.Height), Precision);
            Assert.Equal(opened.Width, shape.Width * screen.Width, Precision);
            Assert.Equal(opened.Height, shape.Height * screen.Height, Precision);
        }
    }

    /// <summary>
    /// The old picture covers the whole screen at every step, so nothing bare shows round a folder as
    /// it opens. Including a shape a zoom had magnified past the screen's edges, which is grown from
    /// the part of it on the screen: grown whole, the old picture would shrink away from the edges.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0.5, 0.5)]
    [InlineData(0.7, 0.8, 0.3, 0.2)]
    [InlineData(0.3, 0.4, 0.01, 0.02)]
    [InlineData(-0.5, 0.2, 1.2, 1.5)]
    public void TheOldPictureCoversTheScreenThroughout(double x, double y, double width, double height)
    {
        var descent = new MapDescent(new MapFrame(x, y, width, height), Start);

        for (var step = 0; step <= 20; step++)
        {
            var screen = descent.Departing(Start + (MapGlide.Duration * (step / 20.0)));

            Assert.True(screen.X <= Precision, $"the left edge was bare at step {step}: {screen}");
            Assert.True(screen.Y <= Precision, $"the top edge was bare at step {step}: {screen}");
            Assert.True(screen.X + screen.Width >= 1 - Precision, $"the right edge was bare at step {step}: {screen}");
            Assert.True(screen.Y + screen.Height >= 1 - Precision, $"the bottom edge was bare at step {step}: {screen}");
        }
    }

    [Fact]
    public void TheNewDrawingComesInWithoutEverFadingBack()
    {
        var descent = new MapDescent(new MapFrame(0.2, 0.2, 0.2, 0.2), Start);
        var previous = 0.0;

        for (var step = 0; step <= 25; step++)
        {
            var opacity = descent.Opacity(Start + (MapGlide.Duration * (step / 25.0)));

            Assert.InRange(opacity, previous, 1);
            previous = opacity;
        }
    }

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }
}
