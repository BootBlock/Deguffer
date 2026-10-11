using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// A zoomed map laid out again keeps the reader on what they zoomed into: the shape under the middle
/// of the screen is under it again, at the same place inside it, and the zoom is left alone.
/// </summary>
public sealed class MapFollowTests
{
    private const int Width = 1200;

    private const int Height = 800;

    private const double Ceiling = 64;

    /// <summary>
    /// The case the Memory page is for: zoomed into a service host, which grows past a sibling at the
    /// next reading and so is laid out on the other side of it. Held where it was, the camera would
    /// show the sibling, which the test asserts before relying on it.
    /// </summary>
    [Fact]
    public void AProcessThatGrewPastASiblingStaysUnderTheMiddle()
    {
        var earlier = Memory(hostMiB: 500);
        var later = Memory(hostMiB: 1_200);
        var viewport = MapViewport.Fitting(Quarter(Drawing(earlier, MapViewport.Whole).FrameOf(Host(earlier))!.Value), Ceiling);
        var watched = Middle(Drawing(earlier, viewport))!.Value;
        var counterpart = earlier.CounterpartsIn(later)!(watched);

        Assert.Equal("svchost.exe", earlier.ProcessOf(watched)?.Name);
        Assert.NotNull(counterpart);
        Assert.NotEqual(counterpart, Middle(Drawing(later, viewport)));

        var onward = MapFollow.Onward(Drawing(earlier, viewport), Drawing(later, viewport));

        Assert.Equal(counterpart, Middle(Drawing(later, onward)));
        Assert.Equal(viewport.Zoom, onward.Zoom, 1e-12);
    }

