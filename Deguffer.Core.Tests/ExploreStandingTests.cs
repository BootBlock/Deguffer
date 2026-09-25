using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// Where the views stand in a scanned tree, and in particular the root of a scan of a whole volume,
/// which is drawn beside the volume's free space until the reader opens it.
/// </summary>
public sealed class ExploreStandingTests
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
        var top = new ExploreStanding(tree.RootNode);

        Assert.Equal(Volume, top.Beside(tree, Volume));

        var opened = top.Opening(tree, tree.RootNode, Volume);

        Assert.Equal(new ExploreStanding(tree.RootNode, RootOpened: true), opened);
        Assert.Equal(VolumeSpace.None, opened!.Value.Beside(tree, Volume));
    }

    /// <summary>Opened already, or with no volume beside it, the root has nothing further to open into.</summary>
    [Fact]
    public void TheRootOpensOnlyOutOfAVolumeDrawnBesideIt()
    {
        var tree = Tree(@"D:\");

        Assert.Null(new ExploreStanding(tree.RootNode, RootOpened: true).Opening(tree, tree.RootNode, Volume));
        Assert.Null(new ExploreStanding(tree.RootNode).Opening(tree, tree.RootNode, VolumeSpace.None));
    }

    [Fact]
    public void GoingUpFromTheOpenedRootGoesBackOutToTheVolume()
    {
        var tree = Tree(@"D:\");
        var opened = new ExploreStanding(tree.RootNode, RootOpened: true);

        var up = opened.Up(tree);

        Assert.Equal(new ExploreStanding(tree.RootNode), up);
        Assert.Equal(Volume, up!.Value.Beside(tree, Volume));
        Assert.Null(up.Value.Up(tree));
    }

    /// <summary>
    /// Below the root the volume is never drawn, and going back up keeps the root as the reader
    /// left it: opened where they opened it, beside the volume where they went in from there.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFolderBelowTheRootKeepsTheRootAsItWasLeft(bool rootOpened)
    {
        var tree = Tree(@"D:\");
        var folder = tree.ChildrenOf(tree.RootNode)[0];

        var inside = new ExploreStanding(tree.RootNode, rootOpened).Opening(tree, folder, Volume);

        Assert.Equal(new ExploreStanding(folder, rootOpened), inside);
        Assert.Equal(VolumeSpace.None, inside!.Value.Beside(tree, Volume));
        Assert.Equal(new ExploreStanding(tree.RootNode, rootOpened), inside.Value.Up(tree));
    }

    /// <summary>
    /// A rescan of the same drive keeps the root open; a scan of another drive is another volume, and
    /// opens at it with its free space beside it.
    /// </summary>
    [Fact]
    public void AnOpenedRootStaysOpenOnlyAcrossScansOfTheSameDrive()
    {
        var first = Tree(@"D:\");
        var opened = new ExploreStanding(first.RootNode, RootOpened: true);

        Assert.True(opened.CarriedTo(first, Tree(@"D:\")).RootOpened);
        Assert.False(opened.CarriedTo(first, Tree(@"E:\")).RootOpened);
        Assert.False(opened.CarriedTo(null, Tree(@"D:\")).RootOpened);
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
