namespace Deguffer.Core.Exploring.Acting;

/// <summary>
/// What is left of a scanned tree once a removal from it has finished: the tree without what the
/// removal's report says went.
///
/// <para>Without rescanning. A rescan of the drive takes minutes, for one deleted folder, and a
/// removal takes a folder and everything in it and nothing else, which
/// <see cref="ExploreTree.Without"/> answers for exactly. What else changed on the disk since the
/// scan is not known, and the page says so (§7.1 allows Explore's numbers to be off provided the
/// picture says which way).</para>
///
/// <para>The tree left keeps the scan's node numbers and holds nothing that went, so a removed item
/// is in no list, in no drawing and under no pointer, and a node of it can never be picked again
/// (§7.1).</para>
/// </summary>
public static class ExploreRemovals
{
    /// <summary>
    /// <paramref name="tree"/> without each of <paramref name="picked"/> that
    /// <paramref name="report"/> says went, or <paramref name="tree"/> itself where none did.
    ///
    /// <para>Matched by path, without regard to case: the report names what it removed by the path
    /// it was handed, and NTFS does not tell two spellings of one path apart. A picked node the
    /// report refused stays, because it is still on the disk.</para>
    /// </summary>
    public static ExploreTree Without(ExploreTree tree, IEnumerable<int> picked, ExploreRemovalReport report)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(picked);
        ArgumentNullException.ThrowIfNull(report);

        var gone = report.Removed.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tree.Without(picked.Where(node => tree.TryPathOf(node) is { } path && gone.Contains(path)));
    }
}
