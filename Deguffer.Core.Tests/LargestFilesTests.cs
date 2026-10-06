using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Files;

namespace Deguffer.Core.Tests;

/// <summary>
/// The Files layout's list: the largest files at any depth under the node on screen, filtered by
/// name, type, size and age, and never a folder.
/// </summary>
public sealed class LargestFilesTests
{
    private const string Root = @"C:\Users\testuser\Data";

    /// <summary>On a minute, because the tree keeps its dates to the minute.</summary>
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The three largest files are three levels apart, and every folder above them is larger than
    /// any of them. A list that took sizes from the tree without asking what each node is would be
    /// headed by the folders.
    /// </summary>
    [Fact]
    public void TheLargestFilesAreFoundAtEveryDepthAndNoFolderIsListed()
    {
        var tree = Tree();

        var ranking = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now, limit: 3);

        Assert.Equal(["big.vhdx", "game.iso", "trip.mp4"], Names(tree, ranking));
        Assert.DoesNotContain(ranking.Files, tree.IsDirectory);
        Assert.Equal(7, ranking.Matched);
        Assert.False(ranking.IsComplete);
    }

    [Fact]
    public void EveryFileIsListedLargestFirstWhenThereAreFewerThanTheLimit()
    {
        var tree = Tree();

        var ranking = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now);

        Assert.Equal(
            ["big.vhdx", "game.iso", "trip.mp4", "clip.mkv", "setup.exe", "save.dat", "notes.txt"],
            Names(tree, ranking));
        Assert.True(ranking.IsComplete);
    }

    /// <summary>The list follows the folder the reader has opened, and nothing outside it is listed.</summary>
    [Fact]
    public void OnlyTheFilesBelowTheNodeOnScreenAreListed()
    {
        var tree = Tree();
        var videos = Child(tree, tree.RootNode, "Videos");

        var ranking = LargestFiles.Find(tree, videos, FileFilter.Everything, Now);

        Assert.Equal(["trip.mp4", "clip.mkv"], Names(tree, ranking));
    }

    /// <summary>
    /// A link holds nothing here, because its target keeps its own place in the tree. This one claims
    /// to be the largest thing on the disk, and listing it would count a file's bytes twice.
    /// </summary>
    [Fact]
    public void ALinkIsNeverListed()
    {
        var tree = Tree();

        var ranking = LargestFiles.Find(tree, tree.RootNode, new FileFilter(Name: "*.iso"), Now);

        Assert.Equal(["game.iso"], Names(tree, ranking));
    }

    [Theory]
    [InlineData("trip", new[] { "trip.mp4" })]
    [InlineData("TRIP", new[] { "trip.mp4" })]
    [InlineData("  trip ", new[] { "trip.mp4" })]
    [InlineData("*.ISO", new[] { "game.iso" })]
    [InlineData("s*.*", new[] { "setup.exe", "save.dat" })]
    [InlineData("?ame.iso", new[] { "game.iso" })]
    [InlineData("iso", new[] { "game.iso" })]
    [InlineData("*.iso*", new[] { "game.iso" })]
    [InlineData("nothing like it", new string[0])]
    public void TheNameIsTextItContainsOrAWildcardItMatches(string name, string[] expected)
    {
        var tree = Tree();

        Assert.Equal(expected, Names(tree, LargestFiles.Find(tree, tree.RootNode, new FileFilter(Name: name), Now)));
    }

    /// <summary>A wildcard matches the whole name, so text after the extension stops it matching.</summary>
    [Fact]
    public void AWildcardMatchesTheWholeName()
    {
        var tree = Tree();

        Assert.Empty(LargestFiles.Find(tree, tree.RootNode, new FileFilter(Name: "*.is"), Now).Files);
    }

    [Theory]
    [InlineData(FileCategory.Video, new[] { "trip.mp4", "clip.mkv" })]
    [InlineData(FileCategory.DiskImages, new[] { "game.iso" })]
    [InlineData(FileCategory.VirtualMachineDisks, new[] { "big.vhdx" })]
    [InlineData(FileCategory.Installers, new[] { "setup.exe" })]
    [InlineData(FileCategory.Other, new[] { "save.dat" })]
    [InlineData(FileCategory.Audio, new string[0])]
    public void TheTypeIsTheCategoryOfTheName(FileCategory category, string[] expected)
    {
        var tree = Tree();

        Assert.Equal(
            expected, Names(tree, LargestFiles.Find(tree, tree.RootNode, new FileFilter(Category: category), Now)));
    }

    /// <summary>The minimum is inclusive: a file of exactly that size is listed.</summary>
    [Fact]
    public void TheMinimumSizeListsAFileOfExactlyThatSize()
    {
        var tree = Tree();

        var ranking = LargestFiles.Find(tree, tree.RootNode, new FileFilter(MinimumBytes: 4_000), Now);

        Assert.Equal(["big.vhdx", "game.iso", "trip.mp4"], Names(tree, ranking));
    }

    /// <summary>
    /// The boundary of "not written for two years": the virtual disk was last written exactly two
    /// years ago and is listed, and the clip's twin a minute later is not. A file with no date is
    /// never listed under an age, because nothing shows it has gone untouched.
    /// </summary>
    [Fact]
    public void TheAgeListsAFileWrittenExactlyAtTheCutoffAndNotAMinuteAfter()
    {
        var tree = Tree();

        var ranking = LargestFiles.Find(tree, tree.RootNode, new FileFilter(UnwrittenFor: FileAge.TwoYears), Now);

        Assert.Equal(["big.vhdx", "game.iso"], Names(tree, ranking));
    }

    [Theory]
    [InlineData(FileAge.Any, new[] { "big.vhdx", "game.iso", "trip.mp4", "clip.mkv", "setup.exe", "save.dat", "notes.txt" })]
    [InlineData(FileAge.ThreeMonths, new[] { "big.vhdx", "game.iso", "clip.mkv", "setup.exe" })]
    [InlineData(FileAge.OneYear, new[] { "big.vhdx", "game.iso", "clip.mkv", "setup.exe" })]
    [InlineData(FileAge.TwoYears, new[] { "big.vhdx", "game.iso" })]
    public void EachAgeListsWhatWentThatLongWithoutAWrite(FileAge age, string[] expected)
    {
        var tree = Tree();

        Assert.Equal(
            expected, Names(tree, LargestFiles.Find(tree, tree.RootNode, new FileFilter(UnwrittenFor: age), Now)));
    }

    /// <summary>
    /// Every criterion at once. Each of the four turns away one file the other three let through:
    /// the name an image called something else, the type a text file named like an ISO, the size a
    /// small ISO, and the age a recent one.
    /// </summary>
    [Fact]
    public void TheCriteriaAreAppliedTogether()
    {
        var builder = new ExploreTreeBuilder(Root);

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                File("wanted.iso", 9_000, Now.AddYears(-3)),
                File("other.img", 9_500, Now.AddYears(-3)),
                File("wanted.iso.txt", 9_900, Now.AddYears(-3)),
                File("small.iso", 10, Now.AddYears(-3)),
                File("recent.iso", 8_000, Now.AddDays(-1)),
            ]);

        var tree = builder.Build(ExploreChildOrder.BySize);
        var filter = new FileFilter("*.iso*", FileCategory.DiskImages, MinimumBytes: 1_000, FileAge.OneYear);

        Assert.Equal(["wanted.iso"], Names(tree, LargestFiles.Find(tree, tree.RootNode, filter, Now)));
    }

    /// <summary>Two files of one size are listed in the same order every time the same tree is asked.</summary>
    [Fact]
    public void FilesOfOneSizeAreListedInNodeOrder()
    {
        var builder = new ExploreTreeBuilder(Root);
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode, [.. Enumerable.Range(0, 6).Select(i => File($"same{i}.bin", 100, Now))]);

        var tree = builder.Build(ExploreChildOrder.BySize);

        var ranking = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now, limit: 4);

        Assert.Equal([first, first + 1, first + 2, first + 3], ranking.Files);
    }

    /// <summary>
    /// The same question on the same tree is answered by the earlier answer. A filter typed one
    /// keystroke at a time asks the same thing again after every pause.
    /// </summary>
    [Fact]
    public void TheSameQuestionIsAnsweredByTheEarlierAnswer()
    {
        var tree = Tree();
        var filter = new FileFilter(Name: "i");

        var first = LargestFiles.Find(tree, tree.RootNode, filter, Now);

        Assert.Same(first, LargestFiles.Find(tree, tree.RootNode, filter with { }, Now, previous: first));
    }

    /// <summary>
    /// A narrower filter over an answer that listed everything it matched is applied to that answer,
    /// with no pass over the tree. The pass is what looks at the cancellation token, so a cancelled
    /// token reaches only a pass.
    /// </summary>
    [Fact]
    public void ANarrowerFilterOverACompleteAnswerMakesNoPass()
    {
        var tree = ManyFiles(10_000);
        var wide = LargestFiles.Find(tree, tree.RootNode, new FileFilter(Category: FileCategory.Archives), Now, limit: 20_000);
        var narrow = new FileFilter(Category: FileCategory.Archives, MinimumBytes: 9_000);

        var reused = LargestFiles.Find(tree, tree.RootNode, narrow, Now, limit: 20_000, previous: wide, ct: Cancelled());

        Assert.Equal(LargestFiles.Find(tree, tree.RootNode, narrow, Now, limit: 20_000).Files, reused.Files);
        Assert.Equal(500, reused.Matched);
        Assert.Throws<OperationCanceledException>(
            () => LargestFiles.Find(tree, tree.RootNode, narrow, Now, limit: 20_000, ct: Cancelled()));
    }

    /// <summary>
    /// A higher minimum over a cut answer is applied to it where the minimum drops one of its files.
    /// Everything the cut left out was no larger than the file dropped, so none of it can return.
    /// </summary>
    [Fact]
    public void AHigherMinimumThatDropsAListedFileReusesACutAnswer()
    {
        var tree = ManyFiles(10_000);
        var cut = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now, limit: 100);
        var raised = new FileFilter(MinimumBytes: 9_950);

        var reused = LargestFiles.Find(tree, tree.RootNode, raised, Now, limit: 100, previous: cut, ct: Cancelled());
        var fresh = LargestFiles.Find(tree, tree.RootNode, raised, Now, limit: 100);

        Assert.Equal(fresh.Files, reused.Files);
        Assert.Equal(fresh.Matched, reused.Matched);
        Assert.Equal(51, reused.Matched);
    }

    /// <summary>
    /// A higher minimum that every listed file still clears says nothing about the files the cut left
    /// out, which may clear it too, so the count has to come from a pass.
    /// </summary>
    [Fact]
    public void AHigherMinimumThatDropsNothingFromACutAnswerMakesAPass()
    {
        var tree = ManyFiles(10_000);
        var cut = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now, limit: 100);
        var raised = new FileFilter(MinimumBytes: 5_000);

        Assert.Throws<OperationCanceledException>(
            () => LargestFiles.Find(tree, tree.RootNode, raised, Now, limit: 100, previous: cut, ct: Cancelled()));

        var answered = LargestFiles.Find(tree, tree.RootNode, raised, Now, limit: 100, previous: cut);

        Assert.Equal(5_001, answered.Matched);
        Assert.Equal(cut.Files, answered.Files);
    }

    /// <summary>
    /// Any other narrowing of a cut answer makes a pass: the files a category drops from the top
    /// hundred are replaced by smaller files of that category the cut never kept.
    /// </summary>
    [Fact]
    public void ANarrowerTypeOverACutAnswerMakesAPass()
    {
        var tree = ManyFiles(10_000);
        var cut = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now, limit: 100);
        var archives = new FileFilter(Category: FileCategory.Archives);

        var answered = LargestFiles.Find(tree, tree.RootNode, archives, Now, limit: 100, previous: cut);

        Assert.Equal(LargestFiles.Find(tree, tree.RootNode, archives, Now, limit: 100).Files, answered.Files);
        Assert.Equal(100, answered.Files.Count);
        Assert.Equal(5_000, answered.Matched);
    }

    /// <summary>A wider filter can never be applied to a narrower answer, which lacks what it adds.</summary>
    [Fact]
    public void AWiderFilterMakesAPass()
    {
        var tree = ManyFiles(10_000);
        var narrow = LargestFiles.Find(tree, tree.RootNode, new FileFilter(MinimumBytes: 9_000), Now, limit: 20_000);

        Assert.Throws<OperationCanceledException>(
            () => LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now, limit: 20_000, previous: narrow, ct: Cancelled()));
    }

    /// <summary>An answer about another tree, or another folder of this one, is never reused.</summary>
    [Fact]
    public void AnAnswerAboutAnotherTreeOrFolderIsNotReused()
    {
        var tree = Tree();
        var other = Tree();
        var first = LargestFiles.Find(tree, tree.RootNode, FileFilter.Everything, Now);

        Assert.NotSame(first, LargestFiles.Find(other, other.RootNode, FileFilter.Everything, Now, previous: first));
        Assert.NotSame(
            first,
            LargestFiles.Find(tree, Child(tree, tree.RootNode, "Videos"), FileFilter.Everything, Now, previous: first));
    }

    /// <summary>
    /// A disk the size this is for:
    /// <code>
    /// Data
    ///   Games
    ///     Old
    ///       Installers
    ///         setup.exe   900, 400 days ago
    ///         game.iso  5,000, three years ago
    ///     save.dat         10, today
    ///   Videos
    ///     trip.mp4      4,000, a month ago
    ///     Clips
    ///       clip.mkv    3,000, a minute short of two years ago
    ///   link.iso       99,999, a link
    ///   big.vhdx        6,000, exactly two years ago
    ///   notes.txt           1, no date
    /// </code>
    /// </summary>
    private static ExploreTree Tree()
    {
        var builder = new ExploreTreeBuilder(Root);

        var top = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [
                Folder("Games"),
                Folder("Videos"),
                new ExploreChild("link.iso", IsDirectory: false, IsLink: true, 99_999),
                File("big.vhdx", 6_000, Now.AddYears(-2)),
                new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, 1),
            ]);

        var old = builder.AddChildren(top, [Folder("Old"), File("save.dat", 10, Now)]);
        var installers = builder.AddChildren(old, [Folder("Installers")]);
        builder.AddChildren(
            installers, [File("setup.exe", 900, Now.AddDays(-400)), File("game.iso", 5_000, Now.AddYears(-3))]);

        var videos = builder.AddChildren(top + 1, [File("trip.mp4", 4_000, Now.AddMonths(-1)), Folder("Clips")]);
        builder.AddChildren(videos + 1, [File("clip.mkv", 3_000, Now.AddYears(-2).AddMinutes(1))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    /// <summary>
    /// Enough files that a pass looks at the cancellation token: half archives and half documents,
    /// sized 1 to <paramref name="count"/> bytes, the archives on the odd sizes.
    /// </summary>
    private static ExploreTree ManyFiles(int count)
    {
        var builder = new ExploreTreeBuilder(Root);

        builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. Enumerable.Range(1, count).Select(size => File(size % 2 == 1 ? $"{size}.zip" : $"{size}.pdf", size, Now))]);

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    private static ExploreChild Folder(string name) => new(name, IsDirectory: true, IsLink: false, 0);

    private static ExploreChild File(string name, long size, DateTime written) =>
        new(name, IsDirectory: false, IsLink: false, size, ExploreTimestamp.FromUtc(written), ExploreTimestamp.FromUtc(written));

    private static int Child(ExploreTree tree, int parent, string name)
    {
        foreach (var child in tree.ChildrenOf(parent))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child named {name}.");
    }

    private static string[] Names(ExploreTree tree, FileRanking ranking) => [.. ranking.Files.Select(tree.NameOf)];
}
