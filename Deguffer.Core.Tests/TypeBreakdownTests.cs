using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;

namespace Deguffer.Core.Tests;

/// <summary>
/// The breakdown of the folder on screen by kind of file: every byte in one share, the shares
/// largest first, and the largest extensions inside each.
/// </summary>
public sealed class TypeBreakdownTests
{
    private const string Root = @"C:\Users\testuser\Data";

    /// <summary>
    /// Every byte the root holds is in one share and no byte is in two, so the shares add up to the
    /// root's own total and the files to every file below it, at every depth. The link is counted in the
    /// share its name says, because the tree counts it in its folder's total.
    /// </summary>
    [Fact]
    public void TheSharesAddUpToTheRootAndEachFileIsInOneShare()
    {
        var tree = Tree();

        var breakdown = TypeBreakdown.Measure(tree, tree.RootNode);

        Assert.Equal(tree.SizeOf(tree.RootNode), breakdown.Shares.Sum(share => share.Bytes));
        Assert.Equal(Files(tree, tree.RootNode), breakdown.Shares.Sum(share => share.Files));
        Assert.Equal(breakdown.Shares.Count, breakdown.Shares.Select(share => share.Category).Distinct().Count());
    }

    /// <summary>The shares, largest first, each with what it holds.</summary>
    [Fact]
    public void EachKindIsMeasuredAndTheLargestComesFirst()
    {
        var tree = Tree();

        var breakdown = TypeBreakdown.Measure(tree, tree.RootNode);

        Assert.Equal(
            [
                (FileCategory.Video, 7_000L, 2),
                (FileCategory.VirtualMachineDisks, 6_000L, 1),
                (FileCategory.DiskImages, 5_500L, 2),
                (FileCategory.Installers, 900L, 1),
                (FileCategory.Other, 10L, 1),
                (FileCategory.Documents, 1L, 1),
            ],
            breakdown.Shares.Select(share => (share.Category, share.Bytes, share.Files)));
    }

    /// <summary>The breakdown follows the folder the reader has opened, and counts nothing outside it.</summary>
    [Fact]
    public void OnlyWhatIsBelowTheFolderIsCounted()
    {
        var tree = Tree();
        var videos = Child(tree, tree.RootNode, "Videos");

        var breakdown = TypeBreakdown.Measure(tree, videos);

        var share = Assert.Single(breakdown.Shares);
        Assert.Equal((FileCategory.Video, 7_000L, 2), (share.Category, share.Bytes, share.Files));
        Assert.Equal(tree.SizeOf(videos), share.Bytes);
    }

    /// <summary>
    /// Two kinds the same size are ordered as <see cref="FileCategories.All"/> lists them, whichever the
    /// tree holds first, so the list never reorders between two scans of the same folder.
    /// </summary>
    [Fact]
    public void ATieIsOrderedTheSameWayWhicheverComesFirst()
    {
        var archivesFirst = TypeBreakdown.Measure(Flat(("a.zip", 50), ("b.mp3", 50)), ExploreTreeBuilder.RootNode);
        var audioFirst = TypeBreakdown.Measure(Flat(("b.mp3", 50), ("a.zip", 50)), ExploreTreeBuilder.RootNode);

        Assert.Equal([FileCategory.Audio, FileCategory.Archives], archivesFirst.Shares.Select(share => share.Category));
        Assert.Equal([FileCategory.Audio, FileCategory.Archives], audioFirst.Shares.Select(share => share.Category));
    }

    /// <summary>
    /// An extension in two cases is one extension, named in lower case, and a name with none is listed
    /// as such. The extensions are largest first, and only the largest few are named.
    /// </summary>
    [Fact]
    public void ExtensionsAreMergedAcrossCaseAndOnlyTheLargestAreNamed()
    {
        var tree = Flat(
            ("a.MP4", 30), ("b.mp4", 20), ("c.mkv", 40), ("d.avi", 4), ("e.mov", 3), ("f.wmv", 2), ("g.webm", 1),
            ("README", 7), ("data.xyz", 9));

        var breakdown = TypeBreakdown.Measure(tree, ExploreTreeBuilder.RootNode);

        var video = breakdown.Shares.Single(share => share.Category == FileCategory.Video);
        Assert.Equal(
            [(".mp4", 50L, 2), (".mkv", 40L, 1), (".avi", 4L, 1), (".mov", 3L, 1), (".wmv", 2L, 1)],
            video.Extensions.Select(extension => (extension.Extension, extension.Bytes, extension.Files)));
        Assert.Equal(100, video.Bytes);
        Assert.Equal(7, video.Files);

        var other = breakdown.Shares.Single(share => share.Category == FileCategory.Other);
        Assert.Equal(
            [(".xyz", 9L, 1), (string.Empty, 7L, 1)],
            other.Extensions.Select(extension => (extension.Extension, extension.Bytes, extension.Files)));
    }

