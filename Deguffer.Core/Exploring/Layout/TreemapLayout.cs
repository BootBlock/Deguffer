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
///
/// <para>Over 500 lines because the row packing, the aggregate and the blocks beside the volume are
/// one pass over one rectangle type, and most of the length is the reasoning behind each rule. Which
/// limits a space is held to at a zoom is <see cref="TreemapDetail"/>'s (G1).</para>
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
    ///
    /// <para>An array rather than the list it is built in, because every consumer indexes it and
    /// <see cref="Rendering.TileRasteriser"/> does so once per rectangle per band of every repaint.
    /// That is a couple of million reads a frame on a full canvas, and an interface indexer
    /// returning a 32-byte struct is not free at that count (G4).</para>
    /// </summary>
    /// <param name="volume">
    /// The volume <paramref name="root"/> is the whole of, whose free space and whose unaccounted use
    /// are drawn as blocks of their own beside it, in proportion to it; or
    /// <see cref="VolumeSpace.None"/> for neither. The caller decides whether they belong in the
    /// picture at all: they do beside the whole of a volume, and nowhere else.
    /// </param>
    /// <param name="viewport">
    /// The part of the picture the canvas shows. The rectangles come back in the canvas's own
    /// coordinates, running off its edges where they do. See <see cref="Visible"/> and
    /// <see cref="TreemapDetail"/>.
    /// </param>
    public static IReadOnlyList<ExploreTile> Compute(
        ISizedTree tree,
        int root,
        float width,
        float height,
        LayoutLimits limits,
        VolumeSpace volume = default,
        MapViewport viewport = default)
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
            return tiles.ToArray();
        }

        var canvas = new Rectangle(0, 0, width, height);

        // The whole picture at this zoom, placed so the part the viewport names lands on the canvas.
        var picture = new Rectangle(
            -viewport.Left * width * viewport.Zoom,
            -viewport.Top * height * viewport.Zoom,
            width * viewport.Zoom,
            height * viewport.Zoom);

        var used = BesideTheVolume(tree.SizeOf(root), volume, picture, canvas, limits, tiles);
        var detail = new TreemapDetail(limits, viewport);

        var pending = new Stack<(int Node, int Depth, Rectangle Frame, int Level)>();
        pending.Push((root, 0, used, 0));

        while (pending.TryPop(out var next))
        {
            var (node, depth, frame, from) = next;

            if (!Visible(frame, canvas))
            {
                continue;
            }

            var level = detail.OpeningLevel(tree, node, depth, frame.Width, frame.Height, from);

            // The frame is what makes nesting visible, and it is only affordable where there is room
            // for it. A rectangle too small to frame is drawn as one block, which is the honest
            // rendering of "there is more in here than fits".
            var (header, gap) = level >= 0 ? detail.FrameOf(frame.Width, frame.Height, level) : (0f, 0f);

            tiles.Add(Tile(node, depth, tree.SizeOf(node), frame, header));

            if (level < 0)
            {
                continue;
            }

            var top = header > 0 ? header : gap;
            var area = new Rectangle(
                frame.X + gap, frame.Y + top,
                frame.Width - (gap * 2), frame.Height - top - gap);

            Place(tree, node, depth + 1, area, level, detail, canvas, tiles, pending);
        }

        return tiles.ToArray();
    }

    /// <summary>
    /// Whether any of <paramref name="frame"/> is on <paramref name="canvas"/>.
    ///
    /// <para>What keeps a zoomed layout the size of the canvas rather than of the picture: nothing off
    /// it is drawn or opened. A shape partly on it comes back whole, because its shading and its
    /// outline are measured across all of it.</para>
    /// </summary>
    private static bool Visible(Rectangle frame, Rectangle canvas) =>
        frame.X < canvas.X + canvas.Width
        && frame.X + frame.Width > canvas.X
        && frame.Y < canvas.Y + canvas.Height
        && frame.Y + frame.Height > canvas.Y;

    /// <summary>
    /// One rectangle, in single precision once the arithmetic that placed it is done.
    ///
    /// <para>The layout runs in double precision because a zoomed picture's offsets are hundreds of
    /// thousands of pixels long, and summed in single precision they are visibly wrong.</para>
    /// </summary>
    private static ExploreTile Tile(int node, int depth, long bytes, Rectangle frame, float header = 0) => new(
        node, depth, bytes,
        (float)frame.X, (float)frame.Y, (float)frame.Width, (float)frame.Height, header);

    /// <summary>
    /// Fit one node's children into <paramref name="area"/>, row by row, largest first.
    ///
    /// <para>The children arrive already ordered by size, which the algorithm requires — the paper
    /// is explicit that squarification only works on a decreasing sequence. The tree settles that
    /// order once at build time rather than per repaint, and <see cref="Compute"/> refuses a tree
    /// that settled on a different one.</para>
    ///
    /// <para>The rows are laid at the <paramref name="level"/> the parent opened at. Where the rest
    /// will not fit there, the rows go on at the next level of detail that fits, in the space the
    /// aggregate would have taken. See <see cref="TreemapDetail"/>.</para>
    /// </summary>
    private static void Place(
        ISizedTree tree,
        int parent,
        int depth,
        Rectangle area,
        int level,
        TreemapDetail detail,
        Rectangle canvas,
        List<ExploreTile> tiles,
        Stack<(int Node, int Depth, Rectangle Frame, int Level)> pending)
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
        var scale = area.Width * area.Height / total;

        var remaining = area;
        var index = 0;

        while (index < children.Length && remaining.Width > 0 && remaining.Height > 0)
        {
            // Every row still to come, and the aggregate after them, is cut from what is left, so
            // none of it can show once that is off the canvas.
            if (!Visible(remaining, canvas))
            {
                return;
            }

            var floor = detail[level].MinimumTileSize;
            var side = Math.Min(remaining.Width, remaining.Height);
            var row = TakeRow(tree, children, index, side, scale, floor);

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
            //
            // At a finer level of detail the rest may fit where it does not at this one.
            if (row.Count == 0 || row.Area / side < floor)
            {
                if (level + 1 < detail.Count)
                {
                    level++;
                    continue;
                }

                Aggregate(tree, children[index..], remaining, canvas, depth, tiles);
                return;
            }

            var thickness = row.Area / side;

            LayRow(tree, children.Slice(index, row.Count), row.Area, thickness, ref remaining, depth, level, pending);
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
        ISizedTree tree,
        ReadOnlySpan<int> children,
        int index,
        double side,
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

    private static double Worst(double sum, double smallest, double largest, double side)
    {
        var squared = side * side;
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
        ISizedTree tree,
        ReadOnlySpan<int> row,
        double rowArea,
        double thickness,
        ref Rectangle remaining,
        int depth,
        int level,
        Stack<(int Node, int Depth, Rectangle Frame, int Level)> pending)
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
            var from = placed / rowArea * side;
            placed += area;
            var to = placed / rowArea * side;

            var tile = vertical
                ? new Rectangle(remaining.X, remaining.Y + from, thickness, to - from)
                : new Rectangle(remaining.X + from, remaining.Y, to - from, thickness);

            // Every child the row admitted is drawn. There is no size check here on purpose:
            // TakeRow already refused to admit one that would not fit, so a check here could only
            // fire on a rounding hair — and skipping a child at this point drops it from the
            // picture entirely, because the aggregate that should have stood for it was decided
            // one frame up.
            pending.Push((child, depth, tile, level));
        }

        remaining = vertical
            ? new Rectangle(remaining.X + thickness, remaining.Y, remaining.Width - thickness, remaining.Height)
            : new Rectangle(remaining.X, remaining.Y + thickness, remaining.Width, remaining.Height - thickness);
    }

    private static double SumOf(ISizedTree tree, ReadOnlySpan<int> nodes)
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
        ISizedTree tree,
        ReadOnlySpan<int> omitted,
        Rectangle area,
        Rectangle canvas,
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
        if (bytes == 0 || !Visible(area, canvas))
        {
            return;
        }

        tiles.Add(Tile(ExploreTile.Aggregated, depth, bytes, area));
    }

    /// <summary>
    /// Share the whole picture between the root, the volume's use the scan did not account for, and
    /// its free space, in proportion to their bytes, and add whichever of the two blocks' rectangles
    /// reach the canvas. Returns what is left for the root.
    ///
    /// <para>Largest first, each taking a slab across the longer side of what is left, which is the
    /// squarified row with one member: every part keeps the full length of the shorter side, so none
    /// becomes a sliver until another dwarfs it. A block whose slab would be thinner than the smallest
    /// tile is not drawn and the others share its room. A block too thin to point at says nothing,
    /// and the drive picker states both figures anyway.</para>
    /// </summary>
    private static Rectangle BesideTheVolume(
        long usedBytes,
        VolumeSpace volume,
        Rectangle picture,
        Rectangle canvas,
        LayoutLimits limits,
        List<ExploreTile> tiles)
    {
        var remaining = picture;

        Span<(int Node, long Bytes)> parts =
        [
            (Root, usedBytes),
            (ExploreTile.Unaccounted, volume.UnaccountedBytes(usedBytes)),
            (ExploreTile.FreeSpace, volume.FreeBytes),
        ];

        double whole = usedBytes + parts[1].Bytes + parts[2].Bytes;
        // Measured against the canvas rather than the magnified picture, so what is dropped is the
        // same at every zoom. Deciding it at each zoom would let a block appear partway into one and
        // take its share from the root, moving every shape in the picture a few pixels times the zoom.
        var longer = Math.Max(canvas.Width, canvas.Height);

        // Dropped before anything is laid, so the parts that are drawn share the whole canvas between
        // them rather than leaving the dropped one's room empty.
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Bytes > 0 && longer * (parts[i].Bytes / whole) < limits.MinimumTileSize)
            {
                parts[i].Bytes = 0;
            }
        }

        parts.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        double left = 0;

        foreach (var part in parts)
        {
            left += part.Bytes;
        }

        var root = remaining;

        foreach (var (node, bytes) in parts)
        {
            if (bytes <= 0)
            {
                continue;
            }

            // The last part takes all that is left, so rounding cannot leave a hairline of canvas
            // belonging to nothing.
            var share = bytes >= left ? 1 : bytes / left;
            left -= bytes;

            Rectangle slab;

            if (remaining.Width >= remaining.Height)
            {
                slab = remaining with { Width = remaining.Width * share };
                remaining = new Rectangle(
                    remaining.X + slab.Width, remaining.Y, remaining.Width - slab.Width, remaining.Height);
            }
            else
            {
                slab = remaining with { Height = remaining.Height * share };
                remaining = new Rectangle(
                    remaining.X, remaining.Y + slab.Height, remaining.Width, remaining.Height - slab.Height);
            }

            if (node == Root)
            {
                root = slab;
                continue;
            }

            // Depth zero, beside the root rather than inside it: neither is part of what was scanned.
            if (Visible(slab, canvas))
            {
                tiles.Add(Tile(node, 0, bytes, slab));
            }
        }

        return root;
    }

    /// <summary>Stands for the root among the parts <see cref="BesideTheVolume"/> lays out.</summary>
    private const int Root = int.MaxValue;

    private readonly record struct Rectangle(double X, double Y, double Width, double Height);
}
