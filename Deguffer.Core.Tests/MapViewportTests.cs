using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A zoom into the map: which part of the picture is on screen, how it moves from one part to another,
/// and where a drawing made at one zoom sits on a screen showing another. The last is what a click is
/// resolved through while a zoom is still moving, so it is what keeps a pick on the shape the screen
/// shows (§7.1).
/// </summary>
public sealed class MapViewportTests
{
    private const double Precision = 1e-9;

    [Fact]
    public void AViewportNobodySetIsTheWholePicture()
    {
        MapViewport unset = default;

        Assert.Equal(MapViewport.Whole, unset);
        Assert.Equal(1, unset.Zoom);
        Assert.True(unset.IsWhole);
        Assert.Equal((0.25, 0.75), unset.PictureAt(0.25, 0.75));
    }

    [Fact]
    public void ZoomingAtAPointKeepsWhatIsUnderItUnderIt()
    {
        var viewport = MapViewport.Anchored(4, pictureX: 0.4, pictureY: 0.55, screenX: 0.3, screenY: 0.6);

        var (x, y) = viewport.PictureAt(0.3, 0.6);

        Assert.Equal(4, viewport.Zoom, Precision);
        Assert.Equal(0.4, x, Precision);
        Assert.Equal(0.55, y, Precision);
    }

    [Theory]
    [InlineData(0.25, 1)]
    [InlineData(1, 1)]
    [InlineData(1000, MapViewport.MaximumZoom)]
    public void TheZoomIsHeldBetweenTheWholePictureAndTheMaximum(double asked, double expected)
    {
        Assert.Equal(expected, MapViewport.Anchored(asked, 0.5, 0.5, 0.5, 0.5).Zoom, Precision);
    }

    /// <summary>
    /// A zoom at the pointer near a corner would otherwise show past the picture's edge, where there is
    /// nothing to draw. The thing under the pointer moves as far as it has to instead.
    /// </summary>
    [Theory]
    [InlineData(0.01, 0.02, 0.9, 0.9)]
    [InlineData(0.99, 0.98, 0.1, 0.05)]
    [InlineData(0.5, 0.5, 0.5, 0.5)]
    public void AScreenNeverShowsPastTheEdgeOfThePicture(double pictureX, double pictureY, double screenX, double screenY)
    {
        var viewport = MapViewport.Anchored(8, pictureX, pictureY, screenX, screenY);

        AssertInside(viewport);
    }

    /// <summary>
    /// The move from one zoom to the next keeps the thing under the pointer still at every step, not
    /// only at the two ends. Blending the zoom and the position separately would pass the ends and
    /// swing the picture sideways in between.
    /// </summary>
    [Fact]
    public void EveryStepOfAZoomAtThePointerKeepsWhatIsUnderItStill()
    {
        var from = MapViewport.Anchored(2, 0.4, 0.45, 0.5, 0.5);
        var (pictureX, pictureY) = from.PictureAt(0.35, 0.65);
        var to = MapViewport.Anchored(16, pictureX, pictureY, 0.35, 0.65);

        for (var step = 0; step <= 20; step++)
        {
            var (x, y) = MapViewport.Between(from, to, step / 20.0).PictureAt(0.35, 0.65);

            Assert.Equal(pictureX, x, 1e-6);
            Assert.Equal(pictureY, y, 1e-6);
        }
    }

    /// <summary>A doubling takes as long wherever it falls, which is what reads as a steady speed.</summary>
    [Fact]
    public void TheZoomMovesByEqualRatiosRatherThanEqualSteps()
    {
        var from = MapViewport.Whole;
        var to = MapViewport.Anchored(16, 0.5, 0.5, 0.5, 0.5);

        Assert.Equal(4, MapViewport.Between(from, to, 0.5).Zoom, Precision);
        Assert.Equal(2, MapViewport.Between(from, to, 0.25).Zoom, Precision);
    }

    /// <summary>
    /// Between two viewports held inside the picture, every step is inside it too, including a move
    /// from one corner at one zoom to the opposite corner at another.
    /// </summary>
    [Fact]
    public void EveryStepOfAMoveStaysInsideThePicture()
    {
        var from = MapViewport.Anchored(3, 0, 0, 0, 0);
        var to = MapViewport.Anchored(40, 1, 1, 1, 1);

        for (var step = 0; step <= 50; step++)
        {
            AssertInside(MapViewport.Between(from, to, step / 50.0));
            AssertInside(MapViewport.Between(to, from, step / 50.0));
        }
    }

