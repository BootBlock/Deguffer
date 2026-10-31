using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The phases of a change of folder on the map: a step in flies at once, a step out and a flight
/// through a folder wait for the picture they go out to, a flight through a folder draws the folder it
/// passes and becomes a step in at the middle, a flight that lands before its picture waits for it,
/// and a reader who asks for the folder left back turns the change round from any phase.
/// </summary>
public sealed class FolderFlightTests
{
    private static readonly VolumeSpace Volume = new(TotalBytes: 100_000_000, FreeBytes: 40_000_000);

    private static readonly MapViewport Zoomed = MapViewport.Anchored(4, 0.3, 0.6, 0.5, 0.5, MapCeiling.Least);

    /// <summary>The zoom the folder asked for opens at: not the zoom the folder left was at, so the two cannot be mistaken.</summary>
    private static readonly MapViewport Opened = MapViewport.Anchored(2, 0.7, 0.2, 0.5, 0.5, MapCeiling.Least);

    private static readonly MapPicture Asked = new(null, 5, ExploreView.Treemap, Volume);

    private static readonly FolderLeft Left = new(new MapPicture(null, 3, ExploreView.Treemap, Volume), Zoomed, null, null);

    private static readonly FolderLeft Leaving = new(Asked, MapViewport.Whole, null, null);

    private const int Meeting = 2;

    [Fact]
    public void ASettledMapHasNoChangeOnItsWay()
    {
        var flight = FolderFlight.None;

        Assert.False(flight.IsUnderWay);
        Assert.Null(flight.Left);
        Assert.Null(flight.Passing);
        Assert.False(flight.Outward);
        Assert.False(flight.AwaitsOuter);
        Assert.False(flight.Owed);
        Assert.Equal(Asked, flight.Drawn(Asked));
    }

    /// <summary>A step in flies at once, into a picture still to be drawn, which grows over the picture left.</summary>
    [Fact]
    public void AStepInFliesAtOnceAndGrowsOverThePictureLeft()
    {
        var flight = Begin(FolderStep.Into);

        Assert.True(flight.IsUnderWay);
        Assert.Same(Left, flight.Left);
        Assert.False(flight.AwaitsOuter);
        Assert.False(flight.Outward);
        Assert.Equal(Asked, flight.Drawn(Asked));
    }

    /// <summary>
    /// A step out waits for the picture it goes out to, and the picture left shrinks back into it. Once
    /// drawn, it flies from the folder left's shape in it, and with no such shape it has nothing to fly
    /// from and ends.
    /// </summary>
    [Fact]
    public void AStepOutWaitsForThePictureItGoesOutToAndFliesFromTheShapeLeftInIt()
    {
        var flight = Begin(FolderStep.OutOf);

        Assert.True(flight.AwaitsOuter);
        Assert.True(flight.Outward);
        Assert.Null(flight.Passing);

        var flying = flight.OuterDrawn(leftDrawn: true);

        Assert.True(flying.IsUnderWay);
        Assert.False(flying.AwaitsOuter);
        Assert.True(flying.Outward);
        Assert.Same(Left, flying.Left);

        Assert.Same(FolderFlight.None, flight.OuterDrawn(leftDrawn: false));
    }

    /// <summary>
    /// A flight through a folder starts as a step out to the folder that holds both, which is the
    /// picture drawn meanwhile, opened and with no volume beside it, and flies out to it once drawn
    /// even where the folder left has no shape in it: that folder fades on its leg.
    /// </summary>
    [Fact]
    public void AFlightThroughAFolderDrawsTheFolderPassedThroughAndFliesOutToIt()
    {
        var flight = Begin(FolderStep.Through);

        Assert.True(flight.AwaitsOuter);
        Assert.True(flight.Outward);
        Assert.Equal(new FolderPassed(Meeting, Opened), flight.Passing);
        Assert.Equal(Asked with { Node = Meeting, Volume = VolumeSpace.None }, flight.Drawn(Asked));

        var flying = flight.OuterDrawn(leftDrawn: false);

        Assert.True(flying.IsUnderWay);
        Assert.False(flying.AwaitsOuter);
        Assert.True(flying.Outward);
        Assert.Equal(flight.Passing, flying.Passing);
        Assert.Equal(flight.Drawn(Asked), flying.Drawn(Asked));
    }

