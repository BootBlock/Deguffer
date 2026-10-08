using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring;

/// <summary>
/// Where the reader has been in a scanned tree, so Back and Forward retrace it the way a browser's
/// history does: a step somewhere new is remembered behind them and forgets what lay ahead, and going
/// back puts where they were ahead of them.
///
/// <para>Separate from <see cref="ExplorePosition"/>, which says where a step from here leads, and
/// from the page, which only asks (G1). The rules are in Core so they are provable without a window
/// (G8).</para>
///
/// <para>A place that can no longer be shown is passed over rather than shown: a folder removed from
/// the page since, and the place the reader is already standing in, which would be a step that goes
/// nowhere. What is passed over is forgotten, because it cannot become showable again in the same
/// tree.</para>
/// </summary>
public sealed class ExploreVisits
{
    /// <summary>
    /// How many steps are kept in either direction, the oldest forgotten first. Bounded because every
    /// snapshot of a walked scan carries each step into the arriving tree (see <see cref="Carry"/>),
    /// which builds two paths per step, at every snapshot.
    /// </summary>
    public const int Depth = 50;

    /// <summary>Where Back leads, the nearest step last.</summary>
    private readonly List<ExploreVisit> _back = [];

    /// <summary>Where Forward leads, the nearest step last.</summary>
    private readonly List<ExploreVisit> _forward = [];

    /// <summary>
    /// The reader is leaving <paramref name="from"/> for somewhere new: it goes behind them, and
    /// whatever lay ahead is forgotten, because it was ahead of a place they have now left by another
    /// route.
    /// </summary>
    public void Leave(ExploreVisit from)
    {
        Push(_back, from);
        _forward.Clear();
    }

    /// <summary>
    /// Whether going back from <paramref name="current"/> leads anywhere: some step behind it that
    /// <see cref="Back"/> would not pass over.
    /// </summary>
    /// <param name="zooms">
    /// Whether the view on screen shows a zoom. Where it does not, a step that differs from here only
    /// by its zoom shows nothing new, and is passed over.
    /// </param>
    /// <param name="wasRemoved">Whether a node of <paramref name="tree"/> has gone from the page since the scan.</param>
    public bool CanGoBack(ExploreVisit current, ExploreTree tree, VolumeSpace volume, bool zooms, Func<int, bool> wasRemoved) =>
        _back.Exists(visit => Leads(visit, current, tree, volume, zooms, wasRemoved));

    /// <summary>The same as <see cref="CanGoBack"/>, ahead.</summary>
    public bool CanGoForward(ExploreVisit current, ExploreTree tree, VolumeSpace volume, bool zooms, Func<int, bool> wasRemoved) =>
        _forward.Exists(visit => Leads(visit, current, tree, volume, zooms, wasRemoved));

    /// <summary>
    /// The nearest step behind <paramref name="current"/> that can be shown, with
    /// <paramref name="current"/> put ahead, or null where none can. Nothing changes on null.
    /// </summary>
    /// <param name="zooms">See <see cref="CanGoBack"/>.</param>
    /// <param name="wasRemoved">Whether a node of <paramref name="tree"/> has gone from the page since the scan.</param>
    public ExploreVisit? Back(ExploreVisit current, ExploreTree tree, VolumeSpace volume, bool zooms, Func<int, bool> wasRemoved) =>
        Step(_back, _forward, current, tree, volume, zooms, wasRemoved);

    /// <summary>The same as <see cref="Back"/>, ahead.</summary>
    public ExploreVisit? Forward(ExploreVisit current, ExploreTree tree, VolumeSpace volume, bool zooms, Func<int, bool> wasRemoved) =>
        Step(_forward, _back, current, tree, volume, zooms, wasRemoved);

    /// <summary>
    /// Bring every step into <paramref name="arriving"/>, which replaces <paramref name="leaving"/>:
    /// a step stays where its node still names what it named, by <see cref="ExplorePlace.TryCarry"/>,
    /// and is forgotten where it does not. A tree rooted somewhere else is somewhere else, and forgets
    /// them all.
    ///
    /// <para>Every step comes back at the whole picture. A zoom is a part of one layout, and the
    /// arriving tree lays out differently, so the same part of it is not the same shapes.</para>
    /// </summary>
    /// <param name="leaving">
    /// The tree the steps were taken in, or null where there was none, which is also a page whose tree
    /// was taken off the screen: a cancelled scan's.
    /// </param>
    public void Carry(ExploreTree? leaving, ExploreTree arriving)
    {
        ArgumentNullException.ThrowIfNull(arriving);

        // The same tree is not a replacement, and its steps' zooms still stand.
        if (ReferenceEquals(leaving, arriving) || (_back.Count == 0 && _forward.Count == 0))
        {
            return;
        }

        if (!ExplorePlace.IsRootedAlike(leaving, arriving))
        {
            Clear();
            return;
        }

        CarryInto(_back, leaving, arriving);
        CarryInto(_forward, leaving, arriving);
    }

    private void Clear()
    {
        _back.Clear();
        _forward.Clear();
    }

    private static ExploreVisit? Step(
        List<ExploreVisit> from,
        List<ExploreVisit> to,
        ExploreVisit current,
        ExploreTree tree,
        VolumeSpace volume,
        bool zooms,
        Func<int, bool> wasRemoved)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(wasRemoved);

        var nearest = from.FindLastIndex(visit => Leads(visit, current, tree, volume, zooms, wasRemoved));

        if (nearest < 0)
        {
            return null;
        }

        var visit = from[nearest];

        // What lay between is passed over and forgotten: see the remarks on the type.
        from.RemoveRange(nearest, from.Count - nearest);
        Push(to, current);

        return visit;
    }

    /// <summary>Whether stepping to <paramref name="visit"/> from <paramref name="current"/> shows something.</summary>
    private static bool Leads(
        ExploreVisit visit,
        ExploreVisit current,
        ExploreTree tree,
        VolumeSpace volume,
        bool zooms,
        Func<int, bool> wasRemoved) =>
        !wasRemoved(visit.Position.Node) && !visit.Shows(current, tree, volume, zooms);

    private static void Push(List<ExploreVisit> steps, ExploreVisit visit)
    {
        steps.Add(visit);

        if (steps.Count > Depth)
        {
            steps.RemoveAt(0);
        }
    }

    /// <summary>
    /// <see cref="Carry"/> for one direction, written over in place and in order, so a step that is
    /// gone closes the gap behind it.
    /// </summary>
    private static void CarryInto(List<ExploreVisit> steps, ExploreTree leaving, ExploreTree arriving)
    {
        var kept = 0;

        for (var read = 0; read < steps.Count; read++)
        {
            var visit = steps[read];

            if (ExplorePlace.TryCarry(leaving, visit.Position.Node, arriving) is null)
            {
                continue;
            }

            steps[kept++] = visit with { Viewport = MapViewport.Whole };
        }

        steps.RemoveRange(kept, steps.Count - kept);
    }
}
