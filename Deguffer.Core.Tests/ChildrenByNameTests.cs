using Deguffer.Core.Exploring;

namespace Deguffer.Core.Tests;

/// <summary>
/// One folder's children found by name, without regard to case, through the names the tree holds.
/// </summary>
public class ChildrenByNameTests
{
    private const string Root = @"C:\Users\testuser";

    [Fact]
    public void FindsAChildByItsNameInAnyCase()
    {
        var (tree, first) = Folder("Documents", "Music", "notes.txt");
        var children = Indexed(tree);

        Assert.Equal(first, children.Find("documents"));
        Assert.Equal(first + 1, children.Find("MUSIC"));
        Assert.Equal(first + 2, children.Find("notes.txt"));
        Assert.Null(children.Find("Pictures"));
        Assert.Null(children.Find("note"));
    }

    /// <summary>
    /// A directory with case sensitivity turned on can hold <c>Cache</c> and <c>cache</c> at once, and
    /// they are two folders. Each spelling finds its own, whichever was added first, and a spelling
    /// that is neither finds the first added, so the answer is the same every time.
    ///
    /// <para>What a later scan of the same place is matched by. Its node numbers are its own, so the
    /// folder is found again by its name, and taking the first match without regard to case would
    /// follow <c>Cache</c> into <c>cache</c>.</para>
    /// </summary>
    [Fact]
    public void EachOfTwoNamesDifferingOnlyInCaseFindsItself()
    {
        var (tree, first) = Folder("Cache", "cache");
        var children = Indexed(tree);

        Assert.Equal(first, children.Find("Cache"));
        Assert.Equal(first + 1, children.Find("cache"));
        Assert.Equal(first, children.Find("CACHE"));
    }

    private static (ExploreTree Tree, int First) Folder(params string[] names)
    {
        var builder = new ExploreTreeBuilder(Root);
        var first = builder.AddChildren(
            ExploreTreeBuilder.RootNode, [.. names.Select(name => new ExploreChild(name, IsDirectory: false, IsLink: false, Size: 1))]);

        return (builder.Build(ExploreChildOrder.ByName), first);
    }

    // In node order, which is the order the children were added in.
    private static ChildrenByName Indexed(ExploreTree tree)
    {
        var children = new ChildrenByName(tree);

        foreach (var child in tree.ChildrenOf(tree.RootNode).ToArray().Order())
        {
            children.Add(child);
        }

        return children;
    }
}