    /// <summary>
    /// A folder's own bytes, which the file table can record for a folder, are in the share for what no
    /// name explains, so the shares still add up to the folder's total.
    /// </summary>
    [Fact]
    public void AFoldersOwnBytesAreOther()
    {
        var builder = new ExploreTreeBuilder(Root);
        var folder = builder.AddChildren(
            ExploreTreeBuilder.RootNode, [new ExploreChild("Index", IsDirectory: true, IsLink: false, 64), File("a.pdf", 100)]);
        builder.AddChildren(folder, [File("b.pdf", 10)]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        var breakdown = TypeBreakdown.Measure(tree, tree.RootNode);

        Assert.Equal(174, tree.SizeOf(tree.RootNode));
        Assert.Equal(
            [(FileCategory.Documents, 110L, 2), (FileCategory.Other, 64L, 0)],
            breakdown.Shares.Select(share => (share.Category, share.Bytes, share.Files)));
    }

    [Fact]
    public void AFolderWithNothingInItHasNoShares()
    {
        var builder = new ExploreTreeBuilder(Root);
        builder.AddChildren(ExploreTreeBuilder.RootNode, [Folder("Empty")]);
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Empty(TypeBreakdown.Measure(tree, tree.RootNode).Shares);
    }

    [Fact]
    public void ACancelledPassThrows()
    {
        var tree = Flat([.. Enumerable.Range(0, 10_000).Select(at => ($"{at}.zip", (long)at))]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => TypeBreakdown.Measure(tree, tree.RootNode, cancelled.Token));
    }

    /// <summary>
    /// Three levels deep, with a link claiming to be large. The installer is an executable named as one,
    /// and the save file has no kind its name says.
    /// </summary>
    internal static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                Folder("Games"),
                Folder("Videos"),
                new ExploreChild("link.iso", IsDirectory: false, IsLink: true, 500),
                File("big.vhdx", 6_000),
                File("notes.txt", 1),
            ]);

        var old = builder.AddChildren(top, [Folder("Old"), File("save.dat", 10)]);
        var installers = builder.AddChildren(old, [Folder("Installers")]);
        builder.AddChildren(installers, [File("setup.exe", 900), File("game.iso", 5_000)]);

        var videos = builder.AddChildren(top + 1, [File("trip.mp4", 4_000), Folder("Clips")]);
        builder.AddChildren(videos + 1, [File("clip.MKV", 3_000)]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    internal static ExploreTree Flat(params (string Name, long Size)[] files)
    {
        var builder = new ExploreTreeBuilder(Root);
        builder.AddChildren(ExploreTreeBuilder.RootNode, [.. files.Select(file => File(file.Name, file.Size))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    internal static int Child(ExploreTree tree, int parent, string name)
    {
        foreach (var child in tree.ChildrenOf(parent))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"{name} is not a child of {tree.NameOf(parent)}.");
    }

    /// <summary>Every file at any depth below <paramref name="root"/>, counted by walking the tree rather than by the code under test.</summary>
    private static int Files(ExploreTree tree, int root)
    {
        var count = 0;
        var waiting = new Stack<int>([root]);

        while (waiting.TryPop(out var node))
        {
            if (!tree.IsDirectory(node))
            {
                count++;
            }

            foreach (var child in tree.ChildrenOf(node))
            {
                waiting.Push(child);
            }
        }

        return count;
    }

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, 0);

    private static ExploreChild File(string name, long size) => new(name, IsDirectory: false, IsLink: false, size);
}
