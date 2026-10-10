namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// A tree whose nodes can be found again in another tree of the same thing read later: the same
/// folder in the next snapshot of a scan, or the same process in the next reading of memory.
///
/// <para>A seam rather than a node number, because a number does not carry from one tree to the
/// next. A scan builds a new tree for each snapshot, and a reading of memory renumbers every node.
/// Each tree knows what keeps a node the same thing (a path, or a process and when it started), and
/// nothing that matches two drawings needs to.</para>
/// </summary>
public interface ITrackedTree : ISizedTree
{
    /// <summary>
    /// How to find a node of this tree in <paramref name="later"/>: a lookup from a node here to the
    /// node there that is the same thing, or null where it has none. Null for a tree of something
    /// else, which holds nothing of this one.
    ///
    /// <para>A lookup rather than one answer, so a caller asking after many nodes pays once for
    /// whatever the finding needs, such as an index of a large folder's names.</para>
    /// </summary>
    Func<int, int?>? CounterpartsIn(ISizedTree later);
}
