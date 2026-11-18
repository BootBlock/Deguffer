using Deguffer.Core.Configuration;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.History;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Exploring.Rendering;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Tests;

/// <summary>
/// The last kept scan of a drive drawn while a new scan of it runs (#491): the tree a summary draws
/// as, when the page shows it, what it says over it, and that nothing on it answers the pointer.
/// </summary>
public class KeptScanTreeTests
{
    private const string Volume = @"\\?\Volume{0b6f5c1e-3a7d-4c2b-9e10-6d2f1a4b8c33}\";

    private const long Megabyte = 1024L * 1024;

    private static readonly DateTime Then = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly VolumeSpace Space = new(1000 * Megabyte, 400 * Megabyte);

    /// <summary>
    /// Each recorded folder is a node at its place in the summary, under its parent, largest first,
    /// and what a recorded folder held beyond its recorded folders is one block under it that is not
    /// a container and says what it stands for.
    /// </summary>
    [Fact]
    public void TheRecordedFoldersAreDrawnLargestFirstWithABlockForWhatEachDidNotRecord()
    {
        var drive = Tree(("Small", 20 * Megabyte), ("Logs", 250 * Megabyte));
        var kept = Kept(drive);
        var logs = Node(kept, "Logs");
        var small = Node(kept, "Small");

        Assert.Equal(0, kept.RootNode);
        Assert.Equal(ExploreChildOrder.BySize, kept.ChildOrder);
        Assert.Equal(270 * Megabyte, kept.SizeOf(kept.RootNode));
        Assert.Equal([logs, small], kept.ChildrenOf(kept.RootNode).ToArray());
        Assert.Equal(kept.RootNode, kept.ParentOf(logs));
        Assert.True(kept.IsContainer(logs));

        // Every folder was recorded, so what Logs held beyond them is its files, and nothing else.
        var block = Assert.Single(kept.ChildrenOf(logs).ToArray());
        Assert.Equal(250 * Megabyte, kept.SizeOf(block));
        Assert.Equal(logs, kept.ParentOf(block));
        Assert.False(kept.IsContainer(block));
        Assert.Null(kept.NameOf(block));
        Assert.Equal("Files, not recorded  250 MB", kept.Caption(block).Text(MapLettering.Standard, 1, MapDetail.Default));
        Assert.Equal("Logs  250 MB", kept.Caption(logs).Text(MapLettering.Standard, 1, MapDetail.Default));
        Assert.True(block >= kept.Summary.Folders.Count, "A block is numbered after every recorded folder.");
    }

