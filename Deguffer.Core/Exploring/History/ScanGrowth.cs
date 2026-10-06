namespace Deguffer.Core.Exploring.History;

/// <summary>How a folder's size compares with what an earlier scan recorded for it, and how that is known.</summary>
public enum FolderChangeKind
{
    /// <summary>
    /// The same size both times, as far as the scans can say: recorded both times, so the change is
    /// exact, or not written since the earlier scan, so its size cannot have moved.
    /// </summary>
    Measured,

    /// <summary>
    /// Made after the earlier scan finished, by the date the file system gives it, so the whole of its
    /// size is growth.
    /// </summary>
    Created,

    /// <summary>
    /// Large enough to be recorded now, and not recorded then: new since, or no larger then than the
    /// earlier summary's <see cref="ScanSummary.UnrecordedAtMost"/>. The change is the folder's whole
    /// size, which overstates it by no more than that.
    /// </summary>
    New,

    /// <summary>Recorded then, and not on the disk now, though the folder holding it is.</summary>
    Removed,
}

/// <summary>
/// Why a comparison can be off by more than the folders' own growth, or that it cannot. Flags, because
/// both can be true at once and each needs saying (§7.1).
/// </summary>
[Flags]
public enum GrowthApproximation
{
    /// <summary>Both scans took the same route and read the whole volume, so every change is measured.</summary>
    None = 0,

    /// <summary>
    /// The scans took different routes. A walk counts less than the file table does: it cannot open
    /// what the account is refused, and it does not see the file system's own records. So a folder
    /// can appear to grow when only the route changed.
    /// </summary>
    RouteChanged = 1,

    /// <summary>Either scan could not read part of the volume, so its totals are short by an amount nobody measured.</summary>
    LowerBound = 2,
}

/// <summary>One folder's change since an earlier scan, or one file's where its dates settle it.</summary>
/// <param name="Before">
/// What the earlier scan recorded, or zero for <see cref="FolderChangeKind.New"/> and
/// <see cref="FolderChangeKind.Created"/>.
/// </param>
/// <param name="After">What this scan counted, or zero for <see cref="FolderChangeKind.Removed"/>.</param>
/// <param name="Node">The entry in this scan's tree, or -1 for <see cref="FolderChangeKind.Removed"/>.</param>
public readonly record struct FolderChange(
    string Path, FolderChangeKind Kind, long Before, long After, int Node)
{
    /// <summary>How many bytes it grew by. Negative where it shrank.</summary>
    public long Bytes => After - Before;
}

/// <summary>
/// What grew and what shrank between an earlier <see cref="ScanSummary"/> of a volume and a scan of
/// it now.
///
/// <para><b>A number, never a judgement.</b> §7.1: growth is reported as bytes against a date. It does
/// not say a folder is safe, and nothing is selected or offered because it grew.</para>
///
/// <para>Matched by name, folder under folder, from the root down, rather than by path text: the root
/// is the volume's whatever letter it wears now, so a disk that moved from <c>E:</c> to <c>F:</c>
/// still compares with itself. Names are compared without regard to case, as Windows compares
/// them.</para>
/// </summary>
public sealed class ScanGrowth
{
    private readonly Dictionary<int, FolderChange> _byNode;

    private ScanGrowth(
        ExploreTree tree,
        ScanSummary earlier,
        GrowthApproximation approximation,
        Dictionary<int, FolderChange> byNode,
        IReadOnlyList<FolderChange> ranked)
    {
        Tree = tree;
        Earlier = earlier;
        Approximation = approximation;
        _byNode = byNode;
        Ranked = ranked;
    }

    /// <summary>The tree this compares, and the only one its node numbers mean anything in.</summary>
    public ExploreTree Tree { get; }

    /// <summary>The summary this compares against.</summary>
    public ScanSummary Earlier { get; }

    /// <summary>When the scan compared against finished.</summary>
    public DateTime SinceUtc => Earlier.TakenUtc;

    /// <summary>Whether, and why, a change can be off by more than the folders' own growth.</summary>
    public GrowthApproximation Approximation { get; }

    /// <summary>
    /// Every folder with a change to report, the largest growth first and the largest shrinkage
    /// last. Removed folders are here and nowhere else, because they have no shape to draw.
    /// </summary>
    public IReadOnlyList<FolderChange> Ranked { get; }

    /// <summary>How many of the largest growths <see cref="Listing"/> lists at most.</summary>
    public const int ListedGrowths = 150;

    /// <summary>How many of the largest shrinkages <see cref="Listing"/> lists at most.</summary>
    public const int ListedShrinkages = 50;

