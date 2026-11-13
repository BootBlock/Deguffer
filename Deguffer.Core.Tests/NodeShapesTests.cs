using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;

namespace Deguffer.Core.Tests;

/// <summary>
/// A drawing finds a node's shapes by the node rather than by a pass over every shape it drew.
///
/// <para>Hovering asks for the shape under the pointer on most frames, and a drawing at 4K holds about
/// a hundred thousand rectangles. The index has to answer exactly what the pass did, because the
/// outline it gives is what tells the user which shape they are about to pick (§7.1).</para>
/// </summary>
public sealed class NodeShapesTests
{
    private const int Width = 640;
    private const int Height = 480;

    /// <summary>
    /// Every node of the tree, drawn or not, inside the drawing's folder or above it, is shown as and
    /// outlined where a pass over the rectangles says.
    /// </summary>
    [Theory]
    [InlineData(ExploreView.Treemap, false)]
    [InlineData(ExploreView.Treemap, true)]
    [InlineData(ExploreView.Icicle, false)]
    [InlineData(ExploreView.Icicle, true)]
    public void ARectangularDrawingAnswersAsAPassOverItsRectanglesDoes(ExploreView view, bool atAFolder)
    {
        var tree = BranchingTree();
        var root = atAFolder ? FirstFolderOf(tree, tree.RootNode) : tree.RootNode;
        var tiles = view == ExploreView.Icicle
            ? IcicleLayout.Compute(tree, root, Width, Height, LayoutLimits.Default)
            : TreemapLayout.Compute(tree, root, Width, Height, LayoutLimits.Default);
        var surface = Tiled(tree, root, tiles);

        Assert.True(atAFolder || tiles.Any(tile => !tile.IsNode), "The whole tree's drawing stands a block in for its long tail.");

        for (var node = 0; node < tree.NodeCount; node++)
        {
            Assert.Equal(PassedShownAs(tree, root, tiles, node), surface.ShownAs(node));
            Assert.Equal(PassedOutlines(tiles, new HashSet<int> { node }), Points(surface.Outlines(new HashSet<int> { node })));
        }

        var many = Enumerable.Range(0, tree.NodeCount).Where(node => node % 3 != 0).ToHashSet();

        Assert.Equal(PassedOutlines(tiles, many), Points(surface.Outlines(many)));
    }

    /// <summary>
    /// The same of a sunburst, whose pass over every sector is what <see cref="ExploreSurface.Uncovered"/>
    /// still makes, since no sector is drawn inside another's ring.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASunburstAnswersAsAPassOverItsSectorsDoes(bool atAFolder)
    {
        var tree = BranchingTree();
        var root = atAFolder ? FirstFolderOf(tree, tree.RootNode) : tree.RootNode;
        var sectors = SunburstLayout.Compute(tree, root, Width, Height, LayoutLimits.Default).Sectors;
        var surface = new SunburstSurface(
            tree, root, Width, Height, LayoutLimits.Default, ShapeColours.ByBranch(ExploreScheme.Standard), MapStyle.Default);

        for (var node = 0; node < tree.NodeCount; node++)
        {
            Assert.Equal(PassedShownAs(tree, root, sectors, node), surface.ShownAs(node));

            var asked = new HashSet<int> { node };

            Assert.Equal(Points(surface.Uncovered(asked.Contains)), Points(surface.Outlines(asked)));
        }

        var many = Enumerable.Range(0, tree.NodeCount).Where(node => node % 3 != 0).ToHashSet();

        Assert.Equal(Points(surface.Uncovered(many.Contains)), Points(surface.Outlines(many)));
    }

    /// <summary>
    /// Outlining the shape under the pointer, and asking which shape a card row's folder is shown as,
    /// read only the rectangles of the nodes asked about: never the rest of the drawing.
    /// </summary>
    [Fact]
    public void AHoverReadsNoRectangleButThoseOfTheNodesItAsksAbout()
    {
        var tree = BranchingTree();
        var laidOut = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default);
        var tiles = new WatchedTiles(laidOut);
        var surface = Tiled(tree, tree.RootNode, tiles);

        Assert.True(laidOut.Count > 50, $"{laidOut.Count} rectangles are too few for a pass over them to show.");

