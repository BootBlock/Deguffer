using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// What a drawing of rectangles answers the same whatever is asked of it, which shapes move on their
/// own and which folders are covered, is worked out once for the drawing and kept (#1026): a drawing
/// at 4K holds about a hundred thousand rectangles, and the map asks at every arrival and every remark,
/// on the UI thread. The answers are the ones the map gave when it worked them out at each asking.
/// </summary>
public sealed class DrawingFactsTests
{
    private const int Width = 1920;

    private const int Height = 1080;

    public static TheoryData<int, ExploreView, bool> Drawings()
    {
        var data = new TheoryData<int, ExploreView, bool>();

        for (var seed = 1; seed <= 6; seed++)
        {
            data.Add(seed, ExploreView.Treemap, false);
            data.Add(seed, ExploreView.Treemap, true);
            data.Add(seed, ExploreView.Icicle, false);
        }

        return data;
    }

    /// <summary>
    /// Between two scans of generated trees, one grown, shrunk, thinned and added to from the other,
    /// the moves are those the map worked out at each arrival, in the same order, both ways round:
    /// in a treemap, one with the volume drawn beside it, whose free space is matched though it stands
    /// for no node, and an icicle, whose shapes are all drawn at the top.
    /// </summary>
    [Theory]
    [MemberData(nameof(Drawings))]
    public void TheMovesAreThoseWorkedOutAtEachArrival(int seed, ExploreView view, bool beside)
    {
        var earlier = Generated(seed, changed: false);
        var later = Generated(seed, changed: true);
        var before = Draw(earlier, view, beside);
        var after = Draw(later, view, beside);

        Assert.True(before.Tiles.Count > ShapeChanges.MostShapes, $"{before.Tiles.Count} rectangles are too few for a cut to choose among.");
        Assert.True(!beside || after.Tiles.Any(tile => tile.Block == MapBlock.FreeSpace), "The volume's free space is drawn beside the root.");

        var forward = ShapeChanges.Between(before, after);

        Assert.Contains(forward, change => change.Kind == ShapeChangeKind.Moved);
        Assert.Contains(forward, change => change.Kind == ShapeChangeKind.Appeared);
        Assert.Contains(forward, change => change.Kind == ShapeChangeKind.Disappeared);
        Assert.Equal(WorkedOut.Between(before, after), forward);
        Assert.Equal(WorkedOut.Between(after, before), ShapeChanges.Between(after, before));
    }

    /// <summary>
    /// What a kind lights, and any other question asked of the shapes nothing is drawn inside, is
    /// outlined as when the covered folders were worked out at each asking.
    /// </summary>
    [Theory]
    [MemberData(nameof(Drawings))]
    public void TheUncoveredShapesAreThoseWorkedOutAtEachAsking(int seed, ExploreView view, bool beside)
    {
        var tree = Generated(seed, changed: true);
        var drawing = Draw(tree, view, beside);
        var measured = DominantTypes.Measure(tree);
        Func<int, bool>[] questions =
        [
            _ => true,
            node => node % 3 == 0,
            node => DominantTypes.KindOf(tree, node, measured) == FileCategory.Video,
        ];

        foreach (var question in questions)
        {
            Assert.Equal(Points(WorkedOut.Uncovered(drawing, question)), Points(drawing.Uncovered(question)));
        }

        Assert.NotEmpty(drawing.Uncovered(questions[0]));
    }

    /// <summary>
    /// Asked again, as the earlier drawing of the next move, or by the next light, a drawing answers
    /// with what it made the first time.
    /// </summary>
    [Fact]
    public void ADrawingsCutAndCoveredFoldersAreMadeOnceAndKept()
    {
        var first = Draw(Generated(1, changed: false), ExploreView.Treemap, beside: false);
        var second = Draw(Generated(1, changed: true), ExploreView.Treemap, beside: false);
        var third = Draw(Generated(2, changed: true), ExploreView.Treemap, beside: false);

        ShapeChanges.Between(first, second);

        var cut = second.Cut;

        ShapeChanges.Between(second, third);

        Assert.Same(cut, second.Cut);
        Assert.Same(second.Covered, second.Covered);
    }

