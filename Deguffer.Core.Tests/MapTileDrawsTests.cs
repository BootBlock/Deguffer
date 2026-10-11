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

    private readonly List<(MapTile Tile, CushionQuads Quads, TileColour Ground)> _shaded = [];

    /// <summary>Each tile is handed over painted as the tile paints itself, on the ground asked for.</summary>
    [Fact]
    public void EachTileIsHandedOverPainted()
    {
        var picture = Picture(Tree());
        var draws = Draws(workers: 3);
        var keys = Keys(level: 4, count: 6);

        draws.Want(picture, Tiling, Ground, shade: false, keys);
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

    /// <summary>
    /// A tile the GPU shades is handed over as the quads its own painter paints, and nothing of it is
    /// painted on the CPU: painting it too would cost the cores the shading frees.
    /// </summary>
    [Fact]
    public void EachTileTheGpuShadesIsHandedOverAsItsQuadsUnpainted()
    {
        var picture = Picture(Tree());
        var draws = Draws(workers: 3);
        var keys = Keys(level: 4, count: 6);

        draws.Want(picture, Tiling, Ground, shade: true, keys);
        _owner.RunUntil(() => _shaded.Count == keys.Count, "every tile to be laid out");

        Assert.Empty(_drawn);
        Assert.Equal(keys.ToHashSet(), _shaded.Select(shaded => shaded.Tile.Key).ToHashSet());

        foreach (var (tile, quads, ground) in _shaded)
        {
            Assert.Equal(Ground, ground);
            Assert.Equal((tile.Width, tile.Height), (quads.Width, quads.Height));
            Assert.Equal(tile.Quads().Quads.ToArray(), quads.Quads.ToArray());
            Assert.NotEqual(0, quads.Quads.Length);
        }
    }

    /// <summary>A tile for the GPU finished before a stop is not handed over after it, as a painted one is not.</summary>
    [Fact]
    public void NoTileForTheGpuFinishedBeforeAStopIsHandedOverAfterIt()
    {
        var draws = Draws(workers: 1);

        draws.Want(Picture(Tree()), Tiling, Ground, shade: true, Keys(level: 4, count: 1));
        _owner.WaitUntilPosted("the tile to be laid out");

        draws.Stop();
        _owner.RunPending();

        Assert.Empty(_shaded);
    }

    /// <summary>One at a time, the tiles are drawn in the order they were wanted: the first is the one the reader is looking at.</summary>
    [Fact]
    public void TilesAreDrawnInTheOrderWanted()
    {
        var keys = Keys(level: 4, count: 8);

        Draws(workers: 1).Want(Picture(Tree()), Tiling, Ground, shade: false, keys);
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
        draws.Want(picture, Tiling, Ground, shade: false, before);
        tree.WaitUntilEntered();

        draws.Want(picture, Tiling, Ground, shade: false, [after]);
        tree.Open();

        _owner.RunUntil(() => _drawn.Any(drawn => drawn.Tile.Key == after), "the tile wanted now");
        _owner.RunPending();

        Assert.Equal([before[0], after], _drawn.Select(drawn => drawn.Tile.Key));
    }

    /// <summary>
    /// Stopped, for a picture that has changed, nothing still being drawn when the stop came is handed
    /// over, and what is wanted after it is.
    /// </summary>
    [Fact]
    public void NothingStartedBeforeAStopIsHandedOver()
    {
        var tree = new GatedTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 1);
        var after = new MapTileKey(5, 1, 1);

        tree.Close();
        draws.Want(picture, Tiling, Ground, shade: false, Keys(level: 4, count: 5));
        tree.WaitUntilEntered();

        draws.Stop();
        tree.Open();

        var next = Picture(Tree());
        draws.Want(next, Tiling, Ground, shade: false, [after]);

        _owner.RunUntil(() => _drawn.Count > 0, "the tile wanted after the stop");
        _owner.RunPending();

        Assert.Equal([after], _drawn.Select(drawn => drawn.Tile.Key));
        Assert.Same(next, _drawn[0].Tile.Picture);
    }

    /// <summary>
    /// Stopped once a tile has been finished and posted to the owner's thread, but before that thread
    /// has run what was posted, the tile is still not handed over.
    /// </summary>
    [Fact]
    public void NothingFinishedBeforeAStopIsHandedOverAfterIt()
    {
        var draws = Draws(workers: 1);

        draws.Want(Picture(Tree()), Tiling, Ground, shade: false, Keys(level: 4, count: 1));
        _owner.WaitUntilPosted("the tile to be finished");

        draws.Stop();
        _owner.RunPending();

        Assert.Empty(_drawn);
    }

    /// <summary>At rest, the tiles not yet started are not drawn, and the one under way is still handed over.</summary>
    [Fact]
    public void AtRestOnlyTheTileUnderWayIsHandedOver()
    {
        var tree = new GatedTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 1);
        var keys = Keys(level: 4, count: 5);
        var next = new MapTileKey(5, 1, 1);

        tree.Close();
        draws.Want(picture, Tiling, Ground, shade: false, keys);
        tree.WaitUntilEntered();

        draws.Rest();
        tree.Open();

        _owner.RunUntil(() => _drawn.Count > 0, "the tile under way");

        // Wanted next, by the one worker: any tile it had gone on to after the rest would be handed
        // over before this one.
        draws.Want(picture, Tiling, Ground, shade: false, [next]);

        _owner.RunUntil(() => _drawn.Any(drawn => drawn.Tile.Key == next), "the tile wanted after the rest");
        _owner.RunPending();

        Assert.Equal([keys[0], next], _drawn.Select(drawn => drawn.Tile.Key));
    }

    private MapTileDraws Draws(int workers) => new(
        _owner,
        (tile, pixels) => _drawn.Add((tile, pixels[..(int)tile.Bytes])),
        (tile, quads, ground) => _shaded.Add((tile, quads, ground)),
        workers);

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
