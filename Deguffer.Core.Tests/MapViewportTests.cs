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

    /// <summary>
    /// Including a zoom asked for past the maximum, as a run of wheel turns at the limit asks. The
    /// position has to be worked out at the zoom that is kept, or the picture slides out from under the
    /// pointer at the one moment nothing else is moving.
    /// </summary>
    [Theory]
    [InlineData(4, 4)]
    [InlineData(1000, MapViewport.MaximumZoom)]
    public void ZoomingAtAPointKeepsWhatIsUnderItUnderIt(double asked, double kept)
    {
        var viewport = MapViewport.Anchored(asked, pictureX: 0.4, pictureY: 0.55, screenX: 0.3, screenY: 0.6);

        var (x, y) = viewport.PictureAt(0.3, 0.6);

        Assert.Equal(kept, viewport.Zoom, Precision);
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
    /// Between two viewports inside the picture, the path never leaves it, including a move from one
    /// corner at one zoom to the opposite corner at another. Asked through the one point both ends
    /// agree on, which every step must keep still: a step that went outside and was held back in would
    /// move it. Checking the bounds instead would only prove that the result is held inside, which it
    /// always is.
    /// </summary>
    [Fact]
    public void AMoveFromCornerToCornerNeverLeavesThePicture()
    {
        var from = MapViewport.Anchored(3, 0, 0, 0, 0);
        var to = MapViewport.Anchored(40, 1, 1, 1, 1);

        // The screen point that shows the same part of the picture at both ends.
        var still = (to.Left - from.Left) / ((1 / from.Zoom) - (1 / to.Zoom));
        var (pictureX, pictureY) = from.PictureAt(still, still);

        Assert.Equal(to.PictureAt(still, still).X, pictureX, Precision);

        for (var step = 0; step <= 50; step++)
        {
            foreach (var (a, b) in new[] { (from, to), (to, from) })
            {
                var (x, y) = MapViewport.Between(a, b, step / 50.0).PictureAt(still, still);

                Assert.Equal(pictureX, x, 1e-9);
                Assert.Equal(pictureY, y, 1e-9);
            }
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

    /// <summary>
    /// A double-clicked shape fills the screen the one way its shape allows, and sits in the middle of
    /// the other. Its edges are asked for through the screen, which is what the reader sees.
    /// </summary>
    [Theory]
    [InlineData(0.2, 0.3, 0.4, 0.1)]
    [InlineData(0.55, 0.1, 0.05, 0.25)]
    [InlineData(0.3, 0.3, 0.2, 0.2)]
    public void FittingAShapeFillsTheScreenWithItAlongItsLongerSide(double x, double y, double width, double height)
    {
        var viewport = MapViewport.Fitting(new MapFrame(x, y, width, height));

        var (left, top) = viewport.PictureAt(0, 0);
        var (right, bottom) = viewport.PictureAt(1, 1);

        Assert.Equal(Math.Min(1 / width, 1 / height), viewport.Zoom, Precision);
        Assert.Equal(x + (width / 2), (left + right) / 2, Precision);
        Assert.Equal(y + (height / 2), (top + bottom) / 2, Precision);

        if (width >= height)
        {
            Assert.Equal(x, left, Precision);
            Assert.Equal(x + width, right, Precision);
        }
        else
        {
            Assert.Equal(y, top, Precision);
            Assert.Equal(y + height, bottom, Precision);
        }
    }

    /// <summary>A shape smaller than the maximum can show whole is shown at the maximum, still centred on it.</summary>
    [Fact]
    public void FittingAShapeTooSmallToFillTheScreenStopsAtTheMaximumZoom()
    {
        var viewport = MapViewport.Fitting(new MapFrame(0.5, 0.4, 0.001, 0.002));

        var (centreX, centreY) = viewport.PictureAt(0.5, 0.5);

        Assert.Equal(MapViewport.MaximumZoom, viewport.Zoom, Precision);
        Assert.Equal(0.5005, centreX, Precision);
        Assert.Equal(0.401, centreY, Precision);
    }

    /// <summary>A shape in a corner is fitted without the screen showing past the picture's edge.</summary>
    [Fact]
    public void FittingAShapeAtAnEdgeStaysInsideThePicture()
    {
        var viewport = MapViewport.Fitting(new MapFrame(0.9, 0, 0.1, 0.05));

        Assert.Equal(10, viewport.Zoom, Precision);
        AssertInside(viewport);
        Assert.Equal(0.9, viewport.Left, Precision);
        Assert.Equal(0, viewport.Top, Precision);
    }

    /// <summary>
    /// A drag keeps the part of the picture under the hand under it, wherever the hand goes, which is
    /// what makes the picture feel held rather than scrolled.
    /// </summary>
    [Theory]
    [InlineData(0.1, -0.05)]
    [InlineData(-0.2, 0.15)]
    public void DraggingKeepsWhatIsUnderTheHandUnderIt(double byX, double byY)
    {
        var viewport = MapViewport.Anchored(4, 0.5, 0.5, 0.5, 0.5);
        var (pictureX, pictureY) = viewport.PictureAt(0.4, 0.6);

        var (x, y) = viewport.Panned(byX, byY).PictureAt(0.4 + byX, 0.6 + byY);

        Assert.Equal(pictureX, x, Precision);
        Assert.Equal(pictureY, y, Precision);
    }

    /// <summary>A drag past the picture's edge stops there, and the whole picture cannot be dragged at all.</summary>
    [Fact]
    public void ADragStopsAtTheEdgeOfThePicture()
    {
        var viewport = MapViewport.Anchored(4, 0.1, 0.1, 0.5, 0.5);

        var dragged = viewport.Panned(3, -3);

        AssertInside(dragged);
        Assert.Equal(0, dragged.Left, Precision);
        Assert.Equal(1 - (1 / dragged.Zoom), dragged.Top, Precision);
        Assert.Equal(MapViewport.Whole, MapViewport.Whole.Panned(0.3, 0.2));
    }

    /// <summary>
    /// A shape of a drawing made at one zoom, found on a screen showing another, is the same part of
    /// the picture as the drawing says it is. This is how a double-click during a zoom or a drag finds the part
    /// to fit, so a wrong answer zooms to a shape the reader did not click.
    /// </summary>
    [Fact]
    public void AShapeTakenFromADrawingToTheScreenIsTheSamePartOfThePicture()
    {
        var drawn = MapViewport.Anchored(3, 0.4, 0.4, 0.5, 0.5);
        var shown = MapViewport.Anchored(5, 0.45, 0.35, 0.3, 0.6);
        var inDrawing = new MapFrame(0.2, 0.3, 0.1, 0.25);

        var onScreen = shown.PlacementOf(drawn).OnScreen(inDrawing);

        var expected = drawn.PictureOf(inDrawing);
        var actual = shown.PictureOf(onScreen);

        Assert.Equal(expected.X, actual.X, Precision);
        Assert.Equal(expected.Y, actual.Y, Precision);
        Assert.Equal(expected.Width, actual.Width, Precision);
        Assert.Equal(expected.Height, actual.Height, Precision);
    }

    private static void AssertInside(MapViewport viewport)
    {
        var span = 1 / viewport.Zoom;

        Assert.InRange(viewport.Left, 0, 1 - span + Precision);
        Assert.InRange(viewport.Top, 0, 1 - span + Precision);
    }
}