    /// <summary>
    /// The middle is kept at the same place inside the shape, as a share of it, so a shape that moved
    /// and grew still has the same part of it under the middle.
    /// </summary>
    [Fact]
    public void TheMiddleStaysAtTheSameShareOfTheShape()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("alpha", 300), ("beta", 200), ("gamma", 260));
        var gamma = Find(earlier, "gamma.bin");
        var whole = Drawing(earlier, MapViewport.Whole).FrameOf(gamma)!.Value;
        var off = whole with { X = whole.X + (whole.Width / 4), Y = whole.Y + (whole.Height / 3) };
        var viewport = MapViewport.Fitting(off with { Width = whole.Width / 4, Height = whole.Height / 4 }, Ceiling);

        var before = Share(Drawing(earlier, viewport), gamma);
        var onward = MapFollow.Onward(Drawing(earlier, viewport), Drawing(later, viewport));
        var after = Share(Drawing(later, onward), Find(later, "gamma.bin"));

        Assert.NotEqual(viewport, onward);
        Assert.Equal(before.X, after.X, 1e-3);
        Assert.Equal(before.Y, after.Y, 1e-3);
    }

    [Fact]
    public void ALayoutThatDidNotChangeKeepsTheCameraWhereItIs()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("gamma", 100), ("alpha", 300), ("beta", 200));
        var viewport = Onto(earlier, Find(earlier, "beta.bin"));

        var onward = MapFollow.Onward(Drawing(earlier, viewport), Drawing(later, viewport));

        Assert.Equal(viewport.Left, onward.Left, 1e-9);
        Assert.Equal(viewport.Top, onward.Top, 1e-9);
    }

    /// <summary>
    /// A shape that went hands over to the folder it was in, which the camera follows instead. Here the
    /// folder grew past its sibling in the same reading, so a camera that followed nothing would not be
    /// on it.
    /// </summary>
    [Fact]
    public void AShapeThatWentHandsOverToItsFolder()
    {
        var earlier = Folders(("one", ["big", "moving"]), ("two", ["other", "more", "most", "extra"]));
        var later = Folders(("one", ["big", "plenty", "lots", "heaps", "loads"]), ("two", ["other", "more", "most", "extra"]));
        var moving = Find(earlier, "moving");
        var viewport = Onto(earlier, moving);

        Assert.NotEqual(Find(later, "one"), Folder(Drawing(later, viewport), later));

        var onward = MapFollow.Onward(Drawing(earlier, viewport), Drawing(later, viewport));

        Assert.Equal(Find(later, "one"), Folder(Drawing(later, onward), later));
    }

    [Fact]
    public void TheWholePictureIsNeverMoved()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("alpha", 300), ("beta", 200), ("gamma", 260));

        Assert.Equal(
            MapViewport.Whole,
            MapFollow.Onward(Drawing(earlier, MapViewport.Whole), Drawing(later, MapViewport.Whole)));
    }

    /// <summary>
    /// Two drawings made at different zooms are not one place laid out again: the camera moved between
    /// them, so it stays where the later one was drawn rather than going back to the earlier.
    /// </summary>
    [Fact]
    public void DrawingsAtTwoZoomsKeepTheLaterZoom()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("alpha", 300), ("beta", 200), ("gamma", 260));
        var zoomed = Onto(later, Find(later, "beta.bin"));

        Assert.Equal(zoomed, MapFollow.Onward(Drawing(earlier, MapViewport.Whole), Drawing(later, zoomed)));
        Assert.Equal(
            MapViewport.Whole,
            MapFollow.Onward(Drawing(earlier, Onto(earlier, Find(earlier, "beta.bin"))), Drawing(later, MapViewport.Whole)));
    }

    /// <summary>A tree of something else holds nothing of this one, so there is nothing to follow.</summary>
    [Fact]
    public void ATreeOfSomethingElseKeepsTheCameraWhereItIs()
    {
        var earlier = Scan(@"C:\", ("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(@"D:\", ("alpha", 300), ("beta", 200), ("gamma", 260));
        var viewport = Onto(earlier, Find(earlier, "gamma.bin"));

        Assert.Equal(viewport, MapFollow.Onward(Drawing(earlier, viewport), Drawing(later, viewport)));
    }

    /// <summary>The middle quarter of <paramref name="frame"/>.</summary>
    private static MapFrame Quarter(MapFrame frame) =>
        new(frame.X + (frame.Width * 3 / 8), frame.Y + (frame.Height * 3 / 8), frame.Width / 4, frame.Height / 4);

    /// <summary>The viewport that fills the screen with <paramref name="node"/>'s shape.</summary>
    private static MapViewport Onto(ISizedTree tree, int node) =>
        MapViewport.Fitting(Drawing(tree, MapViewport.Whole).FrameOf(node)!.Value, Ceiling);

    /// <summary>The deepest node drawn under the middle of the screen.</summary>
    private static int? Middle(ExploreSurface drawing) =>
        drawing.Tiles.LastOrDefault(tile => tile.IsNode && Under(tile, drawing)) is { IsNode: true } tile ? tile.Node : null;

    /// <summary>The top-level folder drawn under the middle of the screen.</summary>
    private static int? Folder(ExploreSurface drawing, ExploreTree tree) =>
        drawing.Tiles.FirstOrDefault(tile => tile.IsNode && tile.Node != tree.RootNode && Under(tile, drawing)) is { IsNode: true } tile
            ? tile.Node
            : null;

    private static bool Under(ExploreTile tile, ExploreSurface drawing) =>
        drawing.Width / 2f >= tile.X && drawing.Width / 2f < tile.X + tile.Width
        && drawing.Height / 2f >= tile.Y && drawing.Height / 2f < tile.Y + tile.Height;

    /// <summary>Where the middle of the screen is inside <paramref name="node"/>'s shape, as a share of it.</summary>
    private static (double X, double Y) Share(ExploreSurface drawing, int node)
    {
        var frame = drawing.FrameOf(node)!.Value;

        return ((0.5 - frame.X) / frame.Width, (0.5 - frame.Y) / frame.Height);
    }

    private static ExploreSurface Drawing(ISizedTree tree, MapViewport viewport) =>
        ExploreSurface.Create(
            tree, tree.RootNode, ExploreView.Treemap, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None, viewport);

    /// <summary>An application and two service hosts, the one watched at <paramref name="hostMiB"/>.</summary>
    private static MemoryTree Memory(long hostMiB) => MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
        .Process(100, 1, "alpha.exe", 3_000, created: 10)
        .Process(200, 1, "svchost.exe", 1_000, created: 20)
        .Service("ExampleSearch", host: 200)
        .Process(300, 1, "svchost.exe", hostMiB, created: 30)
        .Service("ExampleIndexer", host: 300)
        .Build());

    private static int Host(MemoryTree tree) => tree.Find(new MemoryNodeKey(MemoryPart.Process, 300, 30))!.Value;

    private static ExploreTree Scan(params (string Name, long Size)[] folders) => Scan(@"C:\", folders);

    /// <summary>Folders of one file each, sized as given, met in the order given.</summary>
    private static ExploreTree Scan(string root, params (string Name, long Size)[] folders)
    {
        var builder = new ExploreTreeBuilder(root);
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. folders.Select(folder => new ExploreChild(folder.Name, IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [new ExploreChild($"{folders[i].Name}.bin", IsDirectory: false, IsLink: false, Size: folders[i].Size * 1000)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>Folders of files, each file a little smaller than the one before it.</summary>
    private static ExploreTree Folders(params (string Name, string[] Files)[] folders)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. folders.Select(folder => new ExploreChild(folder.Name, IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(
                first + i,
                [.. folders[i].Files.Select((file, k) => new ExploreChild(file, IsDirectory: false, IsLink: false, Size: 300_000 - (k * 40_000)))]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Find(ExploreTree tree, string name)
    {
        var pending = new Stack<int>([tree.RootNode]);

        while (pending.TryPop(out var node))
        {
            if (tree.NameOf(node) == name)
            {
                return node;
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        throw new InvalidOperationException($"No node is named {name}.");
    }
}
