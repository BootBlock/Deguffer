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
/// <para>That last property is why this is what the map draws while a scan is still running,
/// whichever view the user picked. Bederson, Shneiderman and Wattenberg measured layout change
/// under updates across 100 items and put squarified treemaps at 14.82 against slice-and-dice's
/// 0.25 — a treemap redrawn from a growing tree rearranges itself continuously, and an icicle does
/// not. The fixed order the property depends on is <see cref="ExploreChildOrder.ByName"/>, which is
/// what a snapshot of a scan in progress is built with.</para>
///
/// <para>Unlike the treemap this takes its children in whatever order the tree holds them, and it
/// has to: under a size order the ones too narrow to draw are the tail, and under a name order they
/// are scattered through the middle.</para>
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
    /// Divide one node's width among its children, in the order the tree holds them, and gather
    /// whatever is too narrow to draw into one rectangle at the end of the row.
    ///
    /// <para>Which children are drawn is settled before the first one is placed, by a byte
    /// threshold rather than by measuring each rectangle as the row is built. That is what makes
    /// this work under any sibling order: a child's position is the total of the drawn children
    /// before it, so it cannot be known until the drawn set is. Under a size order the omitted
    /// children are the tail and this lays out exactly as stopping at the first narrow one did;
    /// under a name order they are scattered, and stopping at the first would throw away every
    /// large child that happened to follow it.</para>
    ///
    /// <para>Boundaries come from a running total rather than from each child's own rounded width.
    /// Rounding each in turn accumulates along the row, and the last child of a wide directory then
    /// ends either short of the edge or over it.</para>
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

        // The smallest child that can have a rectangle of its own at this width. At least one byte,
        // so an empty file or a link is always gathered rather than given a rectangle of no width
        // and then descended into.
        var floor = Math.Max(1, (long)Math.Ceiling((double)limits.MinimumTileSize * total / width));

        long placed = 0;

        foreach (var child in children)
        {
            var size = tree.SizeOf(child);

            if (size < floor)
            {
                continue;
            }

            var from = (float)((double)placed / total * width);
            placed += size;
            var to = (float)((double)placed / total * width);

            pending.Push((child, depth, x + from, to - from));
        }

        // What is left, in the space the drawn children did not take. A gap here would read as
        // empty space rather than as detail withheld, so the remainder states its own byte count —
        // and a directory whose omitted children are all empty adds up to nothing and draws
        // nothing, rather than putting a block over space no child occupies.
        if (placed < total)
        {
            var edge = (float)((double)placed / total * width);

            tiles.Add(new ExploreTile(
                ExploreTile.Aggregated, depth, total - placed,
                x + edge, depth * rowHeight, width - edge, rowHeight));
        }
    }
}
