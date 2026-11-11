using Deguffer.Core.Exploring;

namespace Deguffer.Testing;

/// <summary>The node of a tree a test built, by path, for a test that knows the path is there.</summary>
public static class TreeNodes
{
    /// <summary>
    /// The node <paramref name="path"/> names. Throws where the tree holds nothing there, because a
    /// test that asks for a node it did not build has a fixture that is wrong, and should say so at once.
    /// </summary>
    public static int At(ExploreTree tree, string path) =>
        ExploreTreePaths.TryNodeAt(tree, path) ?? throw new InvalidOperationException($"The tree holds nothing at {path}.");
}
