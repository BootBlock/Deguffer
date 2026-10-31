using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// A mouse dragging the picture: under the hand inside the picture, against a growing resistance past
/// its edge, and never so far past it that the picture leaves the screen.
/// </summary>
public sealed class MapStretchTests
{
    private const double Width = 1600;

    private const double Height = 900;

    private const double Precision = 1e-9;

    /// <summary>Zoomed four times, with the screen in the middle of the picture.</summary>
    private static readonly MapTracking Middle = new(1.5 * Width, 1.5 * Height, 4, default);

    [Fact]
    public void InsideThePictureItMovesExactlyWithTheHand()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);

        var moved = stretch.Pull(200, -150);

        // The picture moves with the hand, so the screen moves the other way through it.
        Assert.Equal(Middle.X - 200, moved.X, Precision);
        Assert.Equal(Middle.Y + 150, moved.Y, Precision);
        Assert.Equal(Middle.Scale, moved.Scale);
    }

    /// <summary>
    /// Past the edge it still gives, so the hand feels the end of the picture rather than a wall, but
    /// by less than the hand moved.
    /// </summary>
    [Fact]
    public void PastTheEdgeItGivesLessThanTheHandMoves()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);

        // The hand takes the screen 300 pixels past the picture's left edge.
        var moved = stretch.Pull(Middle.X + 300, 0);

        Assert.True(moved.X < 0, $"the picture stopped dead at the edge, at {moved.X}");
        Assert.True(moved.X > -300, $"the picture went as far as the hand, to {moved.X}");
    }

    /// <summary>However far the hand goes, the picture never goes further past the edge than the reach, so it stays in view.</summary>
    [Fact]
    public void NoPullTakesThePictureFurtherPastTheEdgeThanTheReach()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);

        var moved = stretch.Pull(-1e7, -1e7);

        Assert.InRange(moved.X - (3 * Width), 0, MapStretch.Reach * Width);
        Assert.InRange(moved.Y - (3 * Height), 0, MapStretch.Reach * Height);
    }

    /// <summary>
    /// A hand that goes past the edge and comes back puts the picture back exactly where it was under
    /// it. Adding up the resisted steps instead would leave the picture short of the hand on the way back.
    /// </summary>
    [Fact]
    public void AHandThatComesBackFindsThePictureWhereItLeftIt()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);

        stretch.Pull(Middle.X + 500, 0);
        var back = stretch.Pull(-(Middle.X + 500) + 100, 0);

        Assert.Equal(Middle.X - 100, back.X, Precision);
    }

    /// <summary>With animation effects off the edge is a wall: no stretch, so nothing to spring back from.</summary>
    [Fact]
    public void WithStretchingOffTheEdgeIsAWall()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: false);

        var moved = stretch.Pull(Middle.X + 300, -1e6);

        Assert.Equal(0, moved.X);
        Assert.Equal(3 * Height, moved.Y, Precision);
    }

    /// <summary>
    /// The picture's edges are where they are whatever the tracker is measured from. A drag measured
    /// from an origin away from the picture's corner stops, and stretches, at the same edges as one
    /// measured from the corner, or a camera that has moved its origin would let the screen past the
    /// picture or stop it short.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnOriginMovesNoEdge(bool elastic)
    {
        var shown = Middle.Shown(Width, Height, elastic: true, MapCeiling.Least);
        var origin = new MapOrigin(0.4, 0.55);
        var fromCorner = new MapStretch(Middle, Width, Height, elastic);
        var fromOrigin = new MapStretch(MapTracking.Of(shown, Width, Height, origin), Width, Height, elastic);

        foreach (var (x, y) in new[] { (Middle.X + 300, -1e6), (-1e6, Middle.Y + 40), (120.0, -80.0) })
        {
            var cornered = fromCorner.Pull(x, y).Shown(Width, Height, elastic: true, MapCeiling.Least);
            var originated = fromOrigin.Pull(x, y).Shown(Width, Height, elastic: true, MapCeiling.Least);

            Assert.Equal(cornered.Left, originated.Left, Precision);
            Assert.Equal(cornered.Top, originated.Top, Precision);
        }
    }

    /// <summary>
    /// A drag measured from an origin the tracker turned out not to keep goes on from where the hand
    /// has the picture, measured from the origin it did keep: before and after, it shows the same, and
    /// so does every pull that follows, edges included.
    /// </summary>
    [Fact]
    public void ADragRemeasuredFromAnotherOriginGoesOnWhereTheHandHasIt()
    {
        var shown = Middle.Shown(Width, Height, elastic: true, MapCeiling.Least);
        var before = new MapOrigin(0.4, 0.55);
        var stretch = new MapStretch(MapTracking.Of(shown, Width, Height, before), Width, Height, elastic: true);
        var reference = new MapStretch(Middle, Width, Height, elastic: true);

        stretch.Pull(150, -60);
        reference.Pull(150, -60);
        stretch.Remeasure(default);

        foreach (var (x, y) in new[] { (0.0, 0.0), (90.0, 30.0), (-1e6, 0.0) })
        {
            var remeasured = stretch.Pull(x, y);
            var expected = reference.Pull(x, y);

            Assert.Equal(default, remeasured.Origin);
            Assert.Equal(expected.X, remeasured.X, Precision);
            Assert.Equal(expected.Y, remeasured.Y, Precision);
        }
    }

    /// <summary>
    /// Inside the picture the tracker goes where the hand puts it, the camera does not stretch, and the
    /// screen shows the tracker.
    /// </summary>
    [Fact]
    public void AStepInsideThePictureAsksTheTrackerForAllOfIt()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);

        var pull = stretch.Move(200, -150, MapCeiling.Least);

        Assert.Equal(Middle.X - 200, pull.Tracker.X, Precision);
        Assert.Equal(Middle.Y + 150, pull.Tracker.Y, Precision);
        Assert.Equal(0, pull.StretchX, Precision);
        Assert.Equal(0, pull.StretchY, Precision);
        Assert.Equal(pull.Tracker.Shown(Width, Height, elastic: true, MapCeiling.Least), pull.Shown);
        Assert.Equal(pull.Shown, pull.Target);
    }

    /// <summary>
    /// Past an edge the tracker is asked only as far as the edge, which is as far as it goes, and the
    /// camera stretches the rest of the way, so the screen shows the picture past the edge and the
    /// camera is going back inside it.
    /// </summary>
    [Fact]
    public void AStepPastAnEdgeHoldsTheTrackerAndStretchesTheCamera()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);

        var pull = stretch.Move(Middle.X + 300, 0, MapCeiling.Least);

        Assert.Equal(0, pull.Tracker.X, Precision);
        Assert.True(pull.StretchX > 0, $"the camera was not stretched past the edge, by {pull.StretchX}");
        Assert.True(pull.Shown.Left < 0, $"the screen was taken to show the picture inside its edge, at {pull.Shown.Left}");
        Assert.Equal(pull.Shown.Held(MapCeiling.Least), pull.Target);
        Assert.True(pull.Target.Left >= 0, $"the camera was going past the edge, to {pull.Target.Left}");
    }

    /// <summary>
    /// A drag measured from a new origin and asked again from where it is puts the screen where the
    /// hand had it, so a refused move of the origin does not move the picture under the hand.
    /// </summary>
    [Fact]
    public void AStepOfNothingAfterRemeasuringShowsWhereTheHandHasThePicture()
    {
        var stretch = new MapStretch(Middle, Width, Height, elastic: true);
        var before = stretch.Move(120, 40, MapCeiling.Least);
        var origin = new MapOrigin(0.4, 0.45);

        stretch.Remeasure(origin);

        var after = stretch.Move(0, 0, MapCeiling.Least);

        Assert.Equal(before.Shown.Zoom, after.Shown.Zoom, Precision);
        Assert.Equal(before.Shown.Left, after.Shown.Left, Precision);
        Assert.Equal(before.Shown.Top, after.Shown.Top, Precision);
        Assert.Equal(origin, after.Tracker.Origin);
    }
}