    /// <summary>
    /// What a list of changes shows: the largest growths, then the largest shrinkages, in the order
    /// of <see cref="Ranked"/>, so the folder that shrank most is last. A folder that did not change
    /// is left out, because it answers neither question.
    ///
    /// <para>Bounded at both ends, because a list of twenty thousand folders is not read, and the
    /// shrinkages get their own share so that a volume where much grew still shows what was
    /// freed.</para>
    /// </summary>
    public IReadOnlyList<FolderChange> Listing()
    {
        var grew = Ranked.TakeWhile(change => change.Bytes > 0).Take(ListedGrowths);
        var shrank = Ranked.Reverse().TakeWhile(change => change.Bytes < 0).Take(ListedShrinkages).Reverse();

        return [.. grew, .. shrank];
    }

    /// <summary>
    /// <paramref name="node"/>'s own change: the one compared from the two summaries, or else the one
    /// its dates settle. Null where neither can say, which is an entry written since the earlier scan
    /// that neither scan recorded: a file, or a folder too small to have been recorded.
    ///
    /// <para><b>Dates settle two cases, and only those.</b> An entry made after the earlier scan
    /// finished is new, so all of it is growth. An entry with nothing at or below it written since
    /// then cannot have changed size, because a file's size moves only with a write and a removal
    /// inside a folder moves the folder's own date (<see cref="ExploreTree.ModifiedOf"/>). Anything
    /// written since has changed by an amount nothing recorded. A file written during the earlier
    /// scan, before it finished, may have been counted at an earlier size and still read as
    /// unchanged, which is a window of the scan's own length.</para>
    /// </summary>
    public FolderChange? ChangeOf(int node)
    {
        if (_byNode.TryGetValue(node, out var change))
        {
            return change;
        }

        var size = Tree.SizeOf(node);

        return Dated(node) switch
        {
            FolderChangeKind.Created => new FolderChange(Tree.PathOf(node), FolderChangeKind.Created, 0, size, node),
            FolderChangeKind.Measured => new FolderChange(Tree.PathOf(node), FolderChangeKind.Measured, size, size, node),
            _ => null,
        };
    }

    /// <summary>
    /// How many bytes <paramref name="node"/> is painted as having grown by: its own change where
    /// <see cref="ChangeOf"/> has one, or else the nearest folder's above it that has one. Null where
    /// nothing up to the root has one.
    ///
    /// <para>A treemap draws a folder as a frame round its files, so painting only what was recorded
    /// would leave the growth in the frames and the area that grew grey. Borrowing is the last
    /// resort, for an entry written since with nothing to say how much, and it is what puts a log
    /// that has been growing inside the colour of the folder that grew. The readout under the map
    /// states an entry's own change only, and says nothing for a borrowed one.</para>
    ///
    /// <para>Bytes rather than a <see cref="FolderChange"/>, because this is asked for every shape on
    /// every repaint and a path built for each would be paid for and thrown away.</para>
    /// </summary>
    public long? BytesAt(int node)
    {
        if (OwnBytes(node) is { } own)
        {
            return own;
        }

        for (var at = node; at != Tree.RootNode;)
        {
            at = Tree.ParentOf(at);

            if (_byNode.TryGetValue(at, out var change))
            {
                return change.Bytes;
            }
        }

        return null;
    }

    /// <summary><see cref="ChangeOf"/>'s answer in bytes, without building a path.</summary>
    private long? OwnBytes(int node)
    {
        if (_byNode.TryGetValue(node, out var change))
        {
            return change.Bytes;
        }

        return Dated(node) switch
        {
            FolderChangeKind.Created => Tree.SizeOf(node),
            FolderChangeKind.Measured => 0,
            _ => null,
        };
    }

    /// <summary>
    /// What <paramref name="node"/>'s dates settle: <see cref="FolderChangeKind.Created"/> since the
    /// earlier scan, <see cref="FolderChangeKind.Measured"/> unchanged since it, or null where they
    /// settle neither.
    ///
    /// <para>A tree's dates are whole minutes, rounded down (<see cref="ExploreTimestamp"/>), so a
    /// write up to a minute after the earlier scan can carry a minute before it. Each test is put the
    /// way that cannot mistake a change for none: made since needs its minute to start after the
    /// scan, which every instant in that minute then does, and unchanged needs the whole of its
    /// minute to end by it.</para>
    /// </summary>
    private FolderChangeKind? Dated(int node)
    {
        if (Tree.CreatedOf(node).Utc is { } created && created > SinceUtc)
        {
            return FolderChangeKind.Created;
        }

        return Tree.ModifiedOf(node).Utc is { } written && written + TimeSpan.FromMinutes(1) <= SinceUtc
            ? FolderChangeKind.Measured
            : null;
    }

