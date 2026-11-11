using Deguffer.Core.Exploring;
using Deguffer.Testing;

namespace Deguffer.Core.Tests;

/// <summary>
/// Finding a node by its path, which the page asks of a removal made by path. A wrong node here would
/// draw the tree without something that is still on the disk.
/// </summary>
public sealed class ExploreTreePathsTests
{
    [Fact]
    public void APathIsFoundOneExactNameAtATime()
    {
        var tree = Tree(@"C:\Users\a");

        Assert.Equal("Old.lnk", tree.NameOf(TreeNodes.At(tree, @"C:\Users\a\Desktop\Old.lnk")));
        Assert.Equal(tree.RootNode, TreeNodes.At(tree, @"C:\Users\a"));
        Assert.Null(ExploreTreePaths.TryNodeAt(tree, @"C:\Users\a\Desktop\New.lnk"));
    }

    /// <summary>
    /// A path whose text starts with the root's is not under it unless a separator follows: a tree of
    /// <c>C:\Users\a</c> holds nothing of <c>C:\Users\ab</c>, though <c>ab</c> minus its <c>a</c> names
    /// a child here.
    /// </summary>
    [Fact]
    public void APathBesideTheRootIsNotInTheTree() =>
        Assert.Null(ExploreTreePaths.TryNodeAt(Tree(@"C:\Users\a"), @"C:\Users\aDesktop\Old.lnk"));

    private static ExploreTree Tree(string root)
    {
        var builder = new ExploreTreeBuilder(root);
        var desktop = builder.AddChildren(ExploreTreeBuilder.RootNode, [new ExploreChild("Desktop", IsDirectory: true, IsLink: false, Size: 0)]);
        builder.AddChildren(desktop, [new ExploreChild("Old.lnk", IsDirectory: false, IsLink: false, Size: 1)]);

        return builder.Build(ExploreChildOrder.BySize);
    }
}
