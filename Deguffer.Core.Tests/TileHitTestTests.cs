using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Tests;

/// <summary>
/// The rectangles are nested, so the answer is the deepest one under the pointer rather than the
/// first. Without that, a pointer over a file inside a directory inside a volume reports the
/// volume — whose rectangle contains every point on the canvas.
/// </summary>
public sealed class TileHitTestTests
{
    private const float Width = 400;
    private const float Height = 300;

    [Fact]
    public void ThePointerOverNestedRectanglesFindsTheDeepestOne()
    {
        var tiles = new List<ExploreTile>
        {
            new(Node: 0, Depth: 0, Bytes: 100, X: 0, Y: 0, Width: Width, Height: Height),
            new(Node: 1, Depth: 1, Bytes: 60, X: 10, Y: 10, Width: 200, Height: 200),
            new(Node: 2, Depth: 2, Bytes: 20, X: 20, Y: 20, Width: 50, Height: 50),
        };

        var hits = new TileHitTest(tiles, Width, Height);

        Assert.Equal(2, hits.At(30, 30));
        Assert.Equal(1, hits.At(100, 100));
        Assert.Equal(0, hits.At(300, 250));
    }

    [Fact]
    public void APointOutsideEverythingFindsNothing()
    {
        var tiles = new List<ExploreTile>
        {
            new(Node: 0, Depth: 0, Bytes: 1, X: 10, Y: 10, Width: 20, Height: 20),
        };

        var hits = new TileHitTest(tiles, Width, Height);

        Assert.Null(hits.At(5, 5));
        Assert.Null(hits.At(-1, 15));
        Assert.Null(hits.At(15, -1));
        Assert.Null(hits.At(Width + 5, 15));
    }

    /// <summary>
    /// Right and bottom edges are exclusive, so two rectangles sharing an edge do not both claim
    /// the point on it. The index has to agree with that, or it holds entries it will always
    /// reject and misses the one it should have found.
    /// </summary>
    [Fact]
    public void AdjoiningRectanglesDoNotBothClaimTheEdgeBetweenThem()
    {
        var tiles = new List<ExploreTile>
        {
            new(Node: 1, Depth: 1, Bytes: 1, X: 0, Y: 0, Width: 32, Height: 32),
            new(Node: 2, Depth: 1, Bytes: 1, X: 32, Y: 0, Width: 32, Height: 32),
        };

        var hits = new TileHitTest(tiles, Width, Height);

        Assert.Equal(0, hits.At(31.5f, 10));
        Assert.Equal(1, hits.At(32, 10));
    }

    /// <summary>
    /// The grid is an accelerator, so its only correctness requirement is that it agrees with the
    /// scan it replaces. Checked over a real layout rather than a hand-built one, because the cell
    /// arithmetic is what a hand-built case is least likely to exercise.
    /// </summary>
    [Fact]
    public void TheIndexAgreesWithScanningEveryRectangle()
    {
        var tiles = ManyTiles();
        var hits = new TileHitTest(tiles, Width, Height);
        var random = new Random(20260902);

        for (var i = 0; i < 5000; i++)
        {
            var x = (float)(random.NextDouble() * Width);
            var y = (float)(random.NextDouble() * Height);

            Assert.Equal(Deepest(tiles, x, y), hits.At(x, y));
        }
    }

    private static int? Deepest(IReadOnlyList<ExploreTile> tiles, float x, float y)
    {
        int? best = null;
        var deepest = int.MinValue;

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];

            if (tile.Depth > deepest
                && x >= tile.X && x < tile.X + tile.Width
                && y >= tile.Y && y < tile.Y + tile.Height)
            {
                best = i;
                deepest = tile.Depth;
            }
        }

        return best;
    }

    /// <summary>A grid of overlapping rectangles at three depths, on deliberately awkward bounds.</summary>
    private static List<ExploreTile> ManyTiles()
    {
        var tiles = new List<ExploreTile> { new(0, 0, 1, 0, 0, Width, Height) };
        var node = 1;

        for (var column = 0; column < 9; column++)
        {
            for (var row = 0; row < 7; row++)
            {
                var x = column * 43.7f;
                var y = row * 41.3f;

                tiles.Add(new ExploreTile(node++, 1, 1, x, y, 43.7f, 41.3f));
                tiles.Add(new ExploreTile(node++, 2, 1, x + 5.5f, y + 4.25f, 17.1f, 15.9f));
            }
        }

        return tiles;
    }
}
