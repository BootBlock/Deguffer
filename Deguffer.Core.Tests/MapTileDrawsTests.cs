using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The tiles a moving map wants are drawn on workers and handed to its thread as each finishes, the
/// most wanted first; a tile no longer wanted is not drawn; and nothing started before a stop, for a
/// picture that has changed, is ever handed over.
/// </summary>
public sealed class MapTileDrawsTests
{
    private const int Width = 600;
    private const int Height = 400;

    private static readonly TileColour Ground = new(32, 32, 32);

    private static readonly MapTiling Tiling = new(Width, Height);

    private readonly QueuedContext _owner = new();

    private readonly List<(MapTile Tile, byte[] Pixels)> _drawn = [];

    /// <summary>Each tile is handed over painted as the tile paints itself, on the ground asked for.</summary>
    [Fact]
    public void EachTileIsHandedOverPainted()
    {
        var picture = Picture(Tree());
        var draws = Draws(workers: 3);
        var keys = Keys(level: 4, count: 6);

        draws.Want(picture, Tiling, Ground, keys);
        _owner.RunUntil(() => _drawn.Count == keys.Count, "every tile to be drawn");

        Assert.Equal(keys.Count, _drawn.Count);
        Assert.Equal(keys.ToHashSet(), _drawn.Select(drawn => drawn.Tile.Key).ToHashSet());

        foreach (var (tile, pixels) in _drawn)
        {
            var expected = new byte[tile.Bytes];
            tile.Painter(Ground).Paint(expected, new CanvasRegion(0, 0, tile.Width, tile.Height));

            Assert.Equal(expected, pixels);
            Assert.Same(picture, tile.Picture);
        }
    }

    /// <summary>One at a time, the tiles are drawn in the order they were wanted: the first is the one the reader is looking at.</summary>
    [Fact]
    public void TilesAreDrawnInTheOrderWanted()
    {
        var keys = Keys(level: 4, count: 8);

        Draws(workers: 1).Want(Picture(Tree()), Tiling, Ground, keys);
        _owner.RunUntil(() => _drawn.Count == keys.Count, "every tile to be drawn");

        Assert.Equal(keys, _drawn.Select(drawn => drawn.Tile.Key));
    }

    /// <summary>
    /// Asked again, the tiles wanted before and not yet started are not drawn: the camera has moved on
    /// from them. The one already under way is finished, as a tile of the same picture.
    /// </summary>
    [Fact]
    public void ATileNoLongerWantedIsNotDrawn()
    {
        var tree = new GatedTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 1);
        var before = Keys(level: 4, count: 5);
        var after = new MapTileKey(5, 0, 0);

        tree.Close();
        draws.Want(picture, Tiling, Ground, before);
        tree.WaitUntilEntered();

        draws.Want(picture, Tiling, Ground, [after]);
        tree.Open();

        _owner.RunUntil(() => _drawn.Any(drawn => drawn.Tile.Key == after), "the tile wanted now");
        _owner.RunPending();

        Assert.Equal([before[0], after], _drawn.Select(drawn => drawn.Tile.Key));
    }

    /// <summary>
    /// Stopped, for a picture that has changed, nothing started before the stop is handed over, whether
    /// it was still being drawn or already finished and on its way to the owner's thread.
    /// </summary>
    [Fact]
    public void NothingStartedBeforeAStopIsHandedOver()
    {
        var tree = new GatedTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 1);
        var after = new MapTileKey(5, 1, 1);

        tree.Close();
        draws.Want(picture, Tiling, Ground, Keys(level: 4, count: 5));
        tree.WaitUntilEntered();

        draws.Stop();
        tree.Open();

        var next = Picture(Tree());
        draws.Want(next, Tiling, Ground, [after]);

        _owner.RunUntil(() => _drawn.Count > 0, "the tile wanted after the stop");
        _owner.RunPending();

        Assert.Equal([after], _drawn.Select(drawn => drawn.Tile.Key));
        Assert.Same(next, _drawn[0].Tile.Picture);
    }

    /// <summary>At rest, the tiles not yet started are not drawn, and the one under way is still handed over.</summary>
    [Fact]
    public void AtRestOnlyTheTileUnderWayIsHandedOver()
    {
        var tree = new GatedTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 1);
        var keys = Keys(level: 4, count: 5);

        tree.Close();
        draws.Want(picture, Tiling, Ground, keys);
        tree.WaitUntilEntered();

        draws.Rest();
        tree.Open();

        _owner.RunUntil(() => _drawn.Count > 0, "the tile under way");

        // Anything else would have been handed over by now: one worker draws the tiles in turn.
        Thread.Sleep(200);
        _owner.RunPending();

        Assert.Equal([keys[0]], _drawn.Select(drawn => drawn.Tile.Key));
    }

    private MapTileDraws Draws(int workers) => new(_owner, (tile, pixels) => _drawn.Add((tile, pixels[..(int)tile.Bytes])), workers);

    private static List<MapTileKey> Keys(int level, int count) =>
        [.. Tiling.Covering(MapViewport.Whole, level, 0.5, 0.5).Take(count)];

    private static TiledSurface Picture(ISizedTree tree)
    {
        var viewport = MapViewport.Whole;
        var limits = LayoutLimits.Default;

        return new TiledSurface(
            tree, tree.RootNode, Width, Height, limits, ShapeColours.ByBranch(ExploreScheme.Standard),
            TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits, viewport: viewport),
            viewport);
    }

    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, 200).Select(i => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 10_000 - (i * 40)))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// A tree whose root's children cannot be read while it is closed, so a test can hold a tile in the
    /// middle of being laid out.
    /// </summary>
    private sealed class GatedTree(ExploreTree tree) : ISizedTree
    {
        private readonly ManualResetEventSlim _open = new(initialState: true);

        private readonly ManualResetEventSlim _entered = new();

        public int RootNode => tree.RootNode;

        public ExploreChildOrder ChildOrder => tree.ChildOrder;

        public void Close()
        {
            _entered.Reset();
            _open.Reset();
        }

        public void Open() => _open.Set();

        public void WaitUntilEntered() =>
            Assert.True(_entered.Wait(TimeSpan.FromSeconds(20)), "no tile was laid out");

        public long SizeOf(int node) => tree.SizeOf(node);

        public ReadOnlySpan<int> ChildrenOf(int node)
        {
            if (node == tree.RootNode)
            {
                _entered.Set();
                _open.Wait(TimeSpan.FromSeconds(20));
            }

            return tree.ChildrenOf(node);
        }

        public bool IsContainer(int node) => ((ISizedTree)tree).IsContainer(node);

        public int ParentOf(int node) => tree.ParentOf(node);
    }
}
