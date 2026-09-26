using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A zoomed treemap: the picture magnified, only what is on the canvas laid out, detail that was too
/// small to draw drawn, and shapes that run off the canvas shaded and named as the part of them that
/// shows.
/// </summary>
public sealed class TreemapZoomTests
{
    private const int Width = 800;
    private const int Height = 600;

    [Fact]
    public void TheRootCoversTheWholeMagnifiedPicture()
    {
        var tree = FilesOf(400, 300, 200, 100);
        var viewport = MapViewport.Anchored(4, 0.6, 0.3, 0.5, 0.5);

        var root = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, viewport: viewport)
            .Single(tile => tile.Node == tree.RootNode);

        Assert.Equal(-viewport.Left * Width * 4, root.X, 2);
        Assert.Equal(-viewport.Top * Height * 4, root.Y, 2);
        Assert.Equal(Width * 4, root.Width, 2);
        Assert.Equal(Height * 4, root.Height, 2);
    }

    /// <summary>
    /// What keeps a zoomed layout the size of the canvas rather than of the picture: nothing wholly off
    /// the canvas is laid out, so at a high zoom most of the tree is never visited.
    /// </summary>
    [Fact]
    public void NothingWhollyOffTheCanvasIsLaidOut()
    {
        var tree = FilesOf([.. Enumerable.Range(1, 400).Select(i => (long)(401 - i) * 10)]);
        var viewport = MapViewport.Anchored(16, 0.3, 0.7, 0.5, 0.5);

        var zoomed = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, viewport: viewport);

        Assert.All(zoomed, tile => Assert.True(
            tile.X < Width && tile.X + tile.Width > 0 && tile.Y < Height && tile.Y + tile.Height > 0,
            $"node {tile.Node} was laid out at {tile.X},{tile.Y} {tile.Width}x{tile.Height}, off the canvas"));

        Assert.True(
            zoomed.Count(tile => tile.Depth == 1) < 400,
            "every child was laid out, including the ones nowhere near the canvas");
    }

    /// <summary>
    /// Zoomed into the corner of a folder of a hundred thousand files, the layout stops at the edge of
    /// the canvas rather than laying the rest of the folder to throw it away. Culling what comes back
    /// does not show this: a layout that laid every row off the canvas returns the same shapes, and
    /// costs a row per child on every redraw. So it is measured by how often the tree is asked a size.
    /// </summary>
    [Fact]
    public void AZoomedLayoutStopsAtTheEdgeOfTheCanvas()
    {
        var tree = new CountingTree(FilesOf([.. Enumerable.Repeat(10L, 100_000)]));
        var corner = MapViewport.Anchored(16, 0, 0, 0, 0);

        var tiles = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, viewport: corner);

        Assert.True(tiles.Count(tile => tile.IsNode) > 100, "the corner drew too little to prove anything");

        // Laying the whole folder asks every size several times over, a few hundred thousand in all.
        Assert.True(tree.Sizes < 50_000, $"the tree was asked {tree.Sizes} sizes for a corner of a picture");
    }

    /// <summary>
    /// The point of zooming. Files too small to draw across the whole picture are one aggregate block
    /// there, and zoomed into that block they are drawn one by one.
    /// </summary>
    [Fact]
    public void ZoomingIntoWhatWasTooSmallToDrawDrawsIt()
    {
        var tree = FilesOf([1_000_000, .. Enumerable.Repeat(10L, 300)]);
        var limits = LayoutLimits.Default;

        var whole = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);
        var aggregate = whole.Single(tile => tile.IsAggregate);
        var small = tree.ChildrenOf(tree.RootNode)[1..].ToArray();

        Assert.DoesNotContain(whole, tile => small.Contains(tile.Node));

        var into = MapViewport.Anchored(
            MapViewport.MaximumZoom,
            (aggregate.X + (aggregate.Width / 2)) / Width,
            (aggregate.Y + (aggregate.Height / 2)) / Height,
            0.5,
            0.5);

        var zoomed = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits, viewport: into);

        Assert.Contains(zoomed, tile => small.Contains(tile.Node));
    }

    /// <summary>
    /// What a zoom already showed stays exactly where the magnified picture put it. Laid out afresh at
    /// each zoom instead, a folder's contents would shuffle as the zoom settled, and the file under the
    /// pointer when the wheel turned would not be the file under it when the picture stopped.
    /// </summary>
    [Theory]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 40)]
    [InlineData(1.5, 16)]
    public void EveryShapeAZoomShowedKeepsItsPlaceInThePictureAtAGreaterZoom(double near, double far)
    {
        var tree = MixedTree();
        var limits = LayoutLimits.Default;

        var before = MapViewport.Anchored(near, 0.62, 0.41, 0.5, 0.5);
        var after = MapViewport.Anchored(far, 0.62, 0.41, 0.5, 0.5);

        var shown = InPicture(TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits, viewport: before), before);
        var zoomed = InPicture(TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits, viewport: after), after);

        var compared = 0;

        foreach (var (node, place) in zoomed)
        {
            if (shown.TryGetValue(node, out var was))
            {
                Assert.Equal(was.X, place.X, 1e-5);
                Assert.Equal(was.Y, place.Y, 1e-5);
                Assert.Equal(was.Width, place.Width, 1e-5);
                Assert.Equal(was.Height, place.Height, 1e-5);
                compared++;
            }
        }

        Assert.True(compared > 10, $"only {compared} shapes were drawn at both zooms, which proves little");
        Assert.True(zoomed.Count > compared, "the greater zoom drew nothing the lesser one did not");
    }

    /// <summary>
    /// A folder past the depth limit stays shut however far it is magnified unless the limit grows with
    /// the zoom, and then the detail the zoom was for never appears.
    /// </summary>
    [Fact]
    public void AZoomedPictureOpensFoldersPastTheDepthLimit()
    {
        var tree = NestedTree(depth: 12);
        var limits = LayoutLimits.Default with { MinimumTileSize = 1, MaximumDepth = 3 };

        var whole = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits);

        // A folder the limit left shut, which is what a zoom into it has to open.
        var deepest = whole.Where(tile => tile.IsNode && ((ISizedTree)tree).IsContainer(tile.Node)).MaxBy(tile => tile.Depth);

        var into = MapViewport.Anchored(
            8,
            (deepest.X + (deepest.Width / 2)) / Width,
            (deepest.Y + (deepest.Height / 2)) / Height,
            0.5,
            0.5);

        var zoomed = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, limits, viewport: into);

        Assert.Equal(3, whole.Max(tile => tile.Depth));
        Assert.Equal(6, zoomed.Max(tile => tile.Depth));
    }

    /// <summary>
    /// A block beside the volume too thin to draw across the canvas stays undrawn at every zoom. Were it
    /// decided on the magnified picture it would appear partway into a zoom and take its share from the
    /// root, and every shape in the picture would move under the pointer as it did.
    /// </summary>
    [Fact]
    public void WhatIsBesideTheVolumeIsTheSameAtEveryZoom()
    {
        var tree = FilesOf(1_000_000);

        // Free space a fifth of a pixel wide across the canvas, and several pixels wide at full zoom.
        var volume = new VolumeSpace(1_000_250, 250);

        var whole = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, volume);
        var edge = MapViewport.Anchored(MapViewport.MaximumZoom, 1, 1, 1, 1);
        var zoomed = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, volume, edge);

        Assert.DoesNotContain(whole, tile => tile.IsFreeSpace);
        Assert.DoesNotContain(zoomed, tile => tile.IsFreeSpace);

        var root = zoomed.Single(tile => tile.Node == tree.RootNode);

        Assert.Equal(Width, root.X + root.Width, 1);
        Assert.Equal(Height, root.Y + root.Height, 1);
    }

    /// <summary>
    /// A shape running off the canvas is shaded as the part of its cushion that shows. Measured across
    /// only the part on the canvas, it would put a whole cushion there instead, and move it as the zoom
    /// did.
    /// </summary>
    [Fact]
    public void AShapeRunningOffTheCanvasIsShadedAsThePartOfItThatShows()
    {
        const int Side = 40;

        var whole = new ExploreTile(Node: 1, Depth: 0, Bytes: 1, X: 0, Y: 0, Width: Side * 2, Height: Side);
        var cut = whole with { X = -Side };

        var full = Paint([whole], Side * 2, Side);
        var right = Paint([cut], Side, Side);

        for (var y = 0; y < Side; y += 7)
        {
            for (var x = 0; x < Side; x += 5)
            {
                Assert.Equal(At(full, Side * 2, x + Side, y), At(right, Side, x, y));
            }
        }
    }

    /// <summary>
    /// A folder whose band runs off the left is named where the band comes on. Placed at the folder's
    /// own left edge, the name is off the canvas with it, and a zoomed folder goes unnamed however large
    /// it is drawn.
    /// </summary>
    [Fact]
    public void AFolderRunningOffTheCanvasIsNamedOnIt()
    {
        var tree = OneBigFolder();
        var big = tree.ChildrenOf(tree.RootNode)[0];
        var viewport = MapViewport.Anchored(3, 0.5, 0.05, 0.5, 0.2);

        var surface = Draw(tree, ExploreView.Treemap, viewport);
        var band = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height, LayoutLimits.Default, viewport: viewport)
            .Single(tile => tile.Node == big);

        // The case itself: the folder's band is on the canvas and its left edge is well off it.
        Assert.True(band.Header > 0 && band.X < -100, $"the folder is at {band.X} with a band of {band.Header}");

        var name = Assert.Single(surface.Labels, label => label.Node == big);

        Assert.InRange(name.X, 0, LayoutLimits.Default.LabelPadding + 0.01);
        Assert.InRange(name.X + name.Width, 0, Width);
        Assert.InRange(name.Y, 0, Height);
    }

    /// <summary>
    /// A folder's band cut to a sliver by the canvas's edge is not named. A name there would be
    /// narrower than its own padding, and a label of negative width is one the shell cannot lay out.
    /// </summary>
    [Fact]
    public void ABandCutToASliverByTheEdgeIsNotNamed()
    {
        var tree = ThreeFolders();
        const double Zoom = 3;

        // A folder that starts right of the canvas when the zoomed picture's left edge is on it, found
        // in the whole picture, whose shapes the zoom only magnifies. The canvas is then moved until
        // only the first few pixels of the folder show at its right.
        var folder = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default)
            .Where(tile => tile.Header > 0 && tile.Node != tree.RootNode && tile.X * Zoom > Width && tile.Y * Zoom < Height - 40)
            .MinBy(tile => tile.X);

        var sliver = MapViewport.Anchored(Zoom, ((folder.X * Zoom) + 4 - Width) / (Width * Zoom), 0, 0, 0);
        var surface = Draw(tree, ExploreView.Treemap, sliver);
        var cut = TreemapLayout.Compute(tree, tree.RootNode, Width, Height, LayoutLimits.Default, viewport: sliver)
            .Single(tile => tile.Node == folder.Node);

        Assert.True(cut.Header > 0 && cut.X is > Width - 8 and < Width, $"the folder starts at {cut.X}");
        Assert.DoesNotContain(surface.Labels, label => label.Node == folder.Node);
        Assert.All(surface.Labels, label => Assert.True(label.Width >= 0, $"a label {label.Width} wide"));
    }

    /// <summary>A shape running off the canvas is labelled only where the part of it on the canvas has room.</summary>
    [Fact]
    public void EveryLabelOfAZoomedPictureIsOnTheCanvas()
    {
        var tree = OneBigFolder();

        foreach (var (x, y) in new[] { (0.2, 0.2), (0.5, 0.5), (0.9, 0.1), (0.95, 0.95) })
        {
            var surface = ExploreSurface.Create(
                tree, tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
                ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None,
                MapViewport.Anchored(6, x, y, 0.5, 0.5));

            Assert.All(surface.Labels, label =>
            {
                Assert.InRange(label.X, 0, Width);
                Assert.InRange(label.X + label.Width, 0, Width);
            });
        }
    }

    /// <summary>
    /// A scan still running is drawn as an icicle whichever view was picked, and an icicle cannot be
    /// zoomed. The drawing says it shows the whole picture, so the map places it, and resolves every
    /// click on it, as the whole picture rather than as the zoom it asked for (§7.1).
    /// </summary>
    [Fact]
    public void ADrawingThatCannotBeZoomedSaysSo()
    {
        var zoom = MapViewport.Anchored(4, 0.5, 0.5, 0.5, 0.5);
        var finished = FilesOf(500, 300, 200);
        var running = NamedFilesOf(500, 300, 200);

        Assert.Equal(zoom, Draw(finished, ExploreView.Treemap, zoom).Viewport);
        Assert.Null(Draw(running, ExploreView.Treemap, zoom).Viewport);
        Assert.Null(Draw(finished, ExploreView.Icicle, zoom).Viewport);
        Assert.Null(Draw(finished, ExploreView.Sunburst, zoom).Viewport);
    }

    /// <summary>What the pointer finds on a zoomed drawing is the shape laid out under it.</summary>
    [Fact]
    public void WhatIsUnderAPointOfAZoomedDrawingIsTheShapeDrawnThere()
    {
        var tree = FilesOf(400, 300, 200, 100);
        var viewport = MapViewport.Anchored(3, 0.7, 0.7, 0.5, 0.5);
        var surface = Draw(tree, ExploreView.Treemap, viewport);
        var tiles = TreemapLayout.Compute(
            tree, tree.RootNode, Width, Height,
            LayoutLimits.Default.Spaced(ExploreSpacing.Comfortable), viewport: viewport);

        foreach (var tile in tiles.Where(tile => tile.Depth == 1))
        {
            var x = Math.Clamp(tile.X + (tile.Width / 2), 1, Width - 1);
            var y = Math.Clamp(tile.Y + (tile.Height / 2), 1, Height - 1);

            if (x > tile.X && x < tile.X + tile.Width && y > tile.Y && y < tile.Y + tile.Height)
            {
                Assert.Equal(tile.Node, surface.At(x, y)?.Node);
            }
        }
    }

    private static ExploreSurface Draw(ExploreTree tree, ExploreView view, MapViewport viewport) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None, viewport);

    private static byte[] Paint(IReadOnlyList<ExploreTile> tiles, int width, int height)
    {
        var pixels = new byte[PixelBuffer.LengthFor(width, height)];

        TileRasteriser.Paint(pixels, tiles, width, height, TileColour.FromRgb(0x123456), (_, depth) => Hues.Colour(0, depth));

        return pixels;
    }

    private static TileColour At(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;

        return new TileColour(pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static ExploreTree FilesOf(params long[] sizes) => Files(ExploreChildOrder.BySize, sizes);

    private static ExploreTree NamedFilesOf(params long[] sizes) => Files(ExploreChildOrder.ByName, sizes);

    private static ExploreTree Files(ExploreChildOrder order, long[] sizes)
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. sizes.Select((size, i) => new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, size))]);

        return builder.Build(order);
    }

    /// <summary>
    /// Where each node's shape falls in the whole picture, as fractions of it, which is what a zoom
    /// must leave alone. Aggregates are left out: they stand for no node, and a zoom is meant to open
    /// them.
    /// </summary>
    private static Dictionary<int, (double X, double Y, double Width, double Height)> InPicture(
        IReadOnlyList<ExploreTile> tiles, MapViewport viewport)
    {
        var across = Width * viewport.Zoom;
        var down = Height * viewport.Zoom;

        return tiles
            .Where(tile => tile.IsNode)
            .ToDictionary(
                tile => tile.Node,
                tile => (
                    viewport.Left + (tile.X / across),
                    viewport.Top + (tile.Y / down),
                    tile.Width / across,
                    tile.Height / down));
    }

    /// <summary>
    /// Folders of every size, several deep, with runs of files too small to draw at the whole
    /// picture's scale: the shape a zoom is for. Sizes come from a fixed seed, so the tree is the same
    /// every run.
    /// </summary>
    private static ExploreTree MixedTree()
    {
        var random = new Random(155);
        var builder = new ExploreTreeBuilder(@"C:\");
        var folders = new Queue<(int Node, int Depth)>();

        folders.Enqueue((ExploreTreeBuilder.RootNode, 0));

        while (folders.TryDequeue(out var folder))
        {
            var subfolders = folder.Depth < 5 ? random.Next(2, 5) : 0;
            var files = random.Next(5, 60);

            var first = builder.AddChildren(folder.Node, [
                .. Enumerable.Range(0, subfolders).Select(i =>
                    new ExploreChild($"dir{folder.Depth}-{i}", IsDirectory: true, IsLink: false, Size: 0)),
                .. Enumerable.Range(0, files).Select(i =>
                    new ExploreChild($"file{i}.bin", IsDirectory: false, IsLink: false, (long)Math.Pow(10, random.NextDouble() * 6) + 1)),
            ]);

            for (var i = 0; i < subfolders; i++)
            {
                folders.Enqueue((first + i, folder.Depth + 1));
            }
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A tree that counts how often it is asked a size, to measure what a layout visits.</summary>
    private sealed class CountingTree(ExploreTree tree) : ISizedTree
    {
        public int Sizes { get; private set; }

        public int RootNode => tree.RootNode;

        public ExploreChildOrder ChildOrder => tree.ChildOrder;

        public long SizeOf(int node)
        {
            Sizes++;
            return tree.SizeOf(node);
        }

        public ReadOnlySpan<int> ChildrenOf(int node) => tree.ChildrenOf(node);

        public bool IsContainer(int node) => ((ISizedTree)tree).IsContainer(node);

        public int ParentOf(int node) => tree.ParentOf(node);
    }

    /// <summary>Three folders of the same size side by side, each with files in it.</summary>
    private static ExploreTree ThreeFolders()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var first = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            .. Enumerable.Range(0, 3).Select(i => new ExploreChild($"folder{i}", IsDirectory: true, IsLink: false, Size: 0)),
        ]);

        for (var i = 0; i < 3; i++)
        {
            builder.AddChildren(
                first + i,
                [.. Enumerable.Range(0, 10).Select(j => new ExploreChild($"part{j}.bin", IsDirectory: false, IsLink: false, Size: 100))]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A folder holding most of the tree, with files in it, beside one small file.</summary>
    private static ExploreTree OneBigFolder()
    {
        var builder = new ExploreTreeBuilder(@"C:\");

        var big = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("big", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("small.bin", IsDirectory: false, IsLink: false, Size: 50),
        ]);

        builder.AddChildren(
            big,
            [.. Enumerable.Range(0, 10).Select(i => new ExploreChild($"part{i}.bin", IsDirectory: false, IsLink: false, Size: 100))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>A chain of directories, each holding one file and one directory.</summary>
    private static ExploreTree NestedTree(int depth)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var parent = ExploreTreeBuilder.RootNode;

        for (var i = 0; i < depth; i++)
        {
            var first = builder.AddChildren(parent, [
                new ExploreChild($"dir{i}", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild($"file{i}", IsDirectory: false, IsLink: false, Size: 100),
            ]);

            parent = first;
        }

        return builder.Build(ExploreChildOrder.BySize);
    }
}
