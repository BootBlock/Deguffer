using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;

namespace Deguffer.Core.Tests;

/// <summary>
/// The kind of file every node holds the most bytes of, which a map coloured by type paints: a file's
/// own kind, and a folder's largest kind anywhere below it.
/// </summary>
public sealed class DominantTypesTests
{
    private const string Root = @"C:\Users\testuser\Data";

    /// <summary>
    /// A folder follows the kind with the most bytes at any depth below it, not the kind with the most
    /// files and not the kind of its own direct children. Games holds three files, and the disk image
    /// two levels down outweighs the other two together.
    /// </summary>
    [Fact]
    public void AFolderFollowsTheKindWithTheMostBytesAtAnyDepth()
    {
        var builder = new ExploreTreeBuilder(Root);
        var games = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Games")]);
        var old = builder.AddChildren(games, [Folder("Old"), File("a.zip", 300), File("b.zip", 300)]);
        builder.AddChildren(old, [File("game.iso", 1_000)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var types = DominantTypes.Measure(tree);

        Assert.Equal(FileCategory.DiskImages, types.Of(games));
        Assert.Equal(FileCategory.DiskImages, types.Of(old));
        Assert.Equal(FileCategory.DiskImages, types.Of(tree.RootNode));
        Assert.Equal(FileCategory.Archives, types.Of(TypeBreakdownTests.Child(tree, games, "a.zip")));
    }

    /// <summary>
    /// Two kinds with the same bytes are decided by the order <see cref="FileCategories.All"/> lists them,
    /// whichever the folder holds first, so the same folder is painted the same on every drawing.
    /// </summary>
    [Theory]
    [InlineData("a.zip", "b.mp3")]
    [InlineData("b.mp3", "a.zip")]
    public void ATieIsDecidedTheSameWayWhicheverComesFirst(string first, string second)
    {
        var tree = TypeBreakdownTests.Flat((first, 50), (second, 50));

        Assert.Equal(FileCategory.Audio, DominantTypes.Measure(tree).Of(tree.RootNode));
    }

    /// <summary>
    /// A folder holding no bytes in any file has no kind to take, and is painted as what no name
    /// explains. Without the rule the tie between ten zeros would paint it as video.
    /// </summary>
    [Fact]
    public void AFolderHoldingNoBytesIsOther()
    {
        var builder = new ExploreTreeBuilder(Root);
        var empty = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Empty"), Folder("Placeholders"), File("big.mp4", 10)]);
        builder.AddChildren(empty + 1, [File("empty.mp4", 0)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var types = DominantTypes.Measure(tree);

        Assert.Equal(FileCategory.Other, types.Of(TypeBreakdownTests.Child(tree, tree.RootNode, "Empty")));
        Assert.Equal(FileCategory.Other, types.Of(TypeBreakdownTests.Child(tree, tree.RootNode, "Placeholders")));
        Assert.Equal(FileCategory.Video, types.Of(tree.RootNode));
    }

    /// <summary>
    /// The kind a folder holding any bytes is painted is the first share of its breakdown, so the panel
    /// beside a map coloured by type never contradicts the colour of the folder it describes. The tree
    /// also holds a folder of nothing but an empty video, which is not drawn and is passed over.
    /// </summary>
    [Fact]
    public void EveryFolderIsPaintedTheFirstShareOfItsBreakdown()
    {
        var builder = new ExploreTreeBuilder(Root);
        var mixed = builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Mixed"), Folder("Placeholders"), File("a.pdf", 5)]);
        var inner = builder.AddChildren(mixed, [Folder("Inner"), File("b.mp3", 40), File("c.zip", 40), File("d.pdf", 39)]);
        builder.AddChildren(inner, [File("e.iso", 100), File("f.mkv", 99)]);
        builder.AddChildren(mixed + 1, [File("empty.mp4", 0)]);
        var tree = builder.Build(ExploreChildOrder.BySize);
        var types = DominantTypes.Measure(tree);
        var waiting = new Stack<int>([tree.RootNode]);
        var folders = 0;

        while (waiting.TryPop(out var node))
        {
            if (!tree.IsDirectory(node))
            {
                continue;
            }

            if (tree.SizeOf(node) > 0)
            {
                folders++;
                Assert.Equal(TypeBreakdown.Measure(tree, node).Shares[0].Category, types.Of(node));
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                waiting.Push(child);
            }
        }

        Assert.Equal(3, folders);
    }

    /// <summary>
    /// What the colour and the line under the map share: the measured kind where it describes this tree,
    /// a file's own kind otherwise, and nothing for a folder not measured, rather than a guess.
    /// </summary>
    [Fact]
    public void TheKindOfANodeIsTheMeasuredOneOnlyForItsOwnTree()
    {
        var tree = TypeBreakdownTests.Tree();
        var another = TypeBreakdownTests.Tree();
        var videos = TypeBreakdownTests.Child(tree, tree.RootNode, "Videos");
        var notes = TypeBreakdownTests.Child(tree, tree.RootNode, "notes.txt");

        Assert.Equal(FileCategory.Video, DominantTypes.KindOf(tree, videos, DominantTypes.Measure(tree)));

        foreach (var unmeasured in new[] { null, DominantTypes.Measure(another) })
        {
            Assert.Null(DominantTypes.KindOf(tree, videos, unmeasured));
            Assert.Equal(FileCategory.Documents, DominantTypes.KindOf(tree, notes, unmeasured));
        }
    }

    [Fact]
    public void ACancelledPassThrows()
    {
        var tree = TypeBreakdownTests.Flat([.. Enumerable.Range(0, 10_000).Select(at => ($"{at}.zip", (long)at))]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => DominantTypes.Measure(tree, cancelled.Token));
    }

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, 0);

    private static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, size);
}