    [Fact]
    public void AMoveAtOneZoomSlidesAcross()
    {
        var from = MapViewport.Anchored(4, 0, 0, 0, 0);
        var to = MapViewport.Anchored(4, 1, 1, 1, 1);

        var halfway = MapViewport.Between(from, to, 0.5);

        Assert.Equal(4, halfway.Zoom, Precision);
        Assert.Equal((from.Left + to.Left) / 2, halfway.Left, Precision);
        Assert.Equal((from.Top + to.Top) / 2, halfway.Top, Precision);
    }

    [Fact]
    public void ADrawingOfWhatIsOnScreenSitsExactlyOverIt()
    {
        var viewport = MapViewport.Anchored(6, 0.3, 0.7, 0.2, 0.9);

        Assert.Equal(new MapPlacement(1, 0, 0), viewport.PlacementOf(viewport));
    }

    /// <summary>
    /// The case a click is resolved through while a zoom moves: the screen shows one viewport and the
    /// drawing on hand was made at another. Every point of the picture has to land, through the
    /// placement, on the point of the drawing that shows it, or a right-click picks a shape other than
    /// the one under the pointer and the menu acts on that (§7.1).
    /// </summary>
    [Theory]
    [InlineData(0.1, 0.2)]
    [InlineData(0.45, 0.5)]
    [InlineData(0.8, 0.33)]
    public void APlacementTakesAScreenPointToThePartOfTheDrawingThatShowsIt(double pictureX, double pictureY)
    {
        var drawn = MapViewport.Anchored(2, 0.4, 0.4, 0.5, 0.5);
        var shown = MapViewport.Anchored(5, 0.5, 0.35, 0.3, 0.6);

        var screen = ((pictureX - shown.Left) * shown.Zoom, (pictureY - shown.Top) * shown.Zoom);
        var inDrawing = ((pictureX - drawn.Left) * drawn.Zoom, (pictureY - drawn.Top) * drawn.Zoom);

        var (x, y) = shown.PlacementOf(drawn).InDrawing(screen.Item1, screen.Item2);

        Assert.Equal(inDrawing.Item1, x, Precision);
        Assert.Equal(inDrawing.Item2, y, Precision);
    }

    /// <summary>
    /// Zoomed out past what the drawing on hand covers, the edge of the screen is beyond it. That point
    /// has to come back outside the drawing, so the caller can say it is over nothing.
    /// </summary>
    [Fact]
    public void AScreenPointTheDrawingDoesNotReachFallsOutsideIt()
    {
        var drawn = MapViewport.Anchored(8, 0.5, 0.5, 0.5, 0.5);
        var shown = MapViewport.Whole;

        var (x, y) = shown.PlacementOf(drawn).InDrawing(0.02, 0.98);

        Assert.True(x < 0, $"a point left of the drawing came back at {x}");
        Assert.True(y > 1, $"a point below the drawing came back at {y}");
    }

    [Fact]
    public void AGlideStartsWhereTheScreenIsAndArrivesWhereItWasAsked()
    {
        var from = MapViewport.Whole;
        var to = MapViewport.Anchored(8, 0.2, 0.3, 0.5, 0.5);
        var start = TimeSpan.FromSeconds(10);
        var glide = new MapGlide(from, to, start);

        Assert.Equal(from, glide.At(start));
        Assert.False(glide.IsOverAt(start + (MapGlide.Duration / 2)));
        Assert.True(glide.IsOverAt(start + MapGlide.Duration));
        Assert.Equal(to, glide.At(start + MapGlide.Duration));
        Assert.Equal(to, glide.At(start + (MapGlide.Duration * 3)));
    }

    /// <summary>
    /// Eased out: most of the way there in the first half of the time, so the picture answers the
    /// wheel at once and settles rather than lagging behind it.
    /// </summary>
    [Fact]
    public void AGlideIsFastestAtTheStartAndNeverTurnsBack()
    {
        var to = MapViewport.Anchored(16, 0.5, 0.5, 0.5, 0.5);
        var glide = new MapGlide(MapViewport.Whole, to, TimeSpan.Zero);

        var halfway = glide.At(MapGlide.Duration / 2);

        Assert.True(halfway.Zoom > MapViewport.Between(MapViewport.Whole, to, 0.5).Zoom, "the glide was not eased out");

        var previous = 1.0;

        for (var step = 0; step <= 25; step++)
        {
            var zoom = glide.At(MapGlide.Duration * (step / 25.0)).Zoom;

            Assert.True(zoom >= previous, $"the zoom went back from {previous} to {zoom}");
            previous = zoom;
        }
    }

    private static void AssertInside(MapViewport viewport)
    {
        var span = 1 / viewport.Zoom;

        Assert.InRange(viewport.Left, 0, 1 - span + Precision);
        Assert.InRange(viewport.Top, 0, 1 - span + Precision);
    }
}