    /// <summary>
    /// Prepared on the worker that made them, two drawings answer a move from the one to the other by
    /// reading only the shapes that move, and a kind light by one pass over the shapes asking the kind:
    /// never the whole drawing again to work out what it already did.
    /// </summary>
    [Fact]
    public void APreparedArrivalIsAnsweredWithoutAnotherPassOverItsShapes()
    {
        var earlier = Generated(2, changed: false);
        var later = Generated(2, changed: true);
        var beforeTiles = new WatchedTiles(Draw(earlier, ExploreView.Treemap, beside: false).Tiles);
        var afterTiles = new WatchedTiles(Draw(later, ExploreView.Treemap, beside: false).Tiles);
        var before = Tiled(earlier, beforeTiles);
        var after = Tiled(later, afterTiles);
        var light = MapLight.Kind(later, DominantTypes.Measure(later), FileCategory.Video);

        // A move reads each shape of the later cut, the earlier shape matched to it, and each shape of the earlier cut.
        const int MostRead = 3 * ShapeChanges.MostShapes;

        Assert.True(
            beforeTiles.Count + afterTiles.Count > 2 * MostRead,
            $"{beforeTiles.Count + afterTiles.Count} rectangles are too few for a pass over them to show.");

        new DrawingArrival(before, light).Prepare(after);

        beforeTiles.Reads = 0;
        afterTiles.Reads = 0;

        Assert.NotEmpty(ShapeTravel.Between(before, after));
        Assert.InRange(beforeTiles.Reads + afterTiles.Reads, 1, MostRead);

        afterTiles.Reads = 0;
        light.On(after);

        Assert.Equal(afterTiles.Count, afterTiles.Reads);
    }

    /// <summary>
    /// Where a node is drawn twice, a move matches its first shape, and the covered folders are read
    /// from a folder's last shape drawn before the shape inside it: the answers the map gave when it
    /// worked both out at each asking.
    /// </summary>
    [Fact]
    public void ANodeDrawnTwiceIsMatchedByItsFirstShapeAndCoveredByItsLastBeforeWhatItHolds()
    {
        var tree = Generated(1, changed: false);
        var folder = tree.ChildrenOf(tree.RootNode).ToArray().First(child => tree.ChildrenOf(child).Length > 0);
        var file = tree.ChildrenOf(folder)[0];

        ExploreTile[] tiles =
        [
            new(tree.RootNode, 0, 0, 0, 0, 100, 100),
            new(folder, 1, 0, 0, 0, 50, 50),
            new(folder, 1, 0, 50, 50, 50, 50),
            new(file, 2, 0, 10, 10, 20, 20),
        ];

        var drawing = Tiled(tree, tiles);

        Assert.Equal(1, drawing.Cut.IndexOf(folder));
        Assert.DoesNotContain(2, drawing.Cut.Moving);
        Assert.DoesNotContain(folder, drawing.Covered);
        Assert.Equal(2, drawing.Uncovered(node => node == folder).Count);

        ExploreTile[] reversed = [tiles[0], tiles[2], tiles[1], tiles[3]];

        Assert.Contains(folder, Tiled(tree, reversed).Covered);
        Assert.Equal(1, Tiled(tree, reversed).Cut.IndexOf(folder));
    }

