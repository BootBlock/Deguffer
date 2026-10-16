using Deguffer.Core.Exploring;

namespace Deguffer.Testing;

/// <summary>
/// The node of an <see cref="ExploreTree"/> that a path names, found by descending from the tree's
/// root one exactly matched name at a time.
///
/// <para>By path because a tree's node numbers are its own. The file table's route keeps only what
/// its root reaches and numbers that again, so a record number says nothing about which node holds
/// the record.</para>
/// </summary>
public static class ExploreTreePaths
{
    /// <summary>The node <paramref name="path"/> names. Throws where the tree holds nothing there.</summary>
    public static int NodeAt(ExploreTree tree, string path) =>
        TryNodeAt(tree, path) ?? throw new InvalidOperationException($"The tree holds nothing at {path}.");

    /// <summary>The node <paramref name="path"/> names, or null where the tree holds nothing there.</summary>
    public static int? TryNodeAt(ExploreTree tree, string path)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(path);

        if (!path.StartsWith(tree.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var node = tree.RootNode;

        foreach (var component in path[tree.RootPath.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            int? found = null;

            foreach (var child in tree.ChildrenOf(node))
            {
                if (tree.NameSpanOf(child).SequenceEqual(component))
                {
                    found = child;
                    break;
                }
            }

            if (found is not { } next)
            {
                return null;
            }

            node = next;
        }

        return node;
    }
}
