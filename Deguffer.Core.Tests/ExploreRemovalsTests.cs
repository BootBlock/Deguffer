using Deguffer.Core.Execution;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Acting;

namespace Deguffer.Core.Tests;

/// <summary>
/// What is left of the tree on screen once a removal from it has finished: the tree without what the
/// removal's report says went, and nothing else.
/// </summary>
public sealed class ExploreRemovalsTests
{
    private const string Root = @"C:\Users\testuser\Downloads";

    /// <summary>
    /// A removal takes everything inside what was picked, so nothing under the folder is held, and the
    /// folder above it no longer counts it. The sibling and the root are still there (§5.6).
    /// </summary>
    [Fact]
    public void WhatWentLeavesTheTreeWithEverythingInsideIt()
    {
        var (tree, folder, file, sibling) = Tree();

        var left = ExploreRemovals.Without(tree, [folder], Removed(tree, folder));

        Assert.False(left.Holds(folder));
        Assert.False(left.Holds(file));
        Assert.True(left.Holds(sibling));
        Assert.True(left.Holds(left.RootNode));
        Assert.Equal(5, left.TotalBytes);
        Assert.Equal(1, left.RemovedSinceScan);
    }

    /// <summary>
    /// Only what the report says went. A picked item the policy refused is still on the disk, and
    /// leaving it out would be the page claiming a removal that did not happen.
    /// </summary>
    [Fact]
    public void APickedItemTheReportRefusedIsStillThere()
    {
        var (tree, folder, _, sibling) = Tree();

        var report = new ExploreRemovalReport(
            ExploreRemovalMode.RecycleBin,
            [
                new ExploreItemOutcome(tree.PathOf(folder), Removed: true, Bytes: 10, "Removed."),
                new ExploreItemOutcome(tree.PathOf(sibling), Removed: false, Bytes: 0, "Refused."),
            ],
            new VerificationResult());

        var left = ExploreRemovals.Without(tree, [folder, sibling], report);

        Assert.False(left.Holds(folder));
        Assert.True(left.Holds(sibling));
        Assert.Equal(5, left.SizeOf(sibling));
        Assert.Equal(1, left.RemovedSinceScan);
    }

    /// <summary>Nothing went, so the tree on screen is what is left, and the page has nothing to redraw.</summary>
    [Fact]
    public void ARemovalThatTookNothingLeavesTheTreeItself()
    {
        var (tree, folder, _, _) = Tree();

        var report = new ExploreRemovalReport(
            ExploreRemovalMode.RecycleBin,
            [new ExploreItemOutcome(tree.PathOf(folder), Removed: false, Bytes: 0, "Refused.")],
            new VerificationResult());

        Assert.Same(tree, ExploreRemovals.Without(tree, [folder], report));
    }

    /// <summary>NTFS does not tell two spellings of one path apart, and neither does the match.</summary>
    [Fact]
    public void TheReportIsMatchedWithoutRegardToCase()
    {
        var (tree, folder, _, _) = Tree();

        var report = new ExploreRemovalReport(
            ExploreRemovalMode.RecycleBin,
            [new ExploreItemOutcome(tree.PathOf(folder).ToUpperInvariant(), Removed: true, Bytes: 10, "Removed.")],
            new VerificationResult());

        Assert.False(ExploreRemovals.Without(tree, [folder], report).Holds(folder));
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
