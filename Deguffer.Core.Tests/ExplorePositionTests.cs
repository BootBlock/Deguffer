using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where the views are in a scanned tree, and in particular the root of a scan of a whole volume,
/// which is drawn beside the volume's free space until the reader opens it.
/// </summary>
public sealed class ExplorePositionTests
{
    private static readonly VolumeSpace Volume = new(TotalBytes: 100_000, FreeBytes: 40_000);

    /// <summary>
    /// Opening the root of a volume draws the root alone. Without this a double-click on it asked for
    /// the picture already on screen, and the free space stayed beside it.
    /// </summary>
    [Fact]
    public void OpeningTheRootOfAVolumeDrawsItWithoutTheVolumeBesideIt()
    {
        var tree = Tree(@"D:\");
        var top = ExplorePosition.Top(tree);

        Assert.Equal(Volume, top.Beside(tree, Volume));

        var opened = top.Opening(tree, tree.RootNode, Volume);

        Assert.Equal(ExplorePosition.Inside(tree.RootNode), opened);
        Assert.Equal(VolumeSpace.None, opened!.Value.Beside(tree, Volume));
    }

    /// <summary>
    /// Inside the root already, or with no volume drawn beside it, the root has nothing further to
    /// open into: asking would only draw the same picture again.
    /// </summary>
    [Fact]
    public void TheRootOpensOnlyOutOfAVolumeDrawnBesideIt()
    {
        var tree = Tree(@"D:\");

        Assert.Null(ExplorePosition.Inside(tree.RootNode).Opening(tree, tree.RootNode, Volume));
        Assert.Null(ExplorePosition.Top(tree).Opening(tree, tree.RootNode, VolumeSpace.None));
    }

    /// <summary>
    /// A file, and a folder with nothing in it, lead nowhere. Opened, either would draw an empty card
    /// under a trail claiming the reader had gone somewhere.
    /// </summary>
    [Fact]
    public void AFileOrAnEmptyFolderLeadsNowhere()
    {
        var builder = new ExploreTreeBuilder(@"D:\");
        var empty = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("empty", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("file", IsDirectory: false, IsLink: false, Size: 10),
        ]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var top = ExplorePosition.Inside(tree.RootNode);

        Assert.Null(top.Opening(tree, empty, Volume));
        Assert.Null(top.Opening(tree, empty + 1, Volume));
    }

    [Fact]
    public void GoingUpFromTheOpenedRootGoesBackOutToTheVolume()
    {
        var tree = Tree(@"D:\");

        var up = ExplorePosition.Inside(tree.RootNode).Up(tree, Volume);

        Assert.Equal(ExplorePosition.Top(tree), up);
        Assert.Equal(Volume, up!.Value.Beside(tree, Volume));
        Assert.Null(up.Value.Up(tree, Volume));
    }

    /// <summary>
    /// Going up is one step back along the trail whatever route led there: a folder opened straight
    /// from the volume goes up to the root, not past it to the volume.
    /// </summary>
    [Fact]
    public void GoingUpFromAFolderGoesToTheRootWhicheverWayTheReaderCame()
    {
        var tree = Tree(@"D:\");
        var folder = tree.ChildrenOf(tree.RootNode)[0];

        var inside = ExplorePosition.Top(tree).Opening(tree, folder, Volume);

        Assert.Equal(ExplorePosition.Inside(folder), inside);
        Assert.Equal(VolumeSpace.None, inside!.Value.Beside(tree, Volume));
        Assert.Equal(ExplorePosition.Inside(tree.RootNode), inside.Value.Up(tree, Volume));
    }

    /// <summary>
    /// A scan of a folder has no volume, so its root is the top: there is nowhere up to go, and no
    /// step on the trail for a volume. This is also a scan still running, whose snapshots carry none.
    /// </summary>
    [Fact]
    public void WithoutAVolumeTheRootIsTheTop()
    {
        var tree = Tree(@"D:\Projects");
        var top = ExplorePosition.Top(tree);

        Assert.Null(top.Up(tree, VolumeSpace.None));
        Assert.Null(ExplorePosition.Inside(tree.RootNode).Up(tree, VolumeSpace.None));
        Assert.Equal([ExplorePosition.Inside(tree.RootNode)], Trail(top, tree, VolumeSpace.None));
    }

    /// <summary>
    /// The trail names every step back to the top: the volume first, then the root, then each folder.
    /// On the volume itself it is that one step, because the root is inside it.
    /// </summary>
    [Fact]
    public void TheTrailStartsAtTheVolumeAndPassesThroughTheRoot()
    {
        var tree = Tree(@"D:\");
        var folder = tree.ChildrenOf(tree.RootNode)[0];

        Assert.Equal([ExplorePosition.Top(tree)], Trail(ExplorePosition.Top(tree), tree, Volume));
        Assert.Equal(
            [ExplorePosition.Top(tree), ExplorePosition.Inside(tree.RootNode), ExplorePosition.Inside(folder)],
            Trail(ExplorePosition.Inside(folder), tree, Volume));
    }

    /// <summary>
    /// A rescan of the same drive comes back to the same side of its root, including across the
    /// snapshots of a scan still running, which carry no volume. A scan of another drive opens at its top.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSideOfTheRootIsKeptAcrossScansOfTheSameDrive(bool onVolume)
    {
        var first = Tree(@"D:\");
        var position = new ExplorePosition(first.RootNode, onVolume);

        var snapshot = Tree(@"d:\");
        var throughSnapshot = position.CarriedTo(first, snapshot);
        var finished = throughSnapshot.CarriedTo(snapshot, Tree(@"D:\"));

        Assert.Equal(onVolume, finished.OnVolume);
        Assert.True(position.CarriedTo(first, Tree(@"E:\")).OnVolume);
        Assert.True(position.CarriedTo(null, Tree(@"D:\")).OnVolume);
    }

    [Fact]
    public void AFolderIsCarriedToTheSameFolderInsideTheRoot()
    {
        var first = Tree(@"D:\");
        var folder = ExplorePosition.Inside(first.ChildrenOf(first.RootNode)[0]);
        var arriving = Tree(@"D:\");

        Assert.Equal(ExplorePosition.Inside(arriving.ChildrenOf(arriving.RootNode)[0]), folder.CarriedTo(first, arriving));
    }

    private static List<ExplorePosition> Trail(ExplorePosition position, ExploreTree tree, VolumeSpace volume)
    {
        var steps = new List<ExplorePosition>();

        position.Trail(tree, volume, steps);

        return steps;
    }

    private static ExploreTree Tree(string root)
    {
        var builder = new ExploreTreeBuilder(root);
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [new ExploreChild("folder", IsDirectory: true, IsLink: false, Size: 0)]);

        builder.AddChildren(folder, [new ExploreChild("file", IsDirectory: false, IsLink: false, Size: 60_000)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
