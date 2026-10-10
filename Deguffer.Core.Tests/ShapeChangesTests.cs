using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Memory;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// What became of each shape between two layouts of the same place: matched by what it is rather
/// than by number, never one shape inside another, and only as many as move on their own.
/// </summary>
public sealed class ShapeChangesTests
{
    private const int Width = 1200;

    private const int Height = 800;

    private const double Precision = 1e-9;

    /// <summary>
    /// A folder that grew pushes its siblings aside. Each folder holds one file filling it, so the
    /// shapes that move on their own are the files, the deepest the cut reaches. The later scan numbered its folders the other way
    /// round, which the test asserts before relying on it, so the match is by path.
    /// </summary>
    [Fact]
    public void AFolderThatGrewMovesItsSiblingsMatchedByPath()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("gamma", 250), ("beta", 200), ("alpha", 300));

        Assert.NotEqual(Find(earlier, "gamma.bin"), Find(later, "gamma.bin"));

        var changes = ShapeChanges.Between(Treemap(earlier), Treemap(later));

        Assert.NotEmpty(changes);
        Assert.All(changes, change => Assert.Equal(ShapeChangeKind.Moved, change.Kind));

        var gamma = Assert.Single(changes, change => change.Node == Find(later, "gamma.bin"));

        AssertClose(Treemap(earlier).FrameOf(Find(earlier, "gamma.bin"))!.Value, gamma.From);
        AssertClose(Treemap(later).FrameOf(Find(later, "gamma.bin"))!.Value, gamma.To);
        Assert.True(gamma.To.Width * gamma.To.Height > gamma.From.Width * gamma.From.Height);
    }

    [Fact]
    public void ANewFolderGrowsOutOfTheMiddleOfItsPlace()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200));
        var later = Scan(("alpha", 300), ("beta", 200), ("delta", 150));

        var delta = Assert.Single(
            ShapeChanges.Between(Treemap(earlier), Treemap(later)),
            change => change.Kind == ShapeChangeKind.Appeared);

        Assert.Equal(Find(later, "delta.bin"), delta.Node);
        AssertClose(Treemap(later).FrameOf(delta.Node)!.Value, delta.To);
        Assert.Equal(0, delta.From.Width);
        Assert.Equal(0, delta.From.Height);
        Assert.Equal(delta.To.Centre.X, delta.From.X, Precision);
        Assert.Equal(delta.To.Centre.Y, delta.From.Y, Precision);
    }

    [Fact]
    public void AFolderGoneShrinksIntoTheMiddleOfWhereItWas()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("alpha", 300), ("gamma", 100));

        var beta = Assert.Single(
            ShapeChanges.Between(Treemap(earlier), Treemap(later)),
            change => change.Kind == ShapeChangeKind.Disappeared);

        Assert.Equal(Find(earlier, "beta.bin"), beta.Node);
        AssertClose(Treemap(earlier).FrameOf(beta.Node)!.Value, beta.From);
        Assert.Equal(0, beta.To.Width);
        Assert.Equal(beta.From.Centre.X, beta.To.X, Precision);
        Assert.Equal(beta.From.Centre.Y, beta.To.Y, Precision);
    }

    /// <summary>
    /// A file moved into another folder is at another path, so it is a different thing: it goes from
    /// the folder it left and comes into the one it joined, rather than sliding across the picture.
    /// </summary>
    [Fact]
    public void AFileMovedToAnotherFolderGoesFromOneAndComesIntoTheOther()
    {
        var earlier = Folders(("one", ["big", "moving"]), ("two", ["other"]));
        var later = Folders(("one", ["big"]), ("two", ["other", "moving"]));

        var changes = ShapeChanges.Between(Treemap(earlier), Treemap(later));

        Assert.Contains(changes, change => change.Kind == ShapeChangeKind.Disappeared && change.Node == Find(earlier, "moving"));
        Assert.Contains(changes, change => change.Kind == ShapeChangeKind.Appeared && change.Node == Find(later, "moving"));
    }

    [Fact]
    public void TheSameLayoutAgainIsNoChange()
    {
        var earlier = Scan(("alpha", 300), ("beta", 200), ("gamma", 100));
        var later = Scan(("gamma", 100), ("alpha", 300), ("beta", 200));

        Assert.Empty(ShapeChanges.Between(Treemap(earlier), Treemap(later)));
    }

    /// <summary>
    /// On a tree of many shapes, as many move on their own as there is room for, cut as deep as that
    /// allows (files, not only the folders at the top), and none of them inside another, which would
    /// show what is inside it twice.
    /// </summary>
    [Fact]
    public void ManyShapesAreCutToTheLargestWithNoneInsideAnother()
    {
        var earlier = Many(folders: 40, files: 12, grown: -1);
        var later = Many(folders: 40, files: 12, grown: 0);

        var changes = ShapeChanges.Between(Treemap(earlier), Treemap(later));
        var moved = changes.Where(change => change.Kind != ShapeChangeKind.Disappeared).ToList();

        Assert.InRange(moved.Count, 2, ShapeChanges.MostShapes);
        Assert.Contains(moved, change => later.ParentOf(change.Node) != later.RootNode);

        foreach (var outer in moved)
        {
            foreach (var inner in moved.Where(other => other != outer))
            {
                Assert.False(
                    Contains(outer.To, inner.To),
                    $"{later.NameOf(inner.Node)} is reported inside {later.NameOf(outer.Node)}");
            }
        }
    }

    /// <summary>
    /// The cut through a large tree stops at the limit rather than reporting every shape.
    /// </summary>
    [Fact]
    public void NoMoreShapesThanTheLimitMoveOnTheirOwn()
    {
        var earlier = Many(folders: 30, files: 30, grown: -1);
        var later = Many(folders: 30, files: 30, grown: 0);
        var drawn = Treemap(later).Tiles.Count;

        Assert.True(drawn > ShapeChanges.MostShapes * 2, $"the fixture draws only {drawn} shapes");

        var changes = ShapeChanges.Between(Treemap(earlier), Treemap(later));

        Assert.InRange(changes.Count(change => change.Kind != ShapeChangeKind.Disappeared), 1, ShapeChanges.MostShapes);
    }

    /// <summary>
    /// In an icicle a folder's children are drawn beside it rather than in it, so a folder and its
    /// children both move on their own.
    /// </summary>
    [Fact]
    public void InAnIcicleAFolderAndWhatItHoldsBothMove()
    {
        var earlier = Folders(("one", ["big", "small"]), ("two", ["other"]));
        var later = Folders(("one", ["big", "small", "added"]), ("two", ["other"]));

        var changes = ShapeChanges.Between(Icicle(earlier), Icicle(later));

        Assert.Contains(changes, change => change.Node == Find(later, "one"));
        Assert.Contains(changes, change => change.Node == Find(later, "big"));
    }

    /// <summary>A reading of memory renumbers every node, so a process is matched by its identifier and creation time.</summary>
    [Fact]
    public void AProcessIsMatchedAcrossTwoReadingsOfMemory()
    {
        var earlier = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(100, 1, "alpha.exe", 300, created: 10)
            .Process(200, 1, "gamma.exe", 200, created: 20)
            .Build());
        var later = MemoryTreeBuilder.Build(new MemorySnapshotBuilder()
            .Process(50, 1, "beta.exe", 900, created: 5)
            .Process(100, 1, "alpha.exe", 350, created: 10)
            .Process(200, 1, "gamma.exe", 200, created: 20)
            .Build());

        var key = new MemoryNodeKey(MemoryPart.Process, 100, 10);
        var was = earlier.Find(key)!.Value;
        var now = later.Find(key)!.Value;

        Assert.NotEqual(was, now);
        Assert.Equal(now, earlier.CounterpartsIn(later)!(was));

        var changes = ShapeChanges.Between(Treemap(earlier), Treemap(later));
        var alpha = Assert.Single(changes, change => change.Node == now);

        Assert.Equal(ShapeChangeKind.Moved, alpha.Kind);
        AssertClose(Treemap(earlier).FrameOf(was)!.Value, alpha.From);
    }

    [Fact]
    public void TreesOfDifferentThingsHaveNothingToMatch()
    {
        var scan = Scan(("alpha", 300));
        var other = Scan(@"D:\", ("alpha", 300));
        var memory = MemoryTreeBuilder.Build(new MemorySnapshotBuilder().Process(100, 1, "alpha.exe", 300, created: 10).Build());

        Assert.Null(scan.CounterpartsIn(other));
        Assert.Null(scan.CounterpartsIn(memory));
        Assert.Null(memory.CounterpartsIn(scan));
        Assert.Empty(ShapeChanges.Between(Treemap(scan), Treemap(memory)));
    }

    /// <summary>A path is found again without regard to case, as Windows names are, and a name no longer there is not found.</summary>
    [Fact]
    public void AScannedNodeIsFoundByItsPathWithoutRegardToCase()
    {
        var earlier = Folders(("Photos", ["Beach.jpg", "City.png"]));
        var later = Folders(("extra", ["x"]), ("photos", ["beach.JPG"]));
        var find = earlier.CounterpartsIn(later)!;

        Assert.Equal(Find(later, "beach.JPG"), find(Find(earlier, "Beach.jpg")));
        Assert.Equal(Find(later, "photos"), find(Find(earlier, "Photos")));
        Assert.Null(find(Find(earlier, "City.png")));
    }

    /// <summary>
    /// Siblings of the same size come out in name order whatever order the scan met them in, so two
    /// scans of the same disk lay them out the same way.
    /// </summary>
    [Fact]
    public void SiblingsOfTheSameSizeAreInNameOrder()
    {
        string[] names = ["delta", "alpha", "echo", "charlie", "bravo", "foxtrot", "golf"];

        var forward = Scan([.. names.Select(name => (name, 100L))]);
        var backward = Scan([.. names.Reverse().Select(name => (name, 100L))]);

        string[] expected = [.. names.Order(StringComparer.OrdinalIgnoreCase)];

        Assert.Equal(expected, Names(forward));
        Assert.Equal(expected, Names(backward));
    }

    private static string[] Names(ExploreTree tree) =>
        [.. tree.ChildrenOf(tree.RootNode).ToArray().Select(tree.NameOf)];

    private static bool Contains(MapFrame outer, MapFrame inner) =>
        inner.X >= outer.X - Precision && inner.Y >= outer.Y - Precision
        && inner.X + inner.Width <= outer.X + outer.Width + Precision
        && inner.Y + inner.Height <= outer.Y + outer.Height + Precision;

    private static ExploreSurface Treemap(ISizedTree tree) => Drawing(tree, ExploreView.Treemap);

    private static ExploreSurface Icicle(ISizedTree tree) => Drawing(tree, ExploreView.Icicle);

    private static ExploreSurface Drawing(ISizedTree tree, ExploreView view) =>
        ExploreSurface.Create(
            tree, tree.RootNode, view, Width, Height, scale: 1, textScale: 1,
            ShapeColours.ByBranch(ExploreScheme.Standard), ExploreSpacing.Comfortable, VolumeSpace.None);

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
                [.. folders[i].Files.Select((file, k) => new ExploreChild(file, IsDirectory: false, IsLink: false, Size: 300_000 - (k * 50_000)))]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// <paramref name="folders"/> folders of <paramref name="files"/> files, sizes falling steeply, with
    /// the folder at <paramref name="grown"/> twice as large (none for -1).
    /// </summary>
    private static ExploreTree Many(int folders, int files, int grown)
    {
        var builder = new ExploreTreeBuilder(@"C:\");
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(0, folders).Select(i => new ExploreChild($"folder{i:d2}", IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders; i++)
        {
            var scale = (i == grown ? 2 : 1) * (folders - i) * 10_000L;

            builder.AddChildren(
                first + i,
                [.. Enumerable.Range(0, files).Select(k => new ExploreChild($"file{i:d2}-{k:d2}", IsDirectory: false, IsLink: false, Size: scale * (files - k)))]);
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

    private static void AssertClose(MapFrame expected, MapFrame actual)
    {
        Assert.Equal(expected.X, actual.X, 1e-6);
        Assert.Equal(expected.Y, actual.Y, 1e-6);
        Assert.Equal(expected.Width, actual.Width, 1e-6);
        Assert.Equal(expected.Height, actual.Height, 1e-6);
    }
}
