using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// A scanned tree with items taken out since the scan: what the disk holds after a removal, without
/// scanning it again. The sizes above what went lose it, what went is held nowhere, and everything
/// else, the scanned tree included, is as it was.
/// </summary>
public sealed class ExploreTreeWithoutTests
{
    /// <summary>
    /// Every folder above what went loses its size and its length, and nothing else does: not its
    /// siblings, not what is beside the folder above it, and not the tree as scanned, whose arrays the
    /// tree left shares.
    /// </summary>
    [Fact]
    public void TheFoldersAboveWhatWentLoseItsSizeAndNothingElseChanges()
    {
        var tree = Media();
        var films = Find(tree, "films");
        var old = Find(tree, "old");
        var music = Find(tree, "music");

        var left = tree.Without([old]);

        Assert.Equal(400, left.SizeOf(films));
        Assert.Equal(800, left.LengthOf(films));
        Assert.Equal(700, left.SizeOf(tree.RootNode));
        Assert.Equal(700, left.TotalBytes);
        Assert.Equal(1400, left.TotalLength);

        // The negative: what stayed is the size it was, and the scan is untouched.
        Assert.Equal(300, left.SizeOf(music));
        Assert.Equal(400, left.SizeOf(Find(tree, "new")));
        Assert.Equal(1000, tree.SizeOf(films));
        Assert.Equal(1300, tree.TotalBytes);
        Assert.Equal(2600, tree.TotalLength);
        Assert.Contains(old, tree.ChildrenOf(films).ToArray());
        Assert.True(tree.Holds(old));
    }

    /// <summary>
    /// What went, and everything under it, is in no list and has no path, so nothing can draw it,
    /// list it or pick it again (§7.1). What stayed keeps its number and its path.
    /// </summary>
    [Fact]
    public void WhatWentIsHeldNowhereAndWhatStayedKeepsItsNumber()
    {
        var tree = Media();
        var films = Find(tree, "films");
        var old = Find(tree, "old");
        var inside = Find(tree, "old0.mp4");
        var kept = Find(tree, "new0.mp4");

        var left = tree.Without([old]);

        Assert.False(left.Holds(old));
        Assert.False(left.Holds(inside));
        Assert.Null(left.TryPathOf(old));
        Assert.Null(left.TryPathOf(inside));
        Assert.DoesNotContain(old, left.ChildrenOf(films).ToArray());

        Assert.True(left.Holds(kept));
        Assert.True(left.Holds(left.RootNode));
        Assert.Equal(tree.PathOf(kept), left.PathOf(kept));
        Assert.Equal(tree.PathOf(films), left.PathOf(films));
    }

    /// <summary>
    /// A folder that lost bytes is put back among its siblings by size, because the treemap and the
    /// sunburst draw only a list ordered by size. Films held 1000 bytes ahead of music's 300; less
    /// both of its folders, it holds nothing and goes after music.
    /// </summary>
    [Fact]
    public void AFolderThatLostBytesIsOrderedAgainAmongItsSiblings()
    {
        var tree = Media();
        var films = Find(tree, "films");
        var music = Find(tree, "music");

        Assert.Equal([films, music], tree.ChildrenOf(tree.RootNode).ToArray());

        var left = tree.Without([Find(tree, "old"), Find(tree, "new")]);

        Assert.Equal(0, left.SizeOf(films));
        Assert.Equal([music, films], left.ChildrenOf(left.RootNode).ToArray());
        Assert.Equal(ExploreChildOrder.BySize, left.ChildOrder);
    }

    /// <summary>
    /// Siblings that end up the same size are in name order, as the scan puts them, so a removal never
    /// shows equal siblings trading places for no reason.
    /// </summary>
    [Fact]
    public void SiblingsLeftTheSameSizeAreInNameOrder()
    {
        var tree = Media();
        var films = Find(tree, "films");
        var music = Find(tree, "music");

        // Films is 1000 bytes: 600 of old and 400 of new. Less 700 it is music's 300, and named first.
        var left = tree.Without([Find(tree, "old"), Find(tree, "new1.mp4")]);

        Assert.Equal(300, left.SizeOf(films));
        Assert.Equal([films, music], left.ChildrenOf(left.RootNode).ToArray());

        // Named after music, the same folder goes after it.
        var renamed = Media(filmsName: "zfilms");
        var renamedLeft = renamed.Without([Find(renamed, "old"), Find(renamed, "new1.mp4")]);

        Assert.Equal([Find(renamed, "music"), Find(renamed, "zfilms")], renamedLeft.ChildrenOf(renamedLeft.RootNode).ToArray());
    }

    /// <summary>
    /// A folder and something inside it picked together lose the folder's bytes once. Counted twice,
    /// the folder above would be short by what was inside it.
    /// </summary>
    [Fact]
    public void SomethingInsideAFolderTakenWithItIsNotCountedTwice()
    {
        var tree = Media();

        var left = tree.Without([Find(tree, "old"), Find(tree, "old0.mp4")]);

        Assert.Equal(400, left.SizeOf(Find(tree, "films")));
        Assert.Equal(700, left.TotalBytes);
        Assert.Equal(2, left.RemovedSinceScan);
    }