    /// <summary>
    /// At the middle of a flight through a folder the picture passed through fills the screen, and from
    /// there it is a step into the folder asked for, leaving the folder passed through as it is drawn.
    /// </summary>
    [Fact]
    public void AtTheMiddleAFlightThroughAFolderBecomesAStepIntoTheFolderAskedFor()
    {
        var passingAt = MapViewport.Whole;
        var flight = Begin(FolderStep.Through).OuterDrawn(leftDrawn: true).Midway(Asked, passingAt, null, null);

        Assert.True(flight.IsUnderWay);
        Assert.False(flight.Outward);
        Assert.False(flight.AwaitsOuter);
        Assert.Null(flight.Passing);
        Assert.Equal(new FolderLeft(Asked with { Node = Meeting, Volume = VolumeSpace.None }, passingAt, null, null), flight.Left);
        Assert.Equal(Asked, flight.Drawn(Asked));
    }

    [Theory]
    [InlineData(FolderStep.Into)]
    [InlineData(FolderStep.OutOf)]
    [InlineData(FolderStep.Through)]
    public void OnlyAFlightOnItsWayOutThroughAFolderHasAMiddle(FolderStep way)
    {
        Assert.Throws<InvalidOperationException>(() => Begin(way).Midway(Asked, MapViewport.Whole, null, null));
        Assert.Throws<InvalidOperationException>(() => FolderFlight.None.Midway(Asked, MapViewport.Whole, null, null));

        if (way == FolderStep.OutOf)
        {
            Assert.Throws<InvalidOperationException>(() => Begin(way).OuterDrawn(true).Midway(Asked, MapViewport.Whole, null, null));
        }
    }

    /// <summary>A flight that arrives before its picture has landed is owed: the picture left stays under it until the new one arrives.</summary>
    [Fact]
    public void AFlightThatArrivesBeforeItsPictureIsOwedUntilItLands()
    {
        var owed = Begin(FolderStep.Into).Arrived(landing: true);

        Assert.True(owed.IsUnderWay);
        Assert.True(owed.Owed);
        Assert.False(owed.AwaitsOuter);
        Assert.False(owed.Outward);
        Assert.Same(Left, owed.Left);

        Assert.Same(FolderFlight.None, Begin(FolderStep.Into).Arrived(landing: false));
        Assert.Same(FolderFlight.None, Begin(FolderStep.OutOf).OuterDrawn(true).Arrived(landing: false));
        Assert.Same(FolderFlight.None, FolderFlight.None.Arrived(landing: true));
    }

    /// <summary>
    /// A reader who asks for the folder left back turns the change round: what was flying in, or owed
    /// its picture, flies out, and what was on its way out, through a folder included, flies in, with
    /// the folder passed through forgotten. The folder left is the one the change was going to.
    /// </summary>
    [Theory]
    [InlineData(FolderStep.Into, false, false)]
    [InlineData(FolderStep.Into, true, false)]
    [InlineData(FolderStep.OutOf, false, true)]
    [InlineData(FolderStep.OutOf, true, true)]
    [InlineData(FolderStep.Through, false, true)]
    [InlineData(FolderStep.Through, true, true)]
    public void AChangeTurnedRoundGoesBackTheOtherWay(FolderStep way, bool movedOn, bool wasOutward)
    {
        var flight = Begin(way);

        // Moved on a phase: a step in owed its picture, a step out or through flying out.
        if (movedOn)
        {
            flight = way == FolderStep.Into ? flight.Arrived(landing: true) : flight.OuterDrawn(leftDrawn: true);
        }

        Assert.Equal(wasOutward, flight.Outward);

        var turned = flight.Turned(Leaving);

        Assert.True(turned.IsUnderWay);
        Assert.Equal(!wasOutward, turned.Outward);
        Assert.False(turned.AwaitsOuter);
        Assert.False(turned.Owed);
        Assert.Null(turned.Passing);
        Assert.Same(Leaving, turned.Left);
        Assert.Equal(Asked, turned.Drawn(Asked));
    }

    [Fact]
    public void AChangeThatIsNotOnItsWayCannotBeBegunAsOneOrMovedOn()
    {
        Assert.Throws<ArgumentException>(() => FolderFlight.Begin(PictureStep.Across, Left, Zoomed));
        Assert.Throws<ArgumentException>(() => FolderFlight.Begin(new PictureStep(FolderStep.Through, null), Left, Zoomed));
        Assert.Throws<InvalidOperationException>(() => FolderFlight.None.Turned(Leaving));
        Assert.Throws<InvalidOperationException>(() => FolderFlight.None.OuterDrawn(true));
        Assert.Throws<InvalidOperationException>(() => Begin(FolderStep.Into).OuterDrawn(true));
        Assert.Throws<InvalidOperationException>(() => Begin(FolderStep.OutOf).OuterDrawn(true).OuterDrawn(true));
    }

    private static FolderFlight Begin(FolderStep way) =>
        FolderFlight.Begin(new PictureStep(way, way == FolderStep.Through ? Meeting : null), Left, Opened);
}
