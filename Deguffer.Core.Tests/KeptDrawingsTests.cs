using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// Which kept drawing of a map's picture shows over which, which surface the next drawing lands on,
/// and which drawings a new one covers.
/// </summary>
public sealed class KeptDrawingsTests
{
    private const int Width = 1920;

    private const int Height = 1080;

    private static readonly MapViewport Twice = MapViewport.Seen(2, 0, 0);

    private static readonly MapViewport TwiceAside = MapViewport.Seen(2, 0.5, 0.5);

    private static readonly MapViewport Thrice = MapViewport.Seen(3, 0, 0);

    /// <summary>
    /// A drawing landing to replace the one on top is over everything, the one on top over the rest,
    /// and the rest finest uppermost, the one shown less recently under where two share a zoom.
    /// </summary>
    [Fact]
    public void DrawingsStackReplacementThenCurrentThenFinestAndMostRecent()
    {
        var whole = Drawn(MapViewport.Whole, shown: 1);
        var thrice = Drawn(Thrice, shown: 2);
        var twiceLater = Drawn(Twice, shown: 4);
        var twiceEarlier = Drawn(TwiceAside, shown: 3);
        var current = Drawn(MapViewport.Whole, shown: 5);
        var replacing = Landing(MapViewport.Whole, onTop: true);

        var stacked = KeptDrawings.Stacked<Kept>([replacing, current, thrice, whole, twiceLater, twiceEarlier], current);

        Assert.Equal([whole, twiceEarlier, twiceLater, thrice, current, replacing], stacked);
    }

    /// <summary>
    /// A drawing landing under the one on top, as the whole picture under a zoomed one, takes its place
    /// among the rest by zoom, under a finer one kept, rather than over everything.
    /// </summary>
    [Fact]
    public void AnUnderlayStacksByItsZoomUnderTheCurrentDrawing()
    {
        var current = Drawn(Twice, shown: 2);
        var finer = Drawn(Thrice, shown: 1);
        var underlay = Landing(MapViewport.Whole, onTop: false);

        Assert.Equal([underlay, finer, current], KeptDrawings.Stacked<Kept>([current, underlay, finer], current));
    }

    /// <summary>A surface with nothing shown on it is not stacked at all.</summary>
    [Fact]
    public void ADrawingNotShownIsNotStacked()
    {
        var current = Drawn(MapViewport.Whole, shown: 1);
        var empty = Empty();

        Assert.Equal([current], KeptDrawings.Stacked<Kept>([empty, current], current));
    }

    /// <summary>A surface nothing uses, of the size wanted, is taken before one of another size.</summary>
    [Fact]
    public void AFreeSurfaceOfTheSizeWantedIsTakenFirst()
    {
        var other = Empty(width: 800, height: 600);
        var same = Empty();

        Assert.Same(same, Reusable([other, same]));
        Assert.Same(other, Reusable([other]));
    }

    /// <summary>
    /// With no surface free, the drawing of a picture that has changed is taken, least recently shown
    /// first, and never the one on top, which goes on showing until the new one has landed over it.
    /// </summary>
    [Fact]
    public void ADrawingOfAnOldPictureIsTakenLeastRecentFirstButNeverTheCurrent()
    {
        var oldest = Drawn(MapViewport.Whole, shown: 1, picture: 0);
        var older = Drawn(Twice, shown: 2, picture: 0);
        var now = Drawn(MapViewport.Whole, shown: 3, picture: 1);

        Assert.Same(oldest, Reusable([older, oldest, now], current: now));
        Assert.Same(older, Reusable([older, oldest, now], current: oldest));
    }

    /// <summary>While a drawing fades in over the pictures before it, none of their surfaces is taken: they are what shows through it.</summary>
    [Fact]
    public void NoDrawingOfAnOldPictureIsTakenWhileOneFadesIn()
    {
        var old = Drawn(MapViewport.Whole, shown: 1, picture: 0);
        var now = Drawn(MapViewport.Whole, shown: 2, picture: 1);

        Assert.Null(Reusable([old, now], current: now, fading: true));
    }

    /// <summary>
    /// Once as many zoomed drawings are kept as the limit, another zoomed one takes the surface of the
    /// least recently shown that is not on top. The whole picture of the picture now is never taken.
    /// </summary>
    [Fact]
    public void AZoomedDrawingPastTheLimitTakesTheLeastRecentZoomedOne()
    {
        var whole = Drawn(MapViewport.Whole, shown: 1);
        var first = Drawn(Twice, shown: 2);
        var second = Drawn(Thrice, shown: 3);
        List<Kept> drawings = [whole, second, first];

        Assert.Equal(2, KeptDrawings.ZoomedKept);
        Assert.Same(first, Reusable(drawings, current: second, viewport: TwiceAside));
        Assert.Same(second, Reusable(drawings, current: first, viewport: TwiceAside));
        Assert.Null(Reusable(drawings, current: second, viewport: MapViewport.Whole));
    }