    /// <summary>
    /// Compare <paramref name="tree"/>, which <paramref name="now"/> describes, with
    /// <paramref name="earlier"/>. Both are of the same volume: the caller matched them by
    /// <see cref="ScanSummary.Volume"/>.
    /// </summary>
    public static ScanGrowth Between(ScanSummary earlier, ScanSummary now, ExploreTree tree)
    {
        ArgumentNullException.ThrowIfNull(earlier);
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(tree);

        var byNode = new Dictionary<int, FolderChange>();
        var removed = new List<FolderChange>();
        var children = new Dictionary<int, Dictionary<string, int>>();
        var folders = earlier.Folders;

        // Where each recorded folder is in this tree, or -1 where it is not. Parents come first in a
        // summary, so a folder's parent is always placed before it is asked about.
        var placed = new int[folders.Count];
        placed[0] = tree.RootNode;

        for (var i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];

            if (i > 0)
            {
                var parent = placed[folder.Parent];

                if (parent < 0)
                {
                    // Its parent has gone, and the parent's removal already says so.
                    placed[i] = -1;
                    continue;
                }

                placed[i] = FolderNamed(tree, parent, folder.Name, children);

                if (placed[i] < 0)
                {
                    removed.Add(new FolderChange(
                        earlier.PathOf(i, tree.RootPath), FolderChangeKind.Removed, folder.Bytes, 0, -1));
                    continue;
                }
            }

            var node = placed[i];
            byNode[node] = new FolderChange(
                tree.PathOf(node), FolderChangeKind.Measured, folder.Bytes, tree.SizeOf(node), node);
        }

        AddNew(tree, earlier, byNode);

        var ranked = new List<FolderChange>(byNode.Count + removed.Count);
        ranked.AddRange(byNode.Values);
        ranked.AddRange(removed);
        ranked.Sort(static (left, right) =>
        {
            var bytes = right.Bytes.CompareTo(left.Bytes);
            return bytes != 0 ? bytes : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        });

        var approximation = GrowthApproximation.None;

        if (earlier.Strategy != now.Strategy)
        {
            approximation |= GrowthApproximation.RouteChanged;
        }

        if (earlier.LowerBound || now.LowerBound)
        {
            approximation |= GrowthApproximation.LowerBound;
        }

        return new ScanGrowth(tree, earlier, approximation, byNode, ranked);
    }

    /// <summary>
    /// Record every folder too large to have gone unrecorded then, that was not recorded then.
    ///
    /// <para>Only below a folder that was recorded, or below one of these. Anything else sits under a
    /// folder that was itself no larger than the earlier floor, so it was too, and saying it is new
    /// would be a guess. The walk stops at every folder no larger than the floor for the same
    /// reason, so it visits no more of the tree than there is growth to find.</para>
    /// </summary>
    private static void AddNew(ExploreTree tree, ScanSummary earlier, Dictionary<int, FolderChange> byNode)
    {
        var floor = earlier.UnrecordedAtMost;
        var pending = new Stack<int>(byNode.Keys);

        while (pending.TryPop(out var node))
        {
            foreach (var child in tree.ChildrenOf(node))
            {
                if (!tree.IsDirectory(child) || tree.IsLink(child) || byNode.ContainsKey(child)
                    || tree.SizeOf(child) <= floor)
                {
                    continue;
                }

                byNode[child] = new FolderChange(
                    tree.PathOf(child), FolderChangeKind.New, 0, tree.SizeOf(child), child);
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// The folder called <paramref name="name"/> directly inside <paramref name="parent"/>, or -1.
    ///
    /// <para>Each parent's folders are indexed once, the first time one of them is asked about, so a
    /// folder holding a hundred thousand files is read through once rather than once per recorded
    /// folder in it (G4). Where a case-sensitive folder holds two names that differ only in case, the
    /// first one the tree lists answers.</para>
    /// </summary>
    private static int FolderNamed(
        ExploreTree tree, int parent, string name, Dictionary<int, Dictionary<string, int>> children)
    {
        if (!children.TryGetValue(parent, out var named))
        {
            named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var child in tree.ChildrenOf(parent))
            {
                if (tree.IsDirectory(child) && !tree.IsLink(child))
                {
                    named.TryAdd(tree.NameOf(child), child);
                }
            }

            children[parent] = named;
        }

        return named.TryGetValue(name, out var node) ? node : -1;
    }
}