    private static TiledSurface Draw(ExploreTree tree, ExploreView view, bool beside) =>
        (TiledSurface)ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), MapLook.Default,
            beside ? new VolumeSpace(TotalBytes: 4 * tree.SizeOf(tree.RootNode), FreeBytes: 2 * tree.SizeOf(tree.RootNode)) : VolumeSpace.None);

    private static TiledSurface Tiled(ExploreTree tree, IReadOnlyList<ExploreTile> tiles) =>
        new(tree, tree.RootNode, Width, Height, LayoutLimits.Default, ShapeColours.ByBranch(ExploreScheme.Standard), MapLook.Default, tiles);

    /// <summary>
    /// A tree of folders three deep under the root, of files of uneven sizes and a few long tails of
    /// tiny ones, some of them films. Changed, the same tree a later scan found: files grown and shrunk,
    /// some gone and some new, so a drawing of it has shapes that moved, went and appeared.
    /// </summary>
    private static ExploreTree Generated(int seed, bool changed)
    {
        var random = new Random(seed);
        var later = new Random(seed * 7919);
        var builder = new ExploreTreeBuilder(@"C:\");

        Fill(builder, ExploreTreeBuilder.RootNode, "", depth: 0, random, changed ? later : null);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static void Fill(ExploreTreeBuilder builder, int parent, string path, int depth, Random random, Random? change)
    {
        var folders = depth < 3 ? random.Next(3, 7) : 0;
        var files = random.Next(2, 12) + (random.Next(4) == 0 ? 150 : 0);
        var children = new List<ExploreChild>();

        for (var i = 0; i < folders; i++)
        {
            children.Add(new ExploreChild($"{path}d{i}", IsDirectory: true, IsLink: false, Size: 0));
        }

        for (var i = 0; i < files; i++)
        {
            var size = files > 100 ? 10L + random.Next(40) : 1000L * random.Next(1, 4000);
            var name = $"{path}f{i}{(random.Next(4) == 0 ? ".mp4" : ".bin")}";

            // Drawn from its own sequence, so the earlier tree's shape does not shift with the change.
            if (change is not null)
            {
                switch (change.Next(10))
                {
                    case 0:
                        continue;
                    case 1:
                        size *= 3;
                        break;
                    case 2:
                        size = Math.Max(1, size / 3);
                        break;
                    case 3:
                        children.Add(new ExploreChild($"{path}n{i}.bin", IsDirectory: false, IsLink: false, Size: size));
                        break;
                }
            }

            children.Add(new ExploreChild(name, IsDirectory: false, IsLink: false, Size: size));
        }

        var first = builder.AddChildren(parent, [.. children]);

        for (var i = 0; i < folders; i++)
        {
            Fill(builder, first + i, $"{path}d{i}-", depth + 1, random, change);
        }
    }

    /// <summary>Every point of every outline, with which outline it is on, so two lists compare by value and in order.</summary>
    private static List<(int Outline, int Node, ExplorePoint Point)> Points(IReadOnlyList<ExploreOutline> outlines) =>
        [.. outlines.SelectMany((outline, i) => outline.Points.Select(point => (i, outline.Node, point)))];

    /// <summary>
    /// The moves and the uncovered shapes as the map worked them out at every asking, before a drawing
    /// kept them: a fresh index of the drawing's shapes, a parent-chain walk for each shape's holder,
    /// and a fresh set of covered folders.
    /// </summary>
    private static class WorkedOut
    {
        private const double Still = 0.5;

        private static readonly Comparer<(float Area, int Index)> LargestFirst =
            Comparer<(float Area, int Index)>.Create((x, y) => y.Area != x.Area ? y.Area.CompareTo(x.Area) : x.Index.CompareTo(y.Index));

        public static List<ShapeChange> Between(TiledSurface earlier, TiledSurface later)
        {
            var onward = ((ITrackedTree)earlier.Tree).CounterpartsIn(later.Tree)!;
            var back = ((ITrackedTree)later.Tree).CounterpartsIn(earlier.Tree)!;
            var earlierTiles = Indexed(earlier.Tiles);
            var laterTiles = Indexed(later.Tiles);
            var stayed = new HashSet<int>();
            var changes = new List<ShapeChange>();

            foreach (var index in Cut(later, laterTiles))
            {
                var tile = later.Tiles[index];
                var to = tile.FrameIn(later.Width, later.Height);

                if ((tile.IsNode ? back(tile.Node) : tile.Node) is { } was && earlierTiles.TryGetValue(was, out var from))
                {
                    stayed.Add(was);

                    var moved = earlier.Tiles[from].FrameIn(earlier.Width, earlier.Height);

                    if (!Same(moved, to, later))
                    {
                        changes.Add(new ShapeChange(ShapeChangeKind.Moved, tile.Node, moved, to));
                    }
                }
                else
                {
                    changes.Add(new ShapeChange(ShapeChangeKind.Appeared, tile.Node, Point(to), to));
                }
            }

            foreach (var index in Cut(earlier, earlierTiles))
            {
                var tile = earlier.Tiles[index];

                if (stayed.Contains(tile.Node)
                    || ((tile.IsNode ? onward(tile.Node) : tile.Node) is { } now && laterTiles.ContainsKey(now)))
                {
                    continue;
                }

                var from = tile.FrameIn(earlier.Width, earlier.Height);

                changes.Add(new ShapeChange(ShapeChangeKind.Disappeared, tile.Node, from, Point(from)));
            }

            return changes;
        }

        public static List<ExploreOutline> Uncovered(TiledSurface drawing, Func<int, bool> nodes)
        {
            var tiles = drawing.Tiles;
            var shapes = new Dictionary<int, int>();
            var covered = new HashSet<int>();

            for (var i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];

                if (!tile.IsNode)
                {
                    continue;
                }

                shapes[tile.Node] = i;

                if (tile.Node != drawing.Root && shapes.TryGetValue(drawing.Tree.ParentOf(tile.Node), out var folder) && TiledSurface.Inside(tile, tiles[folder]))
                {
                    covered.Add(tiles[folder].Node);
                }
            }

            var outlines = new List<ExploreOutline>();

            foreach (var tile in tiles)
            {
                if (tile.IsNode && !covered.Contains(tile.Node) && nodes(tile.Node))
                {
                    outlines.Add(new ExploreOutline(tile.Node, [
                        new ExplorePoint(tile.X, tile.Y),
                        new ExplorePoint(tile.X + tile.Width, tile.Y),
                        new ExplorePoint(tile.X + tile.Width, tile.Y + tile.Height),
                        new ExplorePoint(tile.X, tile.Y + tile.Height),
                    ]));
                }
            }

            return outlines;
        }

        private static Dictionary<int, int> Indexed(IReadOnlyList<ExploreTile> tiles)
        {
            var indexed = new Dictionary<int, int>(tiles.Count);

            for (var index = 0; index < tiles.Count; index++)
            {
                if (tiles[index].Block != MapBlock.Aggregate)
                {
                    indexed.TryAdd(tiles[index].Node, index);
                }
            }

            return indexed;
        }

        private static List<int> Cut(TiledSurface drawing, Dictionary<int, int> indexed)
        {
            var tiles = drawing.Tiles;
            var inside = new Dictionary<int, List<int>>();
            var cut = new List<int>();

            foreach (var index in indexed.Values)
            {
                if (Holder(drawing, indexed, index) is { } holder)
                {
                    (inside.TryGetValue(holder, out var held) ? held : inside[holder] = []).Add(index);
                }
                else
                {
                    cut.Add(index);
                }
            }

            if (cut.Count > ShapeChanges.MostShapes)
            {
                return [.. cut.OrderByDescending(index => Area(tiles[index])).ThenBy(index => index).Take(ShapeChanges.MostShapes)];
            }

            var open = new PriorityQueue<int, (float Area, int Index)>(LargestFirst);

            foreach (var index in cut)
            {
                Offer(open, inside, tiles, index);
            }

            var members = new HashSet<int>(cut);

            while (open.TryDequeue(out var index, out _))
            {
                var held = inside[index];

                if (members.Count - 1 + held.Count > ShapeChanges.MostShapes)
                {
                    continue;
                }

                members.Remove(index);

                foreach (var child in held)
                {
                    members.Add(child);
                    Offer(open, inside, tiles, child);
                }
            }

            return [.. members.Order()];
        }

        private static void Offer(PriorityQueue<int, (float Area, int Index)> open, Dictionary<int, List<int>> inside, IReadOnlyList<ExploreTile> tiles, int index)
        {
            if (inside.ContainsKey(index))
            {
                open.Enqueue(index, (Area(tiles[index]), index));
            }
        }

        private static int? Holder(TiledSurface drawing, Dictionary<int, int> indexed, int index)
        {
            var tile = drawing.Tiles[index];

            if (!tile.IsNode)
            {
                return null;
            }

            var tree = drawing.Tree;

            for (int node = tile.Node, parent = tree.ParentOf(node); parent != node; node = parent, parent = tree.ParentOf(node))
            {
                if (indexed.TryGetValue(parent, out var holder))
                {
                    return TiledSurface.Inside(tile, drawing.Tiles[holder]) ? holder : null;
                }
            }

            return null;
        }

        private static MapFrame Point(MapFrame frame)
        {
            var (x, y) = frame.Centre;

            return new MapFrame(x, y, 0, 0);
        }

        private static bool Same(MapFrame a, MapFrame b, ExploreSurface drawing) =>
            Math.Abs(a.X - b.X) * drawing.Width < Still
            && Math.Abs(a.Y - b.Y) * drawing.Height < Still
            && Math.Abs(a.Width - b.Width) * drawing.Width < Still
            && Math.Abs(a.Height - b.Height) * drawing.Height < Still;

        private static float Area(ExploreTile tile) => tile.Width * tile.Height;
    }
}
