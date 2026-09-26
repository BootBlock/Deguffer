using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// What the Explore page remembers of a removal: which nodes of the tree on screen have gone, so
/// nothing may offer them again, and how many, so the stale note can say the picture is now larger
/// than the disk.
/// </summary>
public sealed class ExploreRemovalsTests
{
    private const string Root = @"C:\Users\testuser\Downloads";

    /// <summary>
    /// A removal takes everything inside what was picked. What is recorded is the folder, and every
    /// file under it has to read as gone too, or the map would still offer each one.
    /// </summary>
    [Fact]
    public void EverythingInsideARemovedFolderHasGoneWithIt()
    {
        var (tree, folder, file, sibling) = Tree();
        var removals = new ExploreRemovals();

        removals.Record(tree, [folder], Removed(tree, folder));

        Assert.True(removals.WasRemoved(tree, folder));
        Assert.True(removals.WasRemoved(tree, file));
        Assert.False(removals.WasRemoved(tree, sibling));
        Assert.False(removals.WasRemoved(tree, tree.RootNode));
        Assert.Equal(1, removals.CountIn(tree));
    }

    /// <summary>
    /// A node index means nothing outside the tree it came from. Asked against a rescan, the old
    /// tree's indices hid a folder genuinely on the disk and claimed removals from a scan that had
    /// only just started.
    /// </summary>
    [Fact]
    public void ARemovalCountsOnlyInTheTreeItWasRecordedAgainst()
    {
        var (tree, folder, _, _) = Tree();
        var (rescan, _, _, _) = Tree();
        var removals = new ExploreRemovals();

        removals.Record(tree, [folder], Removed(tree, folder));

        Assert.False(removals.WasRemoved(rescan, folder));
        Assert.Equal(0, removals.CountIn(rescan));
        Assert.Equal(0, removals.CountIn(null));
    }

    /// <summary>A removal recorded against a new tree forgets what was recorded against the old one.</summary>
    [Fact]
    public void RecordingAgainstANewTreeForgetsTheOld()
    {
        var (tree, folder, _, _) = Tree();
        var (rescan, _, _, sibling) = Tree();
        var removals = new ExploreRemovals();

        removals.Record(tree, [folder], Removed(tree, folder));
        removals.Record(rescan, [sibling], Removed(rescan, sibling));

        Assert.False(removals.WasRemoved(rescan, folder));
        Assert.Equal(1, removals.CountIn(rescan));
        Assert.Equal(0, removals.CountIn(tree));
    }

    /// <summary>
    /// Only what the report says went. A picked item the policy refused is still on the disk, and
    /// hiding it would be the page claiming a removal that did not happen.
    /// </summary>
    [Fact]
    public void APickedItemTheReportRefusedIsStillThere()
    {
        var (tree, folder, _, sibling) = Tree();
        var removals = new ExploreRemovals();

        var report = new ExploreRemovalReport(
            ExploreRemovalMode.RecycleBin,
            [
                new ExploreItemOutcome(tree.PathOf(folder), Removed: true, Bytes: 10, "Removed."),
                new ExploreItemOutcome(tree.PathOf(sibling), Removed: false, Bytes: 0, "Refused."),
            ],
            new VerificationResult());

        removals.Record(tree, [folder, sibling], report);

        Assert.True(removals.WasRemoved(tree, folder));
        Assert.False(removals.WasRemoved(tree, sibling));
    }

    /// <summary>NTFS does not tell two spellings of one path apart, and neither does the match.</summary>
    [Fact]
    public void TheReportIsMatchedWithoutRegardToCase()
    {
        var (tree, folder, _, _) = Tree();
        var removals = new ExploreRemovals();

        var report = new ExploreRemovalReport(
            ExploreRemovalMode.RecycleBin,
            [new ExploreItemOutcome(tree.PathOf(folder).ToUpperInvariant(), Removed: true, Bytes: 10, "Removed.")],
            new VerificationResult());

        removals.Record(tree, [folder], report);

        Assert.True(removals.WasRemoved(tree, folder));
    }

    private static ExploreRemovalReport Removed(ExploreTree tree, int node) =>
        new(
            ExploreRemovalMode.RecycleBin,
            [new ExploreItemOutcome(tree.PathOf(node), Removed: true, Bytes: tree.SizeOf(node), "Removed.")],
            new VerificationResult());

    private static (ExploreTree Tree, int Folder, int File, int Sibling) Tree()
    {
        var builder = new ExploreTreeBuilder(Root);

        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("installers", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, Size: 5),
        ]);

        var file = builder.AddChildren(folder, [new ExploreChild("setup.exe", IsDirectory: false, IsLink: false, Size: 10)]);

        return (builder.Build(ExploreChildOrder.BySize), folder, file, folder + 1);
    }
}
