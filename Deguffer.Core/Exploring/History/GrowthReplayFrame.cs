using System.Collections.Concurrent;
using Deguffer.Core.Exploring.Layout;
using Deguffer.Core.Scanning;

namespace Deguffer.Core.Exploring.History;

/// <summary>
/// One step of a <see cref="GrowthReplay"/>: the tree with every size part way from the earlier scan
/// to now, as a layout reads it.
///
/// <para>Each folder's children are put in order by their size at this step, largest first, when a
/// layout first asks for them, and kept: a layout visits the few thousand folders it can draw, not
/// every folder on the drive. A layout runs on a worker and the pointer is read on the UI thread, so
/// two may ask at once, and both get the same order.</para>
/// </summary>
public sealed class GrowthReplayFrame : ITrackedTree
{
    private readonly ConcurrentDictionary<int, int[]> _children = new();

    private readonly Comparison<int> _byName;

    private readonly Func<int, long> _sizeOf;

    internal GrowthReplayFrame(GrowthReplay replay, int step)
    {
        Replay = replay;
        Step = step;
        _sizeOf = SizeOf;

        _byName = replay.CompareByName;
    }

    public GrowthReplay Replay { get; }

    /// <summary>How far this is from the earlier scan, in steps of <see cref="GrowthReplay.Steps"/>.</summary>
    public int Step { get; }

    public int RootNode => Replay.Today.RootNode;

    public ExploreChildOrder ChildOrder => ExploreChildOrder.BySize;

    public long SizeOf(int node) => GrowthReplay.Between(Replay.Before(node), Replay.After(node), Step);

    public ReadOnlySpan<int> ChildrenOf(int node) => _children.GetOrAdd(node, Ordered);

    public bool IsContainer(int node) => Replay.IsContainer(node);

    public int ParentOf(int node) => Replay.ParentOf(node);

    /// <summary>
    /// What a shape of this step is labelled: its name and its size at this step. A folder removed
    /// since says so, and so does a block of what the earlier scan did not record.
    /// </summary>
    public string Label(int node)
    {
        var size = FreeSpace.Format(SizeOf(node));

        return Replay.NameOf(node) is not { } name
            ? $"{GrowthText.Unrecorded(Replay.Growth.Earlier.UnrecordedAtMost)}  {size}"
            : node >= Replay.TodayCount
                ? $"{name}, removed since  {size}"
                : $"{name}  {size}";
    }

    /// <summary>
    /// Find each node in another step of the same replay, where it has the same number, or in the
    /// tree now, which numbers every node it holds the same and holds none of the shapes only the
    /// earlier scan had.
    /// </summary>
    public Func<int, int?>? CounterpartsIn(ISizedTree later)
    {
        if (later is GrowthReplayFrame other && ReferenceEquals(other.Replay, Replay))
        {
            return static node => node;
        }

        if (ReferenceEquals(later, Replay.Today))
        {
            var today = Replay.TodayCount;
            return node => node < today ? node : null;
        }

        return null;
    }

    private int[] Ordered(int node)
    {
        var now = node < Replay.TodayCount ? Replay.Today.ChildrenOf(node) : [];
        var extras = Replay.ExtrasUnder(node);
        var children = new int[now.Length + extras.Length];

        now.CopyTo(children);
        extras.CopyTo(children.AsSpan(now.Length));
        ExploreTree.OrderBySize(children, _sizeOf, _byName);

        return children;
    }
}
