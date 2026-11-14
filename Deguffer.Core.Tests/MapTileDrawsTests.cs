using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// The tiles a moving map wants are drawn on workers and handed to its thread as each finishes, the
/// most wanted first; a tile no longer wanted is not drawn; nothing started before a stop, for a
/// picture that has changed, is ever handed over; and each worker's thread is started once, and ended
/// when the map is let go.
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

    /// <summary>Every worker's thread started, in the order they were, all on the test's own thread.</summary>
    private readonly List<Thread> _threads = [];

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

    /// <summary>
    /// A run of asks, each wanting tiles after the last's were all handed over, starts each worker's
    /// thread once: the next ask wakes the parked workers rather than starting new threads, which the UI
    /// thread waited for at every frame of a zoom.
    /// </summary>
    [Fact]
    public void ARunOfAsksStartsEachWorkerOnce()
    {
        var picture = Picture(Tree());
        var draws = Draws(workers: 3);
        var keys = Keys(level: 4, count: 6);

        for (var ask = 0; ask < 20; ask++)
        {
            _drawn.Clear();
            draws.Want(picture, Tiling, Ground, shade: false, keys);
            _owner.RunUntil(() => _drawn.Count == keys.Count, $"every tile of ask {ask} to be drawn");
        }

        Assert.Equal(3, _threads.Count);
        Assert.All(_threads, thread => Assert.True(thread.IsAlive, "a worker ended between asks"));
    }

    /// <summary>An ask that wants fewer tiles than there are workers starts only as many as it has tiles for.</summary>
    [Fact]
    public void AnAskStartsNoMoreWorkersThanItHasTilesFor()
    {
        var draws = Draws(workers: 4);

        draws.Want(Picture(Tree()), Tiling, Ground, shade: false, Keys(level: 4, count: 1));
        _owner.RunUntil(() => _drawn.Count == 1, "the tile to be drawn");

        Assert.Single(_threads);
    }

    /// <summary>
    /// Released, for a map nobody sees, every worker ends, the one drawing a tile once it has finished
    /// it, and that tile is not handed over. The next ask starts them again and is drawn.
    /// </summary>
    [Fact]
    public void ReleasedEveryWorkerEndsAndTheNextAskStartsThemAgain()
    {
        var tree = new GatedTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 2);
        var keys = Keys(level: 4, count: 4);

        draws.Want(picture, Tiling, Ground, shade: false, keys);
        _owner.RunUntil(() => _drawn.Count == keys.Count, "every tile to be drawn");

        tree.Close();
        draws.Want(picture, Tiling, Ground, shade: false, [new MapTileKey(5, 0, 0)]);
        tree.WaitUntilEntered();

        draws.Release();
        tree.Open();

        foreach (var thread in _threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "a worker outlived the release");
        }

        _owner.RunPending();
        Assert.Equal(keys.Count, _drawn.Count);

        var after = new MapTileKey(5, 1, 1);

        draws.Want(picture, Tiling, Ground, shade: false, [after]);
        _owner.RunUntil(() => _drawn.Count > keys.Count, "the tile wanted after the release");

        Assert.Equal(after, _drawn[^1].Tile.Key);
        Assert.Equal(3, _threads.Count);
    }

    /// <summary>
    /// A tile whose layout fails is raised on the owner's thread, where the application's handling of an
    /// unexpected failure sees it, and the worker that met it goes on to the tiles wanted after it.
    /// </summary>
    [Fact]
    public void AFailedTileIsRaisedOnTheOwnersThreadAndTheWorkerDrawsOn()
    {
        var tree = new FailingTree(Tree());
        var picture = Picture(tree);
        var draws = Draws(workers: 1);

        tree.Fails = true;
        draws.Want(picture, Tiling, Ground, shade: false, [new MapTileKey(4, 0, 0)]);

        var failure = Assert.Throws<InvalidOperationException>(() => _owner.RunUntil(() => false, "the failure"));
        Assert.Equal(FailingTree.Message, failure.Message);

        tree.Fails = false;
        var after = new MapTileKey(4, 1, 1);

        draws.Want(picture, Tiling, Ground, shade: false, [after]);
        _owner.RunUntil(() => _drawn.Count > 0, "the tile wanted after the failure");

        Assert.Equal([after], _drawn.Select(drawn => drawn.Tile.Key));
        Assert.Single(_threads);
    }

    private MapTileDraws Draws(int workers) => new(
        _owner,
        (tile, pixels) => _drawn.Add((tile, pixels[..(int)tile.Bytes])),
        (tile, quads, ground) => _shaded.Add((tile, quads, ground)),
        workers,
        Start);

    private void Start(Action work)
    {
        var thread = new Thread(work.Invoke) { IsBackground = true, Name = "Test map tiles" };

        _threads.Add(thread);
        thread.Start();
    }

    private static List<MapTileKey> Keys(int level, int count) =>
        [.. Tiling.Covered(MapViewport.Whole, level, 0.5, 0.5).Take(count)];

    private static TiledSurface Picture(ISizedTree tree)
    {
        var viewport = MapViewport.Whole;
        var limits = LayoutLimits.Default;

        return new TiledSurface(
            tree, tree.RootNode, Width, Height, limits, ShapeColours.ByBranch(ExploreScheme.Standard), MapLook.Default,
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

    /// <summary>A tree whose root's children cannot be read while it fails, as a defect in a layout would.</summary>
    private sealed class FailingTree(ExploreTree tree) : ISizedTree
    {
        public const string Message = "The layout failed.";

        private volatile bool _fails;

        public bool Fails
        {
            get => _fails;
            set => _fails = value;
        }

        public int RootNode => tree.RootNode;

        public ExploreChildOrder ChildOrder => tree.ChildOrder;

        public long SizeOf(int node) => tree.SizeOf(node);

        public ReadOnlySpan<int> ChildrenOf(int node) =>
            node == tree.RootNode && _fails ? throw new InvalidOperationException(Message) : tree.ChildrenOf(node);

        public bool IsContainer(int node) => ((ISizedTree)tree).IsContainer(node);

        public int ParentOf(int node) => tree.ParentOf(node);
    }
}
