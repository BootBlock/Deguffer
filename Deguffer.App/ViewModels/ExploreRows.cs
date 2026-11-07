using System.Collections.ObjectModel;
using Deguffer.Core.Exploring;
using Deguffer.Core.Exploring.Knowledge;
using Deguffer.Core.Viewing;

namespace Deguffer.App.ViewModels;

/// <summary>
/// The Explore page's list of what the folder on screen holds, with the trail of folders above it
/// that leads back to the top.
///
/// <para>Separate from <see cref="ExploreViewModel"/>, as <see cref="ExploreFiles"/> is, because it
/// holds state of its own, the rows and the crumbs, that the page is only told to bring up to date.
/// One type for both, because they describe one place and are rewritten together: updating them
/// apart is how a breadcrumb comes to name a directory the list below it is no longer showing.
/// Where each step of the trail leads is <see cref="ExplorePosition.Trail"/>'s, and how a row is
/// worded is <see cref="ExploreNodeText"/>'s and <see cref="ExploreRowText"/>'s.</para>
/// </summary>
public sealed class ExploreRows
{
    /// <summary>
    /// What the folder holds and what the trail names, per rewrite. Filled again each time and never
    /// replaced: a scan in progress publishes a snapshot as often as every few hundred milliseconds,
    /// and building two lists for each of those is work to say what was already said (G5).
    /// </summary>
    private readonly List<int> _arriving = [];

    private readonly List<ExploreCrumb> _crumbs = [];

    private readonly List<ExplorePosition> _steps = [];

    /// <summary>
    /// What the app knows about well-known files and folders, which a row explains its item from.
    /// See <see cref="ExploreRow"/>.
    /// </summary>
    private readonly ItemGuide _guide;

    /// <summary>What every age in the list is measured against.</summary>
    private readonly TimeProvider _time;

    public ExploreRows(ItemGuide guide, TimeProvider time)
    {
        _guide = guide;
        _time = time;
    }

    /// <summary>
    /// What the current node holds, in the tree's own order: largest first once a scan has
    /// finished, and while one is still running, in the order its snapshots come in. See
    /// <see cref="Core.Exploring.Rendering.ExploreSurface.SnapshotOrder"/>.
    /// </summary>
    public BoundList<ExploreRow> Rows { get; } = [];

    /// <summary>The path from the scan's root down to the current node.</summary>
    public ObservableCollection<ExploreCrumb> Trail { get; } = [];

    /// <summary>
    /// Whether <see cref="Rows"/> is being rewritten from here.
    ///
    /// <para>Read by the page, because a bound <c>ListView</c> drops an item from its own
    /// selection when the collection under it stops holding that item where it was, and reports
    /// that back as a selection change. Taken for a gesture, it overwrites the selection with
    /// whatever the rewrite happened to leave behind — on a rescan that is a node number belonging
    /// to the tree before it, and asking the arriving tree for its path throws.</para>
    /// </summary>
    public bool IsShowing { get; private set; }

    /// <summary>
    /// Bring the rows and the trail to <paramref name="position"/> in <paramref name="tree"/>.
    /// </summary>
    /// <param name="volume">The volume <paramref name="tree"/> is the whole of, whose step the trail names for the whole drive.</param>
    /// <param name="reconcile">
    /// Whether the rows already there are about this same directory.
    ///
    /// <para>Clearing the collection is a Reset for the bound <c>ListView</c>, and a Reset throws
    /// away the scroll position and the selection with it — which, on a scan publishing a
    /// snapshot every few hundred milliseconds, made reading a long list or picking a folder out of
    /// one impossible while the scan ran. So a list still about the same thing is brought up to
    /// date in place: the rows that stay are the same objects, and the <c>ListView</c> is told
    /// nothing it has to recover from.</para>
    ///
    /// <para>A list about something else is rebuilt. The identities are unrelated, so matching them
    /// up would keep a highlight the user put on a different folder, and the scroll position is
    /// about content that is no longer there.</para>
    /// </param>
    public void Show(ExploreTree tree, ExplorePosition position, VolumeSpace volume, bool reconcile)
    {
        IsShowing = true;

        try
        {
            Rewrite(tree, position.Node, reconcile);
        }
        finally
        {
            IsShowing = false;
        }

        ShowTrail(tree, position, volume);
    }

    /// <summary>Empty the list and the trail, where nothing is on screen to describe.</summary>
    public void Clear()
    {
        Rows.Clear();
        Trail.Clear();
    }

    /// <summary>The pass itself. See <see cref="Show"/>, which is what holds <see cref="IsShowing"/> up.</summary>
    private void Rewrite(ExploreTree tree, int node, bool reconcile)
    {
        var total = tree.SizeOf(node);

        // Once for the whole list rather than once per row. Every age in it is measured against the
        // same instant, which is both cheaper and the only way two rows a millisecond apart cannot
        // land in different days (G5).
        var now = _time.GetUtcNow().UtcDateTime;

        _arriving.Clear();

        foreach (var child in tree.ChildrenOf(node))
        {
            _arriving.Add(child);
        }

        ExploreRow Make(int child) => new(tree, child, total, now, _guide);

        // A list about something else is replaced in one reset, which is the one change the
        // ListView answers by rebuilding anyway. See BoundList.
        if (!reconcile)
        {
            Rows.Replace(_arriving.Select(Make));

            return;
        }

        LiveList.Show(
            Rows,
            _arriving,
            row => row.Key,
            child => ExploreRow.KeyOf(tree, child),
            Make,
            (row, child) => row.Describe(tree, child, total, now));
    }

    /// <summary>
    /// The path back to the top, brought up to date rather than rebuilt: a crumb is a button a
    /// reader can put the keyboard on, and clearing the collection destroys it. A scan in progress
    /// publishes a snapshot as often as every few hundred milliseconds, and every one redraws this.
    ///
    /// <para>The steps are <see cref="ExplorePosition.Trail"/>'s. The volume's step is named for
    /// the whole drive rather than by the root's name, because the root is the step after it.</para>
    /// </summary>
    private void ShowTrail(ExploreTree tree, ExplorePosition position, VolumeSpace volume)
    {
        position.Trail(tree, volume, _steps);
        _crumbs.Clear();

        foreach (var step in _steps)
        {
            _crumbs.Add(new ExploreCrumb(
                step,
                step.IsVolume(tree, volume) ? "Whole drive" : tree.NameOf(step.Node),
                FollowsAnother: _crumbs.Count > 0));
        }

        LiveList.Show(Trail, _crumbs, crumb => crumb.Position);
    }
}