    /// <summary>Under the limit of zoomed drawings, another one is given a surface of its own.</summary>
    [Fact]
    public void AZoomedDrawingUnderTheLimitGetsANewSurface()
    {
        var whole = Drawn(MapViewport.Whole, shown: 1);
        var zoomed = Drawn(Twice, shown: 2);

        Assert.Null(Reusable([whole, zoomed], current: zoomed, viewport: Thrice));
    }

    /// <summary>
    /// The whole picture is missing under a zoomed drawing on top where no drawing of the picture now
    /// shows all of it, landing or arrived.
    /// </summary>
    [Fact]
    public void TheWholePictureIsMissingOnlyUnderAZoomedDrawingWithoutOne()
    {
        var zoomed = Drawn(Twice, shown: 2, picture: 1);
        var oldWhole = Drawn(MapViewport.Whole, shown: 1, picture: 0);
        var landingWhole = Landing(MapViewport.Whole, onTop: false, picture: 1);
        var whole = Drawn(MapViewport.Whole, shown: 3, picture: 1);

        Assert.True(KeptDrawings.LacksWhole<Kept>([zoomed, oldWhole], zoomed, picture: 1));
        Assert.False(KeptDrawings.LacksWhole<Kept>([zoomed, landingWhole], zoomed, picture: 1));
        Assert.False(KeptDrawings.LacksWhole<Kept>([zoomed, whole], zoomed, picture: 1));
        Assert.False(KeptDrawings.LacksWhole<Kept>([whole], whole, picture: 1));
        Assert.False(KeptDrawings.LacksWhole<Kept>([oldWhole], current: null, picture: 1));
    }

    /// <summary>
    /// A drawing goes once a drawing of a later picture covers the screen, unless it is still landing:
    /// that is a redraw not yet arrived, which takes it down itself if it is superseded.
    /// </summary>
    [Fact]
    public void OnlyADrawingOfAnEarlierPictureThatIsNotLandingIsCovered()
    {
        var cover = Drawn(MapViewport.Whole, shown: 3, picture: 2);

        Assert.True(KeptDrawings.IsCoveredBy(Drawn(Twice, shown: 1, picture: 1), cover, spared: null));
        Assert.False(KeptDrawings.IsCoveredBy(Landing(Twice, onTop: false, picture: 1), cover, spared: null));
        Assert.False(KeptDrawings.IsCoveredBy(Drawn(Twice, shown: 2, picture: 2), cover, spared: null));
        Assert.False(KeptDrawings.IsCoveredBy(cover, cover, spared: null));
    }

    /// <summary>
    /// A fade that ends after its surface was taken for a later picture spares the drawing worked from,
    /// though that drawing is of an earlier picture than the one the surface now lands.
    /// </summary>
    [Fact]
    public void TheDrawingSparedIsNeverCovered()
    {
        var retaken = Landing(MapViewport.Whole, onTop: true, picture: 3);
        var current = Drawn(MapViewport.Whole, shown: 2, picture: 2);
        var older = Drawn(Twice, shown: 1, picture: 2);

        Assert.False(KeptDrawings.IsCoveredBy(current, retaken, spared: current));
        Assert.True(KeptDrawings.IsCoveredBy(older, retaken, spared: current));
    }

    /// <summary>A drawing is shown again only where all of it is there, of the same part of the same picture.</summary>
    [Fact]
    public void AKeptDrawingIsOfTheSamePartOfThePictureNowAndWhole()
    {
        var old = Drawn(Twice, shown: 1, picture: 0);
        var landing = Landing(Twice, onTop: true, picture: 1);
        var kept = Drawn(Twice, shown: 2, picture: 1);

        Assert.Same(kept, KeptDrawings.Kept<Kept>([old, landing, kept], Twice, picture: 1));
        Assert.Null(KeptDrawings.Kept<Kept>([old, landing], Twice, picture: 1));
        Assert.Null(KeptDrawings.Kept<Kept>([kept], Thrice, picture: 1));
    }

    private static Kept? Reusable(
        List<Kept> drawings,
        Kept? current = null,
        MapViewport? viewport = null,
        bool fading = false) =>
        KeptDrawings.Reusable(drawings, Width, Height, viewport ?? MapViewport.Whole, picture: 1, current, fading);

    private static Kept Drawn(MapViewport viewport, long shown, int picture = 1) =>
        new() { Viewport = viewport, LastShown = shown, Picture = picture, HasDrawing = true };

    private static Kept Landing(MapViewport viewport, bool onTop, int picture = 1) =>
        new() { Viewport = viewport, Picture = picture, IsLanding = true, LandsOnTop = onTop };

    private static Kept Empty(int width = Width, int height = Height) =>
        new() { CanvasWidth = width, CanvasHeight = height };

    /// <summary>A surface as the rules see it. A class, because the rules tell surfaces apart by reference.</summary>
    private sealed class Kept : IKeptDrawing
    {
        public MapViewport Viewport { get; init; }

        public int Picture { get; init; }

        public long LastShown { get; init; }

        public bool HasDrawing { get; init; }

        public bool IsLanding { get; init; }

        public bool LandsOnTop { get; init; }

        public int CanvasWidth { get; init; } = Width;

        public int CanvasHeight { get; init; } = Height;
    }
}
