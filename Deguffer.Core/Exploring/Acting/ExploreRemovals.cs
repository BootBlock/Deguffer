namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What has gone from a scanned tree since it was scanned, by node.
///
/// <para>The tree is parallel arrays built once, and rebuilding it would mean rescanning the drive —
/// minutes, for one deleted folder. So what went is remembered instead, and every screen that shows
/// the tree asks this before it lists a node, offers it, or lets it be picked. §7.1 allows
/// Explore's numbers to be off provided the picture says which way, and the page's stale note is
/// that statement for this direction.</para>
///
/// <para>Recorded against one tree and no other. A node index means nothing outside the tree it
/// came from, and the trees are replaced wholesale, on every mid-scan snapshot as well as at the end
/// of a scan. Read against the wrong one, a rescan filtered the new tree's children through the old
/// tree's indices: a directory genuinely on the disk vanished from the list and could not be
/// opened, while the stale note claimed items had been removed since a scan that had only just
/// started.</para>
/// </summary>
public sealed class ExploreRemovals
{
    private readonly HashSet<int> _removed = [];

    /// <summary>The tree <see cref="_removed"/>'s indices belong to.</summary>
    private ExploreTree? _tree;

    /// <summary>How many picked items have gone from <paramref name="tree"/>: zero for any tree but the one recorded against.</summary>
    public int CountIn(ExploreTree? tree) => ReferenceEquals(tree, _tree) ? _removed.Count : 0;

    /// <summary>
    /// Whether <paramref name="node"/> of <paramref name="tree"/> has gone since the scan.
    ///
    /// <para><b>Walked up, because a removal takes everything inside it.</b> What is recorded is what
    /// the user picked out by hand, which is a handful of folders; what went with them is every file
    /// under each. A deleted directory of ten thousand entries would otherwise leave every one of
    /// them looking present, pickable and deletable — and the map is where that shows, because it
    /// draws descendants the list never lists.</para>
    /// </summary>
    public bool WasRemoved(ExploreTree? tree, int node)
    {
        if (_removed.Count == 0 || tree is null || !ReferenceEquals(tree, _tree))
        {
            return false;
        }

        for (var current = node; ;)
        {
            if (_removed.Contains(current))
            {
                return true;
            }

            var parent = tree.ParentOf(current);

            // Every reader marks its scan root as its own parent, so this is where the walk ends —
            // and it ends for a node outside the scanned subtree too, rather than never.
            if (parent == current)
            {
                return false;
            }

            current = parent;
        }
    }

    /// <summary>
    /// Remember each of <paramref name="picked"/> that <paramref name="report"/> says went, against
    /// <paramref name="tree"/>. What was recorded against any other tree is forgotten first, so a
    /// later tree cannot inherit it.
    ///
    /// <para>Matched by path, without regard to case: the report names what it removed by the path
    /// it was handed, and NTFS does not tell two spellings of one path apart. A picked node the
    /// report refused stays, because it is still on the disk.</para>
    /// </summary>
    public void Record(ExploreTree tree, IEnumerable<int> picked, ExploreRemovalReport report)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(report);

        if (!ReferenceEquals(tree, _tree))
        {
            _removed.Clear();
            _tree = tree;
        }

        var gone = report.Removed.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var node in picked)
        {
            if (gone.Contains(tree.PathOf(node)))
            {
                _removed.Add(node);
            }
        }
    }
}