    /// <summary>
    /// A second removal takes from what the first left. What the first took is not taken again, and
    /// the count says how many items went in both.
    /// </summary>
    [Fact]
    public void ASecondRemovalTakesFromWhatTheFirstLeft()
    {
        var tree = Media();
        var old = Find(tree, "old");

        var first = tree.Without([old]);
        var second = first.Without([Find(tree, "music"), old]);

        Assert.Equal(400, second.TotalBytes);
        Assert.Equal(400, second.SizeOf(Find(tree, "films")));
        Assert.Equal(2, second.RemovedSinceScan);
        Assert.Same(tree, second.Scanned);
        Assert.Equal(700, first.TotalBytes);
    }

    /// <summary>Nothing the tree still holds was picked, so nothing changes, and the tree is itself.</summary>
    [Fact]
    public void TakingOutWhatIsAlreadyGoneLeavesTheTreeAsItIs()
    {
        var tree = Media();
        var left = tree.Without([Find(tree, "old")]);

        Assert.Same(left, left.Without([Find(tree, "old0.mp4")]));
        Assert.Same(tree, tree.Without([]));
        Assert.Same(tree, tree.Scanned);
        Assert.Equal(0, tree.RemovedSinceScan);
    }

    /// <summary>
    /// The root stays, as the place the scan was of, so the page can stand on it and name it. Taken
    /// out, it holds nothing.
    /// </summary>
    [Fact]
    public void TheRootTakenOutStaysAndHoldsNothing()
    {
        var tree = Media();

        var left = tree.Without([tree.RootNode]);

        Assert.True(left.Holds(left.RootNode));
        Assert.Equal(tree.RootPath, left.PathOf(left.RootNode));
        Assert.Equal(0, left.TotalBytes);
        Assert.Empty(left.ChildrenOf(left.RootNode).ToArray());
        Assert.False(left.Holds(Find(tree, "films")));
        Assert.Equal(1, left.RemovedSinceScan);
    }

    /// <summary>A snapshot of a walk is in name order, and stays in it.</summary>
    [Fact]
    public void ATreeInNameOrderStaysInNameOrder()
    {
        var tree = Media(order: ExploreChildOrder.ByName);
        var films = Find(tree, "films");
        var music = Find(tree, "music");

        var left = tree.Without([Find(tree, "old"), Find(tree, "new")]);

        Assert.Equal(ExploreChildOrder.ByName, left.ChildOrder);
        Assert.Equal([films, music], left.ChildrenOf(left.RootNode).ToArray());
    }

    /// <summary>
    /// The map moves from the tree as it was to the tree left by finding each node again in it. What
    /// stayed is itself, and what went is nothing, though its number and its parent are still there.
    /// </summary>
    [Fact]
    public void WhatWentHasNoCounterpartInTheTreeLeft()
    {
        var tree = Media();
        var old = Find(tree, "old");
        var kept = Find(tree, "new0.mp4");

        var left = tree.Without([old]);
        var find = tree.CounterpartsIn(left);

        Assert.NotNull(find);
        Assert.Null(find(old));
        Assert.Null(find(Find(tree, "old0.mp4")));
        Assert.Equal(kept, find(kept));
        Assert.Equal(left.RootNode, find(tree.RootNode));

        // And back the other way, and into what is left after a second removal.
        Assert.Equal(kept, left.CounterpartsIn(tree)!(kept));
        Assert.Null(left.CounterpartsIn(left.Without([kept]))!(kept));
    }

    /// <summary>
    /// The root holds films (old 600, new 400) and music (300). Every file is twice as long as it is
    /// large on the disk, so a length that follows a size can be told from one that does not.
    /// </summary>
    private static ExploreTree Media(ExploreChildOrder order = ExploreChildOrder.BySize, string filmsName = "films")
    {
        var builder = new ExploreTreeBuilder(@"C:\Users\testuser\Videos");

        var films = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder(filmsName), Folder("music")]);
        var music = films + 1;

        var old = builder.AddChildren(films, [Folder("old"), Folder("new")]);

        builder.AddChildren(old, [File("old0.mp4", 300), File("old1.mp4", 300)]);
        builder.AddChildren(old + 1, [File("new0.mp4", 300), File("new1.mp4", 100)]);
        builder.AddChildren(music, [File("song.mp3", 300)]);

        return builder.Build(order);
    }

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, Size: 0);

    private static ExploreChild File(string name, long size) =>
        new(name, IsDirectory: false, IsLink: false, Size: size, Length: size * 2);

    private static int Find(ExploreTree tree, string name)
    {
        for (var node = 0; node < tree.NodeCount; node++)
        {
            if (tree.NameOf(node) == name)
            {
                return node;
            }
        }

        throw new InvalidOperationException($"No node is named {name}.");
    }
}
