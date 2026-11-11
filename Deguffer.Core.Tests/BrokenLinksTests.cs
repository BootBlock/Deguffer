using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;
using Deguffer.Core.Exploring.Links;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// The broken links found in a scanned tree: which entries are read, which are listed and in what
/// order, and that a link whose target cannot be reached is counted and never listed. The tree is
/// built here and every link is described through the fakes.
/// </summary>
public sealed class BrokenLinksTests
{
    private const string Root = @"C:\Users\testuser";

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeLinkTargets _links = new FakeLinkTargets()
        .With($@"{Root}\Desktop\Old Game.lnk", LinkKind.Shortcut, @"C:\Games\Old\game.exe")
        .With($@"{Root}\Desktop\Kept.lnk", LinkKind.Shortcut, @"C:\Games\Kept\game.exe")
        .With($@"{Root}\Desktop\Away.lnk", LinkKind.Shortcut, @"E:\Games\game.exe")
        .With($@"{Root}\Deep\Deeper\Tool.lnk", LinkKind.Shortcut, @"C:\Tools\tool.exe")
        .With($@"{Root}\cache", LinkKind.Junction, @"C:\Moved\cache")
        .With($@"{Root}\cache\Inside.lnk", LinkKind.Shortcut, @"C:\Behind\app.exe")
        .With($@"{Root}\media", LinkKind.SymbolicLink, @"C:\Media");

    private readonly FakePathProbe _probe = new FakePathProbe().File(@"C:\Games\Kept\game.exe").Directory(@"C:\Media");

    private LinkInspection Inspection() => new(_links, new LinkTargets(_probe, new FakeVolumeInventory().With(@"C:\")));

    [Fact]
    public void EveryLinkWhoseTargetIsGoneIsListedByPathAndTheUnreachableOneIsCounted()
    {
        var tree = Tree();

        var found = BrokenLinks.Find(tree, tree.RootNode, new FileFilter(), Now, Inspection());

        Assert.Equal(
            [$@"{Root}\cache", $@"{Root}\Deep\Deeper\Tool.lnk", $@"{Root}\Desktop\Old Game.lnk"],
            found.Links.Select(link => link.Link.Path));
        Assert.All(found.Links, link => Assert.Equal(tree.PathOf(link.Node), link.Link.Path));
        Assert.All(found.Links, link => Assert.True(link.Link.IsOffered));
        Assert.Equal(3, found.Gone);
        Assert.Equal(1, found.Unreachable);
        Assert.True(found.IsComplete);
    }

    /// <summary>
    /// Only links and files named as shortcuts are read: a pass over a volume reads nothing else, and a
    /// link is never descended into, because what it leads to keeps its own place.
    /// </summary>
    [Fact]
    public void OnlyLinksAndShortcutsAreRead()
    {
        var tree = Tree();

        BrokenLinks.Find(tree, tree.RootNode, new FileFilter(), Now, Inspection());

        Assert.Equal(6, _links.Reads.Count);
        Assert.False(_links.Reads.ContainsKey($@"{Root}\Desktop\notes.txt"));
        Assert.False(_links.Reads.ContainsKey($@"{Root}\Desktop"));
        Assert.False(_links.Reads.ContainsKey($@"{Root}\cache\Inside.lnk"));
    }

    [Fact]
    public void TheListFollowsTheFolderAndTheFilter()
    {
        var tree = Tree();
        var desktop = TreeNodes.At(tree, $@"{Root}\Desktop");

        var inDesktop = BrokenLinks.Find(tree, desktop, new FileFilter(), Now, Inspection());
        var named = BrokenLinks.Find(tree, tree.RootNode, new FileFilter(Name: "game"), Now, Inspection());

        Assert.Equal([$@"{Root}\Desktop\Old Game.lnk"], inDesktop.Links.Select(link => link.Link.Path));
        Assert.Equal(1, inDesktop.Unreachable);
        Assert.Equal([$@"{Root}\Desktop\Old Game.lnk"], named.Links.Select(link => link.Link.Path));
        Assert.Equal(0, named.Unreachable);
    }

    [Fact]
    public void AListCutAtTheLimitSaysHowManyThereAre()
    {
        var tree = Tree();

        var found = BrokenLinks.Find(tree, tree.RootNode, new FileFilter(), Now, Inspection(), limit: 2);

        Assert.Equal(2, found.Links.Count);
        Assert.Equal(3, found.Gone);
        Assert.False(found.IsComplete);
    }

    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                new ExploreChild("Desktop", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("Deep", IsDirectory: true, IsLink: false, Size: 0),
                new ExploreChild("cache", IsDirectory: true, IsLink: true, Size: 0),
                new ExploreChild("media", IsDirectory: true, IsLink: true, Size: 0),
            ]);

        builder.AddChildren(
            top,
            [
                new ExploreChild("Old Game.lnk", IsDirectory: false, IsLink: false, Size: 1_200),
                new ExploreChild("Kept.lnk", IsDirectory: false, IsLink: false, Size: 1_100),
                new ExploreChild("Away.lnk", IsDirectory: false, IsLink: false, Size: 1_000),
                new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, Size: 10),
            ]);

        var deeper = builder.AddChildren(top + 1, [new ExploreChild("Deeper", IsDirectory: true, IsLink: false, Size: 0)]);
        builder.AddChildren(deeper, [new ExploreChild("Tool.lnk", IsDirectory: false, IsLink: false, Size: 900)]);

        // What a link leads to is in no tree as its child; here it is, so a pass that descended would read it.
        builder.AddChildren(top + 2, [new ExploreChild("Inside.lnk", IsDirectory: false, IsLink: false, Size: 800)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
