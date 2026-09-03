namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Squarified treemap layout: Bruls, Huizing and van Wijk, <i>Squarified Treemaps</i>, Proc. Joint
/// Eurographics/IEEE TCVG Symposium on Visualization, 2000, pp. 33–42.
///
/// <para>Squarified rather than the slice-and-dice original because slice-and-dice reaches a 304:1
/// aspect ratio across 100 items, at which point a thin sliver and a fat block of equal area do not
/// look equal at all.</para>
///
/// <para>Note what that argument is <em>not</em>. The original paper justifies squares on the
/// grounds that "comparison of the size of rectangles is easier when their aspect ratios are
/// similar", and Kong, Heer and Agrawala (IEEE TVCG 16(6), 2010) measured that and found the
/// opposite: two squares are compared about as badly as two 9:2 slivers, and accuracy is best
/// around 3:2 and when the two rectangles differ from each other. Their conclusion is that
/// squarification helps because it avoids the extremes, and partly because it never quite achieves
/// its own stated objective. So this is here for the 304:1 case, not for the squares.</para>
///
/// <para>The cost is stated in the paper and is real — "the relative ordering of siblings is lost"
/// — which is why this is the layout for a finished scan rather than a running one.</para>
///
/// <para>The layout is iterative throughout. A volume's tree can be arbitrarily deep, and a
/// recursive layout over a real <c>node_modules</c> is a stack overflow in a repaint handler.</para>
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// Lay <paramref name="root"/>'s subtree out across a canvas of
    /// <paramref name="width"/> by <paramref name="height"/>.
    ///
    /// <para>Returns the rectangles in the order they should be painted: a parent before its
    /// children, so a view drawing them in sequence gets the nesting right without sorting.</para>
    ///
    /// <para>Throws for a tree whose children are not ordered by size. See
    /// <see cref="ExploreChildOrder"/>: a scan still running publishes a tree ordered by name, and
    /// this would lay one out into rectangles that look like a treemap and are not one.</para>
    /// </summary>
    public static IReadOnlyList<ExploreTile> Compute(
        ExploreTree tree,
        int root,
        float width,
        float height,
        LayoutLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tree);

        // Squarification is defined over a decreasing sequence, and both the row packing and the
        // aggregate below read the children in the order the tree holds them. Given any other order
        // this would still produce rectangles that tile the canvas, and every one of them would be
        // in the wrong place — so it refuses rather than draws.
        if (tree.ChildOrder != ExploreChildOrder.BySize)
        {
            throw new ArgumentException(
                $"A squarified treemap needs children ordered by size, not by {tree.ChildOrder}.",
                nameof(tree));
        }

        var tiles = new List<ExploreTile>();

        if (width <= 0 || height <= 0 || tree.SizeOf(root) <= 0)
        {
            return tiles;
        }

        var pending = new Stack<(int Node, int Depth, float X, float Y, float Width, float Height)>();
        pending.Push((root, 0, 0, 0, width, height));

        while (pending.TryPop(out var frame))
        {
            tiles.Add(new ExploreTile(
                frame.Node, frame.Depth, tree.SizeOf(frame.Node),
                frame.X, frame.Y, frame.Width, frame.Height));

            if (frame.Depth >= limits.MaximumDepth || !tree.IsDirectory(frame.Node))
            {
                continue;
            }

            // The frame is what makes nesting visible without shading every level, and it is only
            // affordable where there is room for it. A rectangle too small to inset is drawn as one
            // block, which is the honest rendering of "there is more in here than fits".
            var inset = Inset(frame.Width, frame.Height, limits);
            var area = new Rectangle(
                frame.X + inset, frame.Y + inset,
                frame.Width - (inset * 2), frame.Height - (inset * 2));

            if (area.Width < limits.MinimumTileSize || area.Height < limits.MinimumTileSize)
            {
                continue;
            }

            Place(tree, frame.Node, frame.Depth + 1, area, limits, tiles, pending);
        }

        return tiles;
    }

    /// <summary>
    /// Fit one node's children into <paramref name="area"/>, row by row, largest first.
    ///
    /// <para>The children arrive already ordered by size, which the algorithm requires — the paper
    /// is explicit that squarification only works on a decreasing sequence. The tree settles that
    /// order once at build time rather than per repaint, and <see cref="Compute"/> refuses a tree
    /// that settled on a different one.</para>
    /// </summary>
    private static void Place(
        ExploreTree tree,
        int parent,
        int depth,
        Rectangle area,
        LayoutLimits limits,
        List<ExploreTile> tiles,
        Stack<(int Node, int Depth, float X, float Y, float Width, float Height)> pending)
    {
        var children = tree.ChildrenOf(parent);
        var total = tree.SizeOf(parent);

        if (children.Length == 0 || total <= 0)
        {
            return;
        }

        // Bytes per square pixel, fixed for the whole of this node's area. Every child is measured
        // against it, including the ones that turn out to be too small to draw — which is what lets
        // the aggregate below state a byte count rather than a leftover shape.
        var scale = (double)area.Width * area.Height / total;

        var remaining = area;
        var index = 0;

        while (index < children.Length && remaining.Width > 0 && remaining.Height > 0)
        {
            var side = Math.Min(remaining.Width, remaining.Height);
            var row = TakeRow(tree, children, index, side, scale, limits.MinimumTileSize);

            // Two ways to reach the end of what can be drawn, and both take the same exit: the next
            // child has no area at all, or the row it would form is thinner than the floor. Anything
            // still unplaced goes to the aggregate, because a blank corner of a treemap reads as
            // free space rather than as detail withheld.
            //
            // What is *not* here is a check on each rectangle as it is placed. That is TakeRow's
            // job now: it refuses to admit a child the row cannot draw, so by this point everything
            // in the row fits. Checking again down in LayRow was the original bug — a child failing
            // there was skipped where it stood while `index` advanced past it, so its bytes were
            // drawn nowhere and counted in no aggregate.
            if (row.Count == 0 || (float)(row.Area / side) < limits.MinimumTileSize)
            {
                Aggregate(tree, children[index..], remaining, depth, tiles);
                return;
            }

            var thickness = (float)(row.Area / side);

            LayRow(tree, children.Slice(index, row.Count), row.Area, thickness, ref remaining, depth, tiles, pending);
            index += row.Count;
        }
    }

    /// <summary>
    /// How many of the remaining children belong in the next row: keep adding while the worst
    /// aspect ratio in the row improves, and stop at the first child that makes it worse.
    ///
    /// <para>This is the paper's <c>squarify</c> recurrence, flattened. <c>worst</c> is
    /// <c>max(w²r⁺/s², s²/(w²r⁻))</c> over the row's areas, with <c>r⁺</c> and <c>r⁻</c> the largest
    /// and smallest in it.</para>
    ///
    /// <para>With one addition the paper has no reason to make: a child is admitted only while every
    /// member of the row stays wide enough to draw. A member's extent along the row is
    /// <c>area × side / sum</c>, so admitting one more child shrinks every extent already in it —
    /// which means an otherwise healthy row can end up containing a child too narrow for the floor.
    /// Left to <see cref="LayRow"/> to skip, that child was drawn nowhere and counted in no
    /// aggregate, and its bytes left the picture silently. Closing the row early instead sends it,
    /// and everything after it, to the aggregate where it belongs.</para>
    /// </summary>
    private static (int Count, double Area) TakeRow(
        ExploreTree tree,
        ReadOnlySpan<int> children,
        int index,
        float side,
        double scale,
        float minimumTileSize)
    {
        double sum = 0;
        double smallest = 0;
        double largest = 0;
        var best = double.MaxValue;
        var count = 0;

        for (var i = index; i < children.Length; i++)
        {
            var area = tree.SizeOf(children[i]) * scale;

            // A zero-byte file has no area to give a rectangle. Admitting it would divide by zero in
            // `worst` and make every ratio infinite, so the row would close after one child for the
            // rest of the directory.
            if (area <= 0)
            {
                break;
            }

            var candidateSum = sum + area;
            var candidateSmallest = count == 0 ? area : Math.Min(smallest, area);
            var candidateLargest = count == 0 ? area : Math.Max(largest, area);
            var worst = Worst(candidateSum, candidateSmallest, candidateLargest, side);

            if (count > 0 && worst > best)
            {
                break;
            }

            // The smallest member decides, and it is this one: the children arrive in decreasing
            // order. Checked before the row is committed rather than after, because by then the
            // only remedies are drawing a rectangle below the floor or dropping a real child.
            if (count > 0 && candidateSmallest * side / candidateSum < minimumTileSize)
            {
                break;
            }

            (sum, smallest, largest, best) = (candidateSum, candidateSmallest, candidateLargest, worst);
            count++;
        }

        return (count, sum);
    }

    private static double Worst(double sum, double smallest, double largest, float side)
    {
        var squared = (double)side * side;
        return Math.Max(squared * largest / (sum * sum), sum * sum / (squared * smallest));
    }

    /// <summary>
    /// Place one row's children across the short side of what is left, and shrink the remainder.
    ///
    /// <para>Positions come from a running total rather than from each child's own rounded size.
    /// Rounding each in turn accumulates the error along the row, so the last child in a long row
    /// ends up visibly the wrong size or short of the edge.</para>
    /// </summary>
    private static void LayRow(
        ExploreTree tree,
        ReadOnlySpan<int> row,
        double rowArea,
        float thickness,
        ref Rectangle remaining,
        int depth,
        List<ExploreTile> tiles,
        Stack<(int Node, int Depth, float X, float Y, float Width, float Height)> pending)
    {
        var vertical = remaining.Width >= remaining.Height;
        var side = vertical ? remaining.Height : remaining.Width;

        // The row's own bytes-per-pixel. Identical to the parent's, and derived from the row rather
        // than passed in so this reads as the one arithmetic it is: bytes in, fraction of the side
        // out.
        var scale = rowArea / SumOf(tree, row);

        double placed = 0;

        foreach (var child in row)
        {
            var area = tree.SizeOf(child) * scale;
            var from = (float)(placed / rowArea * side);
            placed += area;
            var to = (float)(placed / rowArea * side);

            var tile = vertical
                ? new Rectangle(remaining.X, remaining.Y + from, thickness, to - from)
                : new Rectangle(remaining.X + from, remaining.Y, to - from, thickness);

            // Every child the row admitted is drawn. There is no size check here on purpose:
            // TakeRow already refused to admit one that would not fit, so a check here could only
            // fire on a rounding hair — and skipping a child at this point drops it from the
            // picture entirely, because the aggregate that should have stood for it was decided
            // one frame up.
            pending.Push((child, depth, tile.X, tile.Y, tile.Width, tile.Height));
        }

        remaining = vertical
            ? new Rectangle(remaining.X + thickness, remaining.Y, remaining.Width - thickness, remaining.Height)
            : new Rectangle(remaining.X, remaining.Y + thickness, remaining.Width, remaining.Height - thickness);
    }

    private static double SumOf(ExploreTree tree, ReadOnlySpan<int> nodes)
    {
        double sum = 0;

        foreach (var node in nodes)
        {
            sum += tree.SizeOf(node);
        }

        return sum;
    }

    /// <summary>
    /// One rectangle for everything too small to draw, carrying what it stands for.
    ///
    /// <para>The alternative is to leave the space blank, and a blank corner of a treemap reads as
    /// free space rather than as detail withheld. DaisyDisk calls its equivalent "smaller objects";
    /// WinDirStat's sunburst calls it a muted residual sector. Both exist because the omission has
    /// to be visible.</para>
    /// </summary>
    private static void Aggregate(
        ExploreTree tree,
        ReadOnlySpan<int> omitted,
        Rectangle area,
        int depth,
        List<ExploreTile> tiles)
    {
        long bytes = 0;

        foreach (var node in omitted)
        {
            bytes += tree.SizeOf(node);
        }

        // Nothing to stand for. A directory whose remaining children are all empty would otherwise
        // get a grey block over the space they do not occupy, which invents an occupant.
        if (bytes == 0)
        {
            return;
        }

        tiles.Add(new ExploreTile(
            ExploreTile.Aggregated, depth, bytes, area.X, area.Y, area.Width, area.Height));
    }

    /// <summary>
    /// The border a parent keeps around its children, or zero where the rectangle has no room to
    /// spare. Two pixels of frame inside a six-pixel tile leaves nothing to draw the children in.
    ///
    /// <para>The frame is not free, and the cost is a real distortion rather than lost pixels.
    /// Barlow and Neville (Proc. IEEE InfoVis 2001) put it exactly: with an offset, a rectangle's
    /// area is proportional to its size <em>relative to all its ancestors</em>, so two equal nodes
    /// at different depths get different areas. One pixel per level keeps that within a rounding
    /// error at the sizes drawn here, and it cannot be removed without also removing the only cue
    /// that says where one directory ends and the next begins. Lü and Fogarty's two-stage layout
    /// (Graphics Interface 2008) is the correction, and it is a different algorithm.</para>
    /// </summary>
    private static float Inset(float width, float height, LayoutLimits limits) =>
        Math.Min(width, height) >= limits.MinimumTileSize * 4 ? 1f : 0f;

    private readonly record struct Rectangle(float X, float Y, float Width, float Height);
}
