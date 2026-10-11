using Deguffer.Core.Exploring.Layout;

namespace Deguffer.Core.Exploring.Rendering;

/// <summary>
/// How the shapes of a drawing are read in the tree the page holds now: the same tree, read as it
/// is, or an older tree of the same place, read by finding each shape's node again in the newer one
/// (<see cref="ITrackedTree.CounterpartsIn"/>).
///
/// <para>A scan publishes a snapshot every so often, and the drawing on screen is of the last one
/// until the next one's drawing arrives. A node is a number in the tree it was drawn from, and the
/// page reads every answer in the tree it handed over last, so a shape of the older drawing is
/// answered with the node that is the same thing in the newer tree. One with no counterpart there,
/// such as a folder removed since, answers nothing (§7.1).</para>
///
/// <para>The way back too, so what the page names in its tree is outlined where the drawing on
/// screen draws it: the outline marks the shape a click there picks.</para>
/// </summary>
public sealed class MapReading
{
    /// <summary>Each node of the drawing's tree in the page's, or null where the two are one tree.</summary>
    private readonly Func<int, int?>? _toCurrent;

    /// <summary>Each node of the page's tree in the drawing's, or null where the two are one tree.</summary>
    private readonly Func<int, int?>? _toDrawn;

    /// <summary>What <see cref="InDrawing"/> answers with, kept and rewritten, as it is asked at the pointer's rate (G5).</summary>
    private readonly HashSet<int> _inDrawing = [];

    private MapReading(ISizedTree drawn, ISizedTree current, Func<int, int?>? toCurrent, Func<int, int?>? toDrawn)
    {
        Drawn = drawn;
        Current = current;
        _toCurrent = toCurrent;
        _toDrawn = toDrawn;
    }

    /// <summary>The tree the drawing was drawn from.</summary>
    public ISizedTree Drawn { get; }

    /// <summary>The tree the page holds now, which every answer is read in.</summary>
    public ISizedTree Current { get; }

    /// <summary>
    /// How a drawing of <paramref name="drawn"/> is read in <paramref name="current"/>, or null where
    /// it cannot be: a tree of something else, or no tree on the page at all.
    /// </summary>
    public static MapReading? Of(ISizedTree drawn, ISizedTree? current)
    {
        ArgumentNullException.ThrowIfNull(drawn);

        if (current is null)
        {
            return null;
        }

        if (ReferenceEquals(drawn, current))
        {
            return new MapReading(drawn, current, null, null);
        }

        return drawn is ITrackedTree earlier
            && current is ITrackedTree later
            && earlier.CounterpartsIn(later) is { } toCurrent
            && later.CounterpartsIn(earlier) is { } toDrawn
                ? new MapReading(drawn, current, toCurrent, toDrawn)
                : null;
    }

    /// <summary>
    /// <paramref name="hit"/>, a shape of the drawing, read in the page's tree: its node found again
    /// there, or null where it has no counterpart. A block that stands for something other than a
    /// node is read as it is.
    /// </summary>
    public ExploreHit? Read(ExploreHit hit) =>
        _toCurrent is null || !hit.IsNode ? hit
        : _toCurrent(hit.Node) is { } node ? hit with { Node = node }
        : null;

    /// <summary>
    /// <paramref name="nodes"/> of the page's tree as the drawing numbers them, leaving out any it
    /// does not hold. The set answered with is rewritten by the next call.
    /// </summary>
    public IReadOnlySet<int> InDrawing(IReadOnlySet<int> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        if (_toDrawn is null)
        {
            return nodes;
        }

        _inDrawing.Clear();

        foreach (var node in nodes)
        {
            if (_toDrawn(node) is { } drawn)
            {
                _inDrawing.Add(drawn);
            }
        }

        return _inDrawing;
    }
}
