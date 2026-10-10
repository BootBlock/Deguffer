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
        var shown = Middle.Shown(Width, Height, elastic: true);
        var origin = new MapOrigin(0.4, 0.55);
        var fromCorner = new MapStretch(Middle, Width, Height, elastic);
        var fromOrigin = new MapStretch(MapTracking.Of(shown, Width, Height, origin), Width, Height, elastic);

        foreach (var (x, y) in new[] { (Middle.X + 300, -1e6), (-1e6, Middle.Y + 40), (120.0, -80.0) })
        {
            var cornered = fromCorner.Pull(x, y).Shown(Width, Height, elastic: true);
            var originated = fromOrigin.Pull(x, y).Shown(Width, Height, elastic: true);

            Assert.Equal(cornered.Left, originated.Left, Precision);
            Assert.Equal(cornered.Top, originated.Top, Precision);
        }
    }
}
