namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Icicle layout: one fixed-height row per level, each node's width proportional to its size and
/// each level partitioning its parent's width. A flame graph is the same thing drawn upwards.
///
/// <para>Three properties earn it a place beside the treemap rather than behind it. Area is exactly
/// proportional at every depth, with none of the treemap's aspect-ratio pathology and none of a
/// sunburst's geometric exaggeration of the outer rings. Its rectangles are long and short, which
/// is the shape text wants — several levels can be labelled at once, where a treemap labels one
/// level and a key. And it is the only space-filling layout that stays still under growing data:
/// with the sibling order fixed, a child that grows only widens, so nothing jumps to another
/// row.</para>
///
/// <para>That last property is why it is the right thing to draw while a scan is still running.
/// Bederson, Shneiderman and Wattenberg measured layout change under updates across 100 items and
/// put squarified treemaps at 14.82 against slice-and-dice's 0.25 — a treemap redrawn from a
/// growing tree rearranges itself continuously, and an icicle does not.</para>
/// </summary>
public static class IcicleLayout
{
    /// <summary>
    /// Lay <paramref name="root"/>'s subtree out as rows of <see cref="LayoutLimits.RowHeight"/>.
    ///
    /// <para>Rows stop at whichever comes first: the depth limit, or the bottom of the canvas. The
    /// second is not a failure to draw everything — an icicle over a deep tree is meant to be
    /// scrolled or descended into, and inventing thinner rows to fit would trade the one thing the
    /// layout is good at for the one thing it is not.</para>
    /// </summary>
    public static IReadOnlyList<ExploreTile> Compute(
        ExploreTree tree,
        int root,
        float width,
        float height,
        LayoutLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.RowHeight);

        var rowHeight = limits.RowHeight;
        var tiles = new List<ExploreTile>();

        if (width <= 0 || height < rowHeight || tree.SizeOf(root) <= 0)
        {
            return tiles;
        }

        // The canvas is the limit here, and <see cref="LayoutLimits.MaximumDepth"/> deliberately is
        // not. That cap exists for the treemap, where each extra level is a frame inside a frame
        // costing depth for no width; an icicle spends a whole row per level and simply runs out of
        // panel. Capping at six on a canvas with room for sixteen leaves the lower two thirds blank
        // and throws away the one thing this layout is better at than a treemap — showing, and
        // labelling, several levels at once.
        var deepest = (int)(height / rowHeight) - 1;
        var pending = new Stack<(int Node, int Depth, float X, float Width)>();
        pending.Push((root, 0, 0, width));

        while (pending.TryPop(out var frame))
        {
            tiles.Add(new ExploreTile(
                frame.Node,
                frame.Depth,
                tree.SizeOf(frame.Node),
                frame.X,
                frame.Depth * rowHeight,
                frame.Width,
                rowHeight));

            if (frame.Depth >= deepest || !tree.IsDirectory(frame.Node))
            {
                continue;
            }

            Partition(tree, frame.Node, frame.Depth + 1, frame.X, frame.Width, rowHeight, limits, tiles, pending);
        }

        return tiles;
    }

    /// <summary>
    /// Divide one node's width among its children, largest first, and stop at the first child too
    /// narrow to draw.
    ///
    /// <para>Boundaries come from a running total rather than from each child's own rounded width.
    /// Rounding each in turn accumulates along the row, and the last child of a wide directory then
    /// ends either short of the edge or over it. A child too narrow to receive a rectangle still
    /// advances the running total, so the children after it stay in the right place.</para>
    /// </summary>
    private static void Partition(
        ExploreTree tree,
        int parent,
        int depth,
        float x,
        float width,
        float rowHeight,
        LayoutLimits limits,
        List<ExploreTile> tiles,
        Stack<(int Node, int Depth, float X, float Width)> pending)
    {
        var children = tree.ChildrenOf(parent);
        var total = tree.SizeOf(parent);

        if (children.Length == 0 || total <= 0)
        {
            return;
        }

        double placed = 0;

        for (var i = 0; i < children.Length; i++)
        {
            var from = (float)(placed / total * width);
            placed += tree.SizeOf(children[i]);
            var to = (float)(placed / total * width);

            if (to - from >= limits.MinimumTileSize)
            {
                pending.Push((children[i], depth, x + from, to - from));
                continue;
            }

            // Sorted descending, so nothing after this can be wider. One rectangle for the rest,
            // carrying what it stands for — a gap here would read as empty space rather than as
            // detail withheld.
            Aggregate(tree, children[i..], depth, x + from, width - from, rowHeight, tiles);
            return;
        }
    }

    private static void Aggregate(
        ExploreTree tree,
        ReadOnlySpan<int> omitted,
        int depth,
        float x,
        float width,
        float rowHeight,
        List<ExploreTile> tiles)
    {
        long bytes = 0;

        foreach (var node in omitted)
        {
            bytes += tree.SizeOf(node);
        }

        // Nothing to stand for, so nothing to draw — a block over space its children do not occupy
        // would invent an occupant.
        if (bytes == 0)
        {
            return;
        }

        tiles.Add(new ExploreTile(
            ExploreTile.Aggregated, depth, bytes, x, depth * rowHeight, width, rowHeight));
    }
}
