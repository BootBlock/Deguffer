using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A change of folder between folders where neither holds the other: the camera pulls back out of the
/// one into the picture of the folder that holds both, and flies on into the other, as one flight that
/// keeps its speed through the turn.
/// </summary>
public sealed class MapPassageTests
{
    private const double Precision = 1e-9;

    private static readonly MapFrame Left = new(0.1, 0.2, 0.5, 0.5);

    private static readonly MapFrame Going = new(0.7, 0.75, 0.125, 0.125);

    [Fact]
    public void TheFlightRunsFromTheFolderLeftThroughTheMiddlePictureToTheFolderGoingTo()
    {
        var passage = Passage(travels: true);

        var (first, along) = passage.At(0);

        Assert.Equal(passage.Out, first);
        Assert.Equal(MapFrame.Whole, first.Inner(along));
        Assert.Equal(1, first.Opacity(along));

        // At the middle the picture passed through fills the screen, and the folder going to has not
        // started to come in over it.
        var (middle, start) = passage.At(passage.Midway);

        Assert.Equal(passage.In, middle);
        Assert.Equal(MapFrame.Whole, middle.Outer(start));
        AssertClose(Going, middle.Inner(start));
        Assert.Equal(0, middle.Opacity(start));

        var (last, end) = passage.At(1);

        Assert.Equal(passage.In, last);
        Assert.Equal(MapFrame.Whole, last.Inner(end));
        Assert.Equal(1, last.Opacity(end));
    }

    /// <summary>
    /// Just before the middle the way out has all but finished: the folder left is nearly back in its
    /// shape and nearly gone, so handing over to the way in there moves nothing.
    /// </summary>
    [Fact]
    public void TheWayOutEndsWhereTheWayInBegins()
    {
        var passage = Passage(travels: true);
        var (leg, along) = passage.At(passage.Midway - 1e-12);

        Assert.Equal(passage.Out, leg);
        AssertClose(MapFrame.Whole, leg.Outer(along));
        AssertClose(Left, leg.Inner(along));
        Assert.Equal(0, leg.Opacity(along), Precision);
    }

    /// <summary>
    /// The time is shared by how far each leg zooms: out of a folder half the screen across is one
    /// doubling, into one an eighth across is three, so the way out takes a quarter of the flight. The
    /// zoom then runs at the same speed on both sides of the middle, rather than lurching at it.
    /// </summary>
    [Fact]
    public void EachLegTakesTheShareOfTheFlightThatItsZoomDoes()
    {
        var passage = Passage(travels: true);

        Assert.Equal(0.25, passage.Midway, Precision);

        foreach (var offset in new[] { 0.05, 0.1, 0.2 })
        {
            var before = Magnified(passage, passage.Midway - offset);
            var after = Magnified(passage, passage.Midway + offset);

            Assert.Equal(before, after, Precision);
        }
    }

    /// <summary>With motion off nothing moves, so the folder left fades into the middle picture for half the time, and that into the folder going to.</summary>
    [Fact]
    public void LegsThatTravelNothingShareTheFlightEqually()
    {
        var passage = Passage(travels: false);

        Assert.Equal(0.5, passage.Midway);

        var (first, out25) = passage.At(0.25);
        var (second, in75) = passage.At(0.75);

        Assert.Equal(passage.Out, first);
        Assert.Equal(0.5, first.Opacity(out25), Precision);
        Assert.Equal(MapFrame.Whole, first.Inner(out25));
        Assert.Equal(passage.In, second);
        Assert.Equal(0.5, second.Opacity(in75), Precision);
    }

    /// <summary>How far a leg zooms is the mean, over both axes, of how many times the shape is magnified to fill the screen.</summary>
    [Fact]
    public void ALegsDepthIsItsZoomAcrossBothAxes()
    {
        Assert.Equal(2 * Math.Log(2), new MapDescent(new MapFrame(0, 0, 0.5, 0.125), Travels: true).Depth, Precision);
        Assert.Equal(0, new MapDescent(new MapFrame(0, 0, 0.5, 0.125), Travels: false).Depth);
        Assert.Equal(0, new MapDescent(MapFrame.Whole, Travels: true).Depth);
    }

    private static MapPassage Passage(bool travels) =>
        new(new MapDescent(Left, travels), new MapDescent(Going, travels));

    /// <summary>The natural log of how many times the middle picture is magnified at <paramref name="progress"/>.</summary>
    private static double Magnified(MapPassage passage, double progress)
    {
        var (leg, along) = passage.At(progress);
        var outer = leg.Outer(along);

        return (Math.Log(outer.Width) + Math.Log(outer.Height)) / 2;
    }

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, 1e-6);
        Assert.Equal(expected.Y, actual.Y, 1e-6);
        Assert.Equal(expected.Width, actual.Width, 1e-6);
        Assert.Equal(expected.Height, actual.Height, 1e-6);
    }
}
