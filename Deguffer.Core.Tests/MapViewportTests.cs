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

    /// <summary>The picture's own corner, and an origin away from it, which every placement must cancel out the same.</summary>
    private static readonly MapOrigin[] Origins = [default, new MapOrigin(0.37, 0.81)];

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

    /// <summary>
    /// The screen puts each pixel of a drawing where the drawing is placed in the picture, then where
    /// the camera puts the picture. A click is resolved through <see cref="MapPlacement"/> instead, so
    /// the two have to agree at every zoom, or the shape a click picks is not the shape on screen
    /// under it (§7.1). Including a canvas stretched over a new size while a resize settles.
    /// </summary>
    [Theory]
    [InlineData(1, 0.5, 0.5, 1, 0.5, 0.5, 1904, 1072, 1904, 1072)]
    [InlineData(3, 0.4, 0.4, 5, 0.45, 0.35, 1904, 1072, 1904, 1072)]
    [InlineData(6, 0.7, 0.2, 2, 0.3, 0.8, 3824, 2152, 2856, 1614)]
    [InlineData(64, 0.1, 0.9, 1, 0.5, 0.5, 1200, 700, 1800, 1050)]
    public void TheCameraPutsADrawingWhereAClickIsResolvedThrough(
        double drawnZoom,
        double drawnX,
        double drawnY,
        double shownZoom,
        double shownX,
        double shownY,
        int canvasWidth,
        int canvasHeight,
        double width,
        double height)
    {
        var drawn = MapViewport.Anchored(drawnZoom, drawnX, drawnY, 0.5, 0.5);
        var shown = MapViewport.Anchored(shownZoom, shownX, shownY, 0.4, 0.6);
        var placement = shown.PlacementOf(drawn);

        foreach (var origin in Origins)
        {
            var onScreen = drawn.Canvas(canvasWidth, canvasHeight, width, height, origin).Then(shown.Camera(width, height, origin));

            foreach (var (pixelX, pixelY) in new[] { (0.0, 0.0), (canvasWidth * 0.3, canvasHeight * 0.7), (canvasWidth, canvasHeight) })
            {
                var (x, y) = onScreen.Apply(pixelX, pixelY);
                var (inX, inY) = placement.InDrawing(x / width, y / height);

                Assert.Equal(pixelX / canvasWidth, inX, Precision);
                Assert.Equal(pixelY / canvasHeight, inY, Precision);
            }
        }
    }

    /// <summary>
    /// A label is laid out in device-independent pixels of its drawing, at the display scale the
    /// drawing was made at. Wherever the camera has the picture, it has to sit on the canvas pixels
    /// it was laid out over, the ones a click there resolves to, or it names a neighbour (§7.1).
    /// </summary>
    [Theory]
    [InlineData(1, 0.5, 0.5, 1, 0.5, 0.5, 1.0, 1904, 1072, 1904, 1072)]
    [InlineData(3, 0.4, 0.4, 5, 0.45, 0.35, 1.5, 2856, 1608, 1904, 1072)]
    [InlineData(6, 0.7, 0.2, 2, 0.3, 0.8, 1.25, 3824, 2152, 2856, 1614)]
    public void ALabelSitsOnTheCanvasPixelsItWasLaidOutOver(
        double drawnZoom,
        double drawnX,
        double drawnY,
        double shownZoom,
        double shownX,
        double shownY,
        double displayScale,
        int canvasWidth,
        int canvasHeight,
        double width,
        double height)
    {
        var drawn = MapViewport.Anchored(drawnZoom, drawnX, drawnY, 0.5, 0.5);
        var shown = MapViewport.Anchored(shownZoom, shownX, shownY, 0.4, 0.6);
        var placement = shown.PlacementOf(drawn);

        foreach (var origin in Origins)
        {
            var onScreen = drawn
                .Labels(canvasWidth, canvasHeight, width, height, displayScale, origin)
                .Then(shown.Camera(width, height, origin));

            foreach (var (labelX, labelY) in new[] { (0.0, 0.0), (300.0, 500.0), (canvasWidth / displayScale, canvasHeight / displayScale) })
            {
                var (x, y) = onScreen.Apply(labelX, labelY);
                var (inX, inY) = placement.InDrawing(x / width, y / height);

                Assert.Equal(labelX * displayScale / canvasWidth, inX, Precision);
                Assert.Equal(labelY * displayScale / canvasHeight, inY, Precision);
            }
        }
    }

    /// <summary>
    /// At rest the drawing on screen is of the part of the picture shown, at the screen's own pixel
    /// size, so one of its pixels is one of the display's. Anything else resamples a picture that was
    /// drawn to be sharp.
    /// </summary>
    [Theory]
    [InlineData(1, 1.0)]
    [InlineData(4, 1.5)]
    public void AtRestOneCanvasPixelIsOneDevicePixel(double zoom, double displayScale)
    {
        var viewport = MapViewport.Anchored(zoom, 0.3, 0.6, 0.5, 0.5);
        const double width = 1200;
        const double height = 800;

        var onScreen = viewport
            .Canvas((int)(width * displayScale), (int)(height * displayScale), width, height, MapOrigin.At(viewport))
            .Then(viewport.Camera(width, height, MapOrigin.At(viewport)));

        Assert.Equal(1 / displayScale, onScreen.ScaleX, Precision);
        Assert.Equal(1 / displayScale, onScreen.ScaleY, Precision);
        Assert.Equal(0, onScreen.X, Precision);
        Assert.Equal(0, onScreen.Y, Precision);
    }

    /// <summary>
    /// What a stretched screen shows is kept as it is, past the edge and past the zoom's limits, so a
    /// click lands on what is there; and it comes to rest held inside them.
    /// </summary>
    [Fact]
    public void AStretchedViewportIsKeptAsItIsAndRestsInsideThePicture()
    {
        var seen = MapViewport.Seen(MapViewport.MaximumZoom * 1.2, -0.05, 0.999);

        Assert.True(seen.Left < 0);
        Assert.True(seen.Zoom > MapViewport.MaximumZoom);

        var held = seen.Held();

        Assert.Equal(MapViewport.MaximumZoom, held.Zoom);
        Assert.Equal(0, held.Left);
        Assert.Equal(1 - (1 / MapViewport.MaximumZoom), held.Top, Precision);
    }

    /// <summary>
    /// A viewport can be written out, as a failed assertion or a log line writes it. With a property
    /// of its own type it printed that one, and that one's, until the stack ran out and took the test
    /// host down, so a failing viewport test read as a short green run.
    /// </summary>
    [Fact]
    public void AViewportCanBeWrittenOut()
    {
        var text = MapViewport.Seen(4, -0.1, 0.2).ToString();

        Assert.Contains("Zoom = 4", text, StringComparison.Ordinal);
        Assert.Contains("Left = ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AViewportAlreadyInsideThePictureRestsWhereItIs()
    {
        var viewport = MapViewport.Anchored(6, 0.3, 0.4, 0.2, 0.7);

        Assert.Equal(viewport, viewport.Held());
    }

    [Fact]
    public void ZoomingByAFactorAtAPointKeepsWhatIsUnderItUnderIt()
    {
        var from = MapViewport.Anchored(3, 0.6, 0.4, 0.5, 0.5);
        var under = from.PictureAt(0.25, 0.8);

        var to = from.ZoomedAt(2.5, 0.25, 0.8);

        Assert.Equal(7.5, to.Zoom, Precision);
        Assert.Equal(under.X, to.PictureAt(0.25, 0.8).X, Precision);
        Assert.Equal(under.Y, to.PictureAt(0.25, 0.8).Y, Precision);
    }

    private static void AssertInside(MapViewport viewport)
    {
        var span = 1 / viewport.Zoom;

        Assert.InRange(viewport.Left, 0, 1 - span + Precision);
        Assert.InRange(viewport.Top, 0, 1 - span + Precision);
    }
}
