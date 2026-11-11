using Deguffer.Core.Safety;

namespace Deguffer.Core.Exploring;

/// <summary>
/// The node of an <see cref="ExploreTree"/> that a path names, found by descending from the tree's
/// root one exactly matched name at a time.
///
/// <para>By path because a tree's node numbers are its own. The file table's route keeps only what
/// its root reaches and numbers that again, so a record number says nothing about which node holds
/// the record.</para>
///
/// <para>The page asks it where something it removed without a node, such as a shortcut from the
/// report of the Start menu, may be in the tree on screen, which then has to be drawn without it.</para>
/// </summary>
public static class ExploreTreePaths
{
    /// <summary>The node <paramref name="path"/> names, or null where the tree holds nothing there.</summary>
    public static int? TryNodeAt(ExploreTree tree, string path)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(path);

        // Contains rather than a prefix of the text: a tree of C:\Users\a holds nothing of C:\Users\ab.
        if (!LongPath.Contains(tree.RootPath, path))
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
