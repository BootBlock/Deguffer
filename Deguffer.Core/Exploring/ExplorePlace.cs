using System.Diagnostics.CodeAnalysis;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Where a view standing on one node should stand once the tree under it is replaced.
///
/// <para>A walked scan publishes a partial tree every so often, and every scan hands back a
/// finished one at the end. Each is a whole new <see cref="ExploreTree"/>, so something has to say
/// which of its nodes the user is now looking at. Opening every one at its root is what put
/// somebody back at the drive while they were reading a folder.</para>
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
    /// <para>Found by path rather than by trusting the number. A tree's node numbers are its own: a
    /// walked scan keeps a node's number from one snapshot to the next, but two file-table scans of
    /// one place number only what each reaches (<see cref="ExploreTree.Create"/>), so a folder that
    /// gained a sibling between them can have another number in the second. And two trees of
    /// different places number their nodes alike, where the same number would name one folder while
    /// listing another's contents. Finding the same path settles every case without this having to
    /// know which route produced either tree.</para>
    ///
    /// <para>Compared without regard to case, because the two trees can be rooted at the same
    /// volume written differently — a scan of <c>c:\</c> after one of <c>C:\</c> — and NTFS does not
    /// tell those apart either.</para>
    /// </summary>
    /// <param name="standing">The tree on screen, or null where nothing has been scanned yet.</param>
    /// <param name="node">The node being looked at in <paramref name="standing"/>.</param>
    /// <param name="arriving">The tree replacing it.</param>
    /// <returns>A node <paramref name="arriving"/> holds (<see cref="ExploreTree.Holds"/>).</returns>
    public static int Carry(ExploreTree? standing, int node, ExploreTree arriving) =>
        TryCarry(standing, node, arriving) ?? arriving.RootNode;

    /// <summary>
    /// Whether <paramref name="arriving"/> is rooted where <paramref name="standing"/> is, so places in
    /// the one carry into the other at all. A tree rooted somewhere else is somewhere else, and so is
    /// the first tree after none.
    /// </summary>
    public static bool IsRootedAlike([NotNullWhen(true)] ExploreTree? standing, ExploreTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        return standing is not null && string.Equals(standing.RootPath, arriving.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The node of <paramref name="arriving"/> that stands where <paramref name="node"/> stood in
    /// <paramref name="standing"/>, or null where nothing does.
    ///
    /// <para>The same question as <see cref="Carry"/> and the same answer, without the fallback.
    /// A view standing on one node has to stand somewhere, so that one substitutes the root; a
    /// node picked out by hand has no such obligation, and substituting the root for it would
    /// select a drive nobody asked to select. Both callers need the rule, and only one of them
    /// wants a node whatever happens.</para>
    /// </summary>
    /// <param name="standing">The tree on screen, or null where nothing has been scanned yet.</param>
    /// <param name="node">The node being looked at in <paramref name="standing"/>.</param>
    /// <param name="arriving">The tree replacing it.</param>
    public static int? TryCarry(ExploreTree? standing, int node, ExploreTree arriving) =>
        Carrying(standing, arriving)?.Invoke(node);

    /// <summary>
    /// <see cref="TryCarry"/> for every node of <paramref name="standing"/> at once, for a caller
    /// carrying several: each folder's children in <paramref name="arriving"/> are indexed by name
    /// once, rather than once per node. Null where <paramref name="arriving"/> is rooted somewhere
    /// else (<see cref="IsRootedAlike"/>), where no node carries.
    /// </summary>
    public static Func<int, int?>? Carrying(ExploreTree? standing, ExploreTree arriving)
    {
        if (!IsRootedAlike(standing, arriving))
        {
            return null;
        }

        var counterparts = new ExploreCounterparts(standing, arriving);

        // A node taken out of the tree on screen is no longer there to stand on, wherever its path
        // leads in the other.
        return node => standing.Holds(node) ? counterparts.Find(node) : null;
    }
}
