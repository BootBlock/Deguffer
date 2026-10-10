using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// The invented tree the appearance preview draws: it names nothing on the machine, and it is shaped
/// so that each choice the preview stands for shows on it.
/// </summary>
public sealed class MapSampleTests
{
    /// <summary>A screenshot of the preview must name no folder the reader has, so nothing in the sample is a path.</summary>
    [Fact]
    public void TheSampleNamesNoPlaceOnTheMachine()
    {
        var tree = MapSample.Tree;

        Assert.False(Path.IsPathRooted(tree.RootPath));

        for (var node = 0; node < tree.NodeCount; node++)
        {
            Assert.DoesNotContain(Path.DirectorySeparatorChar, tree.NameOf(node));
            Assert.DoesNotContain(':', tree.NameOf(node));
        }
    }

    /// <summary>
    /// A scheme colours each top-level folder in a hue of its own, so the sample has several to show
    /// them side by side, and spacing leaves room round what each folder holds, so more than one of
    /// those has folders inside it.
    /// </summary>
    [Fact]
    public void TheSampleShowsSeveralBranchesAndFoldersInsideFolders()
    {
        var tree = MapSample.Tree;
        var branches = tree.ChildrenOf(tree.RootNode).ToArray().Where(tree.IsDirectory).ToList();

        Assert.True(branches.Count >= 4, $"only {branches.Count} top-level folders");
        Assert.True(
            branches.Count(branch => tree.ChildrenOf(branch).ToArray().Any(tree.IsDirectory)) >= 2,
            "fewer than two top-level folders hold a folder of their own");
        Assert.All(branches, branch => Assert.True(tree.SizeOf(branch) > 0));
    }
}