    /// <summary>
    /// A folder too small for the summary to record is inside its parent's block, which says the most
    /// any such folder held, and the parent's children still add up to the parent.
    /// </summary>
    [Fact]
    public void AFolderTooSmallToRecordIsInItsParentsBlock()
    {
        var drive = Tree(("Small", 20 * Megabyte), ("Logs", 250 * Megabyte));
        var kept = KeptScanTree.Of(ScanSummaries.Take(ExploreScan.Fast(drive), Volume, Space, Then, limit: 2), @"C:\")!;

        Assert.Null(Find(kept, "Small"));

        var children = kept.ChildrenOf(kept.RootNode).ToArray();
        Assert.Equal(2, children.Length);
        Assert.Equal(Node(kept, "Logs"), children[0]);
        Assert.Equal(20 * Megabyte, kept.SizeOf(children[1]));
        Assert.Equal(
            "Files and folders under 20.0 MB, not recorded  20.0 MB",
            kept.Caption(children[1]).Text(MapLettering.Standard, 1, MapDetail.Default));
        Assert.Equal(kept.SizeOf(kept.RootNode), children.Sum(kept.SizeOf));
    }

    /// <summary>Siblings of one size are in name order, whatever order the summary listed them in.</summary>
    [Fact]
    public void SiblingsOfOneSizeAreInNameOrder()
    {
        var kept = Kept(Tree(("beta", 30 * Megabyte), ("Alpha", 30 * Megabyte), ("gamma", 30 * Megabyte)));

        Assert.Equal(
            ["Alpha", "beta", "gamma"],
            kept.ChildrenOf(kept.RootNode).ToArray().Select(node => kept.NameOf(node)));
    }

    /// <summary>
    /// The root is named where the volume is mounted now, which can be another letter than the one it
    /// was scanned under, and the volume beside it is as the kept scan found it.
    /// </summary>
    [Fact]
    public void TheRootIsWhereTheVolumeIsNowAndTheVolumeIsAsItWas()
    {
        var kept = KeptScanTree.Of(ScanSummaries.Take(ExploreScan.Fast(Tree(("Logs", 10 * Megabyte))), Volume, Space, Then), @"E:\")!;

        Assert.Equal(@"E:\", kept.RootPath);
        Assert.Equal(@"E:\", kept.NameOf(kept.RootNode));
        Assert.Equal(new VolumeSpace(1000 * Megabyte, 400 * Megabyte), kept.Volume);
        Assert.Equal(Then, kept.Summary.TakenUtc);
    }

    /// <summary>A summary that recorded no folder, not even its root, has nothing to draw.</summary>
    [Fact]
    public void ASummaryWithNoFolderDrawsNothing()
    {
        var empty = new ScanSummary(Volume, @"C:\", Then, ScanStrategy.MasterFileTable, false, 1000, 400, 0, []);

        Assert.Null(KeptScanTree.Of(empty, @"C:\"));
    }

    /// <summary>
    /// A recorded folder is found in a new scan of the drive by name, folder under folder, without
    /// regard to case, and the new scan's folders are found in it the same way back, so the map can
    /// carry the shapes from the one to the other. A block, and a folder gone since, have none.
    /// </summary>
    [Fact]
    public void ARecordedFolderIsFoundInANewScanByNameAndBack()
    {
        var kept = Kept(Tree(("Logs", 250 * Megabyte), ("Old", 30 * Megabyte)));
        var now = Tree(("logs", 260 * Megabyte), ("New", 5 * Megabyte));
        var onward = kept.CounterpartsIn(now)!;
        var back = now.CounterpartsIn(kept)!;
        var logs = Node(kept, "Logs");
        var nowLogs = Child(now, "logs");

        Assert.Equal(now.RootNode, onward(kept.RootNode));
        Assert.Equal(nowLogs, onward(logs));
        Assert.Null(onward(Node(kept, "Old")));
        Assert.Null(onward(kept.ChildrenOf(logs)[0]));

        Assert.Equal(kept.RootNode, back(now.RootNode));
        Assert.Equal(logs, back(nowLogs));
        Assert.Null(back(Child(now, "New")));
        Assert.Null(back(now.ChildrenOf(nowLogs)[0]));
    }

    /// <summary>
    /// Of two recorded folders whose names differ only in case, which a directory with case sensitivity
    /// turned on can hold, each is found by its own spelling.
    /// </summary>
    [Fact]
    public void TwoFoldersDifferingOnlyInCaseAreEachFoundByTheirOwnSpelling()
    {
        var drive = Tree(("Cache", 40 * Megabyte), ("cache", 30 * Megabyte));
        var kept = Kept(drive);
        var back = drive.CounterpartsIn(kept)!;

        Assert.Equal(Node(kept, "Cache"), back(Child(drive, "Cache")));
        Assert.Equal(Node(kept, "cache"), back(Child(drive, "cache")));
        Assert.NotEqual(back(Child(drive, "Cache")), back(Child(drive, "cache")));
    }

    /// <summary>A scan rooted anywhere other than where the volume is mounted now is of something else.</summary>
    [Fact]
    public void AScanRootedElsewhereHasNoCounterparts()
    {
        var kept = Kept(Tree(("Logs", 10 * Megabyte)));
        var elsewhere = Tree(@"D:\", ("Logs", 10 * Megabyte));

        Assert.Null(kept.CounterpartsIn(elsewhere));
        Assert.Null(elsewhere.CounterpartsIn(kept));
    }

    /// <summary>
    /// §7.1: nothing drawn of a kept scan answers the pointer, so nothing on it is picked, opened or
    /// removed, and it is never read in the tree the page holds.
    /// </summary>
    [Fact]
    public void NothingDrawnOfAKeptScanAnswersThePointer()
    {
        var now = Tree(("Logs", 10 * Megabyte));
        var kept = Kept(now);

        Assert.False(MapReading.Answers(kept));
        Assert.Null(MapReading.Of(kept, now));
        Assert.Null(MapReading.Of(now, kept));
        Assert.True(MapReading.Answers(now));
    }

    /// <summary>A kept scan recorded no dates, growth or kinds of file, so only a colouring every tree has applies to it.</summary>
    [Theory]
    [InlineData(ExploreColouring.Branch)]
    [InlineData(ExploreColouring.Age)]
    [InlineData(ExploreColouring.Growth)]
    [InlineData(ExploreColouring.Type)]
    public void AKeptScanIsColouredByBranchUnlessByDepth(ExploreColouring colouring)
    {
        Assert.Same(ShapeColours.ByBranch(ExploreScheme.Vivid), ShapeColours.ForKept(colouring, ExploreScheme.Vivid));
        Assert.Same(ShapeColours.ByDepth, ShapeColours.ForKept(ExploreColouring.Depth, ExploreScheme.Vivid));
    }

    /// <summary>The line over the map says when the kept scan was taken, as an age column words it, and that the drive is being scanned again.</summary>
    [Theory]
    [InlineData(0, "Last scan, today. Scanning again.")]
    [InlineData(1, "Last scan, yesterday. Scanning again.")]
    [InlineData(3, "Last scan, 3 days ago. Scanning again.")]
    [InlineData(70, "Last scan, 2 months ago. Scanning again.")]
    public void TheMarkSaysWhenTheKeptScanWasTaken(int daysAgo, string mark)
    {
        var summary = ScanSummaries.Take(ExploreScan.Fast(Tree(("Logs", 10 * Megabyte))), Volume, Space, Then);

        Assert.Equal(mark, KeptScanText.Mark(summary, Then.AddDays(daysAgo).AddHours(2)));
    }

    /// <summary>The maps draw a kept scan, and the two lists do not.</summary>
    [Theory]
    [InlineData(ExploreView.Treemap, true)]
    [InlineData(ExploreView.Icicle, true)]
    [InlineData(ExploreView.Sunburst, true)]
    [InlineData(ExploreView.Tree, true)]
    [InlineData(ExploreView.List, false)]
    [InlineData(ExploreView.Files, false)]
    public void OnlyTheViewsThatDrawAPictureDrawOne(ExploreView view, bool draws)
    {
        var kept = Kept(Tree(("Logs", 10 * Megabyte)));
        var display = new ExploreDisplay(null, view, ExploreColouring.Branch, MapLook.Default, InContrast: false, Kept: kept);

        Assert.Equal(draws, display.DrawsPicture);
        Assert.Equal(draws, display.ShowsKept);
        Assert.Equal(!draws, display.ShowsEmptyState);
    }

    /// <summary>The empty state shows only with nothing scanned and no kept scan on the map.</summary>
    [Fact]
    public void TheEmptyStateGivesWayToATreeOrAKeptScan()
    {
        var drive = Tree(("Logs", 10 * Megabyte));

        Assert.True(new ExploreDisplay(null, ExploreView.Treemap, ExploreColouring.Branch, MapLook.Default, false).ShowsEmptyState);
        Assert.False(new ExploreDisplay(drive, ExploreView.Treemap, ExploreColouring.Branch, MapLook.Default, false).ShowsEmptyState);
        Assert.False(new ExploreDisplay(null, ExploreView.Treemap, ExploreColouring.Branch, MapLook.Default, false).ShowsKept);
        Assert.False(new ExploreDisplay(null, ExploreView.Treemap, ExploreColouring.Branch, MapLook.Default, false, Kept: Kept(drive)).ShowsEmptyState);
    }

    /// <summary>Only folders are recorded, so a file or a link with a recorded folder's name is no counterpart of it, either way.</summary>
    [Fact]
    public void AFileWithARecordedFoldersNameIsNotThatFolder()
    {
        var kept = Kept(Tree(("Logs", 10 * Megabyte)));
        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [new ExploreChild("Logs", IsDirectory: false, IsLink: false, Size: 5 * Megabyte)]);
        var now = builder.Build(ExploreChildOrder.BySize);

        Assert.Null(kept.CounterpartsIn(now)!(Node(kept, "Logs")));
        Assert.Null(now.CounterpartsIn(kept)!(Child(now, "Logs")));
    }

    /// <summary>
    /// A scan that starts looks for the kept scan, shows what is offered for it, and drops it once it
    /// has a tree of its own. An offer after that, for another scan, or after an answer of nothing, is
    /// not shown.
    /// </summary>
    [Fact]
    public void AKeptScanIsShownFromItsOfferUntilTheScanHasATree()
    {
        var kept = Kept(Tree(("Logs", 10 * Megabyte)));
        var looking = KeptScanShown.Begin(3, @"C:\", onScreen: null, VolumeSpace.None);

        Assert.True(looking.Looking);
        Assert.False(looking.Shows);
        Assert.False(looking.Offered(2, kept).Shows);

        var shown = looking.Offered(3, kept);
        Assert.True(shown.Shows);
        Assert.Same(kept, shown.Tree);
        Assert.False(shown.Looking);

        var replaced = shown.Replaced();
        Assert.False(replaced.Shows);
        Assert.False(replaced.Offered(3, kept).Shows);
        Assert.False(looking.Replaced().Offered(3, kept).Shows);

        // A scan of a folder, or of a volume with nothing kept, is answered with nothing.
        var nothing = looking.Offered(3, null);
        Assert.False(nothing.Shows);
        Assert.False(nothing.Offered(3, kept).Shows);
    }

    /// <summary>
    /// A finished scan of the whole volume already on screen is newer than any kept scan, so nothing is
    /// looked for. A snapshot, a scan of a folder there, or a scan of another drive is not.
    /// </summary>
    [Fact]
    public void AFinishedScanOfTheWholeVolumeOnScreenIsNotReplaced()
    {
        var kept = Kept(Tree(("Logs", 10 * Megabyte)));
        var whole = Tree(("Logs", 10 * Megabyte));

        Assert.False(KeptScanShown.Begin(1, @"C:\", whole, Space).Looking);
        Assert.False(KeptScanShown.Begin(1, @"c:\", whole, Space).Offered(1, kept).Shows);

        var builder = new ExploreTreeBuilder(@"C:\");
        builder.AddChildren(ExploreTreeBuilder.RootNode, [new ExploreChild("Logs", IsDirectory: true, IsLink: false, Size: 0)]);

        Assert.True(KeptScanShown.Begin(1, @"C:\", builder.Snapshot(ExploreChildOrder.ByName), Space).Looking);
        Assert.True(KeptScanShown.Begin(1, @"C:\", whole, VolumeSpace.None).Looking);
        Assert.True(KeptScanShown.Begin(1, @"D:\", whole, Space).Looking);
    }

    private static KeptScanTree Kept(ExploreTree drive) =>
        KeptScanTree.Of(ScanSummaries.Take(ExploreScan.Fast(drive), Volume, Space, Then), drive.RootPath)!;

    private static int Node(KeptScanTree kept, string name) =>
        Find(kept, name) ?? throw new InvalidOperationException($"No recorded folder named {name}.");

    private static int? Find(KeptScanTree kept, string name)
    {
        for (var node = 0; node < kept.NodeCount; node++)
        {
            if (kept.NameOf(node) == name)
            {
                return node;
            }
        }

        return null;
    }

    private static ExploreTree Tree(params (string Name, long Bytes)[] folders) => Tree(@"C:\", folders);

    /// <summary>A drive of folders, each holding one file of its size.</summary>
    private static ExploreTree Tree(string root, params (string Name, long Bytes)[] folders)
    {
        var builder = new ExploreTreeBuilder(root);
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode,
            [.. folders.Select(folder => new ExploreChild(folder.Name, IsDirectory: true, IsLink: false, Size: 0))]);

        for (var i = 0; i < folders.Length; i++)
        {
            builder.AddChildren(first + i, [new ExploreChild("data.bin", IsDirectory: false, IsLink: false, Size: folders[i].Bytes)]);
        }

        return builder.Build(ExploreChildOrder.BySize);
    }

    private static int Child(ExploreTree tree, string name)
    {
        foreach (var child in tree.ChildrenOf(tree.RootNode))
        {
            if (tree.NameOf(child) == name)
            {
                return child;
            }
        }

        throw new InvalidOperationException($"No child named {name}.");
    }
}
