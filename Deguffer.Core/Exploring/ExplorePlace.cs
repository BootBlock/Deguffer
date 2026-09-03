namespace Deguffer.Core.Exploring;

/// <summary>
/// Where a view standing on one node should stand once the tree under it is replaced.
///
/// <para>A scan publishes a partial tree every so often, and a finished scan publishes one more.
/// Each is a whole new <see cref="ExploreTree"/>, so something has to say which of its nodes the
/// user is now looking at. Opening every one at its root is what put somebody back at the drive
/// while they were reading a folder.</para>
///
/// <para>Separate from both (G1). <see cref="ExploreTree"/> answers for one tree and knows nothing
/// of the one before it, and the page that shows a tree should not also be deciding when a node
/// number from another one still means something.</para>
/// </summary>
public static class ExplorePlace
{
    /// <summary>
    /// The node of <paramref name="arriving"/> that stands where <paramref name="node"/> stood in
    /// <paramref name="standing"/>, or the new tree's root where nothing does.
    ///
    /// <para>Decided by comparing the two paths rather than by trusting the number. A snapshot and
    /// the tree that follows it come from one <see cref="ExploreTreeBuilder"/>, whose node numbers
    /// are indices into lists that only ever grow, so the same number is the same directory
    /// throughout a walked scan and the place carries forward. The file-table route publishes no
    /// snapshot at all and numbers its nodes by record, so a node held from an earlier scan means
    /// nothing in the tree that replaces it — and the comparison says so instead of this having to
    /// know which route produced either tree.</para>
    ///
    /// <para>Compared without regard to case, because the two trees can be rooted at the same
    /// volume written differently — a scan of <c>c:\</c> after one of <c>C:\</c> — and NTFS does not
    /// tell those apart either.</para>
    /// </summary>
    /// <param name="standing">The tree on screen, or null where nothing has been scanned yet.</param>
    /// <param name="node">The node being looked at in <paramref name="standing"/>.</param>
    /// <param name="arriving">The tree replacing it.</param>
    public static int Carry(ExploreTree? standing, int node, ExploreTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        if (standing is null)
        {
            return arriving.RootNode;
        }

        return standing.TryPathOf(node) is { } was
            && arriving.TryPathOf(node) is { } now
            && string.Equals(was, now, StringComparison.OrdinalIgnoreCase)
                ? node
                : arriving.RootNode;
    }
}
