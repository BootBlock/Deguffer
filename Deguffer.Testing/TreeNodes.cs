using Deguffer.Core.Exploring;

namespace Deguffer.Testing;

/// <summary>
/// The nodes of a tree a test built: one by path, for a test that knows the path is there, or every
/// node reachable from the root.
/// </summary>
public static class TreeNodes
{
    /// <summary>
    /// The node <paramref name="path"/> names. Throws where the tree holds nothing there, because a
    /// test that asks for a node it did not build has a fixture that is wrong, and should say so at once.
    /// </summary>
    public static int At(ExploreTree tree, string path) =>
        ExploreTreePaths.TryNodeAt(tree, path) ?? throw new InvalidOperationException($"The tree holds nothing at {path}.");

    /// <summary>Every node reachable from the root, the root included, in no particular order.</summary>
    public static List<int> Reachable(ExploreTree tree)
    {
        var reached = new List<int>();
        var pending = new Stack<int>();
        pending.Push(tree.RootNode);

        while (pending.TryPop(out var node))
        {
            reached.Add(node);

            foreach (var child in tree.ChildrenOf(node))
            {
                pending.Push(child);
            }
        }

        return reached;
    }
}
