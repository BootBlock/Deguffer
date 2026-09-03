namespace Deguffer.Core.Exploring.Layout;

/// <summary>
/// Sunburst layout: one ring per level, each node's angle proportional to its size and each level
/// partitioning its parent's angle. It is <see cref="IcicleLayout"/> in polar coordinates, and the
/// walk is deliberately the same shape.
///
/// <para>It is here because it is what this category of tool ships — DaisyDisk, Filelight, Baobab
/// and Scanner all draw one, and WinDirStat 2.8 added one — and because Stasko, Catrambone, Guzdial
/// and McDonald (<i>International Journal of Human-Computer Studies</i> 53(5), 2000, pp. 663-694)
/// measured it beating a treemap on correctness, "particularly on initial use", with participants
/// preferring it.</para>
///
/// <para>It is not the default, and that study is the reason to be careful rather than the reason
/// to promote it: the treemap it beat was slice-and-dice with no containment borders, which the
/// authors say themselves, and it is not what <see cref="TreemapLayout"/> draws. Two costs are real
/// and unavoidable. Sector area grows with the square of the radius, so at a constant ring width an
/// outer ring gives more area for the same proportion and deeper items look bigger than they are.
/// And it is the worst of the three under a running scan: one sibling growing rotates every sibling
/// after it, and every descendant with it.</para>
///
/// <para>What it is best at is being pointed at. A radius gives the ring by one division and an
/// angle gives the sector by one binary search, which is the cheapest hit test of the three
/// layouts — see <see cref="SectorHitTest"/>.</para>
/// </summary>
public static class SunburstLayout
{
    /// <summary>
    /// Lay <paramref name="root"/>'s subtree out as rings about the middle of a canvas of
    /// <paramref name="width"/> by <paramref name="height"/>.
    ///
    /// <para>How many rings is decided first and how wide they are follows from it, which is the
    /// opposite of the icicle. An icicle grows downwards and can be descended into, so a fixed row
    /// height and a canvas that runs out is the honest arrangement. A disc is bounded in every
    /// direction at once, so fixing the ring width instead would make the picture deeper on a
    /// larger window and leave the first ring — the one carrying the largest and most useful
    /// wedges — the same handful of pixels across however much room there was.</para>
    ///
    /// <para>So <see cref="LayoutLimits.MaximumDepth"/> caps the levels here as it does on the
    /// treemap, for the same reason: past six the wedges are slivers of slivers, and the cost is
    /// paid on every repaint. <see cref="LayoutLimits.RowHeight"/> is the floor under a ring's
    /// width, so a small window shows fewer levels rather than unreadable ones.</para>
    /// </summary>
    public static Sunburst Compute(ExploreTree tree, int root, float width, float height, LayoutLimits limits)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limits.RowHeight);

        var centreX = width / 2;
        var centreY = height / 2;
        var radius = Math.Min(width, height) / 2;

        // One ring for the node being drawn, and one for each level below it.
        var rings = Math.Min(limits.MaximumDepth + 1, (int)(radius / limits.RowHeight));

        if (rings < 1 || tree.SizeOf(root) <= 0)
        {
            return new Sunburst([], centreX, centreY, limits.RowHeight, 0);
        }

        var ringWidth = radius / rings;

        var sectors = new List<ExploreSector>();

        // Breadth-first, so the sectors come out ordered by ring and then by angle within a ring.
        // That order is not incidental: it is what lets the hit test index a ring by one division
        // and then binary-search it. A depth-first walk would produce the same picture and an
        // index that has to be sorted before it can be used.
        var pending = new Queue<(int Node, int Depth, long Bytes, double Start, double Sweep)>();
        pending.Enqueue((root, 0, tree.SizeOf(root), 0, Math.Tau));

        while (pending.TryDequeue(out var frame))
        {
            var inner = frame.Depth * ringWidth;

            sectors.Add(new ExploreSector(
                frame.Node,
                frame.Depth,
                frame.Bytes,
                inner,
                inner + ringWidth,
                (float)frame.Start,
                (float)frame.Sweep));

            if (frame.Node == ExploreSector.Aggregated
                || frame.Depth + 1 >= rings
                || !tree.IsDirectory(frame.Node))
            {
                continue;
            }

            Partition(tree, frame.Node, frame.Depth + 1, frame.Start, frame.Sweep, ringWidth, limits, pending);
        }

        return new Sunburst(sectors, centreX, centreY, ringWidth, radius);
    }

    /// <summary>
    /// Divide one node's angle among its children, largest first, and stop at the first child too
    /// narrow to draw.
    ///
    /// <para>Boundaries come from a running total for the reason
    /// <see cref="IcicleLayout"/>'s do: rounding each child's own sweep in turn accumulates round
    /// the circle, and the last child of a wide directory then ends short of its parent's edge or
    /// past it.</para>
    ///
    /// <para>What is too narrow is measured at the ring's <em>inner</em> edge, which is the narrow
    /// end of the sector. A wedge wide enough to see at its outer edge and a pixel across at its
    /// inner one is a wedge the user cannot reliably point at, and
    /// <see cref="LayoutLimits.MinimumTileSize"/> is as much about that as about drawing.</para>
    /// </summary>
    private static void Partition(
        ExploreTree tree,
        int parent,
        int depth,
        double start,
        double sweep,
        float ringWidth,
        LayoutLimits limits,
        Queue<(int Node, int Depth, long Bytes, double Start, double Sweep)> pending)
    {
        var children = tree.ChildrenOf(parent);
        var total = tree.SizeOf(parent);

        if (children.Length == 0 || total <= 0)
        {
            return;
        }

        var inner = depth * ringWidth;
        double placed = 0;

        for (var i = 0; i < children.Length; i++)
        {
            var from = placed / total * sweep;
            placed += tree.SizeOf(children[i]);
            var to = placed / total * sweep;

            if ((to - from) * inner >= limits.MinimumTileSize)
            {
                pending.Enqueue((children[i], depth, tree.SizeOf(children[i]), start + from, to - from));
                continue;
            }

            // Sorted descending, so nothing after this can be wider. One wedge for the rest,
            // carrying what it stands for — a gap here would read as space nothing is using rather
            // than as detail withheld. Enqueued rather than emitted directly so it takes its place
            // in the ring's angular order, which the hit test depends on.
            var omitted = Omitted(tree, children[i..]);

            if (omitted > 0)
            {
                pending.Enqueue((ExploreSector.Aggregated, depth, omitted, start + from, sweep - from));
            }

            return;
        }
    }

    private static long Omitted(ExploreTree tree, ReadOnlySpan<int> nodes)
    {
        long bytes = 0;

        foreach (var node in nodes)
        {
            bytes += tree.SizeOf(node);
        }

        return bytes;
    }
}