        foreach (var node in laidOut.Where(tile => tile.IsNode).Select(tile => tile.Node).Distinct())
        {
            tiles.Read.Clear();
            surface.Outlines(new HashSet<int> { node });

            Assert.NotEmpty(tiles.Read);
            Assert.All(tiles.Read, index => Assert.Equal(node, laidOut[index].Node));

            var path = PathOf(tree, tree.RootNode, node);

            tiles.Read.Clear();
            surface.ShownAs(node);

            Assert.NotEmpty(tiles.Read);
            Assert.All(tiles.Read, index => Assert.Contains(laidOut[index].Node, path));
        }
    }

    /// <summary>
    /// A node drawn twice keeps both shapes, in the order the drawing has them, and of two shapes as
    /// deep the first is the one it is shown as.
    /// </summary>
    [Fact]
    public void ANodeDrawnTwiceKeepsBothShapesInDrawingOrder()
    {
        var tree = BranchingTree();
        var folder = FirstFolderOf(tree, tree.RootNode);
        var other = folder + 1;

        ExploreTile[] tiles =
        [
            new(tree.RootNode, 0, 0, 0, 0, 100, 100),
            new(folder, 1, 0, 0, 0, 50, 50),
            new(ExploreTile.Aggregated, 1, 0, 50, 0, 10, 10),
            new(other, 1, 0, 50, 50, 50, 50),
            new(folder, 1, 0, 0, 50, 50, 50),
        ];

        var shapes = new NodeShapes<ExploreTile>(tiles, static tile => tile.IsNode, static tile => tile.Node, static tile => tile.Depth);

        Assert.Equal([1, 3, 4], shapes.Of(new HashSet<int> { folder, other }));
        Assert.Equal([0, 1, 3, 4], shapes.Of(Enumerable.Range(-1, tree.NodeCount + 1).ToHashSet()));
        Assert.Equal(1, shapes.Deepest(tree, tree.RootNode, folder));
        Assert.Equal(1, shapes.Deepest(tree, tree.RootNode, FirstFolderOf(tree, folder)));
        Assert.Equal(0, shapes.Deepest(tree, tree.RootNode, tree.RootNode));
        Assert.Null(shapes.FirstOf(ExploreTile.Aggregated));
    }

    private static TiledSurface Tiled(ExploreTree tree, int root, IReadOnlyList<ExploreTile> tiles) =>
        new(tree, root, Width, Height, LayoutLimits.Default, ShapeColours.ByBranch(ExploreScheme.Standard), MapStyle.Default, tiles);

    /// <summary>What the drawing answered before it kept an index: the deepest shape on the node's path, the first of two as deep.</summary>
    private static int? PassedShownAs(ExploreTree tree, int root, IReadOnlyList<ExploreTile> tiles, int node)
    {
        var path = PathOf(tree, root, node);
        ExploreTile? deepest = null;

        foreach (var tile in tiles)
        {
            if (tile.IsNode && path.Contains(tile.Node) && (deepest is not { } found || tile.Depth > found.Depth))
            {
                deepest = tile;
            }
        }

        return deepest?.Node;
    }

    private static int? PassedShownAs(ExploreTree tree, int root, IReadOnlyList<ExploreSector> sectors, int node)
    {
        var path = PathOf(tree, root, node);
        ExploreSector? deepest = null;

        foreach (var sector in sectors)
        {
            if (sector.Block != MapBlock.Aggregate && path.Contains(sector.Node) && (deepest is not { } found || sector.Depth > found.Depth))
            {
                deepest = sector;
            }
        }

        return deepest?.Node;
    }

    private static List<(int Outline, int Node, ExplorePoint Point)> PassedOutlines(IReadOnlyList<ExploreTile> tiles, IReadOnlySet<int> nodes) =>
        Points(
        [
            .. tiles
                .Where(tile => tile.IsNode && nodes.Contains(tile.Node))
                .Select(tile => new ExploreOutline(tile.Node, [
                    new ExplorePoint(tile.X, tile.Y),
                    new ExplorePoint(tile.X + tile.Width, tile.Y),
                    new ExplorePoint(tile.X + tile.Width, tile.Y + tile.Height),
                    new ExplorePoint(tile.X, tile.Y + tile.Height),
                ])),
        ]);

    /// <summary>Every point of every outline, with which outline it is on, so two lists compare by value and in order.</summary>
    private static List<(int Outline, int Node, ExplorePoint Point)> Points(IReadOnlyList<ExploreOutline> outlines) =>
        [.. outlines.SelectMany((outline, i) => outline.Points.Select(point => (i, outline.Node, point)))];

    /// <summary>The node and every folder above it, up to the drawing's root, or the tree's own for a node outside it.</summary>
    private static HashSet<int> PathOf(ExploreTree tree, int root, int node)
    {
        var path = new HashSet<int>();

        for (var current = node; ; current = tree.ParentOf(current))
        {
            path.Add(current);

            if (current == root || tree.ParentOf(current) == current)
            {
                return path;
            }
        }
    }

    private static int FirstFolderOf(ExploreTree tree, int folder) =>
        tree.ChildrenOf(folder).ToArray().First(child => tree.ChildrenOf(child).Length > 0);

    /// <summary>
    /// Folders three deep of uneven sizes, one of them holding a long tail of tiny files, so the
    /// drawings frame folders, nest them, and stand a block in for what is too small to draw.
    /// </summary>
    private static ExploreTree BranchingTree()
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var top = builder.AddChildren(ExploreTreeBuilder.RootNode, Folders("top", 5));

        for (var i = 0; i < 5; i++)
        {
            var middle = builder.AddChildren(top + i, Folders($"middle{i}-", 4));

            for (var j = 0; j < 4; j++)
            {
                var files = i == 0 && j == 0 ? 300 : 3 + ((i + j) % 4);

                builder.AddChildren(
                    middle + j,
                    [
                        .. Enumerable.Range(0, files).Select(k => new ExploreChild(
                            $"file{k}", IsDirectory: false, IsLink: false, Size: files > 100 ? 1 + k % 3 : 1000L * (1 + ((i * 7) + (j * 3) + k) % 11))),
                    ]);
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static ExploreChild[] Folders(string prefix, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new ExploreChild($"{prefix}{i}", IsDirectory: true, IsLink: false, Size: 0))];
}
