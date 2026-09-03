using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The sunburst is the one layout whose geometry answers "what is under the pointer" directly: the
/// rings are uniform, so a radius gives the ring by one division, and the sectors within a ring are
/// sorted and do not overlap, so an angle gives the sector by one binary search. These hold that
/// index to what it assumes — that a ring is a contiguous run of the list, that a sector's end is
/// exclusive, and that a ring need not be full.
/// </summary>
public sealed class SectorHitTestTests
{
    private const float Centre = 100;
    private const float RingWidth = 20;

    /// <summary>
    /// Angles run clockwise from twelve o'clock, which is the convention every tool in this
    /// category uses and the one a reader assumes when comparing two wedges. A picture built on the
    /// mathematical convention instead would be a quarter turn out and mirrored, and every other
    /// assertion here would still pass on it — so this states the orientation on its own.
    /// </summary>
    [Fact]
    public void TwelveOClockIsWhereTheAnglesStart()
    {
        Assert.Equal(0, SectorHitTest.AngleOf(0, -10), 4);
        Assert.Equal(MathF.PI / 2, SectorHitTest.AngleOf(10, 0), 4);
        Assert.Equal(MathF.PI, SectorHitTest.AngleOf(0, 10), 4);
        Assert.Equal(3 * MathF.PI / 2, SectorHitTest.AngleOf(-10, 0), 4);
    }

    [Fact]
    public void ThePointerInARingFindsTheSectorItsAngleFallsIn()
    {
        var hits = new SectorHitTest(Quarters());

        Assert.Equal(0, Hit(hits, radius: 10, degrees: 45));
        Assert.Equal(1, Hit(hits, radius: 30, degrees: 45));
        Assert.Equal(2, Hit(hits, radius: 30, degrees: 135));
        Assert.Equal(3, Hit(hits, radius: 30, degrees: 225));
        Assert.Equal(4, Hit(hits, radius: 30, degrees: 315));
    }

    [Fact]
    public void APointOutsideTheDiscFindsNothing()
    {
        var hits = new SectorHitTest(Quarters());

        Assert.Null(Hit(hits, radius: 41, degrees: 0));
        Assert.Null(Hit(hits, radius: 200, degrees: 200));
        Assert.Null(hits.At(0, 0));
    }

    /// <summary>
    /// A sector ends where the next one begins, so the two must not both claim the boundary. The
    /// end is exclusive, which is the same rule the rectangles follow along their right and bottom
    /// edges.
    /// </summary>
    [Fact]
    public void AdjoiningSectorsDoNotBothClaimTheAngleBetweenThem()
    {
        var hits = new SectorHitTest(Quarters());

        // Three o'clock exactly, where the first quarter ends and the second begins.
        Assert.Equal(2, hits.At(Centre + 30, Centre));
        Assert.Equal(1, hits.At(Centre + 30, Centre - 0.001f));
    }

    /// <summary>
    /// A ring is not necessarily full. A directory whose children do not account for all of it
    /// leaves a gap, and the pointer over that gap is over nothing — not over the last sector that
    /// happens to start before it.
    /// </summary>
    [Fact]
    public void AGapInARingFindsNothing()
    {
        var hits = new SectorHitTest(new Sunburst(
            [
                Disc(node: 0, depth: 0),
                new ExploreSector(Node: 1, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, 0, MathF.PI / 2),
            ],
            Centre,
            Centre,
            RingWidth,
            RingWidth * 2));

        Assert.Equal(1, Hit(hits, radius: 30, degrees: 45));
        Assert.Null(Hit(hits, radius: 30, degrees: 180));
    }

    /// <summary>
    /// Nesting needs no deepest-wins rule here, unlike the rectangles. A child sits in the ring
    /// outside its parent rather than inside its parent's area, so exactly one sector covers any
    /// point and a parent stays reachable at its own radius however deep the tree goes.
    /// </summary>
    [Fact]
    public void EveryRingAnswersForItself()
    {
        var hits = new SectorHitTest(new Sunburst(
            [Disc(node: 0, depth: 0), Disc(node: 1, depth: 1), Disc(node: 2, depth: 2)],
            Centre,
            Centre,
            RingWidth,
            RingWidth * 3));

        Assert.Equal(0, Hit(hits, radius: 10, degrees: 0));
        Assert.Equal(1, Hit(hits, radius: 30, degrees: 120));
        Assert.Equal(2, Hit(hits, radius: 50, degrees: 240));
    }

    private static int? Hit(SectorHitTest hits, float radius, float degrees)
    {
        var angle = degrees * MathF.PI / 180;

        return hits.At(
            Centre + (radius * MathF.Sin(angle)),
            Centre - (radius * MathF.Cos(angle)));
    }

    /// <summary>A whole ring, or the whole disc in the middle where the depth is zero.</summary>
    private static ExploreSector Disc(int node, int depth) => new(
        node, depth, Bytes: 1, depth * RingWidth, (depth + 1) * RingWidth, 0, MathF.Tau);

    /// <summary>A disc in the middle and one ring of four equal quarters, starting at twelve.</summary>
    private static Sunburst Quarters()
    {
        var quarter = MathF.Tau / 4;

        return new Sunburst(
            [
                Disc(node: 0, depth: 0),
                new ExploreSector(Node: 1, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, 0, quarter),
                new ExploreSector(Node: 2, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, quarter, quarter),
                new ExploreSector(Node: 3, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, quarter * 2, quarter),
                new ExploreSector(Node: 4, Depth: 1, Bytes: 1, RingWidth, RingWidth * 2, quarter * 3, quarter),
            ],
            Centre,
            Centre,
            RingWidth,
            RingWidth * 2);
    }
}
