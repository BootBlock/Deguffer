using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// What of the Explore page survives a redraw. Keeping the selection across a step would act on
/// something picked in another folder (§7.1); dropping it across a snapshot made a folder impossible
/// to pick while a scan ran. Keeping the rows across a reorder moves every one of them; rebuilding
/// them for a snapshot throws the reader's place away several times a second.
/// </summary>
public sealed class ExploreRedrawTests
{
    private const string Root = @"C:\Users\testuser";

    /// <summary>A snapshot measures again the directory the page is standing in, in the same order.</summary>
    [Fact]
    public void ASnapshotOfTheSameDirectoryKeepsBothTheSelectionAndTheRows()
    {
        var (builder, folder) = Builder();
        var snapshot = builder.Build(ExploreChildOrder.ByName);
        var later = builder.Build(ExploreChildOrder.ByName);
        var here = ExplorePosition.Inside(folder);

        Assert.Equal(new ExploreRedraw(KeepsSelection: true, KeepsRows: true), ExploreRedraw.Between(snapshot, here, later, here));
    }

    /// <summary>
    /// The finished tree orders its children by size after snapshots that ordered them by name. The
    /// selection still names what it named; the list is a different list.
    /// </summary>
    [Fact]
    public void TheFinishedTreeKeepsTheSelectionButNotTheRows()
    {
        var (builder, folder) = Builder();
        var snapshot = builder.Build(ExploreChildOrder.ByName);
        var finished = builder.Build(ExploreChildOrder.BySize);
        var here = ExplorePosition.Inside(folder);

        Assert.Equal(new ExploreRedraw(KeepsSelection: true, KeepsRows: false), ExploreRedraw.Between(snapshot, here, finished, here));
    }

    /// <summary>A step into a folder is a new subject, and a selection made in one folder is not one in the next.</summary>
    [Fact]
    public void AStepIntoAFolderKeepsNeither()
    {
        var (builder, folder) = Builder();
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(
            new ExploreRedraw(KeepsSelection: false, KeepsRows: false),
            ExploreRedraw.Between(tree, ExplorePosition.Inside(tree.RootNode), tree, ExplorePosition.Inside(folder)));
    }

    /// <summary>
    /// Opening the root out of the volume is the same directory and the same rows, but it is a step:
    /// the first click of the double-click that opened it picked it.
    /// </summary>
    [Fact]
    public void OpeningTheRootOutOfTheVolumeKeepsTheRowsButNotTheSelection()
    {
        var (builder, _) = Builder();
        var tree = builder.Build(ExploreChildOrder.BySize);

        Assert.Equal(
            new ExploreRedraw(KeepsSelection: false, KeepsRows: true),
            ExploreRedraw.Between(tree, ExplorePosition.Top(tree), tree, ExplorePosition.Inside(tree.RootNode)));
    }

    /// <summary>A scan that came back rooted somewhere else is a different picture, and so is the first one.</summary>
    [Fact]
    public void ATreeRootedElsewhereKeepsNeither()
    {
        var (builder, _) = Builder();
        var standing = builder.Build(ExploreChildOrder.BySize);
        var elsewhere = new ExploreTreeBuilder(@"D:\").Build(ExploreChildOrder.BySize);
        var top = ExplorePosition.Inside(standing.RootNode);

        Assert.Equal(default, ExploreRedraw.Between(standing, top, elsewhere, ExplorePosition.Inside(elsewhere.RootNode)));
        Assert.Equal(default, ExploreRedraw.Between(null, top, standing, top));
    }

    private static (ExploreTreeBuilder Builder, int Folder) Builder()
    {
        var builder = new ExploreTreeBuilder(Root);
        var folder = builder.AddChildren(ExploreTreeBuilder.RootNode, [
            new ExploreChild("Downloads", IsDirectory: true, IsLink: false, Size: 0),
            new ExploreChild("notes.txt", IsDirectory: false, IsLink: false, Size: 5),
        ]);

        builder.AddChildren(folder, [
            new ExploreChild("a.bin", IsDirectory: false, IsLink: false, Size: 1),
            new ExploreChild("b.bin", IsDirectory: false, IsLink: false, Size: 9),
        ]);

        return (builder, folder);
    }
}